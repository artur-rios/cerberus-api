import copy
import importlib
import unittest
from unittest import mock

from cerberus_protocol.encoding import context, base64, unbase64
from cerberus_protocol.errors import ProtocolError
from cerberus_protocol.keys import thumbprint, sign, verify
from cerberus_protocol.symmetric import Context
from tests.fixtures import fixture

OWNER = "00000000-0000-0000-0000-000000000001"
RESOURCE = "00000000-0000-0000-0000-000000000002"
GRANT = "00000000-0000-0000-0000-000000000003"
IDENTITY = "00000000-0000-0000-0000-000000000004"
OTHER = "00000000-0000-0000-0000-000000000005"


class RecipientTests(unittest.TestCase):
    def setUp(self):
        try:
            self.api = importlib.import_module("cerberus_protocol.recipient")
            self.Trust = importlib.import_module("cerberus_protocol.client_trust").ClientTrust
        except ModuleNotFoundError: self.fail("Recipient/ClientTrust feature missing")
        self.author = fixture("author")
        self.recipient = fixture("recipient")
        self.resource = Context(OWNER, "record", RESOURCE, 1, [])
        self.trust = self.Trust(OWNER, self.recipient["publicJwk"], self.author["publicJwk"], 1)
        self.root = bytes(range(32))

    def wrap(self, **changes):
        args = dict(resource_root=self.root, resource=self.resource, grant_id=GRANT, grant_revision=1,
                    recipient_identity_id=IDENTITY, recipient_jwk=self.recipient["publicJwk"], author_private_der=self.author["privateDer"])
        args.update(changes)
        return self.api.wrap(**args)

    def open(self, envelope, **changes):
        args = dict(envelope=envelope, resource=self.resource, grant_id=GRANT, grant_revision=1,
                    recipient_identity_id=IDENTITY, recipient_private_der=self.recipient["privateDer"], trust=self.trust)
        args.update(changes)
        return self.api.open_recipient(**args)

    def signed(self, envelope):
        info = context(["cerberus-recipient-wrap-v1", OWNER, "record", RESOURCE, 1, GRANT, 1, IDENTITY,
                        thumbprint(self.recipient["publicJwk"]), thumbprint(self.author["publicJwk"])])
        return context(["cerberus-recipient-signature-v1", base64(info), envelope["enc"], envelope["ciphertext"]])

    def transition_signature(self, role, new, revision=2, account=OWNER, signer=None, old=None):
        previous = old or (self.recipient if role == "recipient-kem" else self.author)
        raw = context(["cerberus-key-transition-v1", account, role, thumbprint(previous["publicJwk"]),
                       thumbprint(new["publicJwk"]), revision])
        return sign((signer or self.author)["privateDer"], raw)

    def test_GivenPinnedAuthorAndRecipient_WhenWrapped_ThenOnlyRecipientOpens(self):
        envelope = self.wrap()
        self.assertEqual(65, len(unbase64(envelope["enc"], 65)))
        self.assertEqual(48, len(unbase64(envelope["ciphertext"], 48)))
        self.assertEqual(self.root, self.open(envelope))
        self.assertTrue(verify(self.author["publicJwk"], self.signed(envelope), unbase64(envelope["signature"], 64)))
        with self.assertRaises(ProtocolError): self.open(envelope, recipient_private_der=self.author["privateDer"])

    def test_GivenChangedTrustedBinding_WhenOpened_ThenRejectBeforeDecapsulation(self):
        envelope = self.wrap()
        changes = [dict(resource=Context(OTHER, "record", RESOURCE, 1, [])), dict(resource=Context(OWNER, "folder", RESOURCE, 1, [])),
                   dict(resource=Context(OWNER, "record", OTHER, 1, [])), dict(resource=Context(OWNER, "record", RESOURCE, 2, [])),
                   dict(resource=Context(OWNER, "record", RESOURCE, 1, ["extra"])), dict(grant_id=OTHER), dict(grant_revision=2), dict(recipient_identity_id=OTHER)]
        for change in changes:
            with self.subTest(change=list(change)), mock.patch("cerberus_protocol.recipient._decapsulate") as decap:
                with self.assertRaises(ProtocolError): self.open(envelope, **change)
                decap.assert_not_called()

    def test_GivenChangedEnvelopeMetadataOrSignature_WhenOpened_ThenRejectBeforeDecapsulation(self):
        envelope = self.wrap()
        for key, value in [("format", "other"), ("keyEpoch", 2), ("grantId", OTHER), ("grantRevision", True), ("recipientIdentityId", OTHER),
                           ("recipientKeyFingerprint", thumbprint(self.author["publicJwk"])), ("authorKeyFingerprint", thumbprint(self.recipient["publicJwk"])),
                           ("signature", base64(bytes(64))), ("signature", "AA"), ("extra", 1)]:
            changed = copy.deepcopy(envelope); changed[key] = value
            with self.subTest(key=key), mock.patch("cerberus_protocol.recipient._decapsulate") as decap:
                with self.assertRaises(ProtocolError): self.open(changed)
                decap.assert_not_called()

    def test_GivenUnpinnedAuthorOrSuppliedJwk_WhenOpened_ThenReject(self):
        forged = self.wrap(author_private_der=fixture("lease")["privateDer"])
        with mock.patch("cerberus_protocol.recipient._decapsulate") as decap:
            with self.assertRaises(ProtocolError): self.open(forged)
            decap.assert_not_called()
        forged["authorJwk"] = fixture("lease")["publicJwk"]
        with self.assertRaises(ProtocolError): self.open(forged)

    def test_GivenMalformedOrCorruptedHpkeBytes_WhenOpened_ThenReject(self):
        original = self.wrap()
        for key, data in [("enc", bytes(65)), ("enc", b'\x04' + bytes(64)), ("enc", b'\x02' + unbase64(original["enc"], 65)[1:]),
                          ("enc", bytes(64)), ("ciphertext", bytes(48)), ("ciphertext", bytes(47))]:
            value = copy.deepcopy(original); value[key] = base64(data)
            # Even an author-signed invalid point/ciphertext must fail natively.
            value["signature"] = base64(sign(self.author["privateDer"], self.signed(value)))
            with self.subTest(key=key, length=len(data)), self.assertRaises(ProtocolError): self.open(value)
        value = copy.deepcopy(original); del value["enc"]
        with self.assertRaises(ProtocolError): self.open(value)

    def test_GivenInvalidInputsOrRoleReuse_WhenWrapped_ThenReject(self):
        for change in [dict(resource_root=b''), dict(resource_root=bytes(31)), dict(grant_revision=True), dict(grant_revision=0),
                       dict(grant_id="bad"), dict(recipient_identity_id="bad"), dict(resource=Context(OWNER, "recovery", RESOURCE, 1, [])),
                       dict(recipient_jwk=self.author["publicJwk"]), dict(author_private_der=b'bad')]:
            with self.subTest(change=list(change)), self.assertRaises(ProtocolError): self.wrap(**change)
        off_curve = dict(crv="P-256", kty="EC", x=base64(bytes(32)), y=base64(bytes(32)))
        with self.assertRaises(ProtocolError): self.wrap(recipient_jwk=off_curve)

    def test_GivenNormalHpkeWrapping_WhenRepeated_ThenFreshEncapsulation(self):
        one, two = self.wrap(), self.wrap()
        self.assertNotEqual(one["enc"], two["enc"])
        self.assertNotEqual(one["ciphertext"], two["ciphertext"])
        self.assertEqual(self.root, self.open(one)); self.assertEqual(self.root, self.open(two))

    def test_GivenOldAuthorTransition_WhenVerified_ThenNewRecipientPinOpens(self):
        new = fixture("recovery-1")
        self.trust.transition(OWNER, "recipient-kem", new["publicJwk"], 2, self.transition_signature("recipient-kem", new))
        envelope = self.wrap(recipient_jwk=new["publicJwk"])
        self.assertEqual(self.root, self.open(envelope, recipient_private_der=new["privateDer"]))
        with self.assertRaises(ProtocolError): self.open(self.wrap())

    def test_GivenAuthorTransition_WhenVerified_ThenOnlyNewAuthorCanSignNextTransition(self):
        new_author, new_recipient = fixture("lease"), fixture("recovery-2")
        self.trust.transition(OWNER, "envelope-author", new_author["publicJwk"], 2, self.transition_signature("envelope-author", new_author))
        self.assertEqual(self.root, self.open(self.wrap(author_private_der=new_author["privateDer"])))
        with self.assertRaises(ProtocolError): self.open(self.wrap())
        with self.assertRaises(ProtocolError):
            self.trust.transition(OWNER, "recipient-kem", new_recipient["publicJwk"], 3, self.transition_signature("recipient-kem", new_recipient, 3))
        self.trust.transition(OWNER, "recipient-kem", new_recipient["publicJwk"], 3,
                              self.transition_signature("recipient-kem", new_recipient, 3, signer=new_author))

    def test_GivenSelfSignedWrongAccountOrRoleTransition_WhenSubmitted_ThenPinsUnchanged(self):
        new = fixture("recovery-1")
        valid = self.transition_signature("recipient-kem", new)
        attempts = [(OWNER, "recipient-kem", new["publicJwk"], 2, self.transition_signature("recipient-kem", new, signer=new)),
                    (OTHER, "recipient-kem", new["publicJwk"], 2, self.transition_signature("recipient-kem", new, account=OTHER)),
                    (OWNER, "lease", new["publicJwk"], 2, valid), (OWNER, "envelope-author", new["publicJwk"], 2, valid),
                    (OWNER, "recipient-kem", fixture("recovery-2")["publicJwk"], 2, valid),
                    (OWNER, "recipient-kem", new["publicJwk"], 1, self.transition_signature("recipient-kem", new, 1))]
        saved = self.wrap()
        for args in attempts:
            with self.assertRaises(ProtocolError): self.trust.transition(*args)
            self.assertEqual(self.root, self.open(saved))

    def test_GivenSameKeyForTwoRoles_WhenTrustOrTransitionCreated_ThenReject(self):
        with self.assertRaises(ProtocolError): self.Trust(OWNER, self.author["publicJwk"], self.author["publicJwk"], 1)
        with self.assertRaises(ProtocolError):
            self.trust.transition(OWNER, "recipient-kem", self.author["publicJwk"], 2, self.transition_signature("recipient-kem", self.author))

    def test_GivenMutableInputPins_WhenCallerChangesThem_ThenTrustedKeysUnchanged(self):
        recipient, author = copy.deepcopy(self.recipient["publicJwk"]), copy.deepcopy(self.author["publicJwk"])
        trust = self.Trust(OWNER, recipient, author, 1)
        recipient["x"] = author["x"] = base64(bytes(32))
        self.assertEqual(self.root, self.open(self.wrap(), trust=trust))

    def test_GivenNewGrantRevisionAndRevokedRecipient_WhenRegranted_ThenOnlyNewWrappersDistributed(self):
        old = self.wrap()
        new = self.wrap(grant_revision=2)
        with self.assertRaises(ProtocolError): self.open(old, grant_revision=2)
        self.assertEqual(self.root, self.open(new, grant_revision=2))
        self.assertEqual(self.root, self.open(old))  # Earlier copied material cannot be erased.
        permitted = {IDENTITY: self.recipient["publicJwk"], OTHER: fixture("recovery-1")["publicJwk"]}
        wrappers = {identity: self.wrap(recipient_identity_id=identity, recipient_jwk=key, grant_revision=2)
                    for identity, key in permitted.items() if identity != OTHER}
        self.assertNotIn(OTHER, wrappers)


if __name__ == "__main__": unittest.main()
