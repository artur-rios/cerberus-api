# UC06 Request account closure implementation plan

**Goal:** Close the current owner's account recoverably, retain encrypted data for
30 days, and atomically revoke existing online access and grants.

**Specifications:** UC06 main flow and AF01–07; FR-AC-11, FR-AC-12, FR-AC-13;
BR24; System Requirements authorization matrix and deletion strategy.

**Authorization:** The existing unattended batch covers this issue, its branch,
verification, PR/develop merge, issue/project transitions and delivered branch
cleanup. Every verification remains mandatory. Independent release/client review
remains gated. UC43 stays blocked on Heimdall178.

## Design

- Issue7, own `feature/uc-06-request-account-closure` branch from verified clean
  develop ce76dee5af5cf434a7afe6df7f6b1b8292a1f59a. Reloaded the UC, requirements,
  business rule, workflow, testing, technology and operations documents.
- POST `/api/accounts/me/closure` accepts exactly a required safe positive
  `expectedRevision`. The acting account comes from the current Heimdall identity.
  The dedicated lifecycle authorization row requires fresh authentication;
  closing access does not require a decryption key or a vault unlock proof.
  Follow existing signed-token freshness: one positive iat, not in the future,
  less than60 seconds old. Recheck freshness after waits and at mutation time.
- Return only account ID, revision, revocation generation, closure request time,
  fixed30-day deadline and whether closure was already pending. Never expose
  ciphertext, wrappers, internal IDs or a new lease/access token.
- Capture all accounts connected through incoming/outgoing collection grants,
  acquire canonical exclusive account roots, then recheck the captured graph.
  Lock affected collections canonically before grants/child rows, preserving
  native sharing and standalone recipient collection-before-target ordering.
- One transaction changes Active to ClosurePending, advances account revision
  and revocation generation, revokes every own vault session, invalidates pending
  unlock challenges, revokes active incoming/outgoing collection grants and clears
  their recipient wrappers. Advance affected outbound recipients' generations
  once each; preserve other owners' content, profiles and unrelated grants.
- Revoke own software grants and erase recipient key/target provisioning using
  the existing reduction semantics. Preserve encrypted account/resource/protection
  data and minimal sync/operation metadata. Cancellation must use new sessions and
  explicit regranting; no access is revived implicitly.
- Persist a due account purge work item at the original deadline. The subsequent
  account purge/expiry use cases supply execution and restore-ledger reconciliation.
  A cancelled prior work item must not prevent a later closure cycle.
- AlreadyPending is a fresh-identity restricted metadata replay: return the
  original times without extending them or requiring the now-revoked session.
  Invalid retained state fails closed. Expired pending data remains inaccessible.
- Guarded database-time mutations and postconditions reject suppressed/partial
  writes, stale captured state, invalid counters and cancellation. Roll back all
  changes on faults; caller cancellation propagates. Existing issued client leases
  remain bounded by their previous policy until expiry/reconnection.

## Flow mapping and test-first tasks

- [x] Domain/Command RED then GREEN: strict revision input, freshness boundaries,
  expected versus replay output, minimal metadata integrity, safe errors/cancellation.
- [x] PostgreSQL RED then GREEN: atomic close, original-deadline replay, no payload
  loss; incoming/outgoing/software reduction; recipient generation scope; scheduled
  work; revision/counter conflicts; target absence/terminal markers; rollback.
- [x] Concurrency: opposing/reciprocal account graphs, native recipient writers,
  session issuance/sharing/protection, actual lock waits and expired freshness,
  controlled suppressed writes/dependency faults, lost-response and clean retries.
- [x] Absent POST RED, then strict bounded JSON/query/header/media transport,
  current Heimdall revalidation, fresh identity and no-store responses. Prove old
  sessions/native reads/offline renewal fail and unrelated owners' data survive.
- [x] AF01 validation400; AF02 hidden/absent404; AF03 auth/freshness401 and known
  denial403; AF04 state/revision409; AF05 dependency503/rollback; AF06 original
  deadline replay; AF07 explicit reconnect revocation with documented client limits.
- [x] API/operations/changelog/OpenAPI documentation and actual HTTP contract
  validation. Fresh whole-change independent read-only review; resolve blockers.
- [x] Locked restore/audits, zero-warning build, full unfiltered Release suite,
  fresh>=90% production line coverage, helpers/spec/native corpus/server leases,
  OpenAPI/Docker. Update README only after verified implementation.
- [ ] Own PR closes7, exact-head branch-policy/test/docker SUCCESS, normal merge,
  source-tree proof, issueClosed/projectDone, branch cleanup, hash-verified archive
  and clean synchronized develop before UC07.
