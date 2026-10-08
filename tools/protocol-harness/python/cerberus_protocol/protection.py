"""Scoped client-side password bundles; never a server-side password verifier."""
import secrets

from .encoding import fields, guid, integer, text, utf8, unbase64, base64, parse
from .errors import ProtocolError
from .keys import public_jwk, thumbprint
from .primitives import argon2
from .symmetric import Context, _FIELDS as ENVELOPE_FIELDS, _seal_bound, _open_bound, _validate_bound
from .wire import encode

FORMAT = "cerberus-password-wrap-v1"
BUNDLE = "cerberus-protection-bundle-v1"
BUNDLE_FIELDS = {"format", "scopeKind", "scopeId", "roots", "unlockPrivateKey"}
WRAPPER_FIELDS = ENVELOPE_FIELDS | {"kdf"}
KINDS = {"account", "profile", "record", "folder", "collection"}
KDF_FIELDS = {"algorithm", "memoryKiB", "iterations", "parallelism", "salt"}


def _snapshot(value, schema):
    return parse(encode(fields(value, schema)), schema, set())


def _private(value):
    text(value, True)
    return unbase64(value, len(value) * 3 // 4)


def _structure(bundle, scope_kind, scope_id):
    value = _snapshot(bundle, BUNDLE_FIELDS)
    guid(scope_id)
    if scope_kind not in ("account", "profile") or value["format"] != BUNDLE or value["scopeKind"] != scope_kind or value["scopeId"] != scope_id:
        raise ProtocolError()
    roots = value["roots"]
    if type(roots) is not list or not roots:
        raise ProtocolError()
    previous = None
    for root in roots:
        fields(root, {"resourceKind", "resourceId", "keyEpoch", "key"})
        kind, identity = text(root["resourceKind"], True), guid(root["resourceId"])
        if kind not in KINDS:
            raise ProtocolError()
        integer(root["keyEpoch"])
        unbase64(root["key"], 32)
        pair = (kind, identity)
        if previous is not None and pair <= previous:
            raise ProtocolError()
        previous = pair
        if kind == "account" and (scope_kind != "account" or identity != scope_id):
            raise ProtocolError()
        if scope_kind == "profile" and kind == "profile" and identity != scope_id:
            raise ProtocolError()
    public_jwk(_private(value["unlockPrivateKey"]))
    return value


def validate(bundle, scope_kind, scope_id, allowed_roots, unlock_jwk):
    value = _structure(bundle, scope_kind, scope_id)
    if type(allowed_roots) is not set or any(type(item) is not str for item in allowed_roots):
        raise ProtocolError()
    if any(root["resourceKind"] + ":" + root["resourceId"] not in allowed_roots for root in value["roots"]):
        raise ProtocolError()
    if thumbprint(public_jwk(_private(value["unlockPrivateKey"]))) != thumbprint(unlock_jwk):
        raise ProtocolError()
    return value


def _slot(slot):
    if type(slot) is not Context or slot.extra_context != () or slot.resource_kind not in ("account-protection", "profile-protection"):
        raise ProtocolError()
    scope = slot.resource_kind.removesuffix("-protection")
    if scope == "account" and slot.resource_id != slot.owner_id:
        raise ProtocolError()
    return scope


def _kdf(value):
    fields(value, KDF_FIELDS)
    if (value["algorithm"] != "argon2id-v1.3" or integer(value["memoryKiB"]) != 65536
            or integer(value["iterations"]) != 3 or integer(value["parallelism"]) != 4):
        raise ProtocolError()
    return unbase64(value["salt"], 16)


def _context(slot, kdf):
    return Context(slot.owner_id, slot.resource_kind, slot.resource_id, slot.key_epoch,
                   ["argon2id-v1.3", 65536, 3, 4, kdf["salt"]])


def wrap(password, bundle, slot, guard):
    return wrap_fixture(password, bundle, slot, guard, secrets.token_bytes(32), secrets.token_bytes(12), secrets.token_bytes(16))


def wrap_fixture(password, bundle, slot, guard, key_salt, nonce, password_salt):
    scope = _slot(slot)
    value = _structure(bundle, scope, slot.resource_id)
    password_bytes = utf8(password)
    kdf = dict(algorithm="argon2id-v1.3", memoryKiB=65536, iterations=3, parallelism=4, salt=base64(password_salt))
    _kdf(kdf)
    root = argon2(password_bytes, password_salt)
    result = _seal_bound(root, _context(slot, kdf), FORMAT, encode(value), guard, key_salt, nonce)
    result["kdf"] = kdf
    encode(result)
    return result


def unwrap(password, wrapper, slot, allowed_roots, unlock_jwk):
    scope = _slot(slot)
    value = _snapshot(wrapper, WRAPPER_FIELDS)
    password_salt = _kdf(value["kdf"])
    expected = _context(slot, value["kdf"])
    envelope = {key: value[key] for key in ENVELOPE_FIELDS}
    _validate_bound(expected, FORMAT, envelope)
    password_bytes = utf8(password)
    root = argon2(password_bytes, password_salt)
    raw = _open_bound(root, expected, FORMAT, envelope)
    return validate(parse(raw, BUNDLE_FIELDS, set()), scope, slot.resource_id, allowed_roots, unlock_jwk)
