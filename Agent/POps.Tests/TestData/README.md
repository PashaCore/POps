# Test vectors

`manifest.json` and `manifest.json.sig` were produced by `tools/sign_release.py sign` with a throwaway ed25519 key that exists only for these tests. The private key was never committed, and the release key is not involved. The raw public key is in `ReleaseVerifierTests.TestPublicKey`. The vectors prove that the agent's verifier accepts exactly the bytes the release tooling signs.
