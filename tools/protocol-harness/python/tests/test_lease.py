import copy
import importlib
import json
import unittest

from cerberus_protocol import keys
from cerberus_protocol.encoding import base64, context, MAX_INTEGER
from cerberus_protocol.errors import ProtocolError
from tests.fixtures import fixture

NOW = 1700000000
ACCOUNT = "00000000-0000-0000-0000-000000000001"
IDENTITY = "00000000-0000-0000-0000-000000000002"
OTHER = "00000000-0000-0000-0000-000000000008"
ISSUER = "https://cerberus.example.test"


def expected(enabled=True):
    return dict(iss=ISSUER, aud="cerberus-offline-clients-v1", sub=IDENTITY, accountId=ACCOUNT, scopeKind="account", scopeId=ACCOUNT,
                keyEpoch=1, protectionRevision=1, policyRevision=1, revocationGeneration=1, grantRevisions=[], renewalEnabled=enabled)


class LeaseTests(unittest.TestCase):
    def setUp(self):
        try:
            self.api = importlib.import_module("cerberus_protocol.lease")
            trust_api = importlib.import_module("cerberus_protocol.lease_trust")
        except ModuleNotFoundError: self.fail("Lease/LeaseTrust feature missing")
        self.key = fixture("lease")
        self.trust = trust_api.LeaseTrust(ISSUER, self.key["publicJwk"], 1)
        self.expected = expected()

    def claims(self, enabled=True, duration=None, issued=NOW, authority=None):
        return self.api.claims(authority or expected(enabled), issued, enabled, duration)

    def compact(self, payload=None, header=None, role="lease", payload_raw=None, header_raw=None):
        header = header or dict(alg="ES256", typ="cerberus-offline-v1+jwt", kid=keys.thumbprint(self.key["publicJwk"]))
        payload = self.claims() if payload is None else payload
        serialize = lambda value: json.dumps(value, separators=(",", ":"), ensure_ascii=False).encode()
        prefix = base64(header_raw or serialize(header)) + "." + base64(payload_raw or serialize(payload))
        return prefix + "." + base64(keys.sign(fixture(role)["privateDer"], prefix.encode("ascii")))

    def verify(self, token, authority=None, now=NOW):
        return self.api.verify(token, authority or self.expected, self.trust, now)

    def test_GivenEnabledOrDisabledPolicy_WhenSigned_ThenExactClaimsVerified(self):
        for enabled in [True, False]:
            authority = expected(enabled); claims = self.claims(enabled)
            token = self.api.sign(claims, self.key["privateDer"])
            self.assertEqual(claims, self.verify(token, authority))
            self.assertEqual(enabled, "exp" in claims)
            self.assertEqual(NOW+86400, claims.get("exp", NOW+86400))

    def test_GivenRepresentableDurations_WhenIssued_ThenNoInventedMaximum(self):
        for duration in [1, 86400, 10**12, MAX_INTEGER-NOW]:
            self.assertEqual(NOW+duration, self.claims(duration=duration)["exp"])
        for duration in [0, -1, True, MAX_INTEGER, MAX_INTEGER+1, 1.0, "1"]:
            with self.subTest(duration=duration), self.assertRaises(ProtocolError): self.claims(duration=duration)
        with self.assertRaises(ProtocolError): self.claims(issued=MAX_INTEGER)
        with self.assertRaises(ProtocolError): self.claims(False, 86400)
        with self.assertRaises(ProtocolError): self.claims(True, authority=expected(False))

    def test_GivenMissingOrForbiddenExpiry_WhenVerified_ThenRejected(self):
        enabled = self.claims(); del enabled["exp"]
        disabled = self.claims(False); disabled["exp"] = NOW+1
        for claims, authority in [(enabled, expected()), (disabled, expected(False))]:
            with self.assertRaises(ProtocolError): self.verify(self.compact(claims), authority)
        with self.assertRaises(ProtocolError): self.verify(self.compact(self.claims(False)), expected())

    def test_GivenFutureNotBeforeOrExclusiveExpiry_WhenVerified_ThenRejected(self):
        token = self.compact(self.claims(duration=1))
        self.verify(token)
        for now in [NOW-1, NOW+1, -1, True, MAX_INTEGER+1]:
            with self.subTest(now=now), self.assertRaises(ProtocolError): self.verify(token, now=now)
        claims = self.claims(); claims["nbf"] = NOW-1
        with self.assertRaises(ProtocolError): self.verify(self.compact(claims))

    def test_GivenEveryExpectedClaimChanged_WhenVerified_ThenRejected(self):
        changes = dict(iss="https://wrong.test", aud="wrong", sub=OTHER, accountId=OTHER, scopeKind="profile", scopeId=OTHER,
                       keyEpoch=2, protectionRevision=2, policyRevision=2, revocationGeneration=2, grantRevisions=[dict(grantId=OTHER, revision=1)], renewalEnabled=False)
        for field, value in changes.items():
            claims = self.claims(); claims[field] = value
            with self.subTest(field=field), self.assertRaises(ProtocolError): self.verify(self.compact(claims))

    def test_GivenReorderedPropertyBytes_WhenVerified_ThenOriginalSignatureInputUsed(self):
        claims = self.claims(); raw = json.dumps(dict(reversed(list(claims.items()))), separators=(",", ":")).encode()
        self.assertEqual(claims, self.verify(self.compact(payload_raw=raw)))
        token = self.compact(claims).split("."); token[1] = base64(raw)
        with self.assertRaises(ProtocolError): self.verify(".".join(token))

    def test_GivenUntrustedHeaderAlgorithmsKeysOrExtensions_WhenVerified_ThenRejected(self):
        standard = dict(alg="ES256", typ="cerberus-offline-v1+jwt", kid=keys.thumbprint(self.key["publicJwk"]))
        for field, value in [("alg", "none"), ("alg", "HS256"), ("alg", "ES384"), ("typ", "JWT"), ("kid", keys.thumbprint(fixture("author")["publicJwk"])),
                             ("jku", ISSUER), ("jwk", self.key["publicJwk"]), ("x5u", ISSUER), ("crit", ["x"]), ("extra", 1)]:
            header = dict(standard); header[field] = value
            with self.subTest(field=field), self.assertRaises(ProtocolError): self.verify(self.compact(header=header))

    def test_GivenMalformedCompactDuplicateUnknownOrNumericClaims_WhenVerified_ThenRejected(self):
        good = self.compact()
        for token in ["", "a.b", good+".extra", good+"=", good[:-1]+"!", good.rsplit(".", 1)[0]+"."+base64(bytes(64)), good+"x"]:
            with self.subTest(token=token[:2]), self.assertRaises(ProtocolError): self.verify(token)
        for field, value in [("keyEpoch", True), ("iat", True), ("jti", "00000000-0000-0000-0000-000000000000"), ("extra", 1)]:
            claims = self.claims(); claims[field] = value
            with self.subTest(field=field), self.assertRaises(ProtocolError): self.verify(self.compact(claims))
        raw = json.dumps(self.claims(), separators=(",", ":")).encode()
        for bad in [b'\xef\xbb\xbf'+raw, raw[:-1]+b',"iat":1700000000}', raw.replace(b'"keyEpoch":1', b'"keyEpoch":1e0')]:
            with self.assertRaises(ProtocolError): self.verify(self.compact(payload_raw=bad))
        with self.assertRaises(ProtocolError): self.verify(self.compact(header_raw=b'{"alg":"ES256","alg":"ES256"}'))

    def test_GivenGrantSortingDuplicatesOrScopeMismatch_WhenUsed_ThenRejected(self):
        authority = expected(); authority["grantRevisions"] = [dict(grantId=IDENTITY, revision=1), dict(grantId=OTHER, revision=2)]
        self.verify(self.compact(self.claims(authority=authority)), authority)
        for grants in [list(reversed(authority["grantRevisions"])), authority["grantRevisions"]*2,
                       [dict(grantId=IDENTITY, revision=True)], [dict(grantId=IDENTITY, revision=1, extra=2)]]:
            invalid = copy.deepcopy(authority); invalid["grantRevisions"] = grants
            with self.assertRaises(ProtocolError): self.claims(authority=invalid)
        invalid = expected(); invalid["scopeId"] = OTHER
        with self.assertRaises(ProtocolError): self.claims(authority=invalid)

    def test_GivenTrustedOldLeaseKey_WhenRotated_ThenOldVerificationRetained(self):
        new = fixture("author")["publicJwk"]; old = keys.thumbprint(self.key["publicJwk"]); new_fp = keys.thumbprint(new)
        signature = keys.sign(self.key["privateDer"], context(["cerberus-lease-key-transition-v1", ISSUER, old, new_fp, 2]))
        self.trust.transition(new, 2, signature)
        self.assertEqual(self.key["publicJwk"], self.trust.lookup(old))
        self.assertEqual(new, self.trust.lookup(new_fp))
        self.verify(self.compact())
        with self.assertRaises(ProtocolError): self.trust.transition(new, 2, signature)
        with self.assertRaises(ProtocolError): self.trust.lookup("unknown")

    def test_GivenWrongIssuerDomainNewKeyOrRevision_WhenRotated_ThenRejected(self):
        new = fixture("author")["publicJwk"]
        for domain, issuer, revision, role in [("cerberus-key-transition-v1", ISSUER, 2, "lease"),
                ("cerberus-lease-key-transition-v1", "wrong", 2, "lease"), ("cerberus-lease-key-transition-v1", ISSUER, 1, "lease"),
                ("cerberus-lease-key-transition-v1", ISSUER, 2, "author")]:
            signature = keys.sign(fixture(role)["privateDer"], context([domain, issuer, keys.thumbprint(self.key["publicJwk"]), keys.thumbprint(new), revision]))
            with self.subTest(domain=domain, issuer=issuer, revision=revision, role=role), self.assertRaises(ProtocolError): self.trust.transition(new, revision, signature)


if __name__ == "__main__": unittest.main()
