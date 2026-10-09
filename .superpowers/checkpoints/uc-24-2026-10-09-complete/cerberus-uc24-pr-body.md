Folder listing now returns only currently permitted encrypted folders and opaque pagination metadata. `GET /api/folders` accepts canonical `pageSize`/`cursor` with current identity and vault access; success contains only five folder fields in `items` plus `nextCursor`.

Owned and native recipient RO/RW scope includes selected direct/member folders and descendants. A record-only grant or profile-record link cannot expose its containing folder; a member leaf cannot expose unshared parents or siblings. One SQL snapshot filters current permissions and complete active ancestry before pagination, verifies every visible foreign native contributor and applies a stable sequence high-water. Listing makes no writes or locks. No schema, dependency or cryptographic algorithm changes.

Closes #25.

## Validation

- Fresh unfiltered suite: **5,209 passed, zero failed/skipped** across Domain 361, Shared 125, Query 356, Data 1,844, Command 841 and WebApi 1,682.
- Coverage: **98.4% line / 92% branch**; all six production assemblies at least 90% line coverage.
- New focused Query 97, actual PostgreSQL Data 104 and native HTTP 89 passes. Genuine missing-contract/store RED and actual missing-GET 405 failures precede implementation; an earlier zero-match installation attempt is explicitly excluded from RED evidence.
- Locked restore, direct/transitive NuGet audit, native OSV 153 packages/no findings, native corpus 322 cases in four directions, 36 native helpers, 23 Python helpers, requirements and OpenAPI write/drift/shape checks passed.
- Complete branch-range whitespace check passed, including added files.
- One fresh whole-branch review found no blockers; both concrete OpenAPI Minors are retained below. Every declined-review judgment has a reasoned final ruling.
- Exact-head branch-policy/test/docker CI is mandatory before normal develop merge.

## Rulings I made

- Ruling: Reuse the verified freshly merged UC23 baseline instead of rerunning identical baseline suites — new base tree equals tested3bf6b37, fresh4919PASS98.4line91.7branch and exact-head branch-policy/test/docker CI passed in this session — cost if wrong: environmental-only regressions must be detected by whole-family and final fresh six-family UC24 runs.

- Ruling: Folder list returns exactly five encrypted folder fields with no parent or organization references — follows existing record-list convention and prevents unshared ancestor/profile metadata disclosure; UC25 owns detailed visible relationships — cost if wrong: clients need a permitted detail lookup to construct hierarchy.

- Ruling: Direct ProfileRecord and CollectionRecord permission never exposes their containing folder — BR28 grants included content, while folder authority comes only from direct ProfileFolder or member-folder descendants — cost if wrong: clients with record-only access cannot navigate an otherwise unshared folder and must use record APIs.

- Ruling: Verify every visible contributing foreign native grant, including overlapping/nonpage routes, with pins once per distinct account — one SQL snapshot preserves complete authority evidence before pagination and avoids repeatedly parsing the same account pins — cost if wrong: one corrupt relevant contribution makes the entire list503 until repaired and large visible inventories still incur per-grant signature validation.

- Ruling: Hidden/incomplete ancestry omits folders before native or corruption inspection; fully active relevant cycles503 and unrelated cycles remain omitted — preserve hidden scope boundaries and complete owned hierarchy without an availability oracle — cost if wrong: visible persisted corruption requires repair before listing can succeed.

- Ruling: Pagination preserves an initial server-sequence high-water while reauthorizing current permissions on every page, without inventing a TTL or durable synchronization — existing list conventions bind actor/hashhandle/size and dedicated resource purpose; current immutable issued scope/session policy remains authoritative — cost if wrong: content changes and permission removals can shrink pages and key rotation requires400/restart; offline reconciliation needs later sync.

- Ruling: Independent formal security/protocol and external-client approval remains development-only deferred under the user instruction — ordinary native/ownership/snapshot/race tests and ONE fresh branch review still gate integration; release checks remain — cost if wrong: latent security/protocol/client defects must be resolved in mandatory qualification before release.

- Ruling: Register folder list dependencies in existing Startup.cs rather than the plan's guessed Program.cs — source discovery showed all explicit store/cursor/query-handler DI belongs to Startup.CreateApplication, while Program owns host/maintenance execution — cost if wrong: following the actual composition root changes no public API but keeps Startup responsible for further registrations.

