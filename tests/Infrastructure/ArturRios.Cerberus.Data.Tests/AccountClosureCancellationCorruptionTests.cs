using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

public partial class AccountClosureCancellationStoreTests
{
    [FunctionalTheory][InlineData(false,"id")][InlineData(false,"revision")][InlineData(false,"overflow")]
    [InlineData(true,"id")][InlineData(true,"revision")][InlineData(true,"overflow")]
    public async Task GivenCorruptRetainedRevokedGrant_WhenCancelling_ThenFailClosedAndKeepPending(bool software,string fault)
    {
        using var key=new ProtectionFixture();using var recipientKey=new ProtectionFixture();var s=await Setup(key);
        long id;
        await using(var db=fixture.CreateContext())
        {
            if(software){var grant=new SoftwareGrant{AccountId=s.InternalId,PublicId=Guid.NewGuid(),HeimdallSoftwarePublicId=Guid.NewGuid(),RecipientKey=[1]};db.SoftwareGrants.Add(grant);await db.SaveChangesAsync();id=grant.Id;}
            else {var recipient=await ProfileSetup.Create(fixture,recipientKey);var items=await AssociationSetup.Items(fixture,s);id=(await AssociationSetup.Grant(fixture,s,key,recipient,recipientKey,items.Collection)).Id;}
        }
        await Close(s);await using var state=fixture.CreateContext();
        var revision=fault=="overflow"?9007199254740992L:0L;
        if(software){if(fault=="id")await state.SoftwareGrants.Where(x=>x.Id==id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.PublicId,Guid.Empty));
            else await state.SoftwareGrants.Where(x=>x.Id==id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.Revision,revision));}
        else {if(fault=="id")await state.CollectionGrants.Where(x=>x.Id==id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.PublicId,Guid.Empty));
            else await state.CollectionGrants.Where(x=>x.Id==id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.Revision,revision));}
        var before=await Snapshot(state,s);var result=await new AccountClosureCancellationStore(fixture).CancelAsync(Request(s),default);
        Assert.Equal("persistence_unavailable",result.Error);Assert.Null(result.Details);Assert.Equal(before,await Snapshot(state,s));
    }
}
