### Task 2: Command and defensive metadata projection

**Files:** Create src/Application/ArturRios.Cerberus.Command/Profiles/UpdateProfile{Command,Validator,Handler,Output,Messages}.cs; tests/Application/ArturRios.Cerberus.Command.Tests/UpdateProfileHandlerTests.cs.
**Interfaces:** Consumes IProfileUpdateStore. Produces UpdateProfileCommand(ExpectedRevision,Envelope,EditedAt; SetContext(actor,access,profileId)), UpdateProfileHandler validator/store, UpdateProfileOutput metadata4fields, UpdateProfileMessages maps200/400/401/403/404/409/503.
- [ ] Write trustedcontext/input/counter/time tests; successful output and correct hashedaccess/route/request; errorforward, cancellation, null/corrupt/substituted metadata503 with no data including literal pre2000 normalized result. Run; expected missing-command compilation failure.
- [ ] Implement strict JSON and validator, failclosed handler; require newrevision expected+1, canonicalpositive sequence/UTC microseconds and normalized inputtime match. Never accept owner/scope/profile from body.
- [ ] Run whole Command family; expected all pass zero skips. Commit.
**Completion:** strict input and no partial bad dependency output.

