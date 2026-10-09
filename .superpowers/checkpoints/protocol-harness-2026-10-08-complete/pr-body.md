The protocol review gate currently has no executable interoperability evidence. Add isolated Java/Bouncy Castle and Python/cryptography implementations that independently produce, consume and reject the Cerberus v1 contracts, plus committed public vectors and reproducible checks in the required `test` job.

The harness covers strict encoding, scoped envelopes and key roles, signed HPKE recipient wrapping, exact-body challenge proofs, local recovery/retry and offline lease/clock/trust models. It also locks and audits the resolved dependency graph, records isolated KDF measurements, and consolidates the review handoff. Production .NET code, Docker packaging, coverage and the independent protocol approval gate remain separate. The manifest stays pending; this PR completes no business use case and supplies no security approval.

Validation:

- Java 173/173 and Python 142/142 native tests; 322 cases per producer pass both consumers and all six published primitive families.
- Harness helper/delivery tests 34/34; repository helpers 23/23; specifications, OpenAPI and whitespace checks pass.
- Complete unfiltered production .NET suite 124/124, zero skips; NuGet vulnerability scan clean.
- Fresh OSV audit clean across 153 package versions; exact new CI install/audit/replay scripts also pass against a newly resolved temporary dependency graph.
- `scripts/verify_protocol.py` still exits 1 with the expected pending-review message; missing/stale approval tests pass.

Independent security review and real-client qualification are still required before dependent business implementation or release. No issue-closing reference is included because no business issue is completed by reference evidence.

Whole-branch review found three issues, now covered by failing-then-passing regressions: embedded ECPrivateKey DER/schema validation, fixture mismatches being mistaken for negative protocol rejection, and subprocess output/result limits with descendant cleanup. The corrected corpus includes eleven malformed embedded-key cases and two valid controls, and binds 44 source/input/dependency hashes. Both actual consumer CLIs now abort on a valid envelope containing unexpected plaintext. Benchmarks were rerun through the corrected supervisor.

Two nonblocking review observations remain: narrow the Java fixture serializer’s accepted numeric types and add observed runtime/library metadata to each replay report. Current strict protocol parsers are unaffected; runtime observations remain in the benchmark/lock evidence.
