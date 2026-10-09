"""Strict offline JWS; cached verification cannot establish an online time anchor."""
import uuid

from .encoding import fields, guid, integer, text, base64, unbase64, parse, MAX_INTEGER, MAX_REQUEST
from .errors import ProtocolError
from .keys import sign as key_sign, verify as key_verify, thumbprint, public_jwk
from .protection import _snapshot
from .wire import encode
from .lease_trust import LeaseTrust

EXPECTED_FIELDS = {"iss", "aud", "sub", "accountId", "scopeKind", "scopeId", "keyEpoch", "protectionRevision",
                   "policyRevision", "revocationGeneration", "grantRevisions", "renewalEnabled"}
CLAIM_FIELDS = EXPECTED_FIELDS | {"iat", "nbf", "jti"}
TYPE = "cerberus-offline-v1+jwt"


def _expected(value):
    value = _snapshot(value, EXPECTED_FIELDS)
    if not text(value["iss"], True) or value["aud"] != "cerberus-offline-clients-v1": raise ProtocolError()
    for field in ("sub", "accountId", "scopeId"): guid(value[field])
    if value["scopeKind"] not in ("account", "profile") or value["scopeKind"] == "account" and value["scopeId"] != value["accountId"]:
        raise ProtocolError()
    for field in ("keyEpoch", "protectionRevision", "policyRevision", "revocationGeneration"): integer(value[field])
    if type(value["renewalEnabled"]) is not bool or type(value["grantRevisions"]) is not list: raise ProtocolError()
    previous = ""
    for grant in value["grantRevisions"]:
        fields(grant, {"grantId", "revision"})
        identity = guid(grant["grantId"]); integer(grant["revision"])
        if identity <= previous: raise ProtocolError()
        previous = identity
    return value


def _claims(value):
    fields(value, CLAIM_FIELDS, {"exp"})
    value = parse(encode(value), CLAIM_FIELDS, {"exp"})
    _expected({key: value[key] for key in EXPECTED_FIELDS})
    issued = integer(value["iat"], 0); guid(value["jti"])
    if integer(value["nbf"], 0) != issued: raise ProtocolError()
    if value["renewalEnabled"]:
        if "exp" not in value or integer(value["exp"], 0) <= issued: raise ProtocolError()
    elif "exp" in value: raise ProtocolError()
    return value


def claims(expected, issued_at, renewal_enabled, duration_seconds=None):
    value = _expected(expected); issued = integer(issued_at, 0)
    if type(renewal_enabled) is not bool or renewal_enabled != value["renewalEnabled"]: raise ProtocolError()
    value.update(iat=issued, nbf=issued, jti=str(uuid.uuid4()))
    if renewal_enabled:
        duration = 86400 if duration_seconds is None else integer(duration_seconds)
        value["exp"] = integer(issued + duration, 0)
    elif duration_seconds is not None: raise ProtocolError()
    return _claims(value)


def sign(claims, lease_private_der):
    value = _claims(claims)
    header = dict(alg="ES256", typ=TYPE, kid=thumbprint(public_jwk(lease_private_der)))
    prefix = base64(encode(header)) + "." + base64(encode(value))
    token = prefix + "." + base64(key_sign(lease_private_der, prefix.encode("ascii")))
    if len(token) > MAX_REQUEST: raise ProtocolError()
    return token


def verify(compact, expected, trust, effective_now):
    text(compact, True)
    if len(compact) > MAX_REQUEST or type(trust) is not LeaseTrust: raise ProtocolError()
    parts = compact.split(".")
    if len(parts) != 3: raise ProtocolError()
    header = parse(unbase64(parts[0], len(parts[0])*3//4), {"alg", "typ", "kid"}, set())
    if header["alg"] != "ES256" or header["typ"] != TYPE: raise ProtocolError()
    key = trust.lookup(header["kid"])
    signature = unbase64(parts[2], 64)
    if not key_verify(key, (parts[0]+"."+parts[1]).encode("ascii"), signature): raise ProtocolError()
    value = _claims(parse(unbase64(parts[1], len(parts[1])*3//4), CLAIM_FIELDS, {"exp"}))
    authority = _expected(expected); now = integer(effective_now, 0)
    if authority["iss"] != trust.issuer or any(value[key] != authority[key] for key in EXPECTED_FIELDS): raise ProtocolError()
    if now < value["iat"] or value["renewalEnabled"] and now >= value["exp"]: raise ProtocolError()
    return value
