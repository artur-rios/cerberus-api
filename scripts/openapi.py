"""Generate the actual HTTP contract and reject committed-contract drift."""


"""Generate and check the committed API contract without starting the server."""
import argparse
import json
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CONTRACT = ROOT / "docs/contracts/openapi.json"


def is_current(expected, actual):
    def valid(document):
        return isinstance(document, dict) and isinstance(document.get("openapi"), str) and isinstance(document.get("paths"), dict)
    return valid(expected) and valid(actual) and expected == actual


def drift(expected, actual, at="#", limit=20):
    """Where two documents differ, at most `limit` places: (JSON Pointer, committed value, generated value).
    A member only one side has is None on the other."""
    found = []

    def walk(old, new, path):
        if len(found) >= limit:
            return
        if isinstance(old, dict) and isinstance(new, dict):
            for key in list(old) + [key for key in new if key not in old]:
                child = path + "/" + str(key).replace("~", "~0").replace("/", "~1")
                if key not in old or key not in new:
                    found.append((child, old.get(key), new.get(key)))
                else:
                    walk(old[key], new[key], child)
        elif isinstance(old, list) and isinstance(new, list) and len(old) == len(new):
            for index, (first, second) in enumerate(zip(old, new)):
                walk(first, second, path + "/" + str(index))
        elif old != new:
            found.append((path, old, new))

    walk(expected, actual, at)
    return found[:limit]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--write", action="store_true", help="update the committed contract after reviewing API changes")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="cerberus-openapi-") as temporary:
        generated = Path(temporary) / "openapi.json"
        result = subprocess.run(["dotnet", "run", "--project", str(ROOT / "tools/OpenApiGen/OpenApiGen.csproj"), "--", str(generated)], cwd=ROOT)
        if result.returncode:
            return result.returncode
        try:
            actual = json.loads(generated.read_text(encoding="utf-8"))
            if args.write and is_current(actual, actual):
                CONTRACT.parent.mkdir(parents=True, exist_ok=True)
                CONTRACT.write_text(json.dumps(actual, indent=2) + "\n", encoding="utf-8")
                return 0
            expected = json.loads(CONTRACT.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            print("Missing or invalid OpenAPI contract.")
            return 1
        if not is_current(expected, actual):
            print("OpenAPI drift detected. Review the API change, then run scripts/openapi.py --write.")
            for path, old, new in drift(expected, actual):
                print("  " + path + ": " + json.dumps(old)[:300] + " -> " + json.dumps(new)[:300])
            return 1
        print("OpenAPI contract is current.")
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
