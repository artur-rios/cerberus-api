### Task 1: Contracts and verifier isolation

create Domain/Profiles/ProfileAccessContracts.cs and Data/Profiles/ProfileVerifierIsolation.cs (internal fingerprint uniqueness helper); modify ProfileCreateStore to reject same scoped public verifier as another retained owned profile after reserved-ID conflict check, account lock protects check+insert. No schema change needed. RED domain required IDs/revisions/digest/body contract; realPG create isolation same/other account, trash, corruptstoredpublickey and existing duplicate conflict precedence. Existing multi-profile fixtures must generate distinct scoped pairs, retaining explicit verifier values in tests that inspect them; never weaken assertions. GREEN wholeDomain/Data; commit.
Interfaces: ProfileChallengeRequest(actor,profileId,expectedRevision,requestHash); ProfileAccessRequest(actor,profileId,expectedRevision,challengeId,proof,rawBody); IProfileAccessStore.ChallengeAsync/OpenAsync; native ProfileUnlockMaterial.Read validates account protection counters/pins, target content/native owner wrappers/current profile counters and all retained owned verifier isolation.

- [ ] Write the specified failing tests, run the focused project and read the expected RED output.
- [ ] Implement only this task, run the named whole-family tests, read zero failures/skips and commit.

