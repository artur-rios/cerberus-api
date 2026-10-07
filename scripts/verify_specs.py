from pathlib import Path
from urllib.parse import unquote
import re, json

root=Path(__file__).resolve().parent.parent
req=root/'docs/requirements'
errors=[]
def check(condition,message):
    if not condition: errors.append(message)
def text(name): return (req/name).read_text(encoding='utf-8')
files=list((root/'docs/initial').glob('*.md'))+list(req.glob('*.md'))+[root/'README.md']
expected={
'Technology Stack Document.md':range(1,9),'Vision Document.md':range(1,11),
'System Requirements Document.md':range(1,10),'Use Case Specification Document.md':range(1,5),
'Development Workflow Document.md':range(1,7),'Testing Specification Document.md':range(1,10),
'Operations & Infrastructure Document.md':range(1,9)}
check(len(list(req.glob('*.md')))==7,'Expected seven formal files')
for name,sections in expected.items():
    content=text(name)
    got=[int(x) for x in re.findall(r'^## (\d+)\.',content,re.M)]
    check(got==list(sections),f'{name}: top-level template sections mismatch {got}')

def slug(heading):
    return re.sub(r'[^\w\- ]','',heading.lower()).replace(' ','-')
for file in files:
    content=file.read_text(encoding='utf-8')
    check('\u00e2\u20ac' not in content and '\u00e2\u2020' not in content, f'{file.name}: invalid UTF-8 transcoding')
    check(not re.search(r'\b(?:TBD|TODO)\b|\{\{|<!--|GUIDANCE|<[^>]+-path>',content),f'{file.name}: placeholder/guidance')
    check(len(re.findall(r'^```',content,re.M))%2==0,f'{file.name}: unbalanced fences')
    check(not re.search(r'[ \t]+$',content,re.M),f'{file.name}: trailing whitespace')
    for dest in re.findall(r'\]\(([^)]+)\)',content):
        if dest.startswith(('http://','https://')): continue
        path,_,anchor=dest.partition('#')
        check(' ' not in path,f'{file.name}: unencoded link space {dest}')
        target=(file.parent/unquote(path)).resolve() if path else file
        check(target.is_file(),f'{file.name}: missing link {dest}')
        if anchor and target.is_file():
            anchors=[slug(h) for h in re.findall(r'^#{1,6} (.+)$',target.read_text(encoding='utf-8'),re.M)]
            check(unquote(anchor) in anchors,f'{file.name}: missing anchor {dest}')
    if file.parent==req and file.name!='Technology Stack Document.md':
        check(not re.search(r'\bnet\d+\.\d+\b|\b\d+\.\d+\.\d+\b|\.NET \d|C# \d',content),f'{file.name}: duplicated technology version')

system=text('System Requirements Document.md')
uc=text('Use Case Specification Document.md')
fr_rows=re.findall(r'^\| (FR-[A-Z]{2}-\d{2}) \| (.+?) \|$',system,re.M)
frdefs={fid for fid,_ in fr_rows}
check(len(fr_rows)==len(frdefs),'Duplicate FR definition')
check(all(stmt.startswith('The system shall ') for _,stmt in fr_rows),'Non-normative FR assertion')
for f in req.glob('*.md'):
    check(set(re.findall(r'FR-[A-Z]{2}-\d{2}',f.read_text(encoding='utf-8')))<=frdefs,f'{f.name}: undefined FR reference')
