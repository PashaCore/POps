"""Ajanın X-Agent-Version başlığındaki sürümü (ör. "0.1.13-alpha", "v0.1.12") ve X-Agent-Features başlığındaki
özellikleri (ör. "winget"): yeni davranışları yalnızca onu anlayan ajanlara uygulamak için."""

import re
from typing import List, Optional, Tuple

# Başlıktan gelen değer: kısa tutulur ve basamak sayısı sınırlıdır (uzun girdide düzenli ifade yavaşlamasın)
_VERSION = re.compile(r"v?(\d{1,6})\.(\d{1,6})\.(\d{1,6})")
_FEATURE = re.compile(r"[a-z0-9_]{1,32}")


def parse(agent_version: Optional[str]) -> Optional[Tuple[int, int, int]]:
    m = _VERSION.match((agent_version or "")[:32].strip())
    return tuple(int(x) for x in m.groups()) if m else None


def at_least(agent_version: Optional[str], minimum: Tuple[int, int, int]) -> bool:
    parsed = parse(agent_version)
    return parsed is not None and parsed >= minimum


def features(header: Optional[str]) -> List[str]:
    """X-Agent-Features: virgülle ayrılmış özellik adları ("winget"). Tanınmayan biçimdeki ad atılır; en çok 32 ad,
    sıralı ve tekrarsız. Başlık yoksa boş liste (eski ajan)."""
    names = {n.strip().lower() for n in (header or "")[:1024].split(",")}
    return sorted(n for n in names if _FEATURE.fullmatch(n))[:32]
