# Export account data Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Stream a consistent, no-store owner ciphertext archive with related metadata and encrypted keys.
**Architecture:** One read-only PostgreSQL cursor captures package data and initial authority; bounded batches and current local fences preserve privacy without buffering all owner data. Typed entries stream through an owning lease; cleanup precedes the completion footer.
**Tech Stack:** .NET10, ASP.NET Core, EF/Npgsql, PostgreSQL18, xUnit/Testcontainers and actual Kestrel.
**Spec:** docs/superpowers/specs/2026-10-15-uc-54-export-account-data.md

## Global Constraints
- POST `/api/accounts/me/export`; body only required safe integer expectedRevision; current scoped fresh owner and account-wide vault handle.
- Fresh signed iat uses existing exclusive60-second database-time policy. Original retained windows are exclusive720hours; timestamps UTC microsecond.
- `cerberus-account-export-v1`, typed entries, original encrypted bytes as standard base64; internal IDs/authentication/retry machinery and unrelated foreign payloads excluded.
- Read-only transaction, `NO SCROLL` transaction-local cursor, `FETCH FORWARD 32`, one materialized eligibility clock; no total page-size truncation or new dependency/migration/configuration.
- Local account/session/revision and pending typed terminal fences use fresh READ COMMITTED statements between batches; package fields all remain the original cursor view.
- Before-start expected failures use actual ProcessOutput400/401/403/404/409/503. After-start failures abort without complete footer. Dispose before successful footer.
- One issue/branch/PR; full unfiltered six-project Release suite, fresh six-assembly>=90% line coverage and branch report; one fresh final reviewer and exact-head required CI before normal develop merge.
- Existing unattended authorization replaces human gates; native implementation in place, no implementation agents, one fresh whole-branch reviewer. Independent crypto/client release qualification remains deferred for develop only.

## Review Focus
1. Cursor time/data snapshot versus fresh authority: later fetches retain captured content while current revocation/revision or pending-target terminal changes abort delivery (Task2).
2. Corrupt cross-owner relationships and historical recipient-profile UUIDs: no foreign payload/private organization or unfiltered snapshots leave the archive (Task3).
3. Large archives, slow clients and cancellation while FETCH waits: bounded batches, full row count, single ownership and prompt database cleanup (Tasks2/4).
4. Failure before first byte, during stream or during EOF disposal: correct503 or wire truncation with no false success footer/protected exception text (Task4).
5. Legacy opaque crypto, original wrapper counters, retained/permanent typed IDs: preserve bytes/context without claiming dummy fixtures decrypt (Tasks2–4).

### Task 1: Owner export contracts and query admission
**Files:** Create `src/Domain/ArturRios.Cerberus.Domain/Accounts/AccountExportContracts.cs`, `AccountExportEntries.cs`; `src/Application/ArturRios.Cerberus.Query/Accounts/ExportAccountQuery.cs`, `ExportAccountHandler.cs`, `AccountExportMessages.cs`; `tests/Application/ArturRios.Cerberus.Query.Tests/ExportAccountHandlerTests.cs`.
**Interfaces:** Produces AccountExportInput(ExpectedRevision).IsValid(); AccountExportHeader(AccountId,IdentityId,Revision,PolicyRevision,RevocationGeneration,RenewalEnabled,nullable RenewalIntervalSeconds,ServerTime).IsValid(actor,expectedRevision); AccountExportRequest(Actor,AccessVerifier,AuthenticationIssuedAt,ExpectedRevision); AccountExportOpenResult(Lease?,Error?); IAccountExportStore.OpenAsync(request,ct); IAccountExportLease: IAsyncDisposable with Header and ReadAsync(ct):IAsyncEnumerable<AccountExportEntry>. AccountExportException carries only a stable error code. Closed entry DTOs cover spec's account/protection/four native kinds/relationship/collection-grant/shared-reference/software-grant/software-target/trash-operation. AccountExportArchive describes the actual streamed document without being buffered. ExportAccountQuery(Actor,AuthenticationIssuedAt,Access,ExpectedRevision) derives the existing BaseQuery; output holds [JsonIgnore] lease. ExportAccountHandler(store) implements IQueryHandlerAsync; error status map is public AccountExportMessages.StatusCodes.
- [x] Write query tests first: valid scoped request returns the same lease and hashed handle; missing actor/access, malformed handle, unsafe expected revision, nonpositive iat never dispatch; known errors map safely; unknown/mixed error+lease and malformed header/foreign actor are persistence failures and dispose unused lease; cancellation propagates. Header policy/counter/time validation is exercised through these behaviors, not getter tests.
- [x] Run `dotnet test tests/Application/ArturRios.Cerberus.Query.Tests --filter 'FullyQualifiedName~ExportAccount' --no-restore`; add only mechanical contract/stub surfaces needed to compile, then rerun. Expected: actual assertion failures for missing export admission/result behavior; initial missing-symbol compiler errors are not behavioral RED evidence.
- [x] Implement exact safe-input/current-context/result validation and stable output/disposal behavior. No database or wire implementation yet.
- [x] Run the focused query command again; Expected: all PASS,0fail/skip.
- [x] Commit `feat: define owner export admission contract`.

