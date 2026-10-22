"""Uçtan uca test için atılacak sertifikalar: test CA'sı ve localhost / 127.0.0.1 / ::1 için sunucu sertifikası.

Yalnızca geçici runner'da kullanılır; anahtarlar iş bitince runner'la birlikte gider. Ajan SERVER_CA_CERT ile bu CA'ya
sabitlenir (C:\\POpsData\\secure\\server-ca.pem), uvicorn sunucu sertifikasıyla TLS açar.

    python certs.py <çıktı klasörü>
"""

import datetime
import ipaddress
import os
import sys

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID


def _key():
    return rsa.generate_private_key(public_exponent=65537, key_size=2048)


def _write(path, data):
    with open(path, "wb") as f:
        f.write(data)


def main(out):
    os.makedirs(out, exist_ok=True)
    now = datetime.datetime.now(datetime.timezone.utc)
    # Saat testi (T4) sunucu kapalıyken yapılır; yine de geçerlilik iki yönde de geniş tutulur
    start, end = now - datetime.timedelta(days=2), now + datetime.timedelta(days=30)

    ca_key = _key()
    ca_name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "POps E2E Test CA")])
    ca = (
        x509.CertificateBuilder()
        .subject_name(ca_name)
        .issuer_name(ca_name)
        .public_key(ca_key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(start)
        .not_valid_after(end)
        .add_extension(x509.BasicConstraints(ca=True, path_length=0), critical=True)
        .add_extension(x509.KeyUsage(digital_signature=False, content_commitment=False, key_encipherment=False,
                                     data_encipherment=False, key_agreement=False, key_cert_sign=True,
                                     crl_sign=True, encipher_only=False, decipher_only=False), critical=True)
        .add_extension(x509.SubjectKeyIdentifier.from_public_key(ca_key.public_key()), critical=False)
        .sign(ca_key, hashes.SHA256())
    )

    srv_key = _key()
    srv = (
        x509.CertificateBuilder()
        .subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "localhost")]))
        .issuer_name(ca_name)
        .public_key(srv_key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(start)
        .not_valid_after(end)
        .add_extension(x509.SubjectAlternativeName([
            x509.DNSName("localhost"),
            x509.IPAddress(ipaddress.ip_address("127.0.0.1")),
            x509.IPAddress(ipaddress.ip_address("::1")),
        ]), critical=False)
        .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
        .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
        .add_extension(x509.AuthorityKeyIdentifier.from_issuer_public_key(ca_key.public_key()), critical=False)
        .sign(ca_key, hashes.SHA256())
    )

    pem = serialization.Encoding.PEM
    nopass = serialization.NoEncryption()
    pkcs8 = serialization.PrivateFormat.PKCS8
    _write(os.path.join(out, "ca.pem"), ca.public_bytes(pem))
    _write(os.path.join(out, "server.crt"), srv.public_bytes(pem))
    _write(os.path.join(out, "server.key"), srv_key.private_bytes(pem, pkcs8, nopass))
    # CA'nın özel anahtarı yazılmaz: başka sertifika üretilmeyecek
    print("test CA + sunucu sertifikası: %s" % out)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "tls")
