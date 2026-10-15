# UC52 Empty Trash Design

Purpose: permanently empty the current owner's trash, including all four resource kinds and folder operations, without removing active unrelated content or foreign data. User authorized unattended implementation, verified normal develop merges, issue transitions and owned branch cleanup for the whole backlog; no new approval gates.

Sources: UC52/AF01–05, FR-TR-05, BR-21/BR-26; System Requirements authorization matrix and stable errors; existing record/account permanent intent→durable ledger→fenced physical purge; UC51 canonical owner→session→job→resource locking. Base is verified UC51 develop e56bfa8ae1be3b5a480433c8f634bbc80ad7c4cc,12157/0/0,98.8line89.5branch.

## Adopted behavior

DELETE `/api/trash` requires current human identity, active owner, one current account-wide vault handle and strict required JSON `{ "expectedRevision": n }` identifying the **account** revision. No fresh-identity requirement is added: normative fresh authentication applies to account closure/purge/export; this operation uses the same current vault authority as explicit record erasure. No query or authority override headers. Normal no-store 200 `trash_emptied` returns accountId, purgedAt, counts (one kind/count for each record/folder/collection/profile) and totalPurged. Counts identify the retained resources admitted by this request, not unrelated active resources or hidden foreign IDs. Empty current trash succeeds with four zero counts and no new queue/ledger writes. Lost-success retry naturally returns zero.

Current means actual owned rows with DeletedAt set at admission, including past their deadline or damaged opaque ciphertext/history. A strong own account lock serializes normal owner writers; the request checks account expected revision and current wide session after all waits. Resource metadata/state is guarded before irreversible intent. Restoration winning before admission leaves an active row outside the inventory; restoration after typed intent is hidden. No ciphertext decryption or new usable keys.

One reusable `trash-erase/{accountPublicId}` work item avoids a schema/Payload migration and one claim per cascade member. Under own account→own session→job→ordered own profiles→collections→folders/records, commit typed terminal markers for the current owned trash and create/reopen due work atomically. A new target set invalidates obsolete claims; retries with only existing intent do not steal a live claim. Scope/revision/expiry failure before that commit changes nothing. Once terminal intent commits, ledger/worker failures return503 without undoing irrevocable intent; resources stay hidden and a later safe claim retries.

The worker locks owner then its current job, captures only currently owned trashed rows with typed terminal intent, then releases DB locks to durably record every entry. It reacquires owner/job and resource locks, rechecks persisted token, current DB-time lease and complete durable target set, and physically removes only those typed resources. New undurable intent or a lost/expired claim aborts physical work; all physical mutations of a pass roll back together. Register the exact handler for internal retries. Claim completion for this kind uses database time, never a caller clock. An already erased owner is idempotent only with account-terminal evidence.

Extend physical erasure/replay to folder, collection and profile. Preserve contained active R/F when deleting C/P, other owners' native content, account/protection and selected session IDs/revocation. Folder replay can precede terminal children: detach surviving owned child parent links safely, never recursively delete an unmarked active child. Retire typed entries and only completed/empty own operation metadata. Retain an actively claimed original `trash/{op}` job until its worker can complete; never delete the current purge claim before its final fence. Record erasure's existing guards remain intact. Account erasure also removes its owned `trash-erase/{accountId}` job. Backup replay remains traffic-closed and must physically remove completed F/C/P erasures.

## Alternatives considered

Calling public per-resource delete stores in a loop loses one coherent owner-wide admission and cannot cover all kinds. A payload-bearing bulk job adds a migration with no need: retained rows plus typed terminal markers already persist the target set. Chosen per-owner coalesced job preserves the existing no-Payload retention schema and uses claim invalidation/current terminal evidence to prevent stale acknowledgements.

## Rulings and costs

- Whole-owner emptying requires a wide handle; selected-only handles fail403. This follows all-current-trash semantics without widening selection. Cost if wrong: additive selected-subset API rather than silently purging outside current selection.
- expectedRevision is the account revision; current trash inventory linearizes under the owner lock and uses row/state guards. No invented global trash revision. Cost if wrong: additive inventory-precondition contract.
- Keep original marker times and no retention extension. Irreversible intent may survive503/cancellation after admission, matching existing permanent erasure. Cost if wrong: clients need explicit pending-erasure UI; document retry behavior.
- Per-owner coalesced job uses persisted terminal/trashed inventory, with no new Payload/migration. New admitted targets invalidate older claims. Cost if wrong: retry contention; durable-set/fencing tests must detect unsafe completion.
- Native/history corruption does not prevent physical privacy erasure of clearly owned trash; invalid required authority/state fails closed. Cost if wrong: metadata repair before purge.
