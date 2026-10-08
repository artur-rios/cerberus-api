import copy
import importlib
import unittest
from unittest import mock

from cerberus_protocol.encoding import base64, unbase64, MAX_REQUEST
from cerberus_protocol.errors import ProtocolError
from cerberus_protocol.keys import thumbprint
from cerberus_protocol.primitives import argon2
from cerberus_protocol.symmetric import Context, NonceGuard, _seal_bound
from cerberus_protocol.wire import encode
from tests.fixtures import bundle, allowed, fixture

OWNER = "00000000-0000-0000-0000-000000000001"
PROFILE = "00000000-0000-0000-0000-000000000002"


class ProtectionTests(unittest.TestCase):
    def setUp(self):
        try:
            self.p = importlib.import_module("cerberus_protocol.protection")
            self.r = importlib.import_module("cerberus_protocol.recovery_bundle")
        except ModuleNotFoundError:
            self.fail("Protection/RecoveryBundle feature missing")
        self.slot = Context(OWNER, "account-protection", OWNER, 1, [])
        self.recovery_slot = Context(OWNER, "recovery", OWNER, 1, [])
        self.salt, self.nonce, self.password_salt = bytes(range(32)), bytes(range(12)), bytes(range(16))
        self.secret = bytes(range(32, 64))
        self.unlock = fixture("unlock-account")["publicJwk"]
        self.proof = fixture("recovery-1")

    def wrap(self, password=" public É 🔐 ", value=None, slot=None):
        return self.p.wrap_fixture(password, value or bundle("account"), slot or self.slot,
                                   NonceGuard(0), self.salt, self.nonce, self.password_salt)

    def unwrap(self, value, password=" public É 🔐 ", slot=None):
        return self.p.unwrap(password, value, slot or self.slot, allowed("account"), self.unlock)

    def recovery(self, generation=1, proof=None):
        return self.r.wrap_fixture(self.secret, bundle("account"), (proof or self.proof)["privateDer"],
                                   self.recovery_slot, generation, NonceGuard(0), self.salt, self.nonce)

    def recover(self, value, **changes):
        args = dict(secret=self.secret, wrapper=value, slot=self.recovery_slot, generation=1,
                    recovery_jwk=self.proof["publicJwk"], allowed_roots=allowed("account"), unlock_jwk=self.unlock)
        args.update(changes)
        return self.r.unwrap(**args)

    def test_GivenAccountAndProfileBundles_WhenValidatedAndWrapped_ThenScopedKeysRoundTrip(self):
        for scope, kind, identity in [("account", "account-protection", OWNER), ("profile", "profile-protection", PROFILE)]:
            value = bundle(scope)
            unlock = fixture("unlock-" + scope)["publicJwk"]
            slot = Context(OWNER, kind, identity, 1, [])
            self.assertEqual(value, self.p.validate(value, scope, identity, allowed(scope), unlock))
            wrapped = self.wrap(value=value, slot=slot)
            self.assertEqual(value, self.p.unwrap(" public É 🔐 ", wrapped, slot, allowed(scope), unlock))

    def test_GivenProfileSlot_WhenAccountRootIncluded_ThenRejected(self):
        value = bundle("profile")
        value["roots"].insert(0, bundle("account")["roots"][0])
        with self.assertRaises(ProtocolError):
            self.p.validate(value, "profile", PROFILE, allowed("account"), fixture("unlock-profile")["publicJwk"])

    def test_GivenInvalidRootsOrScope_WhenValidated_ThenRejected(self):
        mutations = []
        for roots in [[], list(reversed(bundle("account")["roots"])), [bundle("account")["roots"][0]] * 2]:
            value = bundle("account"); value["roots"] = roots; mutations.append(value)
        for key, replacement in [("format", "other"), ("scopeKind", "profile"), ("scopeId", PROFILE), ("unlockPrivateKey", "AA")]:
            value = bundle("account"); value[key] = replacement; mutations.append(value)
        value = bundle("account"); del value["roots"]; mutations.append(value)
        value = bundle("account"); value["extra"] = 1; mutations.append(value)
        for value in mutations:
            with self.subTest(value=value), self.assertRaises(ProtocolError):
                self.p.validate(value, "account", OWNER, allowed("account"), self.unlock)

    def test_GivenChangedRootFields_WhenValidated_ThenRejected(self):
        for key, replacement in [("resourceKind", "unknown"), ("resourceId", "bad"), ("keyEpoch", True), ("keyEpoch", 0), ("key", "AA"), ("extra", 1)]:
            value = bundle("account"); value["roots"][0][key] = replacement
            with self.subTest(key=key), self.assertRaises(ProtocolError):
                self.p.validate(value, "account", OWNER, allowed("account"), self.unlock)

    def test_GivenUnauthorizedRootOrUnlockKey_WhenValidated_ThenRejected(self):
        with self.assertRaises(ProtocolError):
            self.p.validate(bundle("account"), "account", OWNER, allowed("profile"), self.unlock)
        with self.assertRaises(ProtocolError):
            self.p.validate(bundle("account"), "account", OWNER, allowed("account"), fixture("unlock-profile")["publicJwk"])
        value = bundle("profile"); value["roots"][0]["resourceId"] = OWNER
        with self.assertRaises(ProtocolError):
            self.p.validate(value, "profile", PROFILE, allowed("account"), fixture("unlock-profile")["publicJwk"])

    def test_GivenChangedKdf_WhenUnwrapped_ThenNoDerivation(self):
        wrapped = self.wrap()
        for key, replacement in [("algorithm", "argon2i"), ("memoryKiB", 2**40), ("memoryKiB", True), ("iterations", 4), ("parallelism", 1), ("salt", "AA"), ("extra", 1)]:
            value = copy.deepcopy(wrapped); value["kdf"][key] = replacement
            with self.subTest(key=key), mock.patch("cerberus_protocol.protection.argon2") as derive:
                with self.assertRaises(ProtocolError): self.unwrap(value)
                derive.assert_not_called()

    def test_GivenMalformedEnvelope_WhenUnwrapped_ThenNoDerivation(self):
        wrapped = self.wrap()
        mutations = []
        for key, replacement in [("keyEpoch", True), ("keyEpoch", 2), ("format", "unknown"), ("keySalt", "AA"), ("nonce", "AA"), ("ciphertext", "A"), ("tag", "AA"), ("ciphertext", "A" * MAX_REQUEST), ("extra", 1)]:
            value = copy.deepcopy(wrapped); value[key] = replacement; mutations.append(value)
        value = copy.deepcopy(wrapped); del value["kdf"]["salt"]; mutations.append(value)
        value = copy.deepcopy(wrapped); del value["tag"]; mutations.append(value)
        for value in mutations:
            with mock.patch("cerberus_protocol.protection.argon2") as derive:
                with self.assertRaises(ProtocolError): self.unwrap(value)
                derive.assert_not_called()

    def test_GivenExactUnicodePassword_WhenChangedWithoutNormalization_ThenReject(self):
        for password, changed in [("é", "e\u0301"), (" secret ", "secret"), ("Secret", "secret"), ("🔐", "🔑")]:
            wrapped = self.wrap(password)
            self.assertEqual(bundle("account"), self.unwrap(wrapped, password))
            with self.assertRaises(ProtocolError): self.unwrap(wrapped, changed)
        self.assertNotEqual(argon2("é".encode(), self.password_salt), argon2("e\u0301".encode(), self.password_salt))
        with mock.patch("cerberus_protocol.protection.argon2") as derive:
            with self.assertRaises(ProtocolError): self.wrap("\ud800")
            derive.assert_not_called()

    def test_GivenChangedPasswordSaltOrCiphertext_WhenUnwrapped_ThenReject(self):
        wrapped = self.wrap()
        for key in ["keySalt", "nonce", "tag", "ciphertext", "passwordSalt"]:
            value = copy.deepcopy(wrapped)
            if key == "passwordSalt": value["kdf"]["salt"] = base64(bytes([1]) * 16)
            else: value[key] = ("A" if value[key][0] != "A" else "B") + value[key][1:]
            with self.subTest(key=key), self.assertRaises(ProtocolError): self.unwrap(value)

    def test_GivenAuthenticatedBundle_WhenMembershipOrVerifierWrong_ThenNoKeysReturned(self):
        wrapped = self.wrap()
        with self.assertRaises(ProtocolError): self.p.unwrap(" public É 🔐 ", wrapped, self.slot, allowed("profile"), self.unlock)
        with self.assertRaises(ProtocolError): self.p.unwrap(" public É 🔐 ", wrapped, self.slot, allowed("account"), fixture("author")["publicJwk"])

    def test_GivenRewrap_WhenSlotEpochIncreased_ThenContentRootsUnchanged(self):
        original = self.unwrap(self.wrap())
        slot = Context(OWNER, "account-protection", OWNER, 2, [])
        wrapped = self.wrap("new password", original, slot)
        self.assertEqual(2, wrapped["keyEpoch"])
        self.assertEqual(original, self.unwrap(wrapped, "new password", slot))
        with self.assertRaises(ProtocolError): self.unwrap(wrapped, "new password")

    def test_GivenNormalWrapping_WhenRepeated_ThenAllSaltsAndNoncesFresh(self):
        one = self.p.wrap("pwd", bundle("account"), self.slot, NonceGuard(0))
        two = self.p.wrap("pwd", bundle("account"), self.slot, NonceGuard(0))
        for key in ["keySalt", "nonce"]: self.assertNotEqual(one[key], two[key])
        self.assertNotEqual(one["kdf"]["salt"], two["kdf"]["salt"])

    def test_GivenRecoverySecret_WhenWrapped_ThenAccountAndProofRoundTripWithoutArgon2(self):
        with mock.patch("cerberus_protocol.protection.argon2") as derive:
            value = self.recover(self.recovery())
            self.assertEqual(bundle("account"), value["protectionBundle"])
            self.assertEqual(1, value["generation"])
            self.assertEqual(base64(self.proof["privateDer"]), value["proofPrivateKey"])
            derive.assert_not_called()

    def test_GivenChangedRecoveryBindings_WhenUnwrapped_ThenReject(self):
        wrapped = self.recovery()
        for changes in [dict(secret=bytes(32)), dict(secret=unbase64(self.proof["publicJwk"]["x"], 32)), dict(generation=2),
                        dict(recovery_jwk=fixture("recovery-2")["publicJwk"]), dict(slot=Context(PROFILE, "recovery", OWNER, 1, [])),
                        dict(allowed_roots=allowed("profile")), dict(unlock_jwk=fixture("unlock-profile")["publicJwk"])]:
            with self.subTest(changes=list(changes)), self.assertRaises(ProtocolError): self.recover(wrapped, **changes)
        for key, replacement in [("generation", True), ("generation", 2), ("proofKeyFingerprint", thumbprint(fixture("recovery-2")["publicJwk"])), ("extra", 1)]:
            value = copy.deepcopy(wrapped); value[key] = replacement
            with self.subTest(key=key), self.assertRaises(ProtocolError): self.recover(value)

    def test_GivenNewRecoveryGeneration_WhenWrapped_ThenIndependentProofKeyRequired(self):
        proof = fixture("recovery-2")
        wrapped = self.recovery(2, proof)
        self.assertNotEqual(thumbprint(self.proof["publicJwk"]), wrapped["proofKeyFingerprint"])
        self.assertEqual(2, self.recover(wrapped, generation=2, recovery_jwk=proof["publicJwk"])["generation"])
        with self.assertRaises(ProtocolError): self.recover(wrapped)

    def test_GivenProfileBundleOrBadSlot_WhenRecoveryWrapped_ThenRejected(self):
        with self.assertRaises(ProtocolError):
            self.r.wrap(self.secret, bundle("profile"), self.proof["privateDer"], self.recovery_slot, 1, NonceGuard(0))
        for slot in [self.slot, Context(OWNER, "recovery", PROFILE, 1, []), Context(OWNER, "recovery", OWNER, 1, [1])]:
            with self.assertRaises(ProtocolError):
                self.r.wrap(self.secret, bundle("account"), self.proof["privateDer"], slot, 1, NonceGuard(0))

    def test_GivenAuthenticatedInvalidPasswordPlaintext_WhenUnwrapped_ThenReject(self):
        kdf = dict(algorithm="argon2id-v1.3", memoryKiB=65536, iterations=3, parallelism=4, salt=base64(self.password_salt))
        expected = Context(OWNER, "account-protection", OWNER, 1, ["argon2id-v1.3", 65536, 3, 4, kdf["salt"]])
        root = argon2(" public É 🔐 ".encode(), self.password_salt)
        invalid = []
        for key, replacement in [("unlockPrivateKey", base64(fixture("unlock-profile")["privateDer"])), ("roots", []), ("scopeId", PROFILE), ("extra", 1)]:
            value = bundle("account"); value[key] = replacement; invalid.append(encode(value))
        invalid.extend([b'{"format":"one","format":"two"}', b'\xef\xbb\xbf{}', b'{"format":1.0}'])
        for raw in invalid:
            value = _seal_bound(root, expected, self.p.FORMAT, raw, NonceGuard(0), self.salt, self.nonce)
            value["kdf"] = kdf
            with self.assertRaises(ProtocolError): self.unwrap(value)

    def test_GivenAuthenticatedInvalidRecoveryPlaintext_WhenUnwrapped_ThenReject(self):
        fingerprint = thumbprint(self.proof["publicJwk"])
        expected = Context(OWNER, "recovery", OWNER, 1, [1, fingerprint])
        original = dict(format="cerberus-recovery-bundle-v1", generation=1,
                        proofPrivateKey=base64(self.proof["privateDer"]), protectionBundle=bundle("account"))
        for key, replacement in [("generation", 2), ("proofPrivateKey", base64(fixture("recovery-2")["privateDer"])),
                                 ("proofPrivateKey", "AA"), ("protectionBundle", bundle("profile")), ("extra", 1)]:
            inner = copy.deepcopy(original); inner[key] = replacement
            value = _seal_bound(self.secret, expected, self.r.FORMAT, encode(inner), NonceGuard(0), self.salt, self.nonce)
            value.update(generation=1, proofKeyFingerprint=fingerprint)
            with self.subTest(key=key), self.assertRaises(ProtocolError): self.recover(value)


if __name__ == "__main__": unittest.main()
