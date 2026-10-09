from pathlib import Path
import re,json
w=Path('/tmp/cerberus-uc28-worktree');p=w/'.superpowers/sdd/2026-10-09-uc-28-move-folder/progress.md';s=p.read_text();assert 'Final: Ruling:' not in s
report=Path('/tmp/cerberus-uc28-final-review.md').read_text();assert '**Ready to merge? With fixes.**' in report
section=report.split('## Declined to judge\n',1)[1].split('The two inherited',1)[0];lines=re.findall(r'^\d+\. (.+)$',section,re.M);assert len(lines)==14
judgments=[
('Independent formal','Explicit user development-only deferral stands; ordinary review, native checks and audits do not certify independent crypto/security/protocol qualification','latent protocol/security defects still require independent release approval'),
('External-client','Native server binding and actual GET/list routes are exercised; usable-key provisioning/authenticated external-client decryption remains a release gate','external client integration or decryption defects can remain undiscovered'),
('Durable descendant','Current online GET/list recomputes inheritance after the atomic move; UC48 must supply durable visibility events and offline revocation before release','offline copies or incremental caches can retain stale inherited access'),
('Future restore','Independent prior operations/deadlines/snapshots remain byte-exact; UC51 must revalidate saved parent/memberships/currenttypedstate and consume old entries before re-trash','a future restore could revive removed scope or reuse a retention deadline'),
('Physical retained','This move preserves existing retained trash and does not claim worker completion; UC53 physical expiry and missing handler behavior remain release work','retained ciphertext may outlast logical expiry until physical handlers ship'),
('Future grant','No grant writer is added; current owner/recipient writers are raced and ordered, while BEFOREUC35 implicitrecipientFK audit remains mandatory','a future grant FK lock could create an account/collection deadlock'),
('Safety of arbitrary','The statement-time guarded root write authorizes one atomic transaction under held locks and supported current writer contracts; later/out-of-band writers need an audit','an unaudited writer could invalidate captured scope or write after the supported boundary'),
('Production-scale','Actual bounded EXPLAIN and controlled current-writer races establish query and lock shape only; production load/depth/latency qualification remains separate','very large subtrees can increase memory/latency/lock contention'),
('Prior UC27','The prior deletion external-active-parent PurgeAt Minor retains its archived disposition; this new move validates changedparents and intentionally skips unchangedparent checks fornoop','prior defensive corruption-consistency gap remains in deletion code'),
('Prior UC25','The misleading GetFolderQuery recordId constructor name remains unchanged and positional HTTP calls remain correct','named callers and maintainers can still be confused'),
('Prior UC23','Prior narrower grant/fault fixtures retain their archived limits; current UC28 post-mutation rollback/native fixtures were independently reviewed and the newly found strict-body defect enters this fix pass','prior fixture coverage limits remain until their own followup'),
('Prior UC22','The prior internal permission-outage500 classification retains its archived disposition; current move maps supported persistence failures503','the earlier endpoint can still return500 for that internal failure'),
('Prior UC21','The archived UC21 finding is four trailing blank spaces in RecordMoveHttpTests line143, not a runtime blank-input defect; preserve that precise outside-diff maintenance disposition','prior whitespace cleanup remains pending; mislabeling it would misstate product risk'),
('Remote exact-head','Executor must freshly verify latestexactHEAD3CI/freshbase/rules0approvals/mergeability/normalmatchheadsquash/nineDoD/projectDone/remoteabsence/testedtree/archiveSHA and4primaryfiles before declaring completion','local tests alone cannot certify remote integration or release gates'),
]
final=[]
for line,(prefix,decision,cost) in zip(lines,judgments):
 assert line.startswith(prefix),(prefix,line)
 final.append(f'Final: Ruling: {line} — {decision} — cost if wrong: {cost}.')
minors=[
'Final: minor (deferred): Shared OpenAPI accessheader/body requiredness, four-output required list and wrapper nullability metadata do not fully describe strict runtime. Correct shared generator/regenerate before external-client release. Cost: generated clients can omit input or mis-model successful/error output; runtime authorization remains correct.',
'Final: minor (deferred): Shared OpenAPI advertises ProblemDetails for actualempty413. Correct shared response metadata before external-client release. Cost: generated clients can try to decode an absent error body; bounded-body rejection remains correct.',
]
s+='\nFinal: severity regrade: one Important verification defect accepted for the ONE TDD fix pass; no demonstrated product runtime bug. parentParentFolderId wrongly caused most38strictbody failures, so prior148GREEN/fresh6558 execution does NOT prove those intended cases. Preserveall38 andadd real valid-request control throughsamebodyfixture, watch400 versus200 RED beforecorrectingonlyfixture fieldnames, thenfocusedHTTP+freshwhole6suite. Two inherited generated-client findings remainMinor by effect for development-only delivery; releasefollowups preserved. ONEreviewcomplete; no secondreview.\n'+'\n'.join(final+minors)+'\n';p.write_text(s)
rulings=[x for x in s.splitlines() if 'Ruling:' in x];assert len(rulings)==32
primary=Path('/root/repositories/cerberus-api');a=primary/'.superpowers/checkpoints/batch-review-decisions-2026-10-08.md';assert final[0] not in a.read_text();a.write_text(a.read_text()+'\n## UC28 final review and dispositions\n\n'+'\n\n'.join(final+minors)+'\n')
p=primary/'.superpowers/checkpoints/open-issues-2026-10-08.json';d=json.loads(p.read_text());x=next(x for x in d['useCases'] if x['id']=='UC-28');x.update({'rulingCount':32,'deferredMinorCount':2,'reviewVerdict':'oneImportantverificationFixRequired','currentTask':'ONE TDD fixture correction pass/validsamefixtureHTTPcontrol RED beforefix/freshfullsuite afterGREEN'});d['currentTask']=d['resumeInstruction']=x['currentTask'];p.write_text(json.dumps(d,indent=2)+'\n')
print('Regraded sole review: one Important test defect accepted; all14declinedexplicit reason+cost,32totalRulings/twoMinors preserved. Fix pass starts now.')
