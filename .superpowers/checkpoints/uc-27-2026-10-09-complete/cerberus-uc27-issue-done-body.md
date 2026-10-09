**Specification:** [Use Case Specification](https://github.com/artur-rios/cerberus-api/blob/develop/docs/requirements/Use%20Case%20Specification%20Document.md#uc-27-delete-folder).

**Business rules:** BR-07, BR-22, BR-20. See [Business Rules](https://github.com/artur-rios/cerberus-api/blob/develop/docs/initial/Business%20Rules.md).

| Field | Value |
| --- | --- |
| **ID** | UC-27 |
| **Name** | Delete folder |
| **Actors** | Owner |
| **Description** | Return the trash-operation identifier and 30-day expiry; retain ciphertext only for the recoverable window. |
| **Preconditions** | Authenticated identity; active account and the operation-specific ownership/grant; required selected vault access is valid. |
| **Postconditions** | Return the trash-operation identifier and 30-day expiry; retain ciphertext only for the recoverable window. |
| **Requirements** | FR-FD-05, FR-FD-06 |

**Main Flow**

1. Request recoverable deletion of the folder.
2. Resolve the acting identity/scope and validate the operation-specific input, permissions and current state. Public health and internal worker exceptions follow the authorization matrix.
3. Trash the folder and all active descendant folders and contained records in one cascade operation.
4. Return the trash-operation identifier and 30-day expiry; retain ciphertext only for the recoverable window.

**Alternative Flows**

| ID | Condition | Outcome |
| --- | --- | --- |
| AF-01 | Invalid visible input, unsupported envelope or malformed identifier | Reject with 400 and a stable error; do not decrypt or mutate content. |
| AF-02 | Target is absent or hidden by ownership/profile/grant restrictions | Return nonrevealing 404; lists instead omit inaccessible targets. |
| AF-03 | Authentication fails or an authenticated actor lacks the required operation permission | Return 401 or 403 without changing state or disclosing hidden content. |
| AF-04 | Expected revision/state changed during the operation | Return 409, preserve the winning state, and require a reload/retry; sync uploads use their specified per-item outcomes. |
| AF-05 | A required persistence or external dependency is unavailable | Return 503 or the defined retryable worker outcome; fail closed and keep transactions/retries idempotent. |

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
