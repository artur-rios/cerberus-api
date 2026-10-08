import copy
import importlib.util
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor

from cerberus_protocol.encoding import base64, MAX_REQUEST
from cerberus_protocol.errors import ProtocolError

OWNER = "00000000-0000-0000-0000-000000000001"
RESOURCE = "00000000-0000-0000-0000-000000000002"
OTHER = "00000000-0000-0000-0000-000000000003"
FORMAT = "cerberus-content-v1"


class SymmetricTest(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(importlib.util.find_spec("cerberus_protocol.symmetric"), "symmetric envelope feature missing")
        from cerberus_protocol import symmetric
        self.api = symmetric
        self.root, self.salt, self.nonce = bytes(range(32)), bytes(range(32, 64)), bytes(range(12))
        self.ctx = self.api.Context(OWNER, "record", RESOURCE, 1, [])
        self.guard = self.api.NonceGuard(0)

    def seal(self, **kwargs):
        values = dict(root=self.root, expected=self.ctx, format=FORMAT, plaintext=b"public fixture",
                      guard=self.guard, salt=self.salt, nonce=self.nonce)
        values.update(kwargs)
        return self.api.seal_fixture(**values)

    def reject(self, action):
        with self.assertRaisesRegex(ProtocolError, "^invalid_protocol$"):
            action()

    def test_GivenUsedSaltContext_WhenSealedAgain_ThenRejected(self):
        env = self.seal()
        self.assertEqual(b"public fixture", self.api.open_envelope(self.root, self.ctx, FORMAT, env))
        self.reject(lambda: self.seal(plaintext=b"edit", nonce=b"\x02" * 12))

    def test_GivenExpectedContext_WhenFixtureSealed_ThenIndependentWorkedEnvelopeMatches(self):
        # Worked bytes from direct native HKDF/AESGCM before this module existed;
        # not labeled as an RFC/NIST published known answer.
        self.assertEqual({"format": FORMAT, "keyEpoch": 1,
                          "keySalt": "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8",
                          "nonce": "AAECAwQFBgcICQoL", "ciphertext": "xvJT8pCjlfo5xRIRpss",
                          "tag": "S66V63rqlxPAHtHfeemDGg"}, self.seal())

    def test_GivenAllContentKinds_WhenSealed_ThenExpectedContextOpens(self):
        for kind in ("account", "profile", "record", "folder", "collection"):
            with self.subTest(kind=kind):
                ctx = self.api.Context(OWNER, kind, RESOURCE, 1, [])
                env = self.seal(expected=ctx)
                self.assertEqual(b"public fixture", self.api.open_envelope(self.root, ctx, FORMAT, env))

    def test_GivenChangedTrustedContext_WhenOpened_ThenReject(self):
        env = self.seal()
        for ctx in (self.api.Context(OTHER, "record", RESOURCE, 1, []), self.api.Context(OWNER, "folder", RESOURCE, 1, []),
                    self.api.Context(OWNER, "record", OTHER, 1, []), self.api.Context(OWNER, "record", RESOURCE, 2, []),
                    self.api.Context(OWNER, "record", RESOURCE, 1, ["changed-purpose"])):
            with self.subTest(ctx=ctx):
                self.reject(lambda: self.api.open_envelope(self.root, ctx, FORMAT, env))
        self.reject(lambda: self.api.open_envelope(bytes(32), self.ctx, FORMAT, env))

    def test_GivenChangedEnvelopeField_WhenOpened_ThenReject(self):
        env = self.seal()
        for key, value in (("format", "unknown-v1"), ("keyEpoch", 2), ("keyEpoch", True), ("keySalt", base64(b"s" * 32)),
                           ("nonce", base64(b"n" * 12)), ("ciphertext", base64(b"changed")), ("tag", base64(bytes(16))),
                           ("ciphertext", ""), ("ciphertext", "A"), ("nonce", env["nonce"] + "="), ("tag", None)):
            with self.subTest(key=key, value=value):
                changed = dict(env, **{key: value})
                self.reject(lambda: self.api.open_envelope(self.root, self.ctx, FORMAT, changed))

    def test_GivenUnknownOrMissingFields_WhenOpened_ThenReject(self):
        env = self.seal()
        for value in (None, [], dict(env, unexpected="value"), {k: v for k, v in env.items() if k != "tag"}, dict(env, kdf={})):
            with self.subTest(value=value):
                self.reject(lambda: self.api.open_envelope(self.root, self.ctx, FORMAT, value))

    def test_GivenWrapperFormatOrPurpose_WhenContentApiUsed_ThenReject(self):
        self.reject(lambda: self.seal(format="cerberus-password-wrap-v1"))
        self.reject(lambda: self.seal(expected=self.api.Context(OWNER, "record", RESOURCE, 1, ["wrong-purpose"])))

    def test_GivenNormalSeal_WhenCalledTwice_ThenFreshSaltAndNonce(self):
        one = self.api.seal(self.root, self.ctx, FORMAT, b"public fixture", self.guard)
        two = self.api.seal(self.root, self.ctx, FORMAT, b"public fixture", self.guard)
        self.assertNotEqual(one["keySalt"], two["keySalt"])
        self.assertNotEqual(one["nonce"], two["nonce"])

    def test_GivenFailedEncryption_WhenSaltReused_ThenConsumedReservationRejects(self):
        self.reject(lambda: self.seal(plaintext=b""))
        self.reject(lambda: self.seal())

    def test_GivenUnsentEnvelope_WhenSaltReused_ThenConsumedReservationRejects(self):
        saved = self.seal()
        retry = copy.deepcopy(saved)
        self.assertEqual(saved, retry)
        self.assertEqual(b"public fixture", self.api.open_envelope(self.root, self.ctx, FORMAT, retry))
        self.reject(lambda: self.seal())

    def test_GivenLastBudgetReservation_WhenNextAttempted_ThenReject(self):
        guard = self.api.NonceGuard(2**32 - 1)
        self.seal(guard=guard)
        self.reject(lambda: self.seal(guard=guard, salt=bytes(32)))
        # New root and epoch do not inherit the old domain's encryption history.
        self.seal(guard=guard, root=bytes(32))
        self.seal(guard=guard, root=bytes(32), salt=bytes(32))
        ctx = self.api.Context(OWNER, "record", RESOURCE, 2, [])
        self.seal(guard=guard, expected=ctx)
        self.seal(guard=guard, expected=ctx, salt=bytes(32))

    def test_GivenAlreadyExhaustedBudget_WhenReserved_ThenReject(self):
        self.reject(lambda: self.seal(guard=self.api.NonceGuard(2**32)))

    def test_GivenInvalidGuardCount_WhenConstructed_ThenReject(self):
        for count in (-1, 2**32 + 1, True, 1.0):
            with self.subTest(count=count):
                self.reject(lambda: self.api.NonceGuard(count))

    def test_GivenConcurrentSaltReservations_WhenSealed_ThenExactlyOneWins(self):
        barrier = threading.Barrier(2)
        def attempt(_):
            barrier.wait()
            try:
                self.seal()
                return "accepted"
            except ProtocolError:
                return "rejected"
        with ThreadPoolExecutor(max_workers=2) as pool:
            self.assertCountEqual(["accepted", "rejected"], list(pool.map(attempt, (0, 1))))

    def test_GivenMutableExtraList_WhenChangedAfterConstruction_ThenContextIdentityIsUnchanged(self):
        extra = []
        ctx = self.api.Context(OWNER, "record", RESOURCE, 1, extra)
        extra.append("changed")
        env = self.seal(expected=ctx)
        self.assertEqual(b"public fixture", self.api.open_envelope(self.root, self.ctx, FORMAT, env))
        self.reject(lambda: self.seal(expected=ctx))

    def test_GivenInvalidContext_WhenConstructed_ThenReject(self):
        for owner, kind, resource, epoch, extra in ((None, "record", RESOURCE, 1, []), (OWNER, "é", RESOURCE, 1, []),
                                                  (OWNER, "record", "invalid", 1, []), (OWNER, "record", RESOURCE, True, []),
                                                  (OWNER, "record", RESOURCE, 0, []), (OWNER, "record", RESOURCE, 1, None),
                                                  (OWNER, "record", RESOURCE, 1, [{}])):
            with self.subTest(kind=kind):
                self.reject(lambda: self.api.Context(owner, kind, resource, epoch, extra))

    def test_GivenNestedMutableContext_WhenChangedAfterReservation_ThenDuplicateStillRejects(self):
        nested = [1, None, []]
        ctx = self.api.Context(OWNER, "record", RESOURCE, 1, ["purpose", nested])
        self.guard.reserve(self.root, ctx, self.salt)
        nested[2].append("changed")
        self.reject(lambda: self.guard.reserve(self.root, ctx, self.salt))

    def test_GivenAggregateLimitOrInvalidMaterial_WhenUsed_ThenReject(self):
        for values in ({"root": None}, {"root": bytes(31)}, {"salt": bytes(31)}, {"nonce": bytes(11)},
                       {"plaintext": None}, {"plaintext": b"x" * MAX_REQUEST}, {"guard": None}):
            with self.subTest(values=list(values)):
                self.reject(lambda: self.seal(guard=self.api.NonceGuard(0), **values) if "guard" not in values else self.seal(**values))
        env = self.seal(guard=self.api.NonceGuard(0))
        env["ciphertext"] = "A" * MAX_REQUEST
        self.reject(lambda: self.api.open_envelope(self.root, self.ctx, FORMAT, env))
