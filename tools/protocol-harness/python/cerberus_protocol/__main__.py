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
    elif len(args) == 3 and args[0] == "benchmark": write(args[2], benchmark(int(args[1])))
    else: raise ValueError()


def benchmark(samples):
    import platform
    import time
    import cryptography
    from .primitives import argon2
    if type(samples) is not int or not 1 <= samples <= 1000: raise ValueError()
    password, salt = b'public benchmark fixture', bytes(range(16))
    argon2(password, salt)
    times=[]
    for _ in range(samples):
        start=time.perf_counter_ns(); argon2(password, salt); times.append((time.perf_counter_ns()-start)/1000000)
    return dict(implementation="python",version="1.0.0",samples=samples,elapsedMilliseconds=times,
                profile=dict(memoryKiB=65536,iterations=3,parallelism=4,length=32),
                environment=dict(os=platform.platform(),architecture=platform.machine(),runtime=platform.python_version(),library="cryptography "+cryptography.__version__))


if __name__ == "__main__": raise SystemExit(safe(lambda: main(sys.argv[1:])))
