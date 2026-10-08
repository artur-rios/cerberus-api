import importlib.util
import unittest

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.bindings._rust import openssl as native
from cryptography.hazmat.primitives import hpke
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.kdf.argon2 import Argon2id

from cerberus_protocol.encoding import base64
from cerberus_protocol.errors import ProtocolError
from tests.fixtures import known


class PrimitivesTest(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(importlib.util.find_spec("cerberus_protocol.primitives"), "primitive feature missing")
        from cerberus_protocol import primitives
        self.api = primitives

    def test_GivenRfc5869Case1_WhenDerived_ThenKnownBytesMatch(self):
        v = known("hkdf")
        self.assertEqual(v["okm"], self.api.hkdf(bytes.fromhex(v["ikm"]), bytes.fromhex(v["salt"]), bytes.fromhex(v["info"]), v["length"]).hex())

    def test_GivenInvalidHkdfInputs_WhenDerived_ThenRedactedRejection(self):
        for root, salt, info, length in ((None, b"", b"", 32), (b"", b"", b"", 32),
                                        (b"k", None, b"", 32), (b"k", b"", None, 32),
                                        (b"k", b"", b"", 0), (b"k", b"", b"", 8161),
                                        (b"k", b"", b"", True), (b"k", b"", b"", 1.0)):
            with self.subTest(length=length):
                with self.assertRaisesRegex(ProtocolError, "^invalid_protocol$"):
                    self.api.hkdf(root, salt, info, length)

    def test_GivenNistAes256GcmCases_WhenSealed_ThenPublishedCiphertextAndTagMatch(self):
        for v in known("gcm")["cases"]:
            with self.subTest(section=v["section"]):
                self.assertEqual(v["CT"] + v["Tag"], self.api.gcm_seal(bytes.fromhex(v["Key"]), bytes.fromhex(v["IV"]), bytes.fromhex(v["AAD"]), bytes.fromhex(v["PT"])).hex())

    def test_GivenNistAes256GcmCases_WhenOpened_ThenPublishedPlaintextMatches(self):
        for v in known("gcm")["cases"]:
            with self.subTest(section=v["section"]):
                self.assertEqual(v["PT"], self.api.gcm_open(bytes.fromhex(v["Key"]), bytes.fromhex(v["IV"]), bytes.fromhex(v["AAD"]), bytes.fromhex(v["CT"] + v["Tag"])).hex())

    def test_GivenChangedTagOrAad_WhenOpened_ThenNoPlaintextIsReturned(self):
        v = known("gcm")["cases"][2]
        ciphertext = bytes.fromhex(v["CT"] + v["Tag"])
        for aad, ct in ((bytes.fromhex(v["AAD"]), ciphertext[:-1] + bytes([ciphertext[-1] ^ 1])), (b"wrong", ciphertext)):
            with self.subTest(aad=aad):
                with self.assertRaisesRegex(ProtocolError, "^invalid_protocol$"):
                    self.api.gcm_open(bytes.fromhex(v["Key"]), bytes.fromhex(v["IV"]), aad, ct)

    def test_GivenInvalidGcmInputs_WhenUsed_ThenRedactedRejection(self):
        for key, nonce, aad, value in ((b"k" * 16, b"n" * 12, b"", b"data"), (b"k" * 32, b"n" * 11, b"", b"data"),
                                      (None, b"n" * 12, b"", b"data"), (b"k" * 32, b"n" * 12, None, b"data"),
                                      (b"k" * 32, b"n" * 12, b"", None)):
            for action in (self.api.gcm_seal, self.api.gcm_open):
                with self.subTest(action=action.__name__):
                    with self.assertRaisesRegex(ProtocolError, "^invalid_protocol$"):
                        action(key, nonce, aad, value)
        with self.assertRaisesRegex(ProtocolError, "^invalid_protocol$"):
            self.api.gcm_open(b"k" * 32, b"n" * 12, b"", b"short")

    def test_GivenRfc9106Argon2id_WhenNativeDerived_ThenPublishedTagMatches(self):
        v = known("argon2")
        # Only a primitive qualification utility may use the RFC's secret/AD and 32-KiB memory.
        result = Argon2id(salt=bytes.fromhex(v["salt"]), length=v["length"], iterations=v["iterations"],
                          lanes=v["lanes"], memory_cost=v["memory"], ad=bytes.fromhex(v["ad"]), secret=bytes.fromhex(v["secret"])).derive(bytes.fromhex(v["password"]))
        self.assertEqual(v["tag"], result.hex())

    def test_GivenProtocolPassword_WhenDerived_ThenFixedProfileIsUsed(self):
        expected = Argon2id(salt=b"s" * 16, length=32, iterations=3, lanes=4, memory_cost=65536).derive(b"password")
        self.assertEqual(expected, self.api.argon2(b"password", b"s" * 16))

    def test_GivenInvalidArgon2Inputs_WhenDerived_ThenRedactedRejection(self):
        for password, salt in ((None, b"s" * 16), ("password", b"s" * 16), (b"p", b"s" * 15), (b"p", None)):
            with self.subTest(salt=salt):
                with self.assertRaisesRegex(ProtocolError, "^invalid_protocol$"):
                    self.api.argon2(password, salt)

    def test_GivenRfc7638CanonicalRsaBytes_WhenHashed_ThenPublishedThumbprintMatches(self):
        v = known("thumbprint")
        self.assertEqual(v["value"], base64(self.api.sha256(v["canonical"].encode("ascii"))))

    def test_GivenSha256EmptyAndAbc_WhenHashed_ThenKnownDigestsMatch(self):
        self.assertEqual("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", self.api.sha256(b"").hex())
        self.assertEqual("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", self.api.sha256(b"abc").hex())
        with self.assertRaisesRegex(ProtocolError, "^invalid_protocol$"):
            self.api.sha256(None)

    def test_GivenRfc9180BaseSuite_WhenNativeOpened_ThenPublishedPlaintextMatches(self):
        v = known("hpke")
        suite = hpke.Suite(hpke.KEM.P256, hpke.KDF.HKDF_SHA256, hpke.AEAD.AES_256_GCM)
        private = ec.derive_private_key(int(v["skRm"], 16), ec.SECP256R1())
        # Pinned upstream uses this test-only native API for vectors with nonempty AAD.
        case = v["encryptions"][0]
        sealed = bytes.fromhex(v["enc"] + case["ct"])
        self.assertEqual(bytes.fromhex(case["pt"]), native.hpke._decrypt_with_aad(suite, sealed, private,
                         info=bytes.fromhex(v["info"]), aad=bytes.fromhex(case["aad"])))
        with self.assertRaises(InvalidTag):
            native.hpke._decrypt_with_aad(suite, sealed[:-1] + bytes([sealed[-1] ^ 1]), private,
                                         info=bytes.fromhex(v["info"]), aad=bytes.fromhex(case["aad"]))

    def test_GivenPublicSingleShotHpke_WhenUsed_ThenEmptyAadProfileWorks(self):
        suite = hpke.Suite(hpke.KEM.P256, hpke.KDF.HKDF_SHA256, hpke.AEAD.AES_256_GCM)
        private = ec.generate_private_key(ec.SECP256R1())
        sealed = suite.encrypt(b"profile smoke", private.public_key(), info=b"context")
        self.assertEqual(b"profile smoke", suite.decrypt(sealed, private, info=b"context"))
        with self.assertRaises(InvalidTag):
            suite.decrypt(sealed, private, info=b"wrong")
