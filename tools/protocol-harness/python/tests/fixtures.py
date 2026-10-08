"""Only public test data, never implementations or crypto algorithms."""
import base64
import copy
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2] / "fixtures"


def known(name):
    return json.loads((ROOT / "known-answers.json").read_text(encoding="utf-8"))[name]


def fixture(role):
    value = copy.deepcopy(json.loads((ROOT / "input.json").read_text(encoding="utf-8"))["keys"][role])
    value["privateDer"] = base64.urlsafe_b64decode(value["privateDer"] + "=" * (-len(value["privateDer"]) % 4))
    return value


def bundle(scope):
    return json.loads((ROOT / "input.json").read_text(encoding="utf-8"))["bundles"][scope]


def allowed(scope):
    return set(json.loads((ROOT / "input.json").read_text(encoding="utf-8"))["membership"][scope])
