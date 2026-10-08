"""Explicit public-fixture CLI. Exceptions never echo fixture/key/body contents."""
import json
from pathlib import Path
import sys


def safe(action):
    try: action(); return 0
    except Exception as error:
        dependency = isinstance(error, (ImportError, ModuleNotFoundError)) or getattr(error, "code", None) == "unsupported_dependency"
        print(json.dumps(dict(code="unsupported_dependency" if dependency else "invalid_protocol")), file=sys.stderr)
        return 1


def main(args):
    from .fixture_cases import produce, consume, self_test, read, write
    if len(args) == 3 and args[0] == "produce": write(args[2], produce(read(args[1])))
    elif len(args) == 3 and args[0] == "consume": write(args[2], consume(read(args[1])))
    elif len(args) == 2 and args[0] == "self-test": write(args[1], self_test())
    else: raise ValueError()


if __name__ == "__main__": raise SystemExit(safe(lambda: main(sys.argv[1:])))
