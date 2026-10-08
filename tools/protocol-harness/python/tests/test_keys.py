import importlib.util
import json
from pathlib import Path
import unittest

from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ec

from cerberus_protocol.encoding import base64, unbase64
from cerberus_protocol.errors import ProtocolError
from tests.fixtures import fixture, known

ORDER = int("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", 16)


class KeysTest(unittest.TestCase):
    def test_GivenEmbeddedPrivateKeyMatrix_WhenImported_ThenOnlyValidControlsAccepted(self):
        from cerberus_protocol import keys
        path=Path(__file__).resolve().parents[2]/'fixtures/private-key-cases.json'
        for row in json.loads(path.read_text())['cases']:
            with self.subTest(case=row['id']):
                der=unbase64(row['der'],len(row['der'])*3//4)
                if row['expect']=='success': self.assertEqual(fixture('author')['publicJwk'],keys.public_jwk(der))
                else:
                    with self.assertRaisesRegex(ProtocolError,'^invalid_protocol$'): keys.public_jwk(der)
    def setUp(self):
        self.assertIsNotNone(importlib.util.find_spec("cerberus_protocol.keys"), "key feature missing")
        from cerberus_protocol import keys
        self.api = keys
        self.author = fixture("author")

    def test_GivenRfc6979PrivateKey_WhenSigned_ThenPublishedP1363SignatureMatches(self):
        v = known("ecdsa")
        self.assertEqual(bytes.fromhex(v["signature"]), self.api.sign(self.author["privateDer"], v["message"].encode("ascii")))

    def test_GivenRepeatedMessage_WhenSigned_ThenSameDeterministicSignature(self):
        self.assertEqual(self.api.sign(self.author["privateDer"], b"sample"), self.api.sign(self.author["privateDer"], b"sample"))

    def test_GivenPublishedSignature_WhenVerified_ThenAccepted(self):
        self.assertTrue(self.api.verify(self.author["publicJwk"], b"sample", bytes.fromhex(known("ecdsa")["signature"])))

    def test_GivenWrongMessageKeyOrSignature_WhenVerified_ThenRejected(self):
        signature = bytes.fromhex(known("ecdsa")["signature"])
        self.assertFalse(self.api.verify(self.author["publicJwk"], b"wrong", signature))
        self.assertFalse(self.api.verify(fixture("lease")["publicJwk"], b"sample", signature))
        self.assertFalse(self.api.verify(self.author["publicJwk"], b"sample", signature[:-1] + b"\x00"))

    def test_GivenInvalidSignatureScalarsOrLengths_WhenVerified_ThenRejected(self):
        for signature in (None, b"", b"x" * 63, b"x" * 65, bytes(64), ORDER.to_bytes(32, "big") + b"\x01" * 32,
                          b"\x01" * 32 + ORDER.to_bytes(32, "big")):
            with self.subTest(signature=signature):
                self.assertFalse(self.api.verify(self.author["publicJwk"], b"sample", signature))

    def test_GivenHighAndLowSSignatures_WhenVerified_ThenBothAccepted(self):
        original = bytes.fromhex(known("ecdsa")["signature"])
        alternate = original[:32] + (ORDER - int.from_bytes(original[32:], "big")).to_bytes(32, "big")
        self.assertTrue(self.api.verify(self.author["publicJwk"], b"sample", alternate))

    def test_GivenRfc6979PrivateDer_WhenPublicExported_ThenPublishedCoordinatesMatch(self):
        jwk = self.api.public_jwk(self.author["privateDer"])
        self.assertEqual(known("ecdsa")["x"].lower(), unbase64(jwk["x"], 32).hex())
        self.assertEqual(known("ecdsa")["y"].lower(), unbase64(jwk["y"], 32).hex())

    def test_GivenP256Jwk_WhenThumbprinted_ThenIndependentCanonicalHashMatches(self):
        self.assertEqual("DOvxvJiAdIqVWIkFt5hDtCunXLF0BV4-JGv4f-ALSm0", self.api.thumbprint(self.author["publicJwk"]))
        reordered = {key: self.author["publicJwk"][key] for key in ("y", "x", "kty", "crv")}
        self.assertEqual("DOvxvJiAdIqVWIkFt5hDtCunXLF0BV4-JGv4f-ALSm0", self.api.thumbprint(reordered))

    def test_GivenInvalidKeyMaterial_WhenImported_ThenRedactedRejection(self):
        valid = self.author["publicJwk"]
        mutations = ({"x": base64(bytes(32)), "y": base64(bytes(32))}, {"x": "", "y": ""}, {"crv": "P-384"},
                     {"kty": "RSA"}, {"x": valid["x"] + "="}, {"d": "secret-test-only"}, {"x": None})
        for changes in mutations:
            with self.subTest(changes=changes):
                with self.assertRaisesRegex(ProtocolError, "^invalid_protocol$"):
                    self.api.validate_public(dict(valid, **changes))
        for value in (None, {}, [], {k: v for k, v in valid.items() if k != "y"}):
            with self.subTest(value=value):
                with self.assertRaisesRegex(ProtocolError, "^invalid_protocol$"):
                    self.api.validate_public(value)

    def test_GivenMalformedTrailingWrongCurveOrNonPkcs8Der_WhenImported_ThenRedactedRejection(self):
        other = ec.generate_private_key(ec.SECP384R1()).private_bytes(serialization.Encoding.DER, serialization.PrivateFormat.PKCS8, serialization.NoEncryption())
        sec1 = serialization.load_der_private_key(self.author["privateDer"], None).private_bytes(serialization.Encoding.DER, serialization.PrivateFormat.TraditionalOpenSSL, serialization.NoEncryption())
        for private in (None, b"", b"malformed-secret", self.author["privateDer"] + b"\x00", self.author["privateDer"][:-1], other, sec1):
            with self.subTest(length=None if private is None else len(private)):
                with self.assertRaisesRegex(ProtocolError, "^invalid_protocol$"):
                    self.api.public_jwk(private)
                with self.assertRaisesRegex(ProtocolError, "^invalid_protocol$"):
                    self.api.sign(private, b"sample")

    def test_GivenGeneratedPrivateKeys_WhenUsed_ThenPkcs8RoundTrips(self):
        private = self.api.generate()
        imported = serialization.load_der_private_key(private, None)
        self.assertIsInstance(imported.curve, ec.SECP256R1)
        jwk = self.api.public_jwk(private)
        self.assertTrue(self.api.verify(jwk, b"sample", self.api.sign(private, b"sample")))
        self.assertNotEqual(jwk, self.api.public_jwk(self.api.generate()))

    def test_GivenMismatchedEmbeddedPublicKey_WhenPrivateImported_ThenRedactedRejection(self):
        der = self.author["privateDer"]
        public = b"\x04" + unbase64(self.author["publicJwk"]["x"], 32) + unbase64(self.author["publicJwk"]["y"], 32)
        self.assertEqual(public, der[-65:])
        other = fixture("lease")["publicJwk"]
        inconsistent = der[:-65] + b"\x04" + unbase64(other["x"], 32) + unbase64(other["y"], 32)
        with self.assertRaisesRegex(ProtocolError, "^invalid_protocol$"):
            self.api.public_jwk(inconsistent)

    def test_GivenInvalidPrivateScalar_WhenImported_ThenRedactedRejection(self):
        scalar = bytes.fromhex(known("ecdsa")["privateScalar"])
        self.assertEqual(1, self.author["privateDer"].count(scalar))
        for value in (bytes(32), ORDER.to_bytes(32, "big")):
            with self.subTest(value=value):
                der = self.author["privateDer"].replace(scalar, value)
                with self.assertRaisesRegex(ProtocolError, "^invalid_protocol$"):
                    self.api.public_jwk(der)

    def test_GivenPublicFixtureRoles_WhenInspected_ThenKeyPairsAreDistinct(self):
        fingerprints = [self.api.thumbprint(fixture(role)["publicJwk"]) for role in
                        ("author", "recipient", "unlock-account", "unlock-profile", "recovery-1", "recovery-2", "lease")]
        self.assertEqual(7, len(set(fingerprints)))

    def test_GivenNullMessage_WhenSigned_ThenRedactedRejection(self):
        with self.assertRaisesRegex(ProtocolError, "^invalid_protocol$"):
            self.api.sign(self.author["privateDer"], None)