- Final: Ruling: Independent formal security, threat-model and protocol/cryptographic certification remain development-only deferred — explicit user instruction permits this develop batch while ordinary native/ownership/snapshot checks remain required — cost if wrong: latent security/protocol defects require independent qualification before production.

- Final: Ruling: Real external-client operability and independent client approval remain release prerequisites — no real external client is supplied and the user expressly deferred independent approval for develop — cost if wrong: deployed clients may fail despite passing native HTTP fixtures.

- Final: Ruling: Client content-key provisioning, folder decryption and offline inherited visibility delivery remain release integration obligations — the endpoint returns opaque ciphertext and cannot certify the complete client flow — cost if wrong: clients can lack keys or retain stale visibility until that integration is qualified.

- Final: Ruling: Durable ordered synchronization and immutable cross-request content snapshots remain later use cases — this endpoint implements current-permission high-water pagination only — cost if wrong: pages can shrink and offline reconciliation remains incomplete until synchronization is implemented.

- Final: Ruling: Future folder detail/update/trash/move/restore/purge and writer locking/lifecycle/races require their own gates — this read-only branch cannot certify nonexistent writers — cost if wrong: future writers can violate scope or lock ordering if those gates are skipped.

- Final: Ruling: Other physical erasure paths and the implicit recipient-account foreign-key audit BEFORE UC35 remain mandatory later gates — current typed-terminal filtering is covered but listing adds neither physical handlers nor sharing writers — cost if wrong: resources can retain ciphertext or future sharing writers can deadlock if their gates are skipped.

- Final: Ruling: Production workload capacity, latency and availability qualification remain before release — no workload targets or production load evidence exist; bounded-admission EXPLAIN tests prove only their narrower claims — cost if wrong: full visible-grant signature work can be material on large inventories.

- Final: Ruling: Inherited UC23 narrowed foreign-grant and post-parent-failure fixtures retain their recorded release follow-ups — unchanged UC23 fixtures are outside this diff; UC24 itself installs real folder memberships and proves record-only visibility before negative assertions — cost if wrong: UC23 regression confidence stays narrower until its fixture corrections.

- Final: Ruling: Inherited UC22 permission-outage 500 classification retains its deferred release Minor — unchanged deletion mapping is outside this diff; current folder-list necessary dependency failures map safely to 503 — cost if wrong: operators can receive misleading deletion outage classification until that correction.

- Final: Ruling: Inherited UC21 formatting blank retains its recorded Minor — unchanged line has no UC24 runtime effect and the complete UC24 range passes whitespace checks — cost if wrong: formatting debt remains.

- Final: Ruling: Prior baseline reuse and historical CI provenance remain executor-verified rather than independently re-certified — ruling 1 reuses verified UC23; current UC24 unfiltered tests were freshly inspected and reviewers were read-only — cost if wrong: incorrect historical/environment assumptions require separate diagnosis.

- Final: Ruling: Executor must verify exact-head branch policy/CI, mergeability, normal merge, issue/project closure and merged-tree equivalence; develop does not authorize release/tag/deployment — remote actions were excluded from read-only review and README Done is intended merged state only — cost if wrong: premature completion can leave unverified or unintegrated work; deployment before release gates exposes unqualified behavior.

- Final: Ruling: Independent OS/native-runtime vulnerability certification remains separate release qualification — ordinary package audit/corpus/helper evidence is useful but not formal operating-system/runtime certification — cost if wrong: latent runtime or OS defects can persist until independent qualification.

## Deferred minors

- Final: minor (deferred): Shared OpenAPI omits required access-header/output metadata and advertises nullable items despite runtime requiring access and always emitting the five item fields plus nonnull items and nextCursor. Correct shared generation before external-client release qualification. Cost: generated clients can omit access and receive 401 or model successful data as missing/null; runtime authorization and serialization remain correct.

- Final: minor (deferred): Shared OpenAPI advertises ProblemDetails for the actual empty 413. Correct shared response metadata before external-client release qualification. Cost: generated decoders can try to deserialize an absent body; bounded-body rejection and valid folder listing remain correct.

Independent formal security/protocol and external-client qualification remain development-only deferred under the owner instruction; release gates remain mandatory.
