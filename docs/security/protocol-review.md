# Proposed encryption, recovery and offline protocol

Status: **Proposed; security and client-interoperability review has not occurred.**

This record is a review input for NFR-11 and IR-09. It does not approve an algorithm suite or authorize dependent implementation. The API may scaffold interfaces and review gates while the protocol remains unapproved.

## Threat boundary

The database, backup store and server-held identity validation/signing credentials must not reveal vault plaintext. The API enforces online authorization, but an authorized recipient can retain plaintext or keys already received. A disconnected client enforces its signed lease locally; immediate offline revocation and remote erasure of copied keys are unavailable.

Client software and random-number generation must be trustworthy. A malicious server can deny service and conceal/replay data; clients must verify authenticated metadata, epochs and recipient-key fingerprints. Server assertions alone do not authenticate a recipient encryption key against an active server substitution attack.

## Proposed suite for review

| Concern | Proposed selection |
| --- | --- |
| Content and password/recovery key wrapping | AES-256-GCM, 32-byte key, 12-byte nonce, 16-byte tag |
| Password derivation | Argon2id v1.3, 64 MiB, three passes, four lanes, 16-byte client-generated salt, 32-byte output |
| Domain-separated subkeys | HKDF-SHA-256 with separate labels and account/profile/epoch context |
| Recipient key wrapping | RFC 9180 HPKE base mode, DHKEM(P-256, HKDF-SHA256), HKDF-SHA256, AES-256-GCM; exact suite IDs 0x0010/0x0001/0x0002 |
| Online vault proof | Client-generated ECDSA P-256 key; server stores only the public key; SHA-256 signatures over a single-use bound challenge |
| Recovery | Client-generated random 32-byte recovery secret, separate encryption and proof domains, atomic generation consumption |
| Offline lease | Server ECDSA P-256 signing key, ES256 JWS, published verification key and explicit key identifier |

These choices require confirmation against the actual client libraries and supported devices. Argon2 memory/time parameters must be measured on those clients before adoption; any changed parameters require new vectors and review. AES-GCM nonce reuse under the same key is forbidden. Require fresh client randomness per encryption and maintain per-key nonce tracking; rotate before the reviewed per-key message bound is reached.

## Proposed envelope wire contract

JSON envelope fields: `format` (exact `cerberus-aes256gcm-v1`), `keyEpoch` (positive integer), `nonce` (canonical base64url for 12 bytes), `ciphertext` (canonical base64url) and `tag` (canonical base64url for 16 bytes). Reject unknown fields/formats, malformed encodings, invalid epochs, empty ciphertext and values exceeding approved request limits.

Authenticated associated data is UTF-8 bytes of a fixed ordered JSON array, with no whitespace: `["cerberus-content-v1",accountGuid,resourceKind,resourceGuid,keyEpoch]`. GUIDs are lowercase canonical D format; epoch is a base-10 JSON integer. Only these structural identifiers are server-visible. Names, schemas, passwords, notes and custom values remain inside ciphertext. A resource moves between folders/profiles without altering its authenticated owner/resource identity. A different owner or epoch requires client re-encryption.

Wrapped-key envelopes use a separate format/domain and bind owner, recipient identity, resource, recipient-key fingerprint and epoch. HPKE info and authenticated data use distinct `cerberus-wrap-v1` labels and the same fixed-order identifier encoding. Never accept an API directory key as independently verified: clients must verify/pin recipient fingerprints through an independent authenticated channel before sharing. Key rotation requires a signed transition from the pinned key or a new independent verification.

## Online access proof

Generate a client-only signing private key separately from encryption keys and store it inside the client-encrypted protection bundle. The API stores only its verification key. A server challenge binds operation, authenticated Heimdall public identity, account, selected profile or account scope, protection epoch, request digest and expiry. It has a cryptographically random identifier and nonce, is stored server-side, and is consumed transactionally once. A signature is proof of bundle possession; a Heimdall login alone never unlocks vault content.

The current endpoint inventory needs an explicit challenge issuance contract before this design can be implemented. Proposed addition: authenticated `POST /api/vault/challenges`, returning only a random challenge and binding metadata. Expiry proposal: 60 seconds, measured against client UX requirements. Proof signatures use fixed-width IEEE P1363 encoding; reject wrong sizes, mismatched binding, expired/replayed challenges and superseded epochs.

## Recovery and refresh

The client generates recovery material and derives independently labelled wrap and proof keys; neither the raw secret nor its usable wrap key reaches the API. The client encrypts its protection bundle under the recovery wrap key. The proof construction, verification-key derivation and binding to the recovery bundle must be reviewed together; choosing ECDSA alone does not define a secure deterministic recovery proof.

Recovery requires fresh identity authentication and a single-use generation-bound proof. Consumption, replacement protection, revocation of access sessions/leases and new recovery generation commit in one transaction. An already consumed generation cannot change keys again; lost-response retry reports the operation outcome without consuming twice. Refresh replaces recovery material with newly client-generated material and invalidates the old generation. The API returns no raw client recovery secret.

## Offline lease binding and rotation

The signed lease carries issuer/audience, account and identity GUIDs, explicit selected scope, active grant revisions, protection epochs, issuance/expiry, unique lease ID, policy version and signing key ID. Verification is client-side, using pinned trusted server verification keys and a clock policy reviewed for rollback resistance. An expired or disabled-renewal lease cannot renew locally. Online reconnection revalidates current Heimdall identity and current Cerberus permissions before renewal, synchronization or newly disclosed ciphertext.

Lease signing keys are operational credentials, independent of vault content keys. Publish the overlap/removal policy; removal cannot recall a verification key cached on an offline client. Never describe such rotation as immediate remote revocation.

## Required review evidence

- Independent security review of nonce limits, proof/recovery construction, authenticated metadata, key substitution, key rotation and rollback/replay threats.
- Actual API/client interoperability vectors, including encoded envelopes, AAD bytes, recipient wrapping, proof signatures and ES256 leases, generated/verified by independent implementations.
- Negative vectors for wrong owner/resource/epoch/recipient fingerprint, nonce/tag/encoding corruption, challenge replay, recovery-generation races and malformed lease claims.
- Measured client KDF resource consumption and randomness/nonce behavior.
- Explicit reviewer identities, reviewed artifact hashes, date, outcome and any unresolved findings. User approval of batch automation is not evidence that these reviews occurred.

No review result or client test-vector execution is claimed here. The recovery proof construction, challenge endpoint and client verification/clock policy must be resolved before this protocol can be approved.

## Primary review inputs

- [RFC 9106: Argon2](https://www.rfc-editor.org/info/rfc9106/)
- [RFC 5869: HKDF](https://www.rfc-editor.org/info/rfc5869/)
- [RFC 9180: HPKE](https://www.rfc-editor.org/info/rfc9180/)
- [RFC 5116: authenticated encryption](https://www.rfc-editor.org/info/rfc5116/)
- [RFC 7518: JSON Web Algorithms](https://www.rfc-editor.org/info/rfc7518/)
