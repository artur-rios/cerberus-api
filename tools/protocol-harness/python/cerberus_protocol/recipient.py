"""HPKE base confidentiality plus independently pinned author authentication."""
from cryptography.exceptions import UnsupportedAlgorithm
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives import hpke

from .encoding import context, guid, integer, base64, unbase64
from .errors import ProtocolError
from .keys import public_jwk, thumbprint, sign, verify, _private, _public
from .protection import _snapshot
from .symmetric import Context, CONTENT_KINDS, _material
from .client_trust import ClientTrust, _key
from .wire import encode

FORMAT = "cerberus-recipient-wrap-v1"
FIELDS = {"format", "keyEpoch", "grantId", "grantRevision", "recipientIdentityId", "recipientKeyFingerprint",
          "authorKeyFingerprint", "enc", "ciphertext", "signature"}


def _binding(resource, grant_id, grant_revision, recipient_identity_id):
    if type(resource) is not Context or resource.resource_kind not in CONTENT_KINDS or resource.extra_context != ():
        raise ProtocolError()
    guid(grant_id)
    integer(grant_revision)
    guid(recipient_identity_id)


def _info(resource, grant_id, grant_revision, identity, recipient_fingerprint, author_fingerprint):
    return context([FORMAT, resource.owner_id, resource.resource_kind, resource.resource_id, resource.key_epoch,
                    grant_id, grant_revision, identity, recipient_fingerprint, author_fingerprint])


def _signed(info, enc, ciphertext):
    return context(["cerberus-recipient-signature-v1", base64(info), enc, ciphertext])


def _suite():
    return hpke.Suite(hpke.KEM.P256, hpke.KDF.HKDF_SHA256, hpke.AEAD.AES_256_GCM)


def _decapsulate(private_der, sealed, info):
    try:
        return _suite().decrypt(sealed, _private(private_der), info=info)
    except ProtocolError: raise
    except UnsupportedAlgorithm: raise ProtocolError("unsupported_dependency") from None
    except Exception: raise ProtocolError() from None


def wrap(resource_root, resource, grant_id, grant_revision, recipient_identity_id, recipient_jwk, author_private_der):
    _binding(resource, grant_id, grant_revision, recipient_identity_id)
    _material(resource_root, 32)
    recipient = _key(recipient_jwk)
    recipient_fingerprint, author_fingerprint = thumbprint(recipient), thumbprint(public_jwk(author_private_der))
    if recipient_fingerprint == author_fingerprint:
        raise ProtocolError()
    info = _info(resource, grant_id, grant_revision, recipient_identity_id, recipient_fingerprint, author_fingerprint)
    try:
        sealed = _suite().encrypt(resource_root, _public(recipient), info=info)
    except UnsupportedAlgorithm: raise ProtocolError("unsupported_dependency") from None
    except Exception: raise ProtocolError() from None
    if len(sealed) != 113:
        raise ProtocolError("unsupported_dependency")
    enc, ciphertext = base64(sealed[:65]), base64(sealed[65:])
    result = dict(format=FORMAT, keyEpoch=resource.key_epoch, grantId=grant_id, grantRevision=grant_revision,
                  recipientIdentityId=recipient_identity_id, recipientKeyFingerprint=recipient_fingerprint,
                  authorKeyFingerprint=author_fingerprint, enc=enc, ciphertext=ciphertext,
                  signature=base64(sign(author_private_der, _signed(info, enc, ciphertext))))
    encode(result)
    return result


def open_recipient(envelope, resource, grant_id, grant_revision, recipient_identity_id, recipient_private_der, trust):
    _binding(resource, grant_id, grant_revision, recipient_identity_id)
    if type(trust) is not ClientTrust:
        raise ProtocolError()
    value = _snapshot(envelope, FIELDS)
    recipient, author = trust._pins(resource.owner_id)
    recipient_fingerprint, author_fingerprint = thumbprint(recipient), thumbprint(author)
    if (value["format"] != FORMAT or integer(value["keyEpoch"]) != resource.key_epoch or value["grantId"] != grant_id
            or integer(value["grantRevision"]) != grant_revision or value["recipientIdentityId"] != recipient_identity_id
            or value["recipientKeyFingerprint"] != recipient_fingerprint or value["authorKeyFingerprint"] != author_fingerprint):
        raise ProtocolError()
    enc, ciphertext, signature = unbase64(value["enc"], 65), unbase64(value["ciphertext"], 48), unbase64(value["signature"], 64)
    info = _info(resource, grant_id, grant_revision, recipient_identity_id, recipient_fingerprint, author_fingerprint)
    if not verify(author, _signed(info, value["enc"], value["ciphertext"]), signature):
        raise ProtocolError()
    if thumbprint(public_jwk(recipient_private_der)) != recipient_fingerprint or enc[0] != 4:
        raise ProtocolError()
    try:
        ec.EllipticCurvePublicKey.from_encoded_point(ec.SECP256R1(), enc)
    except UnsupportedAlgorithm: raise ProtocolError("unsupported_dependency") from None
    except Exception: raise ProtocolError() from None
    root = _decapsulate(recipient_private_der, enc + ciphertext, info)
    _material(root, 32)
    return root
