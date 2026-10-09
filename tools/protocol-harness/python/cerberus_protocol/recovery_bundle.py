"""Client recovery bundle: random secret and an independent proof key."""
import secrets

from .encoding import integer, base64, unbase64, parse
from .errors import ProtocolError
from .keys import public_jwk, thumbprint
from .protection import _snapshot, _structure, _private, validate
from .symmetric import Context, _FIELDS as ENVELOPE_FIELDS, _material, _seal_bound, _open_bound, _validate_bound
from .wire import encode

FORMAT = "cerberus-recovery-wrap-v1"
BUNDLE = "cerberus-recovery-bundle-v1"
WRAPPER_FIELDS = ENVELOPE_FIELDS | {"generation", "proofKeyFingerprint"}
BUNDLE_FIELDS = {"format", "generation", "proofPrivateKey", "protectionBundle"}


def _slot(slot):
    if type(slot) is not Context or slot.resource_kind != "recovery" or slot.resource_id != slot.owner_id or slot.extra_context != ():
        raise ProtocolError()


def _context(slot, generation, fingerprint):
    return Context(slot.owner_id, slot.resource_kind, slot.resource_id, slot.key_epoch, [generation, fingerprint])


def wrap(secret, account_bundle, proof_private_der, slot, generation, guard):
    return wrap_fixture(secret, account_bundle, proof_private_der, slot, generation, guard, secrets.token_bytes(32), secrets.token_bytes(12))


def wrap_fixture(secret, account_bundle, proof_private_der, slot, generation, guard, key_salt, nonce):
    _slot(slot)
    _material(secret, 32)
    integer(generation)
    account = _structure(account_bundle, "account", slot.resource_id)
    fingerprint = thumbprint(public_jwk(proof_private_der))
    bundle = dict(format=BUNDLE, generation=generation, proofPrivateKey=base64(proof_private_der), protectionBundle=account)
    result = _seal_bound(secret, _context(slot, generation, fingerprint), FORMAT, encode(bundle), guard, key_salt, nonce)
    result.update(generation=generation, proofKeyFingerprint=fingerprint)
    encode(result)
    return result


def unwrap(secret, wrapper, slot, generation, recovery_jwk, allowed_roots, unlock_jwk):
    _slot(slot)
    _material(secret, 32)
    integer(generation)
    value = _snapshot(wrapper, WRAPPER_FIELDS)
    fingerprint = thumbprint(recovery_jwk)
    unbase64(value["proofKeyFingerprint"], 32)
    if integer(value["generation"]) != generation or value["proofKeyFingerprint"] != fingerprint:
        raise ProtocolError()
    expected = _context(slot, generation, fingerprint)
    envelope = {key: value[key] for key in ENVELOPE_FIELDS}
    _validate_bound(expected, FORMAT, envelope)
    raw = _open_bound(secret, expected, FORMAT, envelope)
    bundle = parse(raw, BUNDLE_FIELDS, set())
    if bundle["format"] != BUNDLE or integer(bundle["generation"]) != generation:
        raise ProtocolError()
    if thumbprint(public_jwk(_private(bundle["proofPrivateKey"]))) != fingerprint:
        raise ProtocolError()
    bundle["protectionBundle"] = validate(bundle["protectionBundle"], "account", slot.resource_id, allowed_roots, unlock_jwk)
    return bundle
