# Account export operations

The [owner export API](../security/account-export-api.md) streams a captured private
vault view without preparing an archive file, changing native rows or renewing a
session. The ordinary restore/reconciliation barrier still protects vault routes.
No migration, package dependency or new runtime setting is required.

Each stream owns a PostgreSQL READ COMMITTED, READ ONLY transaction and a
transaction-local `NO SCROLL` cursor. `FETCH FORWARD 32` bounds ciphertext batches;
there is no whole-account page-size truncation. Permission queries contribute only
current decisions; header/content/relationships/keys all remain the captured view.
There are no owner/resource write locks held behind a slow client. Ordinary schema
locks and a read snapshot remain database operational concerns.

Application buffering consists of the current32 rows, a serialized entry and the
batch's deduplicated typed dependency IDs. Captured ancestor paths add metadata and
database path work for deep folder hierarchies. There is no arbitrary depth cap or
new numerical latency/throughput claim. Capacity planning should account for active
connections, snapshot lifetime, client backpressure and existing transport controls.

The export needs SELECT on account/session authority, protections, native resources,
relationships/grants/target envelopes, terminal exclusions and retained history. It
does not need sequence or native write privileges. The real PostgreSQL capability
test uses a non-owner SELECT-only role and verifies `transaction_read_only=on`.

EOF includes a final current-authority fence. Enumerator, cursor/transaction and
context cleanup precede the successful footer. Disconnect/cancellation releases the
owned backend resources; late failures produce transport truncation. Clients must
require both clean transport EOF and a complete valid archive. Repeating a failed
download captures a new view; it does not resume or reconstruct the old snapshot.

Original retained windows are never extended by export. Selected orphaned or
structurally unusable history fails rather than producing a complete package with
lost metadata. Controlled repair is needed for such stored corruption; invisible
foreign, expired and terminal histories are excluded before their bytes are parsed.

Only deliberately redacted events enter production logging. Export failures log a
stable code, without dependency exception text, request headers, bodies or stored
material. Session verifiers, credentials, proof/retry machinery and erasure-ledger
contents are never archive entries.

Development verification includes real PostgreSQL snapshot/writer and current-fence
tests, all12 entry families with foreign canaries, actual raw OpenAPI bodies, and
Kestrel TCP truncation/disconnect/EOF-disposal cases. A genuine test-only content
AES256GCM/HKDF roundtrip proves that narrow transport property; structural fixture
wrappers do not prove full password/HPKE archive decryption. Existing native-client
corpus and Java/Python lease checks remain mandatory. Independent crypto/client
release qualification remains governed by the existing development-only deferral.
