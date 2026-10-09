**Specification:** [Use Case Specification](https://github.com/artur-rios/cerberus-api/blob/develop/docs/requirements/Use%20Case%20Specification%20Document.md#uc-28-move-folder).

**Business rules:** BR-07. See [Business Rules](https://github.com/artur-rios/cerberus-api/blob/develop/docs/initial/Business%20Rules.md).

| Field | Value |
| --- | --- |
| **ID** | UC-28 |
| **Name** | Move folder |
| **Actors** | Owner |
| **Description** | Commit the hierarchy move and its resulting sharing/synchronization visibility changes. |
| **Preconditions** | Authenticated identity; active account and the operation-specific ownership/grant; required selected vault access is valid. |
| **Postconditions** | Commit the hierarchy move and its resulting sharing/synchronization visibility changes. |
| **Requirements** | FR-FD-07, FR-FD-08 |

**Main Flow**

1. Submit the new parent folder identifier, or null for an account-root folder, and expected revision.
2. Resolve the acting identity/scope and validate the operation-specific input, permissions and current state. Public health and internal worker exceptions follow the authorization matrix.
3. Require same-account ownership and reject a self-parent or ancestor cycle.
4. Commit the hierarchy move and its resulting sharing/synchronization visibility changes.

**Alternative Flows**

| ID | Condition | Outcome |
| --- | --- | --- |
| AF-01 | Invalid visible input, unsupported envelope or malformed identifier | Reject with 400 and a stable error; do not decrypt or mutate content. |
| AF-02 | Target is absent or hidden by ownership/profile/grant restrictions | Return nonrevealing 404; lists instead omit inaccessible targets. |
| AF-03 | Authentication fails or an authenticated actor lacks the required operation permission | Return 401 or 403 without changing state or disclosing hidden content. |
| AF-04 | Expected revision/state changed during the operation | Return 409, preserve the winning state, and require a reload/retry; sync uploads use their specified per-item outcomes. |
| AF-05 | A required persistence or external dependency is unavailable | Return 503 or the defined retryable worker outcome; fail closed and keep transactions/retries idempotent. |
| AF-06 | Moving changes inherited shared visibility | Recalculate access and key-envelope needs; reject the change if the required envelopes are absent. |

## Definition of Done

- [x] Implemented on its own `feature/uc-##-use-case-name` branch from `develop`.
- [x] Main flow and every alternative flow implemented.
- [x] Unit and functional tests cover the use case at the correct layers.
- [x] Required suites and the adopted coverage gate pass with fresh evidence.
- [x] Encryption, ownership, and sharing rules remain satisfied.
- [x] Pull request reviewed by a human and merged, or verified and merged under explicit
      unattended authorization covering that action.
- [x] Branch deleted by the human or explicitly authorized agent.
- [x] Issue closed and in `Done`, with the required transition authorization.
- [x] README backlog status updated when the README tracks this use case.
