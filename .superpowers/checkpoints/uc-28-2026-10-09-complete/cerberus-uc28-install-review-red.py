from pathlib import Path
w=Path('/tmp/cerberus-uc28-worktree');p=w/'tests/Presentation/ArturRios.Cerberus.WebApi.Tests/FolderMoveHttpTests.cs';s=p.read_text();assert s.count('parentParentFolderId')==8,s.count('parentParentFolderId')
Path('/tmp/cerberus-uc28-strict-reviewed-fixture.cs').write_text(s)
start=s.index('        using var owner=new ProtectionFixture();var s=await Setup(owner);var target=await Create(s);var body=',s.index('public async Task GivenStrictOrForgedMoveBody'))
end=s.index('\n    }',start)
old=s[start:end];prefix='        using var owner=new ProtectionFixture();var s=await Setup(owner);var target=await Create(s);';assert old.startswith(prefix)
fixture,rest=old[len(prefix):].split('\n        var raw=',1);raw,tail=rest.split(';var before=await Snapshot(s);',1)
assert raw.startswith('kind switch')
s=s[:start]+prefix+'var raw=StrictBody(kind);var before=await Snapshot(s);'+tail+s[end:]
insert='''    [FunctionalFact]
    public async Task GivenUnmodifiedStrictBodyFixture_WhenMoving_ThenAcceptValidBodyAndReturnExactMetadata()
    {
        using var owner=new ProtectionFixture();var s=await Setup(owner);var target=await Create(s);var original=await FolderRow(target.FolderId);var stable=await Stable(s,original.Id);
        using var request=PutParent(s,"/api/folders/"+target.FolderId,Encoding.UTF8.GetBytes(StrictBody("valid")));
        using var response=await Gateway.Client.SendAsync(request);
        await Moved(original,null,await Success<MoveFolderOutput>(response,200));
        Assert.Equal(stable,await Stable(s,original.Id));
    }

    private static string StrictBody(string kind)
    {
        '''+fixture+'\n        return '+raw+''';
    }

'''
pos=s.index('    [FunctionalTheory]',s.index('public async Task GivenStrictOrForgedMoveBody'));s=s[:pos]+insert+s[pos:]
assert s.count('parentParentFolderId')==8
p.write_text(s)
ledger=w/'.superpowers/sdd/2026-10-09-uc-28-move-folder/progress.md';ledger.write_text(ledger.read_text()+'\nFinal fix pass: extracted existing strictbody fixture unchanged into StrictBody(kind), retaining all38originalnegative cases and all8misspelled occurrences; added real native unmodifiedfixture control expecting200/exactfourmetadata/root-onlynoop +allothergraphstable. RED runs BEFORE correctingtypo; product files unchanged.\n')
print('Installed real valid-request control with reviewed wrong fixture unchanged; runREDnext.')
