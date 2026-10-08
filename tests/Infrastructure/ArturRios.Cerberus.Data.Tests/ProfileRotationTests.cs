using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Protection;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class ProfileRotationTests(PostgresFixture fixture)
{
    [FunctionalTheory][InlineData("Master",false)][InlineData("PerProfile",true)]
    public async Task GivenCompleteProfilesIncludingTrash_WhenRotating_ThenCommitAllOpaqueContentAndWrappers(string mode,bool trashed)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,client);var i=await Create(s,client,scoped,mode);
        if(trashed){await using var db=fixture.CreateContext();await db.Profiles.Where(x=>x.PublicId==i.ProfileId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DeletedAt,DateTimeOffset.UtcNow).SetProperty(p=>p.PurgeAt,DateTimeOffset.UtcNow.AddDays(30)));}
        var change=Change(s,client,scoped,i);var request=await Request(s,client,change);var before=await Snapshot(s);
        Assert.Null((await new VaultProtectionChangeStore(fixture).ChangeAsync(request,default)).Error);
        await using var check=fixture.CreateContext();var p=await check.Profiles.SingleAsync(x=>x.PublicId==i.ProfileId);
        Assert.Equal(2,p.Revision);Assert.True(p.ServerSequence>before.Profiles[0].ServerSequence);Assert.Equal(i.EditedAt,p.EditedAt);
        Assert.Equal(change.ContentReplacements[1].Envelope,JsonSerializer.Deserialize<Domain.Accounts.EncryptedEnvelope>(p.Envelope,ProtectionFixture.Json));
        Assert.Equal(change.ContentReplacements[1].KeyWrappers,JsonSerializer.Deserialize<ProfileKeyWrappers>(p.KeyWrappers,ProtectionFixture.Json));
        Assert.Equal(trashed,p.DeletedAt.HasValue);Assert.Equal(2,(await check.Accounts.SingleAsync(x=>x.Id==s.InternalId)).Revision);
        Assert.True((await check.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==request.ChallengeId)).Consumed);
    }
    [FunctionalTheory]
    [InlineData("missing","validation_failed")][InlineData("extra","validation_failed")][InlineData("foreign","validation_failed")]
    [InlineData("stale","revision_conflict")][InlineData("wrongEpoch","validation_failed")][InlineData("wrongWrapper","validation_failed")]
    [InlineData("oldWrapper","validation_failed")][InlineData("saltReuse","validation_failed")][InlineData("nonceReuse","validation_failed")]
    [InlineData("oldPassword","validation_failed")][InlineData("differentUnlock","validation_failed")][InlineData("corrupt","persistence_unavailable")]
    public async Task GivenIncompleteOrInvalidProfileRotation_WhenChanging_ThenPreserveEveryByteAndProof(string invalid,string error)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();using var other=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,client);var i=await Create(s,client,scoped,"PerProfile");
        var c=Change(s,client,scoped,i);var account=c.ContentReplacements[0];var item=c.ContentReplacements[1];
        if(invalid=="foreign")
        {
            var foreign=await ProfileSetup.Create(fixture,other);var fi=await Create(foreign,other,scoped,"Master");item=item with {ResourceId=fi.ProfileId};
        }
        item=invalid switch
        {
            "extra"=>item with {ResourceId=Guid.NewGuid()},"stale"=>item with {ExpectedRevision=2},
            "wrongEpoch"=>item with {Envelope=item.Envelope with {KeyEpoch=3}},
            "wrongWrapper"=>item with {KeyWrappers=item.KeyWrappers! with {MasterKeyWrapper=other.Wrap(s.AccountId,"profile",i.ProfileId,s.Actor,2,2)}},
            "oldWrapper"=>item with {KeyWrappers=i.KeyWrappers},"saltReuse"=>item with {Envelope=item.Envelope with {KeySalt=i.Envelope.KeySalt}},
            "nonceReuse"=>item with {Envelope=item.Envelope with {Nonce=i.Envelope.Nonce}},
            "oldPassword"=>item with {KeyWrappers=item.KeyWrappers! with {PasswordWrapper=i.KeyWrappers.PasswordWrapper}},
            "differentUnlock"=>item with {KeyWrappers=item.KeyWrappers! with {UnlockVerifier=other.Material.UnlockVerifier}},_=>item
        };
        c=c with {ContentReplacements=invalid=="missing"?[account]:[account,item]};
        if(invalid=="corrupt"){await using var db=fixture.CreateContext();await db.Profiles.Where(x=>x.PublicId==i.ProfileId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.KeyWrappers,new byte[]{1}));}
        var request=await Request(s,client,c);var before=JsonSerializer.Serialize(await Snapshot(s));
        Assert.Equal(error,(await new VaultProtectionChangeStore(fixture).ChangeAsync(request,default)).Error);
        Assert.Equal(before,JsonSerializer.Serialize(await Snapshot(s)));await using var check=fixture.CreateContext();Assert.False((await check.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==request.ChallengeId)).Consumed);
    }
    [FunctionalFact]
    public async Task GivenMultipleProfiles_WhenRotating_ThenRequireEveryCurrentProfile()
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,client);var first=await Create(s,client,scoped,"Master");
        using var secondScoped=new ProtectionFixture();await Create(s,client,secondScoped,"PerProfile");var req=await Request(s,client,Change(s,client,scoped,first));
        Assert.Equal("validation_failed",(await new VaultProtectionChangeStore(fixture).ChangeAsync(req,default)).Error);
        await using var db=fixture.CreateContext();Assert.All(await db.Profiles.Where(x=>x.AccountId==s.InternalId).ToListAsync(),p=>Assert.Equal(1,p.Revision));
    }
    [FunctionalFact]
    public async Task GivenMasterPasswordRewrap_WhenChanging_ThenKeepAllProfileBytesAndSequence()
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,client);await Create(s,client,scoped,"PerProfile");
        var before=JsonSerializer.Serialize((await Snapshot(s)).Profiles);var request=await Request(s,client,new(s.AccountId,1,1,"rewrap",client.Rewrap(),[]));
        Assert.Null((await new VaultProtectionChangeStore(fixture).ChangeAsync(request,default)).Error);Assert.Equal(before,JsonSerializer.Serialize((await Snapshot(s)).Profiles));
    }
    [FunctionalFact]
    public async Task GivenFailureDuringFinalWrites_WhenRotating_ThenRollbackAccountProfilesProtectionAndProof()
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,client);var i=await Create(s,client,scoped,"PerProfile");
        var request=await Request(s,client,Change(s,client,scoped,i));var before=JsonSerializer.Serialize(await Snapshot(s));
        var store=new VaultProtectionChangeStore(new ProfileSetup.Factory(fixture,new FailProtection()));
        Assert.Equal("persistence_unavailable",(await store.ChangeAsync(request,default)).Error);Assert.Equal(before,JsonSerializer.Serialize(await Snapshot(s)));
        await using(var db=fixture.CreateContext())Assert.False((await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==request.ChallengeId)).Consumed);
        Assert.Null((await new VaultProtectionChangeStore(fixture).ChangeAsync(request,default)).Error);
    }
    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenProfileContentEditedSinceWrapper_WhenRotating_ThenAcceptSignedOlderWrapperAndAdvanceCompleteInventory(string mode)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,client);var i=await Create(s,client,scoped,mode);
        // A real content-only edit keeps its existing key wrapper signed at revision1.
        Assert.Null((await new ProfileUpdateStore(fixture).UpdateAsync(new(s.Actor,s.Verifier,i.ProfileId,new(1,ProfileSetup.Envelope(),DateTimeOffset.UtcNow)),default)).Error);
        var c=Change(s,client,scoped,i);var replacement=c.ContentReplacements[1];c=c with {ContentReplacements=[c.ContentReplacements[0],replacement with {ExpectedRevision=2,KeyWrappers=replacement.KeyWrappers! with {MasterKeyWrapper=client.Wrap(s.AccountId,"profile",i.ProfileId,s.Actor,2,3)}}]};
        var request=await Request(s,client,c);Assert.Null((await new VaultProtectionChangeStore(fixture).ChangeAsync(request,default)).Error);
        await using var check=fixture.CreateContext();var p=await check.Profiles.SingleAsync(x=>x.PublicId==i.ProfileId);Assert.Equal(3,p.Revision);Assert.Equal(c.ContentReplacements[1].Envelope,JsonSerializer.Deserialize<Domain.Accounts.EncryptedEnvelope>(p.Envelope,ProtectionFixture.Json));Assert.Equal(3,JsonSerializer.Deserialize<ProfileKeyWrappers>(p.KeyWrappers,ProtectionFixture.Json)!.MasterKeyWrapper.GrantRevision);
    }
    private async Task<ProfileCreateInput> Create(ProfileSetup.State s,ProtectionFixture client,ProtectionFixture scoped,string mode)
    {var i=ProfileSetup.Input(s,client,scoped,mode);Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,i),default)).Error);return i;}
    private static ProtectionChange Change(ProfileSetup.State s,ProtectionFixture client,ProtectionFixture scoped,ProfileCreateInput i)
    {
        var wrappers=i.KeyWrappers with {MasterKeyWrapper=client.Wrap(s.AccountId,"profile",i.ProfileId,s.Actor,2,2),PasswordWrapper=i.KeyWrappers.UnlockMode=="PerProfile"?scoped.Rewrap().PasswordWrapper:null};
        return new(s.AccountId,1,1,"rotate-content",client.Rewrap(),[new("account",s.AccountId,1,ProfileSetup.Envelope(2)),new("profile",i.ProfileId,1,ProfileSetup.Envelope(2),wrappers)]);
    }
    private async Task<ProtectionChangeRequest> Request(ProfileSetup.State s,ProtectionFixture client,ProtectionChange c)
    {
        var raw=JsonSerializer.SerializeToUtf8Bytes(c,ProtectionFixture.Json);
        var result=await new VaultProtectionStore(fixture).ChallengeAsync(s.Actor,"change-protection",ProtocolBinary.Encode(SHA256.HashData(raw)),default);
        Assert.Null(result.Error);return new(s.Actor,s.Verifier,result.Data!.ChallengeId,client.Sign(result.Data),raw,c);
    }
    private sealed record SnapshotData(object Account,object Protection,Profile[] Profiles);
    private async Task<SnapshotData> Snapshot(ProfileSetup.State s)
    {
        await using var db=fixture.CreateContext();return new(await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.InternalId),await db.VaultProtections.AsNoTracking().SingleAsync(x=>x.AccountId==s.InternalId),await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync());
    }
    private sealed class FailProtection:DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {if(command.CommandText.Contains("UPDATE cerberus.vault_protection"))throw new TimeoutException("synthetic final protection failure");return ValueTask.FromResult(result);}
    }
}
