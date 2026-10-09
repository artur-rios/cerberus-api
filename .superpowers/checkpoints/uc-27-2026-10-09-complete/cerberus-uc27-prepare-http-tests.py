from pathlib import Path
w=Path('/tmp/cerberus-uc27-worktree');folder=(w/'tests/Presentation/ArturRios.Cerberus.WebApi.Tests/FolderUpdateHttpTests.cs').read_text();record=(w/'tests/Presentation/ArturRios.Cerberus.WebApi.Tests/RecordTrashHttpTests.cs').read_text()
def block(s,name):
 at=s.index('    public async Task '+name);start=s.rfind('    [Functional',0,at);end=s.find('\n    [Functional',at);return s[start:end]
parts=[]
for name in ['GivenStrictOrForgedDeleteBody_WhenTrashing_Then400WithoutAnyOperation','GivenNoncanonicalRoute_WhenTrashing_Then400WithoutMutation','GivenInvalidTransportOrAccess_WhenTrashing_ThenRejectWithoutMutation','GivenRawEmptyAccessHeader_WhenTrashing_Then400','GivenInvalidCurrentIdentity_WhenTrashing_ThenRejectWithoutPersistence','GivenInvalidCurrentActorState_WhenTrashing_ThenNoMutation']:
 s=block(record,name).replace('"folderId"','"parentFolderId"').replace('Record','Folder').replace('recordId','folderId').replace('/api/records','/api/folders')
 if name.startswith('GivenStrict'):
  s=s.replace('[InlineData("parentFolderId")]', '[InlineData("parentFolderId")][InlineData("recordId")][InlineData("cascade")]');s=s.replace('"parentFolderId","collectionIds"','"parentFolderId","recordId","cascade","collectionIds"')
 parts.append(s)
for name in ['GivenHiddenTargetWithBadCipherAndStaleRevision_WhenReplacing_ThenNonrevealing404WithoutMutation','GivenCommittedSharedRelationshipChange_WhenReplacing_ThenCurrentPermissionWins','GivenRelevantMalformedNativeAuthority_WhenReplacing_Then503WithoutAnyContentChange','GivenSelectedProfileBecomesHidden_WhenReplacing_Then403WithoutNullingOrWideningSelection','GivenNativeMemberLeafWriteGrant_WhenReplacingUnsharedParentOrSibling_Then404AndNoChanges','GivenActualNativeRecordOnlyGrant_WhenReplacingContainingFolder_Then404AfterSuccessfulRecordGet','GivenSelectedDirectRecordOnly_WhenReplacingContainingFolder_Then404AfterSuccessfulRecordGet','GivenCorruptVisibleTargetOrUnavailableDatabase_WhenReplacing_Then503WithoutPartialPayload']:
 s=block(folder,name).replace('WhenReplacing','WhenTrashing').replace('Update(s,','Trash(s,').replace('Replacement()','1').replace('Replacement(99)','99')
 if name.startswith('GivenCorruptVisible'):
  s=s.replace('[InlineData("envelope")]','');start=s.index('if(kind=="envelope")');end=s.index('if(kind=="sequence")');s=s[:start]+s[end:]
 parts.append(s)
headers=folder.split('[Collection("Registration host")]',1)[0].replace('using ArturRios.Cerberus.Query.Folders;','using ArturRios.Cerberus.Query.Folders;\nusing ArturRios.Cerberus.Query.Records;\nusing ArturRios.Cerberus.Domain.Trash;')
helpers='    private async Task<FolderCreateInput> Create'+folder.split('    private async Task<FolderCreateInput> Create',1)[1]
helpers=helpers.replace('Collections=await db.Collections.AsNoTracking()', 'Operations=await db.TrashOperations.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Entries=await db.TrashEntries.AsNoTracking().Where(x=>db.TrashOperations.Any(o=>o.Id==x.OperationId&&o.AccountId==s.InternalId)).OrderBy(x=>x.Id).ToArrayAsync(),Queue=await db.RetentionWorkItems.AsNoTracking().Where(x=>db.TrashOperations.Any(o=>o.AccountId==s.InternalId&&x.OperationKey=="trash/"+o.PublicId)).OrderBy(x=>x.Id).ToArrayAsync(),Collections=await db.Collections.AsNoTracking()')
createRecord=folder.split('    private async Task<ArturRios.Cerberus.Domain.Records.RecordCreateInput> CreateRecord',1)[1].split('    [FunctionalTheory]',1)[0]
helpers='    private async Task<ArturRios.Cerberus.Domain.Records.RecordCreateInput> CreateRecord'+createRecord+helpers
text=headers+'[Collection("Registration host")]\npublic class FolderTrashHttpTests(RegistrationApiFixture fixture):WebApiTest<Program>(EnvironmentType.Local)\n{\n'+''.join(parts)+'\n'+Path('/tmp/cerberus-uc27-http-core-tests.cs').read_text()+'\n'+helpers
Path('/tmp/cerberus-uc27-http-tests.cs').write_text(text)
assert not (w/'tests/Presentation/ArturRios.Cerberus.WebApi.Tests/FolderTrashHttpTests.cs').exists()
print('Prepared complete native HTTP test-only draft outside worktree; do not install until Task2 completion.')
