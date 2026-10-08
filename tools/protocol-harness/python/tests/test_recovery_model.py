import copy
import importlib
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor

from cerberus_protocol import challenge, keys
from cerberus_protocol.encoding import base64, MAX_INTEGER
from cerberus_protocol.errors import ProtocolError
from cerberus_protocol.wire import encode
from tests.fixtures import fixture

ACCOUNT = "00000000-0000-0000-0000-000000000001"
IDENTITY = "00000000-0000-0000-0000-000000000002"
OTHER = "00000000-0000-0000-0000-000000000008"
NOW = 1700000000


def wrappers(epoch, generation, verifier):
    # Opaque valid envelope metadata. No plaintext/key enters the server model.
    envelope = dict(keyEpoch=epoch, keySalt=base64(bytes(32)), nonce=base64(bytes(12)), ciphertext="AA", tag=base64(bytes(16)))
    password = dict(envelope, format="cerberus-password-wrap-v1", kdf=dict(algorithm="argon2id-v1.3", memoryKiB=65536,
                    iterations=3, parallelism=4, salt=base64(bytes(16))))
    recovery = dict(envelope, format="cerberus-recovery-wrap-v1", generation=generation, proofKeyFingerprint=keys.thumbprint(verifier))
    return password, recovery


def initial():
    password, recovery = wrappers(1, 1, fixture("recovery-1")["publicJwk"])
    return dict(accountId=ACCOUNT, identityId=IDENTITY, scopeKind="account", scopeId=ACCOUNT, keyEpoch=1,
                protectionRevision=1, generation=1, revocationGeneration=1, unlockVerifier=fixture("unlock-account")["publicJwk"],
                recoveryVerifier=fixture("recovery-1")["publicJwk"], passwordWrapper=password, recoveryWrapper=recovery)