### Task 2: Read-only native cursor and current fences
**Files:** Create `src/Infrastructure/ArturRios.Cerberus.Data/Accounts/Export/AccountExportStore.cs`, `AccountExportSql.cs`, `AccountExportLease.cs`, `AccountExportRow.cs`; `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/AccountExportStoreTests.cs`, `AccountExportConcurrencyTests.cs`.
**Interfaces:** Consumes Task1 contracts. Produces AccountExportStore(factory):IAccountExportStore. OpenAsync owns READ COMMITTED transaction, sets READ ONLY and UTC, declares fixed transaction-local cursor, prefetches/maps32rows and transfers ownership to lease. Raw rows contain error/header/body JSON; closed strict typed mapping rejects inconsistent header/metadata. Lease ReadAsync is single-consumer, fences before each batch/final completion, and always disposes resources on exit. Fresh fences contribute only permission decisions, never package values.
- [x] Write real PostgreSQL tests first: owner details/protection/all four native kinds and original bytes/contexts; absent/closing/terminal owner; wide-session policy/generation/revocation/expiry/profile denial; signed-iat stale/future and revision winner before capture; read-only non-owner SELECT-only role succeeds and all native/auth state stays unchanged. More than2batches and more than MaxPageSize must export all entries.
- [x] Add current-fence tests before implementation: committed local revocation/account revision or a pending-target terminal marker after first batch aborts later delivery; concurrent native update commits without blocking and package remains coherent old snapshot; fixed eligibility time survives later FETCH across a retained deadline; cancellation/early disposal releases cursor/transaction. Use actual SQL boundaries and real controlled waits, not setup-dependent short TTLs.
- [x] Run `dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests --filter 'FullyQualifiedName~AccountExport' --no-restore`; Expected: behavioral missing-store/native/snapshot/fence failures after mechanical stubs compile.
- [x] Implement cursor guard/native projections, original metadata/opaque-byte preservation, bounded fetch ownership and current local/typed-terminal fences. Use one materialized clock for package eligibility; each fence uses fresh database time. Errors before lease transfer safely clean up and return stable codes.
- [x] Run focused export tests plus affected account-read/native regressions; Expected: all PASS; actual SELECT-only role proves absence of write locks/mutations.
- [x] Commit `feat: stream owned export from a read-only cursor`.

### Task 3: Private relationship and key metadata
**Files:** Create `src/Infrastructure/ArturRios.Cerberus.Data/Accounts/Export/AccountExportRelationsSql.cs`; modify AccountExportSql/row mapping; create `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/AccountExportRelationsTests.cs`, `AccountExportRetentionTests.cs`.
**Interfaces:** Extends the same Task2 cursor SELECT through a focused partial SQL helper; no independent package-data queries. Uses Task1 fixed typed entries and Task2 header/current fences. Exposes owned normalized relationships, related incoming refs, owned grants/target envelopes and original retained-operation metadata.
- [x] Write real tests first: current profile/folder/collection associations, own outgoing grants, own software public-key and record/collection target envelopes, related incoming refs without foreign ciphertext/protection/private profiles. Assert byte equality, original counters and entire foreign canary graph unchanged.
- [x] Add typed UUID collision, corrupt cross-owner native/link/target and historical recipient-profile cases; retain original owned history through fixed-key filtered normalized references. Expired/terminal/hidden ancestor chains are omitted; selected cyclic/invalid metadata cannot silently produce a complete archive. Opaque native crypto remains transportable. Terminal fences cover pending key/relationship identities.
- [x] Run focused AccountExport tests; Expected: native-only implementation fails metadata/foreign/corruption assertions.
- [x] Implement all whitelisted relationships/grants/encrypted target keys/retained metadata within the captured cursor snapshot. Never dump EF entities, raw association snapshots or foreign protection bundles.
- [ ] Run all focused AccountExport tests and relevant trash/native/grant regressions; Expected: all PASS,0fail/skip.
- [x] Commit `feat: export private relationships and wrapped keys`.


