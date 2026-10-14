"""Verify .NET-produced public lease fixtures with independent Python and Java clients."""
import base64
import json
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
HARNESS = ROOT / "tools/protocol-harness"


def main():
    # This script uses the same pinned native environment as verify.py. It does
    # not implement cryptography or derive expected bindings from a signed token.
    sys.path.insert(0, str(HARNESS / "python"))
    sys.path.insert(0, str(HARNESS))
    from cerberus_protocol.lease import verify
    from cerberus_protocol.lease_trust import LeaseTrust
    from cerberus_protocol.errors import ProtocolError
    from verify import default_commands

    def reject(action):
        try:
            action()
        except ProtocolError:
            return
        raise RuntimeError("Native verifier accepted a rejected lease.")

    with tempfile.TemporaryDirectory(prefix="cerberus-server-leases-") as folder:
        artifact = Path(folder) / "leases.json"
        subprocess.run(["dotnet", "run", "--project", str(ROOT / "tools/OfflineLeaseGen/OfflineLeaseGen.csproj"), "--", str(artifact)], cwd=ROOT, check=True, timeout=300)
        doc = json.loads(artifact.read_text())
        if doc["classification"] != "public-test-fixtures" or len(doc["cases"]) != 4:
            raise RuntimeError("Invalid public fixture artifact.")
        trust = LeaseTrust(doc["issuer"], doc["pinnedJwk"], 1)
        wrong = LeaseTrust(doc["issuer"], doc["wrongJwk"], 1)
        for item in doc["cases"]:
            token, expected, now, seconds = item["token"], item["expected"], item["now"], item["seconds"]
            claims = verify(token, expected, trust, now)
            assert claims["iat"] == now and (claims["exp"] == now + seconds if seconds else "exp" not in claims)
            reject(lambda: verify(token, expected, wrong, now))
            reject(lambda: verify(token, expected, trust, now - 1))
            reject(lambda: verify(token, dict(expected, policyRevision=99), trust, now))
            parts = token.split(".")
            signature = bytearray(base64.urlsafe_b64decode(parts[2] + "==")); signature[0] ^= 1
            damaged = ".".join(parts[:2]) + "." + base64.urlsafe_b64encode(signature).decode().rstrip("=")
            reject(lambda: verify(damaged, expected, trust, now))
            if seconds:
                reject(lambda: verify(token, expected, trust, now + seconds))
            else:
                verify(token, expected, trust, now + 1000000)
        java, _ = default_commands()
        subprocess.run(java[:3] + [str(ROOT / "tools/OfflineLeaseGen/VerifyServerLeases.java"), str(artifact)], cwd=ROOT, check=True, timeout=300)
    print("Python verified four server leases and rejected invalid signatures, trust, binding, future issuance and enabled expiry.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