class RecoveryModelTests(unittest.TestCase):
    def setUp(self):
        try: self.api = importlib.import_module("cerberus_protocol.recovery_model")
        except ModuleNotFoundError: self.fail("RecoveryModel feature missing")
        self.now = NOW
        self.model = self.api.RecoveryModel(initial(), lambda: self.now)

    def request(self, model=None, operation="recover", key="one", changes=None, role=None, register=True):
        model = model or self.model
        state = model.snapshot()["state"]
        new_key = fixture("recovery-2")["publicJwk"] if state["generation"] == 1 else keys.public_jwk(keys.generate())
        password, recovery = wrappers(state["keyEpoch"] + 1, state["generation"] + 1, new_key)
        body = dict(operation=operation, idempotencyKey=key, expectedRevision=state["protectionRevision"],
                    passwordWrapper=password, recoveryWrapper=recovery, newRecoveryVerifier=new_key)
        body.update(changes or {})
        raw = encode(body)
        binding = {field: state[field] for field in ("identityId", "accountId", "scopeKind", "scopeId", "keyEpoch", "protectionRevision")}
        binding.update(operation=operation, generation=state["generation"] if operation == "recover" else None)
        issued = challenge.issue(binding, raw, NOW)
        if register: model.add_challenge(issued)
        role = role or ("recovery-1" if operation == "recover" else "unlock-account")
        return dict(identity_id=IDENTITY, fresh_auth=True, raw_body=raw, challenge_id=issued["challengeId"],
                    proof=challenge.sign(issued, fixture(role)["privateDer"]), current_vault_access=True, inject_commit_failure=False)

    def reject_unchanged(self, request):
        before = self.model.snapshot()
        with self.assertRaises(ProtocolError): self.model.execute(**request)
        self.assertEqual(before, self.model.snapshot())

    def test_GivenConcurrentRecovery_WhenCommitted_ThenOneTransitionWins(self):
        for iteration in range(100):
            model = self.api.RecoveryModel(initial(), lambda: NOW)
            requests = [self.request(model, key="first"), self.request(model, key="second")]
            before = model.snapshot(); barrier = threading.Barrier(2)
            def run(request):
                barrier.wait(timeout=10)
                try: return model.execute(**request)
                except ProtocolError: return dict(status="rejected")
            with ThreadPoolExecutor(max_workers=2) as pool: outcomes = list(pool.map(run, requests))
            with self.subTest(iteration=iteration):
                self.assertEqual(1, sum(item["status"] == "committed" for item in outcomes))
                after = model.snapshot()
                self.assertEqual(2, after["state"]["protectionRevision"])
                self.assertEqual(2, after["state"]["revocationGeneration"])
                self.assertEqual(2, after["state"]["generation"])
                self.assertEqual(1, len(after["consumed"]))
                self.assertEqual(1, len(after["results"]))
                winner = next(i for i, item in enumerate(outcomes) if item["status"] == "committed")
                self.assertEqual(outcomes[winner], model.execute(**requests[winner]))
                self.assertEqual(after, model.snapshot())
                self.assertNotEqual(before, after)

    def test_GivenExactRetry_WhenSignatureMalleatedOrChallengeExpired_ThenStoredResultReturned(self):
        request = self.request(); outcome = self.model.execute(**request); after = self.model.snapshot()
        order = keys._ORDER  # Native qualified curve order; test-only alternate signature.
        proof = request["proof"]
        request["proof"] = proof[:32] + (order-int.from_bytes(proof[32:], "big")).to_bytes(32, "big")
        self.now = NOW+60
        self.assertEqual(outcome, self.model.execute(**request))
        self.assertEqual(after, self.model.snapshot())
        self.assertEqual({"status", "protectionRevision", "generation", "revocationGeneration"}, set(outcome))

    def test_GivenStoredResult_WhenIdentityBodyKeyOrAuthenticationChanges_ThenRejected(self):
        request = self.request(); self.model.execute(**request)
        for change in [dict(identity_id=OTHER), dict(fresh_auth=False), dict(raw_body=request["raw_body"]+b' '),
                       dict(raw_body=request["raw_body"].replace(b'"one"', b'"two"'))]:
            with self.subTest(change=list(change)): self.reject_unchanged(dict(request, **change))

    def test_GivenUnknownExpiredOrWrongPurposeChallenge_WhenExecuted_ThenRejected(self):
        self.reject_unchanged(self.request(register=False))
        request = self.request(); self.now = NOW+60; self.reject_unchanged(request)
        self.now = NOW
        self.reject_unchanged(self.request(role="recovery-2"))
        self.reject_unchanged(self.request(role="unlock-account"))

    def test_GivenConsumedChallenge_WhenDifferentOperationRetried_ThenRejected(self):
        request = self.request(); self.model.execute(**request)
        changed = dict(request, raw_body=request["raw_body"].replace(b'"one"', b'"other"'))
        self.reject_unchanged(changed)

    def test_GivenStaleRevisionOrGeneration_WhenExecuted_ThenRejected(self):
        self.reject_unchanged(self.request(changes=dict(expectedRevision=2)))
        stale = self.request(key="stale"); self.model.execute(**self.request(key="winner"))
        self.reject_unchanged(stale)

    def test_GivenRefreshWithoutCurrentAccess_WhenExecuted_ThenRejected(self):
        request = self.request(operation="refresh-recovery"); request["current_vault_access"] = False
        self.reject_unchanged(request)

    def test_GivenRefresh_WhenCommitted_ThenOldRecoveryGenerationInvalidated(self):
        old = self.request(key="old")
        request = self.request(operation="refresh-recovery")
        self.assertEqual(2, self.model.execute(**request)["generation"])
        self.reject_unchanged(old)
        self.assertEqual(3, self.model.execute(**self.request(key="new", role="recovery-2"))["generation"])

    def test_GivenInjectedCommitFailure_WhenExecuted_ThenEveryEffectRolledBack(self):
        request = self.request(); request["inject_commit_failure"] = True; self.reject_unchanged(request)
        request["inject_commit_failure"] = False
        self.assertEqual("committed", self.model.execute(**request)["status"])

    def test_GivenMalformedReplacementMetadata_WhenExecuted_ThenRejectedBeforeCommit(self):
        valid = self.request()
        from cerberus_protocol.encoding import parse_object
        body = parse_object(valid["raw_body"])
        for field, value in [("expectedRevision", True), ("idempotencyKey", ""), ("idempotencyKey", "é"),
                             ("operation", "unlock-account"), ("newRecoveryVerifier", fixture("recovery-1")["publicJwk"]),
                             ("passwordWrapper", {}), ("recoveryWrapper", {}), ("extra", 1)]:
            request = self.request(key="bad-" + field, changes={field: value})
            with self.subTest(field=field): self.reject_unchanged(request)

    def test_GivenDuplicateChallengeOrMutableSnapshots_WhenUsed_ThenRegistryProtected(self):
        request = self.request()
        snapshot = self.model.snapshot()
        issued = next(iter(snapshot["challenges"].values()))
        with self.assertRaises(ProtocolError): self.model.add_challenge(issued)
        snapshot["state"]["recoveryVerifier"]["x"] = "AA"
        self.assertNotEqual(snapshot, self.model.snapshot())
        self.assertEqual("committed", self.model.execute(**request)["status"])

    def test_GivenRevisionOverflow_WhenExecuted_ThenNoPartialChanges(self):
        state = initial(); state["revocationGeneration"] = MAX_INTEGER
        self.model = self.api.RecoveryModel(state, lambda: NOW)
        self.reject_unchanged(self.request())


if __name__ == "__main__": unittest.main()
