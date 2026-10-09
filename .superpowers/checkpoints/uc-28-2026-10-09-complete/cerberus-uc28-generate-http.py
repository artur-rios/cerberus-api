from pathlib import Path
import re
w=Path('/tmp/cerberus-uc28-worktree');p=w/'tests/Presentation/ArturRios.Cerberus.WebApi.Tests';t=(p/'FolderTrashHttpTests.cs').read_text();r=(p/'RecordMoveHttpTests.cs').read_text()
def method(src,name):
 a=src.index('    public async Task '+name);a=src.rfind('[Functional',0,a);a=src.rfind('    ',0,a+1) if src[a-4:a]=='    ' else a
 # Normalize indentation; adjacent attributes follow a method closing brace on same line.
 b=src.find('[Functional',src.index('public async Task '+name)+len(name)+22);private=src.find('    private ',src.index('public async Task '+name));b=min(x for x in [b,private] if x>=0)
 block=src[a:b];block=block.rstrip();return '    '+block.lstrip()+'\n'
header=t[:t.index('    [Functional')].replace('FolderTrashHttpTests','FolderMoveHttpTests')
names=['GivenNoncanonicalRoute_WhenTrashing_Then400WithoutMutation','GivenInvalidTransportOrAccess_WhenTrashing_ThenRejectWithoutMutation','GivenRawEmptyAccessHeader_WhenTrashing_Then400','GivenInvalidCurrentIdentity_WhenTrashing_ThenRejectWithoutPersistence','GivenInvalidCurrentActorState_WhenTrashing_ThenNoMutation','GivenHiddenTargetWithBadCipherAndStaleRevision_WhenTrashing_ThenNonrevealing404WithoutMutation','GivenCommittedSharedRelationshipChange_WhenTrashing_ThenCurrentPermissionWins','GivenRelevantMalformedNativeAuthority_WhenTrashing_Then503WithoutAnyContentChange','GivenSelectedProfileBecomesHidden_WhenTrashing_Then403WithoutNullingOrWideningSelection','GivenNativeMemberLeafWriteGrant_WhenTrashingUnsharedParentOrSibling_Then404AndNoChanges','GivenActualNativeRecordOnlyGrant_WhenTrashingContainingFolder_Then404AfterSuccessfulRecordGet','GivenSelectedDirectRecordOnly_WhenTrashingContainingFolder_Then404AfterSuccessfulRecordGet','GivenCorruptVisibleTargetOrUnavailableDatabase_WhenTrashing_Then503WithoutPartialPayload','GivenActualNativeForeignFolderScope_WhenDeletingStaleDamagedCipher_Then403ForEveryRecipientMode','GivenCorruptOverlappingContributor_WhenDeleting_ThenSelectedValidRoute403AndWide503WithoutMutation']
body=''
for name in names:
 b=method(t,name).replace('Trash(','Move(').replace('Delete(','PutParent(').replace('WhenTrashing','WhenMoving').replace('WhenDeleting','WhenMoving')
 b=re.sub(r'new\{expectedRevision=([^}]+)\}',r'new{expectedRevision=\1,parentFolderId=(Guid?)null}',b)
 b=b.replace('c.Request.Method="DELETE"','c.Request.Method="PUT"').replace('c.Request.Path="/api/folders/"+Guid.NewGuid();','c.Request.Path="/api/folders/"+Guid.NewGuid()+"/parent";')
 body+=b+'\n'
b=method(r,'GivenStrictOrForgedMoveBody_WhenMoving_Then400WithoutMutation')
b=b.replace('folderId','parentFolderId').replace('FolderId','ParentFolderId').replace('recordId','folderId').replace('RecordId','FolderId').replace('PutFolder(','PutParent(').replace('/api/records/','/api/folders/')
b=b.replace('[InlineData("actor")]','[InlineData("recordId")][InlineData("cascade")][InlineData("actor")]').replace('var fields=new[]{"actor"','var fields=new[]{"recordId","cascade","actor"')
b=b.replace('kind.StartsWith("folder")','kind is "folderZero" or "folderBad" or "folderUppercase" or "folderCompact" or "folderNumeric"')
body+=b+'\n'
# Existing live PostgreSQL, native Master/PerProfile and real POST folder/record helpers.
helpers=t[t.index('    private async Task<ArturRios.Cerberus.Domain.Records.RecordCreateInput> CreateRecord'):]
old='private static async Task<T> Success<T>(HttpResponseMessage r,int status) where T:class{Assert.Equal((HttpStatusCode)status,r.StatusCode);Assert.True(r.Headers.CacheControl?.NoStore);'
new=old+'if(typeof(T)==typeof(MoveFolderOutput)){using var raw=JsonDocument.Parse(await r.Content.ReadAsStringAsync());Assert.Equal(new[]{"folderId","parentFolderId","revision","serverSequence"},raw.RootElement.GetProperty("data").EnumerateObject().Select(x=>x.Name).Order());}'
assert helpers.count(old)==1;helpers=helpers.replace(old,new)
extra=Path('/tmp/cerberus-uc28-http-new-cases.txt').read_text();h=Path('/tmp/cerberus-uc28-http-move-helpers.txt').read_text()
out=header+body+'\n'+extra+'\n'+h+'\n'+helpers
Path('/tmp/cerberus-uc28-http-draft.cs').write_text(out);print('HTTP draft prepared outside repo:',len(out.splitlines()),'lines; no route/DI installed')
