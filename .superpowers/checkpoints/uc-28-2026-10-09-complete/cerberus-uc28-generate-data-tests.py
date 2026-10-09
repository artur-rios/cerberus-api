from pathlib import Path
import re,json
w=Path('/tmp/cerberus-uc28-worktree');r=(w/'tests/Infrastructure/ArturRios.Cerberus.Data.Tests/RecordMoveStoreTests.cs').read_text();t=(w/'tests/Infrastructure/ArturRios.Cerberus.Data.Tests/FolderTrashStoreTests.cs').read_text()
def method(src,name):
 m=re.search(r'    public async Task '+name+r'\(',src);assert m,name
 start=src.rfind('    [Functional',0,m.start());brace=src.index('{',m.end());depth=0
 # Use the next top-level attribute/helper as boundary; methods have no top-level helper before their closing brace.
 nexts=[p for p in [src.find('\n    [Functional',brace),src.find('\n    private ',brace)] if p!=-1];end=min(nexts)
 return src[start:end].strip('\n')+'\n'
def adapted(src):
 for a,b in [('.FolderId','.ParentFolderId'),('.RecordId','.FolderId'),('RecordMove','FolderMove'),('await Record(','await Folder('),('db.Records','db.Folders'),('RecordReadStore','FolderReadStore')]:src=src.replace(a,b)
 # Precise typed literals, not C# record declarations.
 src=src.replace('ResourceKind="record"','ResourceKind="folder"').replace('new("record",target.PublicId','new("folder",target.PublicId')
 return src
names=['GivenInvalidCurrentAuthority_WhenMoving_ThenFailClosed','GivenInvalidInternalRequest_WhenMoving_ThenRejectWithoutWrite','GivenMissingHiddenOrCyclicDestination_WhenMovingWithStaleRevision_ThenCurrentDestinationStateWins','GivenCorruptOrConflictingVisibleTarget_WhenMoving_ThenPreserveEveryRow','GivenInvalidChangedImmediateParentMetadata_WhenMoving_ThenNoPartialRecordOrFolderUpdate','GivenSameParentAtMaximumRevision_WhenNoopMoving_ThenAdvanceOnlyRecordAndRetryOldRevisionConflicts','GivenMalformedRequiredResultingNativeEnvelope_WhenMovingIntoCollectionFolder_Then503NoMutation','GivenMalformedOldOnlyNativeRoute_WhenMovingOutOfIt_ThenRetainNoInvalidResultingEnvelopeAndRemoveAccess','GivenCanceledCallerOrUnavailableFactory_WhenMoving_ThenPropagateCancellationOrSafe503','GivenConcurrentMovesAndLostSuccessRetry_WhenReplacingParent_ThenOneStructuralChangeAndNoDuplicateBumps','GivenActualRecipientProtectionRotationAfterNativeValidation_WhenMoving_ThenStaleEvidenceConflictsAndFreshRetrySucceeds','GivenActualOwnerRotationWaitsBehindMove_WhenMoveCommits_ThenStaleInventoryConflictsWithoutConsumingProof','GivenActualOwnerRotationBeforeOrAfterMove_WhenReloading_ThenRetainCurrentParentAndRespectEpochRevisionAndHandle','GivenSequenceExhaustionOrRegressionAtTargetOrParent_WhenMoving_ThenRollbackAllStructuralChanges','GivenInvalidSelectedProfile_WhenMoving_Then403WithoutBroadening','GivenUnsafeImmediateParentSequence_WhenMoving_Then503WithNoPartialChanges','GivenExpiryAfterAllLocksBeforeFinalStatement_WhenMoving_Then403WithoutCommit','GivenProviderInternalCancellationBeforeFinalStatement_WhenMoving_Then503WithoutMutation']
body='\n'.join(adapted(method(r,n)) for n in names)
# Folder-authority tests reuse the genuine native CF fixtures, preserving successful reads before denials.
for n in ['GivenActualNativeVisibleForeignFolder_WhenDeletingStaleDamagedTarget_Then403AndNoGraphMutation','GivenActualNativeRecordOnlyMembership_WhenDeletingItsContainingFolder_Then404AfterSuccessfulRecordRead','GivenSelectedDirectRecordWithoutFolderMembership_WhenDeletingContainer_Then404AfterSuccessfulRecordRead','GivenHiddenTargetWithBadNativeAndStaleRevision_WhenTrashing_Then404BeforeDisclosure','GivenMalformedContributingNativeAuthority_WhenNonownerRequestsTrash_Then503WithoutMutation','GivenOverlappingNativeAuthorityWithMalformedContributor_WhenNonownerDeletes_ThenWholeDenialFailsClosed','GivenNativeMemberLeafGrant_WhenDeletingPrivateParentOrSibling_Then404WithoutMutation']:
 b=method(t,n).replace('Trash(s,target,99)','Move(s,target,Guid.NewGuid(),99)').replace('Trash(s,','Move(s,').replace('TrashAsync','MoveAsync').replace('WhenDeleting','WhenMoving').replace('WhenTrashing','WhenMoving').replace('WhenNonownerDeletes','WhenNonownerMoves').replace('RequestsTrash','RequestsMove')
 b=b.replace('Move(s,a.Folder,99)','Move(s,a.Folder,null,99)').replace('Move(s,folder,99)','Move(s,folder,null,99)').replace('Move(s,kind=="parent"?a.Folder:sibling,99)','Move(s,kind=="parent"?a.Folder:sibling,null,99)').replace('Move(s,a.Folder)','Move(s,a.Folder,null)')
 b=b.replace('target.PublicId,99),default)','target.PublicId,99,null),default)').replace('Guid.NewGuid():target.PublicId,99),default)','Guid.NewGuid():target.PublicId,99,null),default)')
 body+='\n'+b
helpers=r[r.index('    private async Task<string> Snapshot('):]
# Keep the complete graph snapshot and real folder/record/profile/association helpers, replacing only the target Row helper.
helpers=helpers.replace('private async Task<VaultRecord> Row(long id){await using var db=fixture.CreateContext();return await db.Records.AsNoTracking().SingleAsync(x=>x.Id==id);}','private async Task<VaultFolder> Row(long id){await using var db=fixture.CreateContext();return await db.Folders.AsNoTracking().SingleAsync(x=>x.Id==id);}')
header=t[:t.index('    [Functional')].replace('FolderTrashStoreTests','FolderMoveStoreTests')
out=header+body+'\n'+Path('/tmp/cerberus-uc28-data-new-cases.txt').read_text()+'\n'+Path('/tmp/cerberus-uc28-data-move-helpers.txt').read_text()+'\n'+helpers
p=w/'tests/Infrastructure/ArturRios.Cerberus.Data.Tests/FolderMoveStoreTests.cs';assert not p.exists();p.write_text(out)
print('Installed tests before FolderMoveStore exists:',len(out.splitlines()),'lines')