uc_sections=re.split(r'^### (UC-\d{2}): (.+)$',uc,flags=re.M)
ids=[]; exercised=set()
for i in range(1,len(uc_sections),3):
    uid,name,body=uc_sections[i:i+3]
    body=body.split('## 3. Use Case',1)[0]
    ids.append(uid)
    for key in ['ID','Name','Actors','Description','Preconditions','Postconditions','Requirements']:
        check(f'| **{key}** |' in body,f'{uid}: missing {key}')
    check('**Main Flow**' in body and '**Alternative Flows**' in body,f'{uid}: missing flow table')
    check(len(re.findall(r'^\d+\. ',body,re.M))>=4,f'{uid}: incomplete main flow')
    af=re.findall(r'^\| (AF-\d{2}) \|',body,re.M)
    check(af==[f'AF-{n:02}' for n in range(1,len(af)+1)],f'{uid}: non-sequential AF')
    check(len(af)>=3,f'{uid}: missing exception coverage')
    row=re.search(r'^\| \*\*Requirements\*\* \| (.+?) \|$',body,re.M)
    if row: exercised.update(re.findall(r'FR-[A-Z]{2}-\d{2}',row[1]))
check(ids==[f'UC-{i:02}' for i in range(1,56)],'UC inventory is not UC-01 through UC-55')
check(frdefs==exercised,f'Unexercised requirements: {sorted(frdefs-exercised)}')
features=set(re.findall(r'^\| (F-\d{2}) \|',text('Vision Document.md'),re.M))
featuremap=set(re.findall(r'^\| (F-\d{2}) ',system,re.M))
check(features==featuremap and len(features)==12,'Feature traceability mismatch')
brdefs=set(re.findall(r'^\| (BR-\d{2}) \|',(root/'docs/initial/Business Rules.md').read_text(encoding='utf-8'),re.M))
brrows=re.findall(r'^\| (BR-\d{2}) \| (.*?) \|$',system,re.M)
check(brdefs=={bid for bid,_ in brrows},'BR traceability coverage mismatch')
check(all(re.search(r'FR-[A-Z]{2}-\d{2}',refs) for _,refs in brrows),'Business rule lacks functional requirement')
check(all(set(re.findall(r'FR-[A-Z]{2}-\d{2}',refs))<=frdefs for _,refs in brrows),'Business rule cites missing requirement')
readme=(root/'README.md').read_text(encoding='utf-8')
backlog=readme.split('## Backlog',1)[1].split('## Contributing',1)[0]
backlog_ids=re.findall(r'^\| [^|]+ \| (UC-\d{2}) ',backlog,re.M)
check(sorted(backlog_ids)==ids,'Each UC must appear exactly once in README backlog')
check(backlog.count('Project scaffold and initial infrastructure')==1,'Expected one foundation issue')
check(len(backlog_ids)+1==56,'Issue count != UC count plus one')
roadmap=readme.split('## Roadmap',1)[1].split('## Backlog',1)[0]
counts=[int(n) for n in re.findall(r'\| (\d+) \| (?:planned|\d+ / \d+ closed) \|',roadmap)]
check(len(counts)==6 and sum(counts)==56, f'Invalid milestone issue counts: {counts}')
initial=(root/'docs/initial/Workflow.md').read_text(encoding='utf-8')
formal=text('Development Workflow Document.md')
initial_dod=initial.split('## Definition of Done',1)[1].split('Reference convention:',1)[0].strip()
formal_dod=formal.split('## 5. Definition of Done',1)[1].split('---',1)[0].strip()
check(initial_dod==formal_dod,'Workflow Definition of Done differs')
for token in ['feature/uc-##-use-case-name','Todo','In Progress','Testing','Done']:
    check(token in initial and token in formal,f'Workflow missing {token}')
check('24 hours' in system and 'disable periodic offline renewal' in system,'Offline policy steering omitted')
check('No numerical latency, throughput or availability thresholds' in system,'Approved unset targets omitted')
check('explicitly deferred' in system,'Protocol review decision missing')
if errors:
    print('\n'.join('FAIL '+e for e in errors)); raise SystemExit(1)
print(f'PASS: {len(files)} Markdown files; 7 template structures; links/anchors; no placeholders or duplicated framework versions; {len(frdefs)} FRs fully covered by 55 UCs; 12 features; 29 BRs; workflow parity; 6 milestones and 56 backlog entries.')
