import base64
import json
import os

import pytest
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

from conftest import REPO
from pops_agent import release

# Agent/POps.Tests/TestData: tools/sign_release.py ile atılacak bir anahtarla üretilmiş vektör (Windows testleriyle
# ortak)
TESTDATA = os.path.join(REPO, "Agent", "POps.Tests", "TestData")
TEST_PUBLIC_KEY = "9enOkSVQHBXaXoAurkfvUFBqSbfYbJQbMwL0zEIPTqQ="


def _vector():
    with open(os.path.join(TESTDATA, "manifest.json"), "rb") as f:
        manifest = f.read()
    with open(os.path.join(TESTDATA, "manifest.json.sig"), encoding="ascii") as f:
        sig = f.read()
    return manifest, sig


def test_signed_vector_verifies_like_windows():
    manifest, sig = _vector()
    assert release.verify_signature(manifest, sig, TEST_PUBLIC_KEY)
    parsed = release.parse(manifest)
    assert parsed.version == "0.1.3-alpha" and parsed.tag == "v0.1.3-alpha" and len(parsed.artifacts) == 2


def test_tampered_or_foreign_signature_rejected():
    manifest, sig = _vector()
    assert not release.verify_signature(manifest.replace(b"0.1.3-alpha", b"0.1.4-alpha"), sig, TEST_PUBLIC_KEY)
    assert not release.verify_signature(manifest, sig)   # gerçek release anahtarıyla değil
    assert not release.verify_signature(manifest, "bm90LWJhc2U2NA", TEST_PUBLIC_KEY)
    assert not release.verify_signature(manifest, "", TEST_PUBLIC_KEY)
    assert not release.verify_signature(manifest, "@@@", TEST_PUBLIC_KEY)


def test_embedded_key_is_the_repo_release_key():
    with open(os.path.join(REPO, "keys", "pops_release_ed25519.pub.pem"), "rb") as f:
        key = serialization.load_pem_public_key(f.read())
    raw = key.public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)
    assert base64.b64encode(raw).decode() == release.PUBLIC_KEY_B64


@pytest.mark.parametrize("a,b,expected", [
    ("0.1.2-alpha", "0.1.2", -1), ("0.1.2", "0.1.3-alpha", -1), ("0.1.10", "0.1.9", 1), ("v1.0.0", "1.0.0", 0),
    ("1.0.0+build.5", "1.0.0", 0), ("1.0.0-alpha.2", "1.0.0-alpha.10", -1), ("1.0.0-alpha", "1.0.0-alpha.1", -1),
    ("1.0.0-alpha.beta", "1.0.0-beta", -1), ("1.0.0-1", "1.0.0-alpha", -1), ("0.1.22-alpha", "0.1.21-alpha", 1),
])
def test_semver_precedence(a, b, expected):
    assert release.compare_versions(a, b) == expected
    assert release.compare_versions(b, a) == -expected


@pytest.mark.parametrize("bad", ["", "1.2", "1.2.3.4", "a.b.c", "1.2.3-", "1.2.3-al..pha", None])
def test_invalid_versions(bad):
    assert release.parse_semver(bad) is None


def test_deb_version_mapping():
    assert release.deb_version("0.1.22-alpha") == "0.1.22~alpha"
    assert release.deb_version("v0.1.22") == "0.1.22"
    assert release.from_deb_version("0.1.22~alpha") == "0.1.22-alpha"
    assert release.deb_name("0.1.22-alpha") == "pops-agent_0.1.22-alpha_all.deb"


def _manifest(version="0.1.22-alpha", artifacts=None):
    if artifacts is None:
        artifacts = [
            {"name": "POps-Agent-%s-win-x64.msi" % version, "sha256": "a" * 64, "size": 40000000},
            {"name": "pops-agent_%s_all.deb" % version, "sha256": "b" * 64, "size": 90000},
            {"name": "pops-server-%s.tar.gz" % version, "sha256": "c" * 64, "size": 900000},
        ]
    return release.parse(json.dumps({"schema": "pops-manifest/1", "version": version, "tag": "v" + version,
                                     "released_at": 1, "artifacts": artifacts}).encode())


def test_select_deb_from_mixed_manifest():
    deb = release.select_deb(_manifest())
    assert deb.name == "pops-agent_0.1.22-alpha_all.deb" and deb.sha256 == "b" * 64 and deb.size == 90000


@pytest.mark.parametrize("artifacts,message", [
    ([{"name": "POps-Agent-0.1.22-alpha-win-x64.msi", "sha256": "a" * 64, "size": 1}], "0 bulundu"),
    ([{"name": "pops-agent_0.1.22-alpha_all.deb", "sha256": "b" * 64, "size": 1},
      {"name": "pops-agent_0.1.21-alpha_all.deb", "sha256": "b" * 64, "size": 1}], "2 bulundu"),
    ([{"name": "pops-agent_0.1.21-alpha_all.deb", "sha256": "b" * 64, "size": 1}], "uyuşmuyor"),
    ([{"name": "pops-agent_0.1.22-alpha_all.deb", "sha256": "B" * 63, "size": 1}], "geçersiz"),
    ([{"name": "pops-agent_0.1.22-alpha_all.deb", "sha256": "b" * 64, "size": 0}], "geçersiz"),
    ([{"name": "pops-agent_0.1.22-alpha_all.deb", "sha256": "b" * 64, "size": 10 ** 9}], "geçersiz"),
    ([{"name": "pops-agent_0.1.22-alpha_all.deb", "sha256": "b" * 64}], "geçersiz"),
])
def test_select_deb_rejections(artifacts, message):
    with pytest.raises(release.ManifestError, match=message):
        release.select_deb(_manifest(artifacts=artifacts))


@pytest.mark.parametrize("doc", [
    b"[]", b"not json", json.dumps({"schema": "x", "version": "1.0.0", "artifacts": []}).encode(),
    json.dumps({"schema": "pops-manifest/1", "version": "latest", "artifacts": []}).encode(),
    json.dumps({"schema": "pops-manifest/1", "version": "1.0.0"}).encode(),
])
def test_parse_rejects_malformed(doc):
    with pytest.raises(release.ManifestError):
        release.parse(doc)


def sign(manifest_bytes: bytes, key: Ed25519PrivateKey) -> str:
    return base64.b64encode(key.sign(manifest_bytes)).decode()


def canonical(manifest: dict) -> bytes:
    """tools/sign_release.py'nin imzaladığı baytlar."""
    return (json.dumps(manifest, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode("utf-8")


def test_sign_release_bytes_roundtrip():
    key = Ed25519PrivateKey.generate()
    pub = base64.b64encode(key.public_key().public_bytes(serialization.Encoding.Raw,
                                                         serialization.PublicFormat.Raw)).decode()
    body = canonical({"schema": "pops-manifest/1", "version": "9.9.9", "tag": "v9.9.9", "released_at": 1,
                      "artifacts": [{"name": "pops-agent_9.9.9_all.deb", "sha256": "d" * 64, "size": 5}]})
    assert release.verify_signature(body, sign(body, key), pub)
    assert release.select_deb(release.parse(body)).name == "pops-agent_9.9.9_all.deb"
