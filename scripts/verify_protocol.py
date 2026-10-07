"""Fail the dependent-implementation/release gate until protocol reviews are recorded."""
import argparse
import json
import hashlib
from datetime import datetime
from pathlib import Path


def validate(record, root):
    if not isinstance(record, dict) or record.get("version") != 1:
        return ["Invalid protocol review record version."]
    if record.get("status") != "approved":
        return ["Protocol security and client review is pending."]
    errors = []
    if not all(isinstance(record.get(key), str) and record[key].strip()
               for key in ("securityReviewer", "interoperabilityReviewer")):
        errors.append("Security and interoperability reviewers are required.")
    try:
        reviewed = datetime.fromisoformat(record.get("reviewedAt", "").replace("Z", "+00:00"))
        if reviewed.utcoffset() is None:
            raise ValueError()
    except (ValueError, TypeError, AttributeError):
        errors.append("A valid review date is required.")
    reference = record.get("reviewReference")
    if not isinstance(reference, str) or not reference.startswith("https://"):
        errors.append("An HTTPS review evidence reference is required.")
    artifacts = record.get("artifacts")
    if not isinstance(artifacts, list) or not artifacts:
        errors.append("Reviewed artifacts are required.")
        return errors
    root = root.resolve()
    for artifact in artifacts:
        if not isinstance(artifact, dict) or not isinstance(artifact.get("path"), str):
            errors.append("Invalid reviewed artifact entry.")
            continue
        path = (root / artifact["path"]).resolve()
        if not path.is_relative_to(root) or not path.is_file():
            errors.append("Reviewed artifact is missing or outside the repository.")
            continue
        try:
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
        except OSError:
            errors.append("Cannot read reviewed artifact.")
            continue
        if digest != artifact.get("sha256"):
            errors.append("Reviewed artifact digest does not match: " + artifact["path"])
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--record", type=Path, default=Path("docs/security/protocol-review.json"))
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    try:
        record = json.loads(args.record.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        print("BLOCKED: Protocol review record is missing or invalid.")
        return 1
    errors = validate(record, root)
    for error in errors:
        print("BLOCKED: " + error)
    return int(bool(errors))


if __name__ == "__main__":
    raise SystemExit(main())
