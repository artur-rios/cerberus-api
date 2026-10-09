# UC10 final ordinary review — /root/uc01_review

Reviewed d521230..b869875; read-only. Strengths: single-snapshot permission/boundary/page selection, pre-store cursor binding, pre-pagination filtering, real persistence/HTTP/current-access tests. Verified supplied1452/0fail/0skip97.6/89.1 evidence.

Critical none. Important1 ListProfilesHandler.cs:53 omits MasterKeyWrapper.RecipientIdentityId==query.Actor. Read-only compiled valid Row probe modified only recipient identity; Success=True mismatched wrapper returned. Require owner match; query+HTTP corrupt-second-row regressions,503/nullpayload.

Important2 ProfileListStore.cs:33 filters sequence>After before metadata validation: zero can silently disappear on initial page; [1,1,3] size1 advances beyond duplicate and omits profile. Schema default has no positivity/uniqueness constraints. Detect invalid ordering among permitted rows in same snapshot or equivalent DBinvariant; real PG/HTTP zero+duplicate regressions.

Minor none new; known global OpenAPI metadata release follow-up retained.

Declined to judge:
1. Independent cryptographic security and real-client interoperability approval: explicitly deferred development-only; release approval pending.
2. Selected-session minting and cross-profile key isolation: UC15; current restrictions reviewed.
3. Future collection-grant implementation: absent; BR03/28 correctly prevent foreign profiles.
4. Cross-request synchronization snapshot guarantees: UC48; ordinary listing refresh documented.
5. Server verification of encrypted-content decryptability and a fresh stored-wrapper crypto audit: outside structural/visible-metadata list contract.

Verdict: Ready with fixes; ordinary review does not grant independent/release approval. Reviewer did not rerun entire suite; examined evidence and targeted compiled recipient probe.
