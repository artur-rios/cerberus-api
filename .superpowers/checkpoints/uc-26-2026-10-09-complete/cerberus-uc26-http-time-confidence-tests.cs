    [FunctionalTheory][InlineData("older")][InlineData("equal")][InlineData("pre2000")][InlineData("sameEnvelope")]
    public async Task GivenMatchingRevisionAndRepresentableClientTime_WhenReplacing_ThenRevisionWinsAndGetPreservesAcceptedCipher(string kind)
    {
        using var owner=new ProtectionFixture();var s=await Setup(owner);var target=await Create(s);var input=Replacement() with{EditedAt=kind=="older"?target.EditedAt.AddTicks(-11):kind=="pre2000"?new DateTimeOffset(1999,12,31,23,59,59,TimeSpan.Zero).AddTicks(19):target.EditedAt};if(kind=="sameEnvelope")input=input with{Envelope=target.Envelope};
        using(var response=await Update(s,target.FolderId,input))await Changed(s,target,input,await Success<UpdateFolderOutput>(response,200));using var get=await Read(s,"/api/folders/"+target.FolderId);var result=await Success<FolderDetailsOutput>(get,200);Assert.Equal(2,result.Revision);Assert.Equal(Normalize(input.EditedAt),result.EditedAt);Assert.Equal(input.Envelope,result.Envelope);
    }
