"""Stateless challenge/proof encoding. Registry and atomic consumption are separate."""
import builtins
import secrets
import uuid

from .encoding import guid, integer, text, context, base64, unbase64, parse_object, MAX_INTEGER, MAX_REQUEST
from .errors import ProtocolError
from .keys import sign as sign_key, verify as verify_key
from .primitives import sha256
from .protection import _snapshot
from .symmetric import _material

FORMAT = "cerberus-challenge-v1"
BINDING_FIELDS = {"operation", "identityId", "accountId", "scopeKind", "scopeId", "keyEpoch", "protectionRevision", "generation"}
FIELDS = BINDING_FIELDS | {"format", "challengeId", "nonce", "requestHash", "issuedAt", "expiresAt"}
OPERATIONS = {"unlock-account", "unlock-profile", "change-protection", "recover", "refresh-recovery"}


def _binding(expected):
    value = _snapshot(expected, BINDING_FIELDS)
    operation, scope = text(value["operation"], True), text(value["scopeKind"], True)
    if operation not in OPERATIONS or scope not in ("account", "profile"):
        raise ProtocolError()
    guid(value["identityId"]); guid(value["accountId"]); guid(value["scopeId"])
    integer(value["keyEpoch"]); integer(value["protectionRevision"])
    if scope == "account" and value["scopeId"] != value["accountId"]:
        raise ProtocolError()
    if operation == "unlock-account" and scope != "account" or operation == "unlock-profile" and scope != "profile":
        raise ProtocolError()
    if operation == "recover":
        if scope != "account": raise ProtocolError()
        integer(value["generation"])
    elif value["generation"] is not None:
        raise ProtocolError()
    return value


def _body_hash(raw):
    if type(raw) is not builtins.bytes or len(raw) > MAX_REQUEST:
        raise ProtocolError()
    digest = sha256(raw)
    parse_object(raw)
    return base64(digest)


def _challenge(challenge):
    value = _snapshot(challenge, FIELDS)
    _binding({key: value[key] for key in BINDING_FIELDS})
    if value["format"] != FORMAT:
        raise ProtocolError()
    guid(value["challengeId"])
    unbase64(value["nonce"], 32); unbase64(value["requestHash"], 32)
    issued = integer(value["issuedAt"], 0, MAX_INTEGER-60)
    if integer(value["expiresAt"], 0) != issued + 60:
        raise ProtocolError()
    return value


def issue(expected_binding, raw_body, now):
    return issue_fixture(expected_binding, raw_body, now, str(uuid.uuid4()), secrets.token_bytes(32))


def issue_fixture(expected_binding, raw_body, now, challenge_id, nonce):
    value = _binding(expected_binding)
    integer(now, 0, MAX_INTEGER-60)
    guid(challenge_id)
    _material(nonce, 32)
    value.update(format=FORMAT, challengeId=challenge_id, nonce=base64(nonce), requestHash=_body_hash(raw_body), issuedAt=now, expiresAt=now+60)
    return _challenge(value)


def bytes(challenge):
    value = _challenge(challenge)
    return context(["cerberus-proof-v1", value["challengeId"], value["nonce"], value["operation"], value["identityId"], value["accountId"],
                    value["scopeKind"], value["scopeId"], value["keyEpoch"], value["protectionRevision"], value["generation"],
                    value["requestHash"], value["issuedAt"], value["expiresAt"]])


def sign(challenge, scoped_private_der):
    return sign_key(scoped_private_der, bytes(challenge))


def verify(challenge, expected_binding, raw_body, registered_jwk, signature, now):
    value, expected = _challenge(challenge), _binding(expected_binding)
    integer(now, 0)
    if now < value["issuedAt"] or now >= value["expiresAt"]:
        raise ProtocolError()
    if any(value[key] != expected[key] for key in BINDING_FIELDS) or value["requestHash"] != _body_hash(raw_body):
        raise ProtocolError()
    if not verify_key(registered_jwk, bytes(value), signature):
        raise ProtocolError()
