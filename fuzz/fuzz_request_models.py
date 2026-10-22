#!/usr/bin/env python3
"""Fuzz hedefi: HTTP istek gövdelerinin modelleri (Backend/pops/models.py) ve politika alan adı listesinin temizlenmesi
(pops/routers/agents.py _clean_dns_domains).

Girdi bir JSON gövdesidir; pops.models'taki her istek modeliyle (panelin, ajanın ve /api/v1 istemcilerinin
gövdeleri) doğrulanır. Denetlenen: doğrulama ya modeli verir ya ValidationError (FastAPI'de 422), başka hiçbir hata
yok (özel doğrulayıcılar dahil); StrictInput modelleri tanımadığı alanı kabul etmez (iç içe StrictInput dahil);
target_mode büyük/küçük harf duyarsız okunur ama ALL, LAB ya da PC dışında bir değer kabul edilmez; kabul edilen
politikanın alan adı listesi temizlendikten sonra kategori başına en çok 5000 tekil, boşluksuz ve denetim/biçim
karaktersiz, en çok 253 karakterlik ad, yolsuz ve şemasız; kategori adı boş değil, en çok 60 karakter, denetim
karaktersiz (ajanda eşleşmeyen ya da PostgreSQL'in saklayamadığı değer kalmaz).

    python fuzz/fuzz_request_models.py -max_total_time=60 <yeni-girdiler-klasörü> fuzz/corpus/request_models
"""

import inspect
import json
import unicodedata

import common

from pydantic import BaseModel, ValidationError  # noqa: E402

with common.backend_imports():
    from pops import models
    from pops.routers import agents

MODELS = [
    cls for _name, cls in inspect.getmembers(models, inspect.isclass)
    if issubclass(cls, BaseModel) and cls.__module__ == models.__name__ and cls is not models.StrictInput
]


def accepted_keys(cls):
    keys = set()
    for name, field in cls.model_fields.items():
        keys.add(name)
        alias = field.validation_alias
        if isinstance(alias, str):
            keys.add(alias)
        elif alias is not None:
            keys.update(c for c in getattr(alias, "choices", []) if isinstance(c, str))
    return keys


def check_strict(cls, raw, where):
    if issubclass(cls, models.StrictInput):
        extra = set(raw) - accepted_keys(cls)
        assert not extra, "%s tanımadığı alanı kabul etti: %s" % (where, sorted(extra))


def bad_char(text):
    return any(c.isspace() or unicodedata.category(c) in ("Cc", "Cf", "Cs") for c in text)


def TestOneInput(data):
    try:
        raw = json.loads(data)
    except (ValueError, RecursionError):
        raw = None
    for cls in MODELS:
        try:
            model = cls.model_validate_json(data)
        except ValidationError:
            continue
        assert isinstance(raw, dict), "%s JSON nesnesi olmayan gövdeyi kabul etti" % cls.__name__
        check_strict(cls, raw, cls.__name__)
        if cls is models.OrchestrationInput:
            assert model.target_mode in ("ALL", "LAB", "PC"), model.target_mode
            items = raw.get("task_sequence", raw.get("taskSequence"))
            for item in items:
                check_strict(models.TaskSequenceItem, item, "TaskSequenceItem")
        if cls is models.AgentPoliciesInput and model.dns_domains is not None:
            check_domains(agents._clean_dns_domains(model.dns_domains))


def check_domains(cleaned):
    for cat, domains in cleaned.items():
        assert cat and len(cat) <= 60, "kategori: %r" % cat
        assert not any(unicodedata.category(c) in ("Cc", "Cf", "Cs") for c in cat), "kategori: %r" % cat
        assert len(domains) <= 5000 and len(set(domains)) == len(domains), "kategori %r: tekrar ya da fazla ad" % cat
        for d in domains:
            assert d and len(d) <= 253 and "/" not in d and not bad_char(d), "alan adı: %r" % d
            assert not d.startswith((".", "*")) and not d.endswith("."), "alan adı: %r" % d


if __name__ == "__main__":
    common.run(TestOneInput)
