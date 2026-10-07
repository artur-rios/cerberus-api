# Project Overview — Cerberus API

## What This Is

Cerberus API is the back end of an end-to-end encrypted vault for passwords, credentials,
notes, and other sensitive information. It stores encrypted content, manages its organization
and access, and integrates with Heimdall for authentication and authorization. This repository
specifies and delivers the API; client applications perform encryption and decryption.

The original [Brainstorm](Brainstorm.md) and the user's subsequent decisions define the scope.
These initial documents incorporate those decisions, including shared collections and
collections belonging to accounts rather than individual profiles.

## The Problem

Sensitive information needs one protected home that supports both human use across devices
and retrieval by authorized software. Users also need to expose selected information on a
particular device without exposing every part of their vault. Separate profiles, flexible
records, and explicit collection sharing provide those boundaries.

## Who It's For

| Consumer | Purpose |
| --- | --- |
| Account owner | Manage a personal vault and choose the information available through each profile. |
| Shared-collection recipient | Read or edit explicitly shared content according to the owner's grant. |
| Authorized software client | Authenticate through Heimdall, retrieve permitted ciphertext, and decrypt locally using explicitly granted keys. |
| Future Windows, Linux, Android, and web clients | Consume the API and enforce client encryption, display, and local-storage policies. |
| Initial beta user | The project owner validates the first release through personal use. |

## What It Does

- Register and authenticate users through Cerberus with identity delegated to Heimdall.
- Maintain Cerberus account details, linked to exactly one Heimdall account, separately from
  vault profiles.
- Store named records as user-defined key/value fields: text, numeric, boolean, or hidden text.
- Support records created from scratch and client-provided templates with required fields.
- Organize records in nested folders, account-owned collections, and multiple vault profiles.
- Share collections between accounts with read-only or read/write permissions and end-to-end
  encrypted key distribution. The collection owner controls sharing and deletion.
- Support a master vault password or individual profile passwords without server access to
  plaintext vault contents or usable decryption keys.
- Support one-time recovery keys that users can refresh at any time; recovery replaces the key.
- Supply synchronization and authorization capabilities for offline-capable clients. Resolve
  conflicting edits by the latest edit.
- Default periodic offline authorization renewal to 24 hours. Let an account owner choose
  another interval or disable renewal. Enforce known revocations upon reconnection.
- Support online-only use and web clients with no persistent vault storage or caching.
- Provide a trash bin for records, folders, collections, and profiles, with restoration during
  a 30-day window, automatic permanent deletion afterward, and immediate purge on request.
- Support account closure with immediate access revocation, a 30-day cancellation window,
  and an option for immediate permanent deletion.
- Provide the API capabilities needed to meet the brainstorm's LGPD and GDPR requirements.

## What It Doesn't Do

- Build Flutter applications, desktop/mobile interfaces, or browser user interfaces here.
- Replace Heimdall's identity management, authentication, or authorization responsibilities.
- Add an external runtime service besides Heimdall; PostgreSQL is deployment infrastructure.
- Grant access to unrelated account data merely because a collection was shared.
- Let the API decrypt vault data for users or software clients.
- Promise immediate remote revocation on a disconnected device. With renewal disabled,
  an offline client can retain access until it reconnects.
- Promise to retract plaintext or keys a previously authorized recipient has already copied.
- Deliver every Bitwarden or AWS Secrets Manager feature by implication. Those products are
  inspiration; the listed and approved capabilities define the release.

## How Success Is Measured

The first release includes every approved capability; there is no smaller feature subset.
The project owner is the initial beta user.

| Outcome | Observable evidence |
| --- | --- |
| Protected content | API responses, persistence, and logs do not expose plaintext vault content or usable decryption keys. |
| Identity integration | A user registers/authenticates through the Heimdall integration and manages a distinct Cerberus account. |
| Flexible organization | Named custom records, template-based records, nested folders, and multi-profile associations work. |
| Controlled sharing | A second account sees only explicitly shared collection content; read-only recipients cannot modify it. |
| Safe recovery | A recovery succeeds once, the consumed key is rejected, and refreshing invalidates the previous recovery credential. |
| Offline policy | Synchronization follows the latest-edit rule; renewal defaults to 24 hours and honors the owner's configured policy. |
| Deletion behavior | Trash restoration, scheduled expiry, immediate purge, and the different container deletion rules work. |
| Release quality | Heimdall-style unit and functional coverage verifies normal, alternative, ownership, and authorization flows. |

These are acceptance outcomes, not measured production results. Numerical performance targets
and the detailed compliance controls belong in the formal specifications after this review.

Related documents: [Technology Stack](Technology%20Stack.md),
[Business Rules](Business%20Rules.md), and [Workflow](Workflow.md).
