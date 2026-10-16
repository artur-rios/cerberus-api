# Operational health

`GET /health/live` returns HTTP200 and exactly `{"status":"healthy"}` when the process can answer. It performs no database or Heimdall request and remains reachable during dependency failures and restore reconciliation.

`GET /health/ready` returns HTTP200 healthy or HTTP503 `{"status":"unhealthy"}`. Both public endpoints return only status. Bearer tokens, query parameters and headers cannot opt into diagnostics. Global no-store and request limits apply; conditional requests do not produce cached or304 health responses.

Readiness checks three fixed components:

| Name | A healthy observation means |
| --- | --- |
| database | The configured PostgreSQL connection can execute `SELECT public_id FROM cerberus.account LIMIT 0`. The account schema/column exists and SELECT is allowed. No user row is fetched or changed. |
| identity | The configured non-user service bearer is valid, GET of the configured Heimdall scope confirms its current ownership, and GET of the service owner confirms current role/deletion/scope ownership. |
| restore | Restore is not required, or the traffic gate is open after reconciliation. This state is sampled after the dependency checks. |

Database and identity probes run independently, each with a five-second cooperative cancellation budget. Existing shorter transport timeouts also apply. Request cancellation propagates and disposes database/HTTP resources; no probe is cached, retried or scheduled. The five-second budget bounds cooperative work rather than defining a latency SLO. Database schema/write capability beyond the account SELECT, erasure-ledger integrity, capacity and every business path are not certified by a healthy response. Concurrent state may change after an observation.

The identity probe never logs in or registers a user. An anonymous monitor's bearer is not used as the configured service credential. Keep `CERBERUS_HEIMDALL_SERVICE_CREDENTIAL` current using the existing protected configuration process.

## Authorized details

`GET /health/details` additionally requires a separate explicit operational grant and the current actual caller's authority. Successful authorization returns HTTP200 or503 with this shape:

```json
{
  "status": "healthy",
  "components": [
    { "name": "database", "status": "healthy", "elapsedMilliseconds": 0 },
    { "name": "identity", "status": "healthy", "elapsedMilliseconds": 0 },
    { "name": "restore", "status": "healthy", "elapsedMilliseconds": 0 }
  ]
}
```

The durations above are illustrative. Names and states are fixed; real dependency durations are measured and nonnegative. No subject IDs, URLs, database/host topology, credentials, vault data, upstream payloads or exception messages/stacks enter health output. Before authorization, errors use safe ProcessOutput:401 `authentication_required`,403 `operational_access_required`,503 `identity_unavailable`, with no components. Thus details503 has either an authorized unhealthy report or a safe identity error; OpenAPI describes both.

Configure operational grants separately:

| Environment setting | Configuration alias | Default |
| --- | --- | --- |
| CERBERUS_OPERATIONAL_PERSON_IDS | Cerberus:OperationalPersonIds | Empty: no person grants |
| CERBERUS_OPERATIONAL_APPLICATION_IDS | Cerberus:OperationalApplicationIds | Empty: no application grants |

Each value is a comma-separated list of canonical lowercase nonzero UUIDs in D format, without whitespace or duplicate/empty members. Missing or an empty string grants nobody. Invalid values fail startup with the setting name only. Environment settings override the configuration aliases. Configure them through the existing operator-controlled configuration and restart normally to change grants. Never publish production identities or credentials in the example file.

An explicitly granted scoped role3 person or role2 scope owner may request details; role2 owners may own multiple scopes. A separately granted application uses the distinct application audience and its current application-self/credential contract. Equal UUIDs in the other actor-kind list do not confer access. Ordinary vault sharing/software grants and possession of the configured service credential do not automatically authorize health details. A valid signed actor without its separate grant gets403 before dependency probes; invalid tokens get401. Granted callers are revalidated with their own bearer, without service-credential substitution. Live denial/revocation returns401 and unavailable live authority returns503. The token is checked again after the provider wait.

Only the three health actions bypass a closed restore barrier. Details continues to require operational authorization; its restore component is unhealthy while the gate is closed. Ordinary vault routes remain503 `restore_reconciliation_required`. Health does not open the gate, reconcile data, grant vault access or expose a restore maintenance operation.

## Verification limits

Development checks exercise actual Kestrel, PostgreSQL and a loopback identity contract, including database downtime, account schema/SELECT refusal, current caller binding, restore closure during a pending probe, a stalled provider body, and client disconnect during an observed database lock wait. Restricted SELECT-only roles and ciphertext canaries verify the probe's read-only behavior. Full local/CI suites, dependency/protocol/client checks and development review gate integration. This does not qualify deployment capacity or independent cryptographic/real-client release readiness. The unchanged account404 error-envelope discrepancy remains a separate documented minor.
