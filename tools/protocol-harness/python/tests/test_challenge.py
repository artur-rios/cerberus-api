import copy
import hashlib
import importlib
import unittest

from cerberus_protocol.encoding import base64, context, MAX_INTEGER, MAX_REQUEST
from cerberus_protocol.errors import ProtocolError
from tests.fixtures import fixture

OWNER = "00000000-0000-0000-0000-000000000001"
PROFILE = "00000000-0000-0000-0000-000000000002"
IDENTITY = "00000000-0000-0000-0000-000000000003"
CHALLENGE = "00000000-0000-0000-0000-000000000004"
BODY = b'{"idempotencyKey":"fixture","expectedRevision":1}'
REORDERED = b'{"expectedRevision":1,"idempotencyKey":"fixture"}'
NOW = 1700000000


class ChallengeTests(unittest.TestCase):
    def setUp(self):
        try: self.api = importlib.import_module("cerberus_protocol.challenge")
        except ModuleNotFoundError: self.fail("Challenge feature missing")
        self.binding = dict(operation="unlock-account", identityId=IDENTITY, accountId=OWNER, scopeKind="account",
                            scopeId=OWNER, keyEpoch=1, protectionRevision=1, generation=None)
        self.key = fixture("unlock-account")
        self.nonce = bytes(range(32))

    def issue(self, **changes):
        args = dict(expected_binding=self.binding, raw_body=BODY, now=NOW, challenge_id=CHALLENGE, nonce=self.nonce)
        args.update(changes)
        return self.api.issue_fixture(**args)

    def verify(self, challenge, signature=None, **changes):
        args = dict(challenge=challenge, expected_binding=self.binding, raw_body=BODY, registered_jwk=self.key["publicJwk"],
                    signature=signature if signature is not None else self.api.sign(challenge, self.key["privateDer"]), now=NOW)
        args.update(changes)
        return self.api.verify(**args)

    def test_GivenBoundBody_WhenByteOrderChanges_ThenProofRejected(self):
        challenge = self.issue()
        self.assertEqual(NOW + 60, challenge["expiresAt"])
        self.assertEqual(base64(hashlib.sha256(BODY).digest()), challenge["requestHash"])
        proof = self.api.sign(challenge, self.key["privateDer"])
        self.verify(challenge, proof, now=NOW + 59)
        with self.assertRaises(ProtocolError): self.verify(challenge, proof, now=NOW + 60)
        with self.assertRaises(ProtocolError): self.verify(challenge, proof, raw_body=REORDERED)
        reordered = self.issue(raw_body=REORDERED)
        self.assertNotEqual(challenge["requestHash"], reordered["requestHash"])

    def test_GivenChallenge_WhenEncoded_ThenExactLiteralArrayAndDeterministicSignature(self):
        challenge = self.issue()
        literal = context(["cerberus-proof-v1", CHALLENGE, base64(self.nonce), "unlock-account", IDENTITY, OWNER,
                           "account", OWNER, 1, 1, None, base64(hashlib.sha256(BODY).digest()), NOW, NOW + 60])
        self.assertEqual(literal, self.api.bytes(challenge))
        self.assertEqual(self.api.sign(challenge, self.key["privateDer"]), self.api.sign(challenge, self.key["privateDer"]))
        reordered = dict(reversed(list(challenge.items())))
        self.assertEqual(self.api.bytes(challenge), self.api.bytes(reordered))

    def test_GivenEveryChangedChallengeField_WhenVerified_ThenRejected(self):
        challenge = self.issue(); signature = self.api.sign(challenge, self.key["privateDer"])
        changes = dict(format="other", challengeId=PROFILE, nonce=base64(bytes(32)), operation="change-protection", identityId=PROFILE,
                       accountId=PROFILE, scopeKind="profile", scopeId=PROFILE, keyEpoch=2, protectionRevision=2, generation=1,
                       requestHash=base64(bytes(32)), issuedAt=NOW-1, expiresAt=NOW+59)
        for key, replacement in changes.items():
            changed = copy.deepcopy(challenge); changed[key] = replacement
            with self.subTest(key=key), self.assertRaises(ProtocolError): self.verify(changed, signature)

    def test_GivenChangedExpectedBinding_WhenVerified_ThenRejected(self):
        challenge = self.issue()
        for key, replacement in dict(operation="change-protection", identityId=PROFILE, accountId=PROFILE, scopeKind="profile",
                                     scopeId=PROFILE, keyEpoch=2, protectionRevision=2, generation=1).items():
            changed = copy.deepcopy(self.binding); changed[key] = replacement
            with self.subTest(key=key), self.assertRaises(ProtocolError): self.verify(challenge, expected_binding=changed)

    def test_GivenAllOperationScopes_WhenIssued_ThenCorrectPurposeKeyRequired(self):
        for operation in ["unlock-account", "unlock-profile", "change-protection", "recover", "refresh-recovery"]:
            binding = copy.deepcopy(self.binding); binding["operation"] = operation
            if operation == "unlock-profile": binding.update(scopeKind="profile", scopeId=PROFILE)
            if operation == "recover": binding["generation"] = 1
            key = fixture("recovery-1" if operation == "recover" else "unlock-profile" if operation == "unlock-profile" else "unlock-account")
            challenge = self.issue(expected_binding=binding)
            proof = self.api.sign(challenge, key["privateDer"])
            self.verify(challenge, proof, expected_binding=binding, registered_jwk=key["publicJwk"])
            with self.assertRaises(ProtocolError): self.verify(challenge, proof, expected_binding=binding, registered_jwk=fixture("lease")["publicJwk"])

    def test_GivenInvalidScopeOperationOrGeneration_WhenIssued_ThenRejected(self):
        changes = [dict(scopeId=PROFILE), dict(operation="unknown"), dict(generation=1), dict(generation=True), dict(keyEpoch=True),
                   dict(protectionRevision=0), dict(identityId="bad"), dict(extra=1),
                   dict(operation="recover", generation=None), dict(operation="recover", generation=0),
                   dict(operation="recover", generation=1, scopeKind="profile", scopeId=PROFILE),
                   dict(operation="unlock-profile"), dict(operation="unlock-account", scopeKind="profile", scopeId=PROFILE)]
        for change in changes:
            binding = copy.deepcopy(self.binding); binding.update(change)
            with self.subTest(change=change), self.assertRaises(ProtocolError): self.issue(expected_binding=binding)
        binding = copy.deepcopy(self.binding); del binding["generation"]
        with self.assertRaises(ProtocolError): self.issue(expected_binding=binding)

    def test_GivenFutureExpiredOrOverflowTime_WhenUsed_ThenRejected(self):
        challenge = self.issue()
        with self.assertRaises(ProtocolError): self.verify(challenge, now=NOW-1)
        for now in [-1, True, MAX_INTEGER-59, MAX_INTEGER+1]:
            with self.subTest(now=now), self.assertRaises(ProtocolError): self.issue(now=now)
        maximum = self.issue(now=MAX_INTEGER-60)
        self.assertEqual(MAX_INTEGER, maximum["expiresAt"])
        self.verify(maximum, now=MAX_INTEGER-1)
        with self.assertRaises(ProtocolError): self.verify(maximum, now=MAX_INTEGER)
        self.verify(self.issue(now=0), now=0)

    def test_GivenInvalidProofLengthScalarsOrOldKey_WhenVerified_ThenRejected(self):
        challenge = self.issue()
        for proof in [b'', bytes(63), bytes(64), bytes([255])*64, bytes(65), "not bytes"]:
            with self.subTest(proof=type(proof)), self.assertRaises(ProtocolError): self.verify(challenge, proof)
        proof = self.api.sign(challenge, fixture("recovery-1")["privateDer"])
        with self.assertRaises(ProtocolError): self.verify(challenge, proof)

    def test_GivenMalformedRawBody_WhenIssued_ThenRejected(self):
        for raw in [b'', b'[]', b'null', b'{}{}', b'{"x":1,"x":2}', b'{"x":{"y":1,"y":2}}', b'\xef\xbb\xbf{}',
                    b'{"x":"\xff"}', b'{"x":"\\ud800"}', b'{"x":1.0}', b'{"x":1e0}', b'{"x":NaN}', b'\x1f\x8bcompressed',
                    b'{"x":9007199254740992}']:
            with self.subTest(raw=raw[:30]), self.assertRaises(ProtocolError): self.issue(raw_body=raw)

    def test_GivenRequestAtAggregateBound_WhenIssued_ThenExactlyOneMiBAccepted(self):
        prefix, suffix = b'{"idempotencyKey":"fixture","value":"', b'"}'
        raw = prefix + b'a'*(MAX_REQUEST-len(prefix)-len(suffix)) + suffix
        challenge = self.issue(raw_body=raw)
        self.verify(challenge, raw_body=raw)
        with self.assertRaises(ProtocolError): self.issue(raw_body=raw+b' ')

    def test_GivenMalformedChallengeSchema_WhenSignedOrVerified_ThenRejected(self):
        original = self.issue()
        for key, replacement in [("keyEpoch", True), ("issuedAt", True), ("expiresAt", NOW+61), ("nonce", "AA"), ("requestHash", "AA"), ("extra", 1)]:
            value = copy.deepcopy(original); value[key] = replacement
            with self.subTest(key=key), self.assertRaises(ProtocolError): self.api.bytes(value)
        missing = copy.deepcopy(original); del missing["generation"]
        with self.assertRaises(ProtocolError): self.api.bytes(missing)

    def test_GivenNormalIssuance_WhenRepeated_ThenFreshIdsAndNonces(self):
        a = self.api.issue(self.binding, BODY, NOW); b = self.api.issue(self.binding, BODY, NOW)
        self.assertNotEqual(a["challengeId"], b["challengeId"]); self.assertNotEqual(a["nonce"], b["nonce"])
        self.assertEqual(self.binding, {key: a[key] for key in self.binding})
        self.assertNotIn("access", a); self.assertNotIn("session", a)


if __name__ == "__main__": unittest.main()
