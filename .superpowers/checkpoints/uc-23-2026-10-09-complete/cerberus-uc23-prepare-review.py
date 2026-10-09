import json,subprocess
from pathlib import Path
w=Path('/tmp/cerberus-uc23-worktree');head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=w,text=True).strip();base='d72d92072eaba28509597c545f4833a8ef58faa6'
workspace=w/'.superpowers/sdd/2026-10-09-uc-23-create-folder';l=(workspace/'progress.md').read_text();assert all(f'Task {n}: complete' in l for n in range(1,5))
rulings=[x for x in l.splitlines() if 'Ruling:' in x];assert len(rulings)==9
validation=json.loads(Path('/tmp/cerberus-uc23-validation.json').read_text());assert validation['tests']==4919
plan=w/'docs/superpowers/plans/2026-10-09-uc-23-create-folder.md';s=plan.read_text();focus=s.split('## Review Focus\n',1)[1].split('### Task 1:',1)[0]
packages=list(workspace.glob('review-*.diff'));assert len(packages)==1
old=Path('/tmp/cerberus-uc22-review-prompt.md').read_text();tail=old.split('\n## Git Range to Review\n',1)[1].replace('5c994f6f5626c83cb332cb5170d93907cd8e10ef',base).replace('cc2b8bb1983950ec45e8f2c7e0252d9e88e2dd88',head)
prompt=f'''You are a Senior Code Reviewer with expertise in software architecture,
design patterns, and best practices. Your job is to review completed work
against its plan or requirements and identify issues before they cascade.

## What Was Implemented

UC23 owned encrypted folder creation using the existing folder/profile-link schema. Strict POST /api/folders requires folderId/envelope/editedAt/profileIds and optional nullable parentFolderId; exact output folderId/revision/serverSequence/editedAt. Current account-wide or singleton selected profile access; optional complete active owned parent ancestry through direct folders or owned collections, no foreign-grant parent administration. Typed terminal ID reservation, UTC microsecond normalization, guarded statement-time authorization, atomic direct links and direct-profile/immediate-parent metadata preserving encrypted content/client times. New folders integrate with profile projection, record creation/read and complete nested protection rotation. Five commits; 48 Domain,106 Data,58 Command and111 actual native HTTP new cases.

## Requirements / Plan

Review ONLY workdir {w}. Binding spec {w}/docs/superpowers/specs/2026-10-09-uc-23-create-folder.md; plan {plan}; ledger {workspace}/progress.md; complete branch review package {packages[0]}. All four tasks complete. Fresh unfiltered six families 4919 passed, zero failures/skips; actual validation /tmp/cerberus-uc23-validation.json and full log /tmp/cerberus-uc23-coverage.log: {json.dumps(validation['families'])}; actual coverage {validation['line']}% line/{validation['branch']}% branch; all six production assemblies>=90% line. Evidence /tmp/cerberus-uc23-{{domain, data, command}}-{{red,green,whole}}.log and http-red.log/http-green.log, actual OpenAPI-shape/drift, locked restore/dependency/native/helpers/spec logs. Read actual evidence and code; do not rerun builds/tests. A first HTTP installer attempt produced zero matching tests and is explicitly NOT RED evidence; the corrected installed matrix produced110 missing-route failures/1 pass BEFORE route/DI, then111 passes.

This is the ONE ordinary whole-branch review required by executing-plans. User explicitly deferred independent formal security/protocol/crypto and real external-client operability approval for develop; main/tag/release gates remain mandatory. Review current ownership, native use, safe handling, transaction/lock/expiry and current product correctness; do not perform or certify deferred independent audits or release/deployment. Existing shared OpenAPI missing header/body/output requiredness and advertised ProblemDetails for actual empty413 remain release Minors. The UC22 permission-outage500 classification and UC21 formatting blank are inherited unchanged outside this diff. Existing native external-client key provisioning/decryption and ordered inherited visibility sync remain before-release qualification/integration prerequisites. Future folder CRUD/trash/moves/purge, other physical erasure and BEFORE UC35 implicit recipient-account FK audit retain their explicit later gates; do not claim nonexistent future writers are certified. Record every behavior set aside in Declined to judge with reason. Read-only checkout/index/HEAD/branches; no edits, subagents, approvals, remote GitHub actions or review submissions. Write only the final report outside worktree to /tmp/cerberus-uc23-final-review.md, then return verdict and severity-by-actual-effect findings with file:line. No duplicate reviewer or rerun.

Verbatim Review Focus:
{focus}
ALL rulings, with reasons and costs (do not silently drop):
'''+ '\n'.join(rulings)+'\n\n## Git Range to Review\n'+tail
assert '[HEAD_SHA]' not in prompt and '[BASE_SHA]' not in prompt
Path('/tmp/cerberus-uc23-review-prompt.md').write_text(prompt);Path('/tmp/cerberus-uc23-tested-head.txt').write_text(head+'\n');print('Complete reviewer prompt prepared for',head,'with all9 rulings, verbatim five focus areas and actual4919-pass evidence.')