### Task 4: HTTP streaming, wire failures and full local gates
**Files:** Create `src/Presentation/ArturRios.Cerberus.WebApi/Controllers/AccountExportController.cs`, `Results/AccountExportStreamResult.cs`; modify Startup registrations; add `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/AccountExportHttpTests.cs`, `AccountExportStreamingTests.cs`; update `docs/contracts/openapi.json`, operations docs, new `docs/security/account-export-api.md`, README.
**Interfaces:** Consumes ExportAccountQuery/output/map and owning lease. Controller POST strict metadata/body, actual actor+signed iat+single access header. Stream result advertises AccountExportArchive, pre-enumerates first entry before headers, flushes prefix/entries with async PipeWriter backpressure, disposes after EOF/final fence before entryCount/complete footer, maps pre-start known/dependency errors, aborts after-start failure. Attachment constant filename; no-store/no conditional validators.
- [ ] Write HTTP tests first for success/raw typed schema/unchanged native graph, malformed JSON/unknown members/revision/header/media/body limits, owner/profile/software/wrong-scope/provider/current-state denials and conflicts. Expected first run: missing route404 instead of declared behavior.
- [ ] Write real Kestrel ResponseHeadersRead tests: late actual FETCH failure yields nonempty truncated unparseable JSON with no success/footer/error replacement; caller disconnect while FETCH is pending releases actual backend transaction; EOF disposal failure prevents a complete archive. Add narrow genuine test-only AES256GCM content roundtrip through returned bytes/context, separate from structural wrapper fixtures.
- [ ] Implement endpoint, DI and streaming lifetime/error behavior; generate OpenAPI and compare actual raw bodies. Existing unrelated account404 schema Minor stays deferred; new export errors must conform to their actual ProcessOutput declarations.
- [ ] Run focused query/Data/HTTP/streaming checks; Expected: all PASS,0fail/skip, no protected log text. Update README UC54Done (55Done1Todo, foundation+54business, M06 8/9).
- [ ] Run locked restore, zero-warning Release build, audits/helpers/specs/native322×4/actual Java-Python lease/OpenAPI/Docker gates; Expected: all PASS with actual raw response-schema evidence.
- [ ] Commit `feat: serve private owner export streams` after focused checks to freeze a clean head.
- [ ] Run full unfiltered six-project Release suite and fresh coverage; Expected:0fail/skip, six-assembly>=90% line, branch reported. Complete this implementation task only after all local gates; record the actual frozen-head run rather than redundantly repeating an identical full run.

## Delivery after implementation
- [ ] Prepare whole-branch package and dispatch exactly one fresh read-only most-capable reviewer with Review Focus/ledger. Regrade all findings; Important/Critical one RED→GREEN fix pass +full suite, Minor deferred; rule on every declined judgment with cost.
- [ ] Open PR closing55; require exact-head branch-policy/test/docker SUCCESS and downloaded proof. Recheck current develop/rules, normal squash merge, source/merged tree equality, issueClosed/projectDone/README/M06, owned cleanup and hash archive. Then load UC55 fresh automatically.

## Gate1 self-check
FR-PV-01→Tasks1–4; FR-PV-02→Tasks2–4. AF01→Tasks1/4; AF02→Tasks2/3/4; AF03→Tasks1/2/4; AF04→Tasks2/4; AF05→Tasks1/2/4. Both FR identifiers exist. Contracts precede consumers; captured content and fresh permission fences are distinct; metadata whitelist includes original software target envelopes. No external migration/dependency/service or release claim. All listed review risks have named owning tests.
