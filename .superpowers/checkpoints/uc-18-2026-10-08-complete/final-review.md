# UC18 ordinary whole-branch review

Reviewed base `cdd89ab3df2cab6f0dda9969c0f5155b85f59dc6` through exact HEAD `86e914ea450ccac0bd77a22f4dac2ec6590bd04c` in `/tmp/cerberus-uc18-worktree`, against the supplied review package, binding spec, plan and progress rulings. One fresh read-only review; no checkout/index/branch mutations, builds, test runs, or subagents.

## Strengths

- The record public-ID predicate and unique resource index bound the materialized candidate to one record. Recursive traversal uses only that candidate's same-owner upward path and terminates cycles. Unrelated record metadata cannot poison this get.
- Current authority, selected profile, contributing collection routes, visible references and native protection/grant evidence share one SQL snapshot. Native verification performs no subsequent database reads.
- Direct record admission alone does not expose the parent. Foreign records cannot expose owner profile IDs. Hidden ancestors take precedence over relevant cycle/native failures; unselected cycles remain 404.
- The query projection checks the exact requested target, all eight output fields, reference-array validity and content metadata before producing output. The controller rejects noncanonical input, overrides and bodies.
- Tests cover real PostgreSQL snapshots and analyzed candidate/ancestry counts, native grants, actual HTTP/profile-access flows, state changes, no mutation, safe error mapping, and malformed target payloads. I inspected the supplied completed coverage log: 3314 passed, zero failures/skips across all six families, 98.3% lines. Branch coverage 91.5% and other audit results are supplied verification evidence, not independently rerun here. `git diff --check` passed during review.

## Issues

### Critical

None found.

### Important

1. **Malformed contributing collection envelopes can be accepted because numeric strings are enabled.**
   - New call site: `src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordReadStore.cs:140` (collection envelope supplied at line 142). Root cause: `src/Infrastructure/ArturRios.Cerberus.Data/Resources/CollectionGrantBinding.cs:9` and `:25`; `src/Domain/ArturRios.Cerberus.Domain/Accounts/EncryptedEnvelope.cs:6` has no strict-number attribute.
   - Trigger: take an otherwise valid, currently contributing foreign collection and replace the stored collection envelope's numeric `"keyEpoch":1` with `"keyEpoch":"1"`, retaining its valid grant, database epoch and pins. The shared validator constructs `JsonSerializerOptions(JsonSerializerDefaults.Web)`, which enables reading numbers from strings. It deserializes the string as long 1; `IsValid`, epoch equality and grant verification still succeed. UC18 consequently returns the record and collection reference with 200 instead of rejecting the malformed relevant native evidence with whole-response 503.
   - Effect: this violates the binding strict native-evidence contract and lets corrupted/native-noncanonical storage silently pass. No unauthorized-recipient disclosure is demonstrated by this finding. The target-record path is stricter because `RecordProjection` explicitly sets `JsonNumberHandling.Strict`; the collection path lacks that protection. Recipient/protection numeric records already carry strict attributes, so they do not close this particular gap.
   - Correction: set strict numeric handling in the shared binding deserializer, or equivalently enforce it on the envelope model with appropriate regression coverage. In the owning Data task, add a valid-shared-target regression with only the collection-envelope epoch changed to a numeric string, expecting 503/no data; cover selected/account-wide and hidden/unselected nonrevealing 404 as appropriate. Check representative alternate malformed collection-envelope shapes deliberately. Run affected whole families, including users of the shared helper. This finding is established by code inspection; no additional test was run during this read-only review.

### Minor

These are the already tracked shared-generator discrepancies, not new release deferrals:

1. `docs/contracts/openapi.json:2352`: mandatory vault-access header lacks `required: true`. Generated clients can omit a header that runtime requires. Correct shared OpenAPI metadata generation before release.
2. `docs/contracts/openapi.json:5386` and `:5410`: the exact output fields are mostly not required and the two always-present ID arrays are nullable. Generated clients receive weaker types than the actual contract. Mark the eight fields required and make the ID arrays non-nullable in generation; retain nullable folder value.
3. `docs/contracts/openapi.json:2460`: 413 advertises a ProblemDetails body although the common guard returns an empty body. Clients following the schema can attempt to parse nonexistent JSON. Remove the advertised response content through the shared generator before release.

## Recommendations

Use the authorized single blocking TDD correction pass for the collection-envelope numeric-string gap. Preserve the existing one-statement snapshot and hidden-before-native ordering. Keep all documented release and purge gates unchanged.

## Declined to judge

Each line states a separate behavior and reason. No other considered behavior was silently excluded.

- Independent cryptographic/protocol security qualification — explicitly deferred by the user for development only; ordinary implementation review does not replace the mandatory independent release approval.
- Actual supported-client decryption/interoperability qualification — explicitly deferred by the user for development only; native fixtures and API tests do not establish production-client operability.
- Physical purge with cross-kind terminal GUID collisions — this branch has no purge operation, and the binding ruling preserves the conservative current lookup pending mandatory typed repair before purge. False hiding from a cross-kind tombstone remains the recorded cost.
- Public collection membership/grant mutation endpoint behavior — belongs to UC34 and is absent from UC18; I reviewed read-side response to committed relationship/grant changes, not delivery of those mutation endpoints.
- Durable synchronization and inventory-wide ordering integrity — belongs to UC48/listing; targeted get intentionally validates its own record only and must not traverse unrelated record metadata.
- Deployment-scale latency/capacity certification for extreme ancestry depth or large eligible-collection counts — no production workload benchmark is supplied and this review must not run shared builds/tests. I reviewed the recursion, candidate bound, constraints and analyzed-plan test design, but do not certify a production capacity threshold. Very deep visited-path traversal has increasing storage/work cost and remains an operational sizing consideration, not an observed regression here.
- Final remote CI/mergeability, merge, issue/project closure and release approval — these are executor delivery gates after the reviewed commit, not behavior established by this local read-only review.

## Assessment

**Ready to merge? With fixes.**

The branch matches the approved architecture and has strong scope/snapshot/error tests, but the contributing collection-envelope parser must reject numeric-string epochs before merge. The known OpenAPI minors remain tracked before release; this review grants no independent security, client or release approval.
