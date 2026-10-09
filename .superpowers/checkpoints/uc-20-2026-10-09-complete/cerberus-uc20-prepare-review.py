from pathlib import Path
r=Path('/tmp/cerberus-uc20-worktree');p=r/'docs/superpowers/plans/2026-10-09-uc-20-delete-record.md';s=p.read_text();focus=s.split('## Review Focus\n',1)[1].split('### Task 1:',1)[0];ledger=r/'.superpowers/sdd/2026-10-09-uc-20-delete-record/progress.md';rulings='\n'.join(x for x in ledger.read_text().splitlines() if 'Ruling:' in x)
template=Path('/root/.codex/plugins/cache/openai-curated-remote/superpowers/6.4.2/skills/requesting-code-review/code-reviewer.md').read_text()
text=f'''Review UC20 owned recoverable record deletion. Checkout: {r}. Read-only; do not change files/index/HEAD/branch, do not spawn agents. This is the ONE fresh whole-branch ordinary implementation review mandated by executing-plans; independent formal security/protocol and real-client qualification are user-deferred for develop only. Still assess ordinary implementation correctness/security. Use most capable model as directed by that skill.

Base:1713397fea3deb4ae9d30119e493997eb7943514
Head: REVIEW_HEAD_PLACEHOLDER
Package: REVIEW_PACKAGE_PLACEHOLDER
Spec: {r}/docs/superpowers/specs/2026-10-09-uc-20-delete-record.md
Plan: {p}
Ledger: {ledger}

Implemented purpose-built PostgreSQL transaction, typed retained association snapshot, strict command/output, DELETE route/DI, docs/OpenAPI and 289 new cases (116 Data/74 Command/99 HTTP). Actual owned opaque bytes remain; allstoredlinks snapshot/remove; activeimmediateparents bump; retain720hours+oneop/entry/queue; currentnativeforeignRO/RW403 even RW; finalcurrentexpiry/lifecycle/fullancestry; noforeignlocks; actualstoreconcurrency/rollback/native fixtures. No schema/dependencychange. Read adjacent stores/schema to evaluate FK locks and scope interactions.

Verification: /tmp/cerberus-uc20-coverage.log fresh unfiltered full suite (expected3878; inspect actual final output), all six production assemblies >=90line. Other actual logs /tmp/cerberus-uc20-{{http-red,http-green-1,data-red,data-green-1,data-green-2,data-green-3,task1-data,command-red,command-green,task2-command,locked-restore,nuget-audit,native-audit,native-verify,native-tests,helper-tests,specs,openapi-write,openapi-check}}.log. No need duplicate full suite unless concrete diagnostic. Full native153OSV/322vectors/36helpers/23Python/spec107FR55UC29BR alreadypassed.

Review Focus verbatim:
{focus}
Initial Rulings verbatim (evaluate each):
{rulings}

Known shared generator Minors: required header/requestBody/outputfields not allmarkedrequired (inputexpectedRevisionISrequired); generated413ProblemDetails though testedactualempty. These are mandatory release contract cleanup, not evidence of runtime wrongbehavior. Grade by actualeffect, not silence. Every behavior considered and declined as outside scope MUST be enumerated with reason; emptylistonlyifnone. Check allFocus deliberately; name uncovered effects. Return precise Critical/Important/Minor and ready-to-merge judgment. Write full report to /tmp/cerberus-uc20-final-review.md (outside checkout); final message concise with reportpath and findings.

Use the complete requested template below; placeholders resolved by preceding paths/description/range:
{template}
'''
Path('/tmp/cerberus-uc20-final-review-brief.md').write_text(text)
