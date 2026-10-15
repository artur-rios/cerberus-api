# UC-54: Export account data

Authority: UC54 main flow and AF01–05; FR-PV-01–02; BR10,28,29; NFR01–05,08–11. Implement POST `/api/accounts/me/export` for the currently authenticated owner. Existing unattended batch authorization replaces human transitions; all automated verification remains mandatory. Scope is development integration, not release/privacy-law qualification.

## Design choice

Use one PostgreSQL read-only transaction and one transaction-local `NO SCROLL` cursor containing both authorization and all exported projections. Bounded `FETCH FORWARD 32` batches avoid accumulating the whole archive in application memory. The transaction uses `READ COMMITTED`, no explicit resource/owner write locks, and UTC local time zone. A materialized one-row clock/authority CTE freezes time-based eligibility across fetches. The same cursor snapshot supplies header and entries; no subsequent independent database query contributes package data.

The alternatives were owner/session/collection locks held throughout streaming, which delay writers behind a slow client, and a prepared file/archive, which adds storage, cleanup and whole-package preparation. The read-only cursor preserves a coherent captured view while writers can proceed. PostgreSQL documents small cursor fetches, insensitive cursors, and transaction-local lifetime: https://www.postgresql.org/docs/18/sql-declare.html. Real database tests must prove the actual driver, fixed-clock and concurrent-writer behavior.

## Admission and request

The strict JSON request contains only required integer `expectedRevision` in 1..9007199254740991. The existing protected endpoint validates the actual scoped Heimdall person and live provider state. A single canonical `X-Cerberus-Vault-Access` handle must resolve to the owner's current account-wide session; selected-profile handles cannot export an account. The signed identity `iat` must be positive, not future, and within the existing exclusive 60-second freshness window using database time.

The cursor's captured view requires an active nonterminal owner, current unrevoked account-wide session, matching policy/revocation generations and correct issued/expiry policy. Current account revision must equal the request. Invalid visible input is400, missing authentication/access401, denied/freshness authority403, absent/hidden owner404, revision conflict409, and required dependency unavailable503. Errors are actual `ProcessOutput` bodies with stable existing-style codes, not a typed null-data response.

Admission is the captured cursor view before HTTP output. A change committed before capture wins and is rechecked. Package content remains that captured view; native edits after capture never overwrite the winning database state or mix newer content into older metadata. The archive's serverTime is its frozen database eligibility-check time, not a globally ordered transaction identifier.

Fresh local authority queries on the same READ COMMITTED connection recheck account/session state, generations, expiry and expected account revision before releasing each bounded batch and before completion. Pending typed payload/relationship identifiers are checked against current terminal exclusions. These queries contribute only permission decisions, never newer package data. Before output, a changed account revision is409 and missing/denied authority uses404/403; after output, loss of authority, conflict or new terminal exclusion aborts the archive. Already admitted buffered rows or delivered bytes cannot be retracted; no instantaneous in-flight erasure claim is made. Heimdall's live identity remains verified at HTTP admission under the established current-request boundary. Native retention visibility uses the frozen captured time, and fresh terminal fences prevent later batches from bypassing permanent intent.

## Versioned archive

The response is JSON with `format:"cerberus-account-export-v1"`, `header`, `entries`, `entryCount` and `complete:true`. The header contains accountId, identityId, account revision, policy revision, revocation generation, renewalEnabled, nullable whole-second renewalIntervalSeconds and serverTime. Entry ordering is unspecified; typed public IDs reconstruct relationships. No import endpoint or archive signature is introduced.

Entries use a closed `entryType` discriminator and explicitly named fields:

- `account`: original details-envelope bytes.
- `protection`: original protection revision, key epoch, recovery generation and material bytes (encrypted wrappers and public JWK pins).
- `profile`, `record`, `folder`, `collection`: typed public resource ID, original revision/server sequence/edit/deletion/purge metadata and envelope bytes; profile key-wrapper bytes; collection key epoch; eligible owned parent/folder public references.
- `relationship`: source/target kinds and public IDs, relationship kind and nullable original trash-operation ID. Current and filtered historical owned relationships remain distinct. relationshipKind is current or historical; profiles point to record/folder/collection membership, collections to record/folder membership, records to their folder and folders to their parent. Historical edges carry the original operationId; current edges carry null.
- `collection-grant`: an owned collection's grant ID, recipient account reference, access/state/revision and original recipient-key envelope.
- `shared-collection-reference`: current related incoming collection/owner/grant IDs, grant revision and access. These identify stored sharing relationships; they copy no foreign native ciphertext, protection bundle or private profile organization and do not certify a currently decryptable foreign payload.
- `software-grant`: own grant ID, related Heimdall software ID, state/revision and original public recipient-key bytes.
- `software-target`: own grant ID, owned typed target ID and its original encrypted recipient-key envelope.
- `trash-operation`: original operation/root IDs and kind, deletion time and purge deadline.

