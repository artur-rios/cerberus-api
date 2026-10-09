using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Protection;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class VaultResourceRotationTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenCompleteTypedInventoryIncludingTrash_WhenRotating_ThenReplaceOwnedContentAndActiveGrantsAtomically()
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var scoped=new ProtectionFixture();
        var s=await Setup(owner,recipient,scoped);var c=Change(s,owner,recipient,scoped);var request=await Request(s,owner,c);
        var untouched=await ForeignAndRevoked(s);var before=await ResourceRows(s);
        Assert.Null((await Store().ChangeAsync(request,default)).Error);
        await using var db=fixture.CreateContext();
        foreach(var row in await ResourceRows(s))
        {
            var old=Assert.Single(before,x=>x.Kind==row.Kind && x.Id==row.Id);var replacement=Assert.Single(c.ContentReplacements,x=>x.ResourceKind==row.Kind && x.ResourceId==row.Id);
            Assert.Equal(old.Revision+1,row.Revision);Assert.True(row.Sequence>old.Sequence);Assert.Equal(old.EditedAt,row.EditedAt);Assert.Equal(old.Account,row.Account);Assert.Equal(old.DeletedAt,row.DeletedAt);Assert.Equal(old.PurgeAt,row.PurgeAt);
            Assert.Equal(replacement.Envelope,JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope,ProtectionFixture.Json));
        }
        foreach(var original in s.Grants)
        {
            var grant=await db.CollectionGrants.SingleAsync(x=>x.Id==original.Id);var collection=await db.Collections.SingleAsync(x=>x.Id==grant.CollectionId);var replacement=Assert.Single(c.GrantReplacements,x=>x.GrantId==grant.PublicId);
            Assert.Equal(2,grant.Revision);Assert.Equal(2,collection.KeyEpoch);Assert.Equal(original.Access,grant.Access);Assert.Equal(original.State,grant.State);Assert.Equal(original.RecipientAccountId,grant.RecipientAccountId);
            var wrapper=JsonSerializer.Deserialize<RecipientEnvelope>(grant.RecipientKeyEnvelope,ProtectionFixture.Json)!;Assert.Equal(replacement.KeyEnvelope,wrapper);Assert.True(wrapper.Verify(s.Owner.AccountId,"collection",collection.PublicId,2,grant.PublicId,2,s.Recipient.Actor,recipient.Material.RecipientKey,owner.Material.AuthorKey));
        }
        Assert.Equal(untouched,await ForeignAndRevoked(s));Assert.True((await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==request.ChallengeId)).Consumed);Assert.Equal(2,(await db.Accounts.SingleAsync(x=>x.Id==s.Owner.InternalId)).RevocationGeneration);
    }

    [FunctionalTheory]
    [InlineData("missingRecord","validation_failed")][InlineData("missingFolder","validation_failed")][InlineData("missingCollection","validation_failed")]
    [InlineData("extraResource","validation_failed")][InlineData("duplicateResource","validation_failed")][InlineData("foreignResource","validation_failed")]
    [InlineData("missingGrant","validation_failed")][InlineData("extraGrant","validation_failed")][InlineData("duplicateGrant","validation_failed")]
    [InlineData("receivedGrant","validation_failed")][InlineData("revokedGrant","validation_failed")]
    [InlineData("staleResource","revision_conflict")][InlineData("staleGrant","revision_conflict")]
    [InlineData("accountSalt","validation_failed")][InlineData("accountNonce","validation_failed")][InlineData("resourceAheadSequence","persistence_unavailable")][InlineData("profileAheadSequence","persistence_unavailable")]
    [InlineData("epoch","validation_failed")][InlineData("salt","validation_failed")][InlineData("nonce","validation_failed")]
    [InlineData("grantEpoch","validation_failed")][InlineData("grantRevision","validation_failed")][InlineData("grantIdentity","validation_failed")]
    [InlineData("grantRecipient","validation_failed")][InlineData("grantAuthor","validation_failed")][InlineData("grantResource","validation_failed")][InlineData("grantId","validation_failed")]
    [InlineData("grantCiphertextReuse","validation_failed")][InlineData("grantEncReuse","validation_failed")]
    [InlineData("corruptRecord","persistence_unavailable")][InlineData("corruptFolder","persistence_unavailable")][InlineData("corruptCollection","persistence_unavailable")]
    [InlineData("collectionEpoch","persistence_unavailable")][InlineData("corruptGrant","persistence_unavailable")][InlineData("corruptRecipientPins","persistence_unavailable")]
    [InlineData("zeroResourceRevision","persistence_unavailable")][InlineData("zeroResourceSequence","persistence_unavailable")]
    [InlineData("exhaustedResource","revision_conflict")][InlineData("exhaustedGrant","revision_conflict")]
    public async Task GivenIncompleteOrUnboundInventory_WhenRotating_ThenPreserveAllRowsAndUnconsumedProof(string kind,string error)
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var scoped=new ProtectionFixture();using var other=new ProtectionFixture();
        var s=await Setup(owner,recipient,scoped);var c=Change(s,owner,recipient,scoped);var replacements=c.ContentReplacements.ToList();var grants=c.GrantReplacements.ToList();var index=replacements.FindIndex(x=>x.ResourceKind=="record");var item=replacements[index];var first=s.Items[0];var g=grants[0];var key=g.KeyEnvelope;var old=JsonSerializer.Deserialize<RecipientEnvelope>(s.Grants[0].RecipientKeyEnvelope,ProtectionFixture.Json)!;
        if(kind.StartsWith("missing") && kind!="missingGrant")replacements.RemoveAt(replacements.FindIndex(x=>x.ResourceKind==kind[7..].ToLowerInvariant()));
        if(kind=="extraResource")replacements.Add(item with {ResourceId=Guid.NewGuid()});
        if(kind=="duplicateResource")replacements.Add(item);
        if(kind=="foreignResource")replacements[index]=item with {ResourceId=s.Foreign.Record.PublicId};
        if(kind=="staleResource")replacements[index]=item with {ExpectedRevision=2};
        if(kind is "accountSalt" or "accountNonce"){var previous=JsonSerializer.Deserialize<EncryptedEnvelope>(s.Owner.AccountEnvelope,ProtectionFixture.Json)!;replacements[0]=replacements[0] with {Envelope=kind=="accountSalt"?replacements[0].Envelope with {KeySalt=previous.KeySalt}:replacements[0].Envelope with {Nonce=previous.Nonce}};}
        if(kind=="epoch")replacements[index]=item with {Envelope=item.Envelope with {KeyEpoch=3}};
        if(kind=="salt")replacements[index]=item with {Envelope=item.Envelope with {KeySalt=JsonSerializer.Deserialize<EncryptedEnvelope>(first.Record.Envelope,ProtectionFixture.Json)!.KeySalt}};
        if(kind=="nonce")replacements[index]=item with {Envelope=item.Envelope with {Nonce=JsonSerializer.Deserialize<EncryptedEnvelope>(first.Record.Envelope,ProtectionFixture.Json)!.Nonce}};
        if(kind=="missingGrant")grants.RemoveAt(0);
        if(kind=="extraGrant")grants.Add(g with {GrantId=Guid.NewGuid()});
        if(kind=="duplicateGrant")grants.Add(g);
        if(kind=="receivedGrant")grants[0]=g with {GrantId=s.Received.PublicId};
        if(kind=="revokedGrant")grants[0]=g with {GrantId=s.Revoked.PublicId};
        if(kind=="staleGrant")grants[0]=g with {ExpectedRevision=2};
        key=kind switch {
            "grantEpoch"=>key with {KeyEpoch=3},"grantRevision"=>key with {GrantRevision=3},"grantIdentity"=>key with {RecipientIdentityId=Guid.NewGuid()},
            "grantRecipient"=>owner.WrapGrant(s.Owner.AccountId,first.Collection.PublicId,g.GrantId,s.Recipient.Actor,other.Material.RecipientKey,2,2),
            "grantAuthor"=>other.WrapGrant(s.Owner.AccountId,first.Collection.PublicId,g.GrantId,s.Recipient.Actor,recipient.Material.RecipientKey,2,2),
            "grantResource"=>owner.WrapGrant(s.Owner.AccountId,Guid.NewGuid(),g.GrantId,s.Recipient.Actor,recipient.Material.RecipientKey,2,2),
            "grantId"=>owner.WrapGrant(s.Owner.AccountId,first.Collection.PublicId,Guid.NewGuid(),s.Recipient.Actor,recipient.Material.RecipientKey,2,2),
            "grantCiphertextReuse"=>key with {Ciphertext=old.Ciphertext},"grantEncReuse"=>key with {Enc=old.Enc},_=>key};
        if(kind is "grantCiphertextReuse" or "grantEncReuse")key=owner.SignGrant(key,s.Owner.AccountId,first.Collection.PublicId);
        if(kind.StartsWith("grant"))grants[0]=g with {KeyEnvelope=key};
        await using(var db=fixture.CreateContext())
        {
            if(kind=="corruptRecord")await db.Records.Where(x=>x.Id==first.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Envelope,new byte[]{1}));
            if(kind=="corruptFolder")await db.Folders.Where(x=>x.Id==first.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Envelope,new byte[]{1}));
            if(kind=="corruptCollection")await db.Collections.Where(x=>x.Id==first.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Envelope,new byte[]{1}));
            if(kind=="collectionEpoch")await db.Collections.Where(x=>x.Id==first.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.KeyEpoch,2));
            if(kind=="corruptGrant")await db.CollectionGrants.Where(x=>x.Id==s.Grants[0].Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.RecipientKeyEnvelope,new byte[]{1}));
            if(kind=="corruptRecipientPins")await db.VaultProtections.Where(x=>x.AccountId==s.Recipient.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Material,new byte[]{1}));
            if(kind is "zeroResourceRevision" or "exhaustedResource")await db.Records.Where(x=>x.Id==first.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Revision,kind=="zeroResourceRevision"?0:ProtocolBinary.MaxInteger));
            if(kind=="resourceAheadSequence")await db.Records.Where(x=>x.Id==first.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ServerSequence,ProtocolBinary.MaxInteger-1));
            if(kind=="profileAheadSequence")await db.Profiles.Where(x=>x.PublicId==s.Profile.ProfileId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ServerSequence,ProtocolBinary.MaxInteger-1));
            if(kind=="zeroResourceSequence")await db.Records.Where(x=>x.Id==first.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ServerSequence,0));
            if(kind=="exhaustedGrant")await db.CollectionGrants.Where(x=>x.Id==s.Grants[0].Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Revision,ProtocolBinary.MaxInteger));
        }
        c=c with {ContentReplacements=replacements.ToArray(),GrantReplacements=grants.ToArray()};var request=await Request(s,owner,c);var before=await Snapshot(s);
        Assert.Equal(error,(await Store().ChangeAsync(request,default)).Error);Assert.Equal(before,await Snapshot(s));await Unconsumed(request);
    }
    [FunctionalFact]
    public async Task GivenRewrapWithOwnedAndReceivedContent_WhenChanging_ThenRetainEveryContentGrantAndLinkByte()
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(owner,recipient,scoped);var rows=JsonSerializer.Serialize(await ResourceRows(s));var others=await ForeignAndRevoked(s);await using var db=fixture.CreateContext();var grants=JsonSerializer.Serialize(await db.CollectionGrants.AsNoTracking().Where(x=>s.Grants.Select(g=>g.Id).Contains(x.Id)).OrderBy(x=>x.Id).ToArrayAsync());
        var request=await Request(s,owner,new(s.Owner.AccountId,1,1,"rewrap",owner.Rewrap(),[]));Assert.Null((await Store().ChangeAsync(request,default)).Error);Assert.Equal(rows,JsonSerializer.Serialize(await ResourceRows(s)));Assert.Equal(others,await ForeignAndRevoked(s));Assert.Equal(grants,JsonSerializer.Serialize(await db.CollectionGrants.AsNoTracking().Where(x=>s.Grants.Select(g=>g.Id).Contains(x.Id)).OrderBy(x=>x.Id).ToArrayAsync()));
    }
    [FunctionalFact]
    public async Task GivenFailureAfterProofConsumption_WhenRotating_ThenRollbackAllRootsAndGrantsAndAllowProofRetry()
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(owner,recipient,scoped);var request=await Request(s,owner,Change(s,owner,recipient,scoped));var before=await Snapshot(s);var failed=await new VaultProtectionChangeStore(new ProfileSetup.Factory(fixture,new RejectWrite())).ChangeAsync(request,default);Assert.Equal("persistence_unavailable",failed.Error);Assert.Equal(before,await Snapshot(s));await Unconsumed(request);Assert.Null((await Store().ChangeAsync(request,default)).Error);
    }
    [FunctionalFact]
    public async Task GivenSignedBodyDifferentFromParsedGrantManifest_WhenRotating_ThenRejectWithoutConsuming()
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(owner,recipient,scoped);var c=Change(s,owner,recipient,scoped);var request=await Request(s,owner,c);request=request with {Change=c with {GrantReplacements=c.GrantReplacements.Select(g=>g with {KeyEnvelope=owner.WrapGrant(s.Owner.AccountId,s.Items.Single(i=>i.Collection.Id==s.Grants.Single(x=>x.PublicId==g.GrantId).CollectionId).Collection.PublicId,g.GrantId,s.Recipient.Actor,recipient.Material.RecipientKey,2,2)}).ToArray()}};var before=await Snapshot(s);Assert.Equal("vault_proof_rejected",(await Store().ChangeAsync(request,default)).Error);Assert.Equal(before,await Snapshot(s));await Unconsumed(request);
    }
    [FunctionalTheory][InlineData("collection")][InlineData("grant")]
    public async Task GivenAccessExpiresWhileWaitingForSharedRows_WhenRotating_ThenNoWritesOrProofConsumption(string kind)
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(owner,recipient,scoped);var request=await Request(s,owner,Change(s,owner,recipient,scoped));
        var deadline=DateTimeOffset.UtcNow.AddSeconds(2);await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Owner.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));
        await using var holder=fixture.CreateContext();await using var tx=await holder.Database.BeginTransactionAsync();
        if(kind=="collection")await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.collection WHERE id={s.Items[0].Collection.Id} FOR UPDATE");
        else await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.collection_grant WHERE id={s.Grants[0].Id} FOR UPDATE");
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var before=await Snapshot(s);var pending=new VaultProtectionChangeStore(new ProfileSetup.Factory(fixture,new Signal(reached,kind))).ChangeAsync(request,default);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);var remaining=deadline-DateTimeOffset.UtcNow;if(remaining>TimeSpan.Zero)await Task.Delay(remaining+TimeSpan.FromMilliseconds(100));await tx.CommitAsync();Assert.Equal("vault_access_denied",(await pending).Error);Assert.Equal(before,await Snapshot(s));await Unconsumed(request);
    }
    [FunctionalFact]
    public async Task GivenGrantRevokedDuringLockWait_WhenRotating_ThenRejectObsoleteManifestWithoutRegranting()
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(owner,recipient,scoped);var request=await Request(s,owner,Change(s,owner,recipient,scoped));
        await using var holder=fixture.CreateContext();await using var tx=await holder.Database.BeginTransactionAsync();await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.collection_grant WHERE id={s.Grants[0].Id} FOR UPDATE");var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var pending=new VaultProtectionChangeStore(new ProfileSetup.Factory(fixture,new Signal(reached,"grant"))).ChangeAsync(request,default);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);await holder.CollectionGrants.Where(x=>x.Id==s.Grants[0].Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.State,CollectionGrantState.Revoked));await tx.CommitAsync();var before=await Snapshot(s);Assert.Equal("validation_failed",(await pending).Error);Assert.Equal(before,await Snapshot(s));await Unconsumed(request);
    }
    private sealed record Scenario(ProfileSetup.State Owner,ProfileSetup.State Recipient,AssociationSetup.ResourceSet[] Items,AssociationSetup.ResourceSet Foreign,ProfileCreateInput Profile,CollectionGrant[] Grants,CollectionGrant Revoked,CollectionGrant Received);
    private async Task<Scenario> Setup(ProtectionFixture owner,ProtectionFixture recipient,ProtectionFixture scoped)
    {
        var state=await ProfileSetup.Create(fixture,owner);var other=await ProfileSetup.Create(fixture,recipient);var removed=await ProfileSetup.Create(fixture,recipient);var items=new[]{await AssociationSetup.Items(fixture,state,Guid.NewGuid()),await AssociationSetup.Items(fixture,state,Guid.NewGuid())};var foreign=await AssociationSetup.Items(fixture,other);var profile=ProfileSetup.Input(state,owner,scoped,"PerProfile");Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(state.Actor,state.Verifier,profile),default)).Error);var grants=new[]{await AssociationSetup.Grant(fixture,state,owner,other,recipient,items[0].Collection),await AssociationSetup.Grant(fixture,state,owner,other,recipient,items[1].Collection,CollectionGrantAccess.ReadWrite)};var revoked=await AssociationSetup.Grant(fixture,state,owner,removed,recipient,items[0].Collection);var received=await AssociationSetup.Grant(fixture,other,recipient,state,owner,foreign.Collection);
        await using var db=fixture.CreateContext();var deleted=DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());await db.Records.Where(x=>x.Id==items[1].Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,deleted).SetProperty(v=>v.PurgeAt,deleted.AddDays(30)));await db.Folders.Where(x=>x.Id==items[1].Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,deleted).SetProperty(v=>v.PurgeAt,deleted.AddDays(30)));await db.Collections.Where(x=>x.Id==items[1].Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,deleted).SetProperty(v=>v.PurgeAt,deleted.AddDays(30)));await db.CollectionGrants.Where(x=>x.Id==revoked.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.State,CollectionGrantState.Revoked));return new(state,other,items,foreign,profile,grants,revoked,received);
    }
    private static ProtectionChange Change(Scenario s,ProtectionFixture owner,ProtectionFixture recipient,ProtectionFixture scoped)
    {
        var content=new List<ContentReplacement>{new("account",s.Owner.AccountId,1,ProfileSetup.Envelope(2)),new("profile",s.Profile.ProfileId,1,ProfileSetup.Envelope(2),s.Profile.KeyWrappers with {MasterKeyWrapper=owner.Wrap(s.Owner.AccountId,"profile",s.Profile.ProfileId,s.Owner.Actor,2,2),PasswordWrapper=scoped.Rewrap().PasswordWrapper})};foreach(var item in s.Items){content.Add(new("record",item.Record.PublicId,1,ProfileSetup.Envelope(2)));content.Add(new("folder",item.Folder.PublicId,1,ProfileSetup.Envelope(2)));content.Add(new("collection",item.Collection.PublicId,1,ProfileSetup.Envelope(2)));}return new(s.Owner.AccountId,1,1,"rotate-content",owner.Rewrap(),content.ToArray()){GrantReplacements=s.Grants.Select(g=>new CollectionGrantReplacement(g.PublicId,1,owner.WrapGrant(s.Owner.AccountId,s.Items.Single(i=>i.Collection.Id==g.CollectionId).Collection.PublicId,g.PublicId,s.Recipient.Actor,recipient.Material.RecipientKey,2,2))).ToArray()};
    }
    private async Task<ProtectionChangeRequest> Request(Scenario s,ProtectionFixture owner,ProtectionChange c)
    {var raw=JsonSerializer.SerializeToUtf8Bytes(c,ProtectionFixture.Json);var result=await new VaultProtectionStore(fixture).ChallengeAsync(s.Owner.Actor,"change-protection",ProtocolBinary.Encode(SHA256.HashData(raw)),default);Assert.Null(result.Error);return new(s.Owner.Actor,s.Owner.Verifier,result.Data!.ChallengeId,owner.Sign(result.Data),raw,c);}
    private VaultProtectionChangeStore Store()=>new(fixture);
    private async Task Unconsumed(ProtectionChangeRequest r){await using var db=fixture.CreateContext();Assert.False((await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==r.ChallengeId)).Consumed);}
    private sealed record Row(string Kind,Guid Id,long Account,long Revision,long Sequence,DateTimeOffset EditedAt,DateTimeOffset? DeletedAt,DateTimeOffset? PurgeAt,byte[] Envelope);
    private async Task<Row[]> ResourceRows(Scenario s)
    {await using var db=fixture.CreateContext();var records=await db.Records.AsNoTracking().Where(x=>x.AccountId==s.Owner.InternalId).OrderBy(x=>x.Id).Select(x=>new Row("record",x.PublicId,x.AccountId,x.Revision,x.ServerSequence,x.EditedAt,x.DeletedAt,x.PurgeAt,x.Envelope)).ToArrayAsync();var folders=await db.Folders.AsNoTracking().Where(x=>x.AccountId==s.Owner.InternalId).OrderBy(x=>x.Id).Select(x=>new Row("folder",x.PublicId,x.AccountId,x.Revision,x.ServerSequence,x.EditedAt,x.DeletedAt,x.PurgeAt,x.Envelope)).ToArrayAsync();var collections=await db.Collections.AsNoTracking().Where(x=>x.AccountId==s.Owner.InternalId).OrderBy(x=>x.Id).Select(x=>new Row("collection",x.PublicId,x.AccountId,x.Revision,x.ServerSequence,x.EditedAt,x.DeletedAt,x.PurgeAt,x.Envelope)).ToArrayAsync();return [..records,..folders,..collections];}
    private async Task<string> ForeignAndRevoked(Scenario s){await using var db=fixture.CreateContext();return JsonSerializer.Serialize(new{Records=await db.Records.AsNoTracking().Where(x=>x.AccountId==s.Recipient.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Folders=await db.Folders.AsNoTracking().Where(x=>x.AccountId==s.Recipient.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Collections=await db.Collections.AsNoTracking().Where(x=>x.AccountId==s.Recipient.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Grants=await db.CollectionGrants.AsNoTracking().Where(x=>x.Id==s.Received.Id || x.Id==s.Revoked.Id).OrderBy(x=>x.Id).ToArrayAsync()});}
    private async Task<string> Snapshot(Scenario s){await using var db=fixture.CreateContext();return JsonSerializer.Serialize(new{Account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.Owner.InternalId),Protection=await db.VaultProtections.AsNoTracking().SingleAsync(x=>x.AccountId==s.Owner.InternalId),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.Owner.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Rows=await ResourceRows(s),Grants=await db.CollectionGrants.AsNoTracking().Where(x=>s.Grants.Select(g=>g.Id).Contains(x.Id)).OrderBy(x=>x.Id).ToArrayAsync(),Others=await ForeignAndRevoked(s)});}
    private sealed class Signal(TaskCompletionSource reached,string kind):DbCommandInterceptor
    {public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default){if(c.CommandText.Contains("FOR UPDATE") && c.CommandText.Contains(kind=="grant"?"cerberus.collection_grant":"cerberus.collection "))reached.TrySetResult();return ValueTask.FromResult(r);}}
    private sealed class RejectWrite:DbCommandInterceptor
    {public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default){if(c.CommandText.Contains("UPDATE cerberus.vault_protection"))throw new TimeoutException("synthetic final write failure");return ValueTask.FromResult(r);}}
}
