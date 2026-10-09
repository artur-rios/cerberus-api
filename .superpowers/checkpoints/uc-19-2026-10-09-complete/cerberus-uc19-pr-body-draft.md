Owners and current read/write collection recipients can replace a visible encrypted record with `PUT /api/records/{id}`. A matching expected revision advances record revision/server sequence and returns exactly four metadata fields. Current selection, complete folder ancestry, grants and native evidence are checked again at the final database statement; conflicts and failures preserve the winner. Content-only replacement leaves all relationships and parent metadata unchanged.

The strict three-field request accepts only `expectedRevision`, `envelope`, and `editedAt`. Changed ciphertext stays in the same epoch with fresh salt and nonce. Read-only visible access returns403; hidden targets return404 before content or revision inspection. No plaintext, usable keys or migration/dependency changes.

Closes #20.

## Validation

- Fresh unfiltered six-family suite: **3589 passed; zero failed/skipped** (Domain313, Query259, Shared125, Command567, Data1188, Web1137).
- Six production assemblies each exceed90% line coverage; merged **98.3% line /91.5% branch**.
- Task-level observed RED→GREEN:104 Data cases (including reproduced implicit-FK locking regression),71 Command cases,86 actual PostgreSQL/native-unlock HTTP cases. Real rotation/update and reciprocal-recipient races, every lock-class expiry, final ancestry/lifecycle/current-authority races, retry/rollback/native corruption and exact query-plan bounds.
- Native OSV153packages clean; native corpus322cases/fourdirections;36native-helper and23Python-helper tests; locked restore/NuGet audit clean; OpenAPI generated/driftchecked/shapeaudited;107FR/55UC/29BR spec validation; diff check clean.
- Ordinary whole-branch final review: PENDING. Integration checks remain pending.

## Rulings I made

- Ruling: Targeted transactional snapshot/ordered locks/final guarded SQL, not separate get then update — current grants/native evidence and expected revision must agree at write — cost if wrong: SQL complexity and bounded target-owner collection contention.
- Ruling: No folder row locks; recompute COMPLETE visited ancestry and current scope INSIDE final UPDATE — existing create locks leaf-to-root; adding publicGUID-ordered folder locks can deadlock with creation — cost if wrong: extra target recursion; final statement snapshot linearizes update relative to concurrent later moves/trash.
- Ruling: Own account/session/selectedprofile then collectionsSHAREpublicGUID then grantsSHAREpublicGUID then targetrecordUPDATE; no foreign account/profile locks — compatible with protection rotation and reciprocal recipient writes — cost if wrong: future sharing writers must preserve collection→grant order and no recipient-account locks afterward.
- Ruling: Validate all current contributing foreign native RO/RW evidence; permit owner or at least one current valid RW contributor — consistent with get and failclosed native boundary — cost if wrong: a malformed contributing RO can503 despite another valid RW.
- Ruling: Hidden404 before native/conflict, validvisibleRO403 before expectedrevision; recheck access after waits and at finalstatement — nonrevealing scopes and current operation permission — cost if wrong: failure precedence requires deliberate race tests.
- Ruling: Matching expectedrevision accepts older/equal/new clienttimestamps, sameenvelope allowed; changedenvelope sameepoch/freshsaltANDnonce — existing online profile update contract; offlineLWW/keyrotation separate — cost if wrong: lost-success retry409 requires reload; caller chooses meaningful editedAt.
- Ruling: Reuse RecordCreateDetails as fourfield mutation metadata; no parent/profile structural bumps for content-only update — no relationship changes or public ownergraph — cost if wrong: clients must reload target content separately when needed.
- Ruling: Retain conservative global terminal GUID behavior until typed repair BEFORE ANY physicalpurge; never null selectedProfileId — known persistent safety obligations outside contentupdate — cost if wrong: cross-kind tombstone may falsely hide/reserve sameGUID until repair.
- Ruling: Independent security/protocol and actualclient approvals remain development-only deferred by user — ordinary review/native/fullsuite/CI remain mandatory and releasegates unchanged — cost if wrong: security/client defects may await required release qualification.
- Ruling: use FOR NO KEY UPDATE for own actor account and target record, retaining session/selection UPDATE and collection/grant SHARE — observed failed newauthority regression and pg_stat_activity/pg_locks proved foreign-key KEY SHARE checks blocked on held target FOR UPDATE; account FK would likewise block grant insertion; non-key row locks still serialize policy/content writers while allowing harmless referencing inserts — cost if wrong: new association/grant inserts may commit concurrently, so final current scope and held/native-validated-set guards remain mandatory.
- Ruling: Task3 implementation/test completion precedes the plan paragraph’s whole-branch review and integration gates — reviewing all committed tasks requires the Task3 commit first; review/PR/CI/merge/DoD remain mandatory global finish gates before UC19Done — cost if wrong: task checkbox alone must never be mistaken for integrated use-case completion.

## Deferred minors

Pending final reviewer; known shared OpenAPI required header/body/output metadata and schema for actually empty413 remain before-release follow-up. Runtime strictness and empty413 are tested.

## Approval scope

Existing explicit unattended authorization covers this per-UC development PR and a normal squash merge only after latest exact-head branch-policy/test/docker checks succeed and fresh base is unchanged. Independent security/protocol and actual-client qualification are deferred for development at the owner's request. Main/tag/release approval gates remain intact.