All stored encrypted JSON/wrapper bytes are transported unchanged as standard base64 byte strings. Existing envelopes inside those bytes retain their original base64url values, epochs, signing contexts and counters. Do not parse/rewrite opaque encrypted payloads to fit the newest crypto format. Exported opaque bytes are not a claim that damaged historical material decrypts. A separate genuine test-only content AEAD roundtrip may prove that narrow transport property; structural ProtectionFixture wrappers cannot prove password/HPKE archive decryption.

Native entries are owned, nonterminal and either active or inside their original valid exclusive 720-hour retained window. Expired/permanent/foreign entries and hidden ancestor chains are excluded. Cyclic or structurally unusable selected metadata fails safely. Retained records need not have their opaque ciphertext parsed. Current relationships join eligible owned targets; historical snapshots use fixed typed keys and filter references against eligible owned resources or the defined current shared references. Foreign profile IDs, dangling permanent targets and arbitrary raw association snapshots never leave the API. Invalid selected history cannot silently produce a falsely complete package. Each selected retained native must have its original owned operation and typed entry with a matching window; orphaned selected history fails503.

Software grants and target envelopes belong to the exporting account; targets must also be eligible owned native rows. Outgoing collection-grant metadata belongs to owned collections. Incoming shares remain references. No foreign account details/material/native bytes are projected, including through corrupt cross-owner links.

Excluded storage includes sessions and handle verifiers, proof challenges, recovery-operation proofs, registration idempotency/fingerprints/HMAC, synchronization payload/idempotency fingerprints and download caches, retention claims/jobs, erasure ledger contents, configured service/signing credentials and internal numeric keys. These are authentication, execution or retry machinery, not portable vault contents. No database/Heimdall mutation or session renewal occurs during export.

## Streaming and cleanup

`IAccountExportStore.OpenAsync` opens the owned read-only transaction/cursor and prefetches/maps the first bounded batch before returning a lease. Known initial database/projection errors return503 before headers. The lease owns context/transaction/cursor and exposes typed entries through a single-consumer asynchronous enumeration. Every raw row must have the same captured header and a valid closed entry shape. Private typed permission dependencies include captured folder ancestor paths and original historical operation roots; they never enter the archive. No request-owned context is disposed before the stream finishes.

The presenter obtains the first validated entry before headers, flushes a JSON prefix, then streams entries with asynchronous backpressure and request cancellation. It disposes the cursor/transaction/context after EOF and its final current-authority fence **before** writing entryCount/complete and the closing object. If enumeration, authorization, serialization, transport or disposal fails after output starts, it disposes resources and aborts, without a success footer or replacement error envelope. A client accepts only successful transport EOF plus a complete valid archive. Caller cancellation releases database resources even while a later FETCH is waiting.

All responses are no-store with no ETag/Last-Modified. Success is an attachment named `cerberus-account-export.json`. Logs contain only safe codes/status, never tokens, stored bodies, plaintext, usable keys or dependency exception text. No artificial whole-account page-size truncation, new numerical SLO, new dependency, migration or configuration is added. Application buffering is bounded by32 rows plus one serialized row and the current batch’s deduplicated dependency IDs; deep ancestry adds metadata/path work, with no arbitrary depth cutoff; a database snapshot remains open for the response lifetime, so capacity and ordinary transport controls remain operational concerns.

## Verification and delivery

Task1 covers strict input, query admission/result/disposal boundaries. Task2 covers real read-only cursor authority, owned native/protection data, timing, concurrent writers, multiple batches and cleanup. Task3 covers owned relationships/grants/target envelopes/retained history and corruption/foreign canaries. Task4 covers actual HTTP, genuine narrow AEAD transport, raw OpenAPI bodies and real Kestrel wire faults/cancellation/disposal-before-footer.

AF01→Tasks1/4; AF02→Tasks2/3/4; AF03→Tasks1/2/4; AF04→Tasks2/4; AF05→Tasks1/2/4. No flow is waived. Follow the full required local Release suite, fresh six-assembly90% line floor/branch report, locked restore/build/audits/helper/spec/native corpus322×4/actual Java-Python lease/OpenAPI/Docker gates, one fresh whole-branch review and exact-head required CI. Normal develop squash/tree equality, issue55Closed/projectDone/README55Done1Todo/M06 8/9 and owned cleanup precede UC55.
