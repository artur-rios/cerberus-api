"""Atomic in-memory reference only: no database, identity service or client secrets."""
import copy
import threading

from . import challenge, keys, protection, recovery_bundle
from .encoding import base64, guid, integer, parse, text, MAX_INTEGER
from .errors import ProtocolError
from .primitives import sha256
from .symmetric import Context, _FIELDS, _validate_bound

STATE_FIELDS = {"accountId", "identityId", "scopeKind", "scopeId", "keyEpoch", "protectionRevision", "generation",
                "revocationGeneration", "unlockVerifier", "recoveryVerifier", "passwordWrapper", "recoveryWrapper"}
BODY_FIELDS = {"operation", "idempotencyKey", "expectedRevision", "passwordWrapper", "recoveryWrapper", "newRecoveryVerifier"}


def _wrappers(password, recovery, account, epoch, generation, verifier):
    password = protection._snapshot(password, protection.WRAPPER_FIELDS)
    protection._kdf(password["kdf"])
    slot = Context(account, "account-protection", account, epoch, [])
    _validate_bound(protection._context(slot, password["kdf"]), protection.FORMAT, {k: password[k] for k in _FIELDS})
    recovery = protection._snapshot(recovery, recovery_bundle.WRAPPER_FIELDS)
    fingerprint = keys.thumbprint(verifier)
    if integer(recovery["generation"]) != generation or recovery["proofKeyFingerprint"] != fingerprint:
        raise ProtocolError()
    slot = Context(account, "recovery", account, epoch, [])
    _validate_bound(recovery_bundle._context(slot, generation, fingerprint), recovery_bundle.FORMAT, {k: recovery[k] for k in _FIELDS})
    return password, recovery


class RecoveryModel:
    def __init__(self, initial_public_state, clock):
        state = protection._snapshot(initial_public_state, STATE_FIELDS)
        for field in ("accountId", "identityId", "scopeId"): guid(state[field])
        if state["scopeKind"] != "account" or state["scopeId"] != state["accountId"] or not callable(clock):
            raise ProtocolError()
        for field in ("keyEpoch", "protectionRevision", "generation", "revocationGeneration"): integer(state[field])
        for field in ("unlockVerifier", "recoveryVerifier"): state[field] = keys.validate_public(state[field])
        if keys.thumbprint(state["unlockVerifier"]) == keys.thumbprint(state["recoveryVerifier"]): raise ProtocolError()
        state["passwordWrapper"], state["recoveryWrapper"] = _wrappers(state["passwordWrapper"], state["recoveryWrapper"],
            state["accountId"], state["keyEpoch"], state["generation"], state["recoveryVerifier"])
        self._state, self._clock = state, clock
        self._challenges, self._consumed, self._results = {}, set(), {}
        self._lock = threading.RLock()

    def add_challenge(self, issued):
        value = challenge._challenge(issued)
        with self._lock:
            if value["challengeId"] in self._challenges: raise ProtocolError()
            self._challenges[value["challengeId"]] = value

    def snapshot(self):
        with self._lock:
            results = [dict(identityId=identity, idempotencyKey=key, requestHash=digest, outcome=outcome)
                       for (identity, key), (digest, outcome) in sorted(self._results.items())]
            return copy.deepcopy(dict(state=self._state, challenges=self._challenges, consumed=sorted(self._consumed), results=results))

    def execute(self, identity_id, fresh_auth, raw_body, challenge_id, proof, current_vault_access, inject_commit_failure):
        with self._lock:
            guid(identity_id)
            if fresh_auth is not True or identity_id != self._state["identityId"]: raise ProtocolError()
            if type(current_vault_access) is not bool or type(inject_commit_failure) is not bool: raise ProtocolError()
            body = parse(raw_body, BODY_FIELDS, set())
            key = text(body["idempotencyKey"], True)
            if not key: raise ProtocolError()
            digest = base64(sha256(raw_body)); identity = (identity_id, key)
            if identity in self._results:
                stored_digest, outcome = self._results[identity]
                if digest != stored_digest: raise ProtocolError()
                return dict(outcome)
            operation = body["operation"]
            if operation not in ("recover", "refresh-recovery"): raise ProtocolError()
            if operation == "refresh-recovery" and not current_vault_access: raise ProtocolError()
            guid(challenge_id)
            if challenge_id not in self._challenges or challenge_id in self._consumed: raise ProtocolError()
            state = self._state
            if integer(body["expectedRevision"]) != state["protectionRevision"]: raise ProtocolError()
            binding = {field: state[field] for field in ("identityId", "accountId", "scopeKind", "scopeId", "keyEpoch", "protectionRevision")}
            binding.update(operation=operation, generation=state["generation"] if operation == "recover" else None)
            verifier = state["recoveryVerifier"] if operation == "recover" else state["unlockVerifier"]
            challenge.verify(self._challenges[challenge_id], binding, raw_body, verifier, proof, self._clock())
            next_state = copy.deepcopy(state)
            for field in ("keyEpoch", "protectionRevision", "generation", "revocationGeneration"):
                next_state[field] = integer(state[field] + 1)
            new_verifier = keys.validate_public(body["newRecoveryVerifier"])
            if keys.thumbprint(new_verifier) in {keys.thumbprint(verifier), keys.thumbprint(state["recoveryVerifier"]), keys.thumbprint(state["unlockVerifier"])}:
                raise ProtocolError()
            password, recovery = _wrappers(body["passwordWrapper"], body["recoveryWrapper"], state["accountId"],
                next_state["keyEpoch"], next_state["generation"], new_verifier)
            next_state.update(passwordWrapper=password, recoveryWrapper=recovery, recoveryVerifier=new_verifier)
            outcome = dict(status="committed", **{key: next_state[key] for key in ("protectionRevision", "generation", "revocationGeneration")})
            if inject_commit_failure: raise ProtocolError()
            self._state = next_state
            self._consumed.add(challenge_id)
            self._results[identity] = (digest, outcome)
            return dict(outcome)
