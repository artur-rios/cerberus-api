using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Data.Collections;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Protection;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class CollectionUpdateStoreTests(PostgresFixture fixture)
{
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenCurrentOwnerScope_WhenUpdating_ThenOnlyTargetEnvelopeAndMetadataAdvance(bool selected)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var key=new ProtectionFixture();
        var s=await ProfileSetup.Create(fixture,owner);var recipient=await ProfileSetup.Create(fixture,client);
        var a=await AssociationSetup.Items(fixture,s);await AssociationSetup.Items(fixture,s);
        await Members(s,a);await AssociationSetup.Grant(fixture,s,owner,recipient,client,a.Collection,CollectionGrantAccess.ReadWrite);
        var p=await Profile(s,owner,key,[a.Collection.PublicId]);if(selected)s=s with{Verifier=await Selected(s,p.Id)};
        var before=await Snapshot(s,a.Collection.Id);var recipientBefore=await Snapshot(recipient);var input=Input();
        await Success(a.Collection,input,await Update(s,a.Collection,input));
        Assert.Equal(before,await Snapshot(s,a.Collection.Id));Assert.Equal(recipientBefore,await Snapshot(recipient));
    }

    [FunctionalTheory][InlineData("minimum")][InlineData("pre2000")][InlineData("older")][InlineData("equal")][InlineData("sameEnvelope")]
    public async Task GivenMatchingRevisionAndRepresentableTime_WhenUpdating_ThenNormalizeTimeAndAcceptIdenticalEnvelope(string kind)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);
        var input=Input() with{EditedAt=kind=="minimum"?new(10,TimeSpan.Zero):kind=="pre2000"?new DateTimeOffset(1999,12,31,23,59,59,TimeSpan.Zero).AddTicks(19):kind=="equal"?target.EditedAt:target.EditedAt.AddTicks(-11)};
        if(kind=="sameEnvelope")input=input with{Envelope=JsonSerializer.Deserialize<EncryptedEnvelope>(target.Envelope,ProtectionFixture.Json)!};
        await Success(target,input,await Update(s,target,input));
    }

    [FunctionalTheory][InlineData(CollectionGrantAccess.ReadOnly,false)][InlineData(CollectionGrantAccess.ReadOnly,true)]
    [InlineData(CollectionGrantAccess.ReadWrite,false)][InlineData(CollectionGrantAccess.ReadWrite,true)]
    public async Task GivenValidVisibleNativeRecipient_WhenUpdatingWithStaleRevision_Then403AndNoMutation(CollectionGrantAccess access,bool selected)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var key=new ProtectionFixture();
        var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var target=await Collection(o);
        await AssociationSetup.Grant(fixture,o,owner,s,client,target,access);
        if(selected){var p=await Profile(s,client,key,[target.PublicId]);s=s with{Verifier=await Selected(s,p.Id)};}
        var before=await Snapshot(o);var recipient=await Snapshot(s);var capture=new Capture();
        var r=await new CollectionUpdateStore(new ProfileSetup.Factory(fixture,capture)).UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,Input(77)),default);
        Assert.Equal("vault_access_denied",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(o));Assert.Equal(recipient,await Snapshot(s));
        Assert.Single(capture.Locks,x=>x.Contains("FROM cerberus.account "));
        Assert.DoesNotContain(capture.Locks,x=>x.Contains("cerberus.collection ")||x.Contains("cerberus.collection_grant")||x.Contains("cerberus.vault_protection"));
    }

    [FunctionalTheory][InlineData("missing")][InlineData("ungranted")][InlineData("unselectedOwner")][InlineData("unselectedForeign")]
    [InlineData("deleted")][InlineData("terminal")][InlineData("ownerClosing")][InlineData("ownerTerminal")]
    [InlineData("revoked")][InlineData("grantTerminal")][InlineData("invalidAccess")][InlineData("zeroGrantRevision")][InlineData("unsafeGrantRevision")]
    public async Task GivenHiddenTargetWithBadCipherAndStaleRevision_WhenUpdating_Then404BeforeInspectingEvidence(string kind)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var key=new ProtectionFixture();
        var o=await ProfileSetup.Create(fixture,owner);var s=kind=="unselectedOwner"?o:await ProfileSetup.Create(fixture,client);var target=await Collection(o);
        CollectionGrant? grant=null;if(kind!="ungranted"&&s.InternalId!=o.InternalId)grant=await AssociationSetup.Grant(fixture,o,owner,s,client,target);
        if(kind.StartsWith("unselected")){var p=await Profile(s,kind=="unselectedOwner"?owner:client,key,[]);s=s with{Verifier=await Selected(s,p.Id)};}
        await using(var db=fixture.CreateContext())
        {
            await db.Collections.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.Envelope,"bad"u8.ToArray()).SetProperty(c=>c.DeletedAt,kind=="deleted"?(DateTimeOffset?)DateTimeOffset.UtcNow:null));
            if(grant is not null)await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,"bad"u8.ToArray()));
            if(kind=="ownerClosing")await db.Accounts.Where(x=>x.Id==o.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.State,AccountState.ClosurePending));
            if(kind=="revoked")await db.CollectionGrants.Where(x=>x.Id==grant!.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.State,CollectionGrantState.Revoked));
            if(kind=="invalidAccess")await db.CollectionGrants.Where(x=>x.Id==grant!.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.Access,(CollectionGrantAccess)99));
            if(kind is "zeroGrantRevision" or "unsafeGrantRevision")await db.CollectionGrants.Where(x=>x.Id==grant!.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.Revision,kind=="zeroGrantRevision"?0:ProtocolBinary.MaxInteger+1));
            if(kind is "terminal" or "ownerTerminal" or "grantTerminal"){db.TerminalErasures.Add(new(){ResourceKind=kind=="terminal"?"collection":kind=="ownerTerminal"?"account":"grant",ResourceId=kind=="terminal"?target.PublicId:kind=="ownerTerminal"?o.AccountId:grant!.PublicId,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        }
        var before=await Snapshot(o);var r=await Store().UpdateAsync(new(s.Actor,s.Verifier,kind=="missing"?Guid.NewGuid():target.PublicId,Input(77)),default);
        Assert.Equal("not_found",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(o));
    }

    [FunctionalTheory][InlineData("signature")][InlineData("collectionEnvelope")][InlineData("grantRevision")][InlineData("epoch")]
    [InlineData("ownerPins")][InlineData("recipientPins")][InlineData("ownerPinsMissing")][InlineData("recipientPinsMissing")]
    [InlineData("ownerPinRevision")][InlineData("recipientPinEpoch")][InlineData("identity")][InlineData("numericString")][InlineData("duplicate")]
    public async Task GivenCorruptRelevantForeignNativeEvidence_WhenUpdating_Then503BeforeOwnerOnlyDenial(string kind)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var target=await Collection(o);
        var grant=await AssociationSetup.Grant(fixture,o,owner,s,client,target,CollectionGrantAccess.ReadWrite);
        await using(var db=fixture.CreateContext())
        {
            if(kind=="signature")await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,"bad"u8.ToArray()));
            if(kind=="collectionEnvelope")await db.Collections.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.Envelope,"bad"u8.ToArray()));
            if(kind is "numericString" or "duplicate")await db.Collections.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.Envelope,Malformed(target.Envelope,kind)));
            if(kind=="grantRevision")await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.Revision,2));
            if(kind=="epoch")await db.Collections.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.KeyEpoch,2));
            if(kind is "ownerPins" or "recipientPins")await db.VaultProtections.Where(x=>x.AccountId==(kind=="ownerPins"?o.InternalId:s.InternalId)).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Material,"bad"u8.ToArray()));
            if(kind is "ownerPinsMissing" or "recipientPinsMissing")await db.VaultProtections.Where(x=>x.AccountId==(kind=="ownerPinsMissing"?o.InternalId:s.InternalId)).ExecuteDeleteAsync();
            if(kind=="ownerPinRevision")await db.VaultProtections.Where(x=>x.AccountId==o.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Revision,0));
            if(kind=="recipientPinEpoch")await db.VaultProtections.Where(x=>x.AccountId==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.KeyEpoch,2));
            if(kind=="identity")await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,Bytes(owner.WrapGrant(o.AccountId,target.PublicId,grant.PublicId,Guid.NewGuid(),client.Material.RecipientKey))));
        }
        var before=await Snapshot(o);var recipient=await Snapshot(s);var r=await Update(s,target,Input(77));
        Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(o));Assert.Equal(recipient,await Snapshot(s));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenMalformedUnrelatedGrant_WhenUpdating_ThenOnlyRelevantEvidenceMatters(bool ownerPath)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);
        var target=await Collection(o);var unrelated=await Collection(o);var good=await AssociationSetup.Grant(fixture,o,owner,s,client,target);var bad=await AssociationSetup.Grant(fixture,o,owner,s,client,unrelated);
        await using(var db=fixture.CreateContext())
        {
            await db.CollectionGrants.Where(x=>x.Id==bad.Id||(ownerPath&&x.Id==good.Id)).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,"bad"u8.ToArray()));
            await db.Collections.Where(x=>x.Id==unrelated.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.Envelope,"bad"u8.ToArray()));
            if(ownerPath)await db.VaultProtections.Where(x=>x.AccountId==o.InternalId||x.AccountId==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Material,"bad"u8.ToArray()));
        }
        var before=await Snapshot(o,ownerPath?target.Id:null);var input=Input();var r=await Update(ownerPath?o:s,target,input);
        if(ownerPath)await Success(target,input,r);else Assert.Equal("vault_access_denied",r.Error);
        Assert.Equal(before,await Snapshot(o,ownerPath?target.Id:null));
    }

    [FunctionalTheory][InlineData("access","vault_access_denied")][InlineData("revoked","vault_access_denied")][InlineData("expired","vault_access_denied")]
    [InlineData("future","vault_access_denied")][InlineData("policy","vault_access_denied")][InlineData("generation","vault_access_denied")]
    [InlineData("zeroPolicy","vault_access_denied")][InlineData("zeroGeneration","vault_access_denied")][InlineData("nullExpiry","vault_access_denied")]
    [InlineData("disabledExpiry","vault_access_denied")][InlineData("noExpiry",null)][InlineData("selection","vault_access_denied")]
    [InlineData("closing","not_found")][InlineData("actorAbsent","not_found")][InlineData("actorTerminal","not_found")]
    public async Task GivenInvalidCurrentAccountOrSession_WhenUpdating_ThenNoMutation(string kind,string? error)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);
        await using(var db=fixture.CreateContext())
        {
            var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var v=await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier);
            if(kind=="closing")a.State=AccountState.ClosurePending;if(kind=="revoked")v.Revoked=true;if(kind=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);if(kind=="future")v.IssuedAt=DateTimeOffset.UtcNow.AddMinutes(1);
            if(kind=="policy")v.PolicyRevision++;if(kind=="generation")v.RevocationGeneration++;if(kind=="zeroPolicy")v.PolicyRevision=0;if(kind=="zeroGeneration")v.RevocationGeneration=0;
            if(kind is "noExpiry" or "disabledExpiry")a.RenewalEnabled=false;if(kind is "nullExpiry" or "noExpiry")v.ExpiresAt=null;if(kind=="selection")v.ProfileId=long.MaxValue;
            if(kind=="actorTerminal")db.TerminalErasures.Add(new(){ResourceId=s.AccountId,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();
        }
        var before=await Snapshot(s);var input=Input();var r=await Store().UpdateAsync(new(kind=="actorAbsent"?Guid.NewGuid():s.Actor,kind=="access"?new string('a',64):s.Verifier,target.PublicId,input),default);
        Assert.Equal(error,r.Error);if(error is not null){Assert.Null(r.Data);Assert.Equal(before,await Snapshot(s));}else await Success(target,input,r);
    }

    [FunctionalTheory][InlineData("trash")][InlineData("terminal")][InlineData("foreign")]
    public async Task GivenInvalidSelectedProfile_WhenUpdating_Then403WithoutWideningSelection(string kind)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);var p=await Profile(s,owner,key,[target.PublicId]);s=s with{Verifier=await Selected(s,p.Id)};
        await using(var db=fixture.CreateContext())
        {
            if(kind=="trash")await db.Profiles.Where(x=>x.Id==p.Id).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DeletedAt,DateTimeOffset.UtcNow));
            if(kind=="terminal"){db.TerminalErasures.Add(new(){ResourceId=p.PublicId,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
            if(kind=="foreign"){var foreign=await ProfileSetup.Create(fixture,key);var foreignProfile=await Profile(foreign,key,owner,[]);await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ProfileId,foreignProfile.Id));}
        }
        var before=await Snapshot(s);Assert.Equal("vault_access_denied",(await Update(s,target,Input())).Error);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData("actor","authentication_required")][InlineData("handle","vault_access_required")][InlineData("id","validation_failed")]
    [InlineData("null","validation_failed")][InlineData("revision","validation_failed")][InlineData("unsafeRevision","validation_failed")]
    [InlineData("time","validation_failed")][InlineData("offset","validation_failed")][InlineData("envelope","validation_failed")]
    public async Task GivenInvalidInternalInput_WhenUpdating_ThenRejectBeforeWrites(string kind,string error)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);var input=Input();
        input=kind switch{"revision"=>input with{ExpectedRevision=0},"unsafeRevision"=>input with{ExpectedRevision=ProtocolBinary.MaxInteger+1},"time"=>input with{EditedAt=DateTimeOffset.MinValue.AddTicks(9)},"offset"=>input with{EditedAt=input.EditedAt.ToOffset(TimeSpan.FromHours(1))},"envelope"=>input with{Envelope=null!},_=>input};
        var before=await Snapshot(s);var r=await Store().UpdateAsync(new(kind=="actor"?Guid.Empty:s.Actor,kind=="handle"?"":s.Verifier,kind=="id"?Guid.Empty:target.PublicId,kind=="null"?null!:input),default);
        Assert.Equal(error,r.Error);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData("stale","revision_conflict")][InlineData("max","revision_conflict")]
    [InlineData("revision","persistence_unavailable")][InlineData("unsafeRevision","persistence_unavailable")][InlineData("sequence","persistence_unavailable")]
    [InlineData("unsafeSequence","persistence_unavailable")][InlineData("envelope","persistence_unavailable")][InlineData("numericString","persistence_unavailable")]
    [InlineData("unknown","persistence_unavailable")][InlineData("duplicate","persistence_unavailable")][InlineData("case","persistence_unavailable")]
    [InlineData("storedEpoch","persistence_unavailable")][InlineData("zeroEpoch","persistence_unavailable")][InlineData("time","persistence_unavailable")]
    [InlineData("infinity","persistence_unavailable")][InlineData("epoch","validation_failed")][InlineData("salt","validation_failed")][InlineData("nonce","validation_failed")]
    public async Task GivenInvalidStoredTargetOrReplacement_WhenUpdating_ThenPreserveState(string kind,string error)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);var old=JsonSerializer.Deserialize<EncryptedEnvelope>(target.Envelope,ProtectionFixture.Json)!;
        var input=Input(kind=="stale"?8:kind=="max"?ProtocolBinary.MaxInteger:1);
        if(kind=="epoch")input=input with{Envelope=input.Envelope with{KeyEpoch=2}};if(kind=="salt")input=input with{Envelope=input.Envelope with{KeySalt=old.KeySalt}};if(kind=="nonce")input=input with{Envelope=input.Envelope with{Nonce=old.Nonce}};
        await using(var db=fixture.CreateContext())
        {
            var c=await db.Collections.SingleAsync(x=>x.Id==target.Id);
            if(kind=="max")c.Revision=ProtocolBinary.MaxInteger;if(kind=="revision")c.Revision=0;if(kind=="unsafeRevision")c.Revision=ProtocolBinary.MaxInteger+1;
            if(kind=="sequence")c.ServerSequence=0;if(kind=="unsafeSequence")c.ServerSequence=ProtocolBinary.MaxInteger+1;if(kind=="envelope")c.Envelope="bad"u8.ToArray();
            if(kind is "numericString" or "unknown" or "duplicate" or "case")c.Envelope=Malformed(c.Envelope,kind);
            if(kind=="storedEpoch")c.KeyEpoch=2;if(kind=="zeroEpoch")c.KeyEpoch=0;await db.SaveChangesAsync();
            if(kind=="time")await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.collection SET edited_at='0001-01-01 00:00:00+00' WHERE id={target.Id}");
            if(kind=="infinity")await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.collection SET edited_at='infinity' WHERE id={target.Id}");
        }
        var before=await Snapshot(s);var r=await Update(s,target,input);Assert.Equal(error,r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalFact]
    public async Task GivenConcurrentSameRevisionAndLostResponseRetry_WhenUpdating_ThenExactlyOneWinner()
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);var first=Input();var second=Input();
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));var results=await Task.WhenAll(Store().UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,first),ct.Token),Store().UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,second),ct.Token));
        var winner=Assert.Single(results,x=>x.Error is null);Assert.Single(results,x=>x.Error=="revision_conflict");await Success(target,results[0].Error is null?first:second,winner);
        var before=await Snapshot(s);Assert.Equal("revision_conflict",(await Update(s,target,first)).Error);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData("account")][InlineData("vault_access_session")][InlineData("profile")][InlineData("collection")]
    public async Task GivenNaturalExpiryDuringEachLockWait_WhenUpdating_Then403BeforeRevisionConflict(string table)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);var p=await Profile(s,owner,key,[target.PublicId]);s=s with{Verifier=await Selected(s,p.Id)};
        var deadline=DateTimeOffset.UtcNow.AddSeconds(2);await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));
        var before=await Snapshot(s);await using var held=fixture.CreateContext();await using var tx=await held.Database.BeginTransactionAsync();await Hold(held,table,s,target,p);
        var reached=SignalSource();using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(15));var pending=new CollectionUpdateStore(new ProfileSetup.Factory(fixture,new Signal(reached,table))).UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,Input(77)),ct.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);await Past(deadline);await tx.CommitAsync();
        Assert.Equal("vault_access_denied",(await pending).Error);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData("policy","vault_access_denied")][InlineData("generation","vault_access_denied")]
    [InlineData("link","not_found")][InlineData("profile","vault_access_denied")][InlineData("profileTerminal","vault_access_denied")]
    [InlineData("delete","not_found")][InlineData("terminal","not_found")]
    public async Task GivenAuthorityChangesDuringLockWait_WhenUpdating_ThenReloadCommittedState(string kind,string error)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);var p=await Profile(s,owner,key,[target.PublicId]);s=s with{Verifier=await Selected(s,p.Id)};
        await using var held=fixture.CreateContext();await using var tx=await held.Database.BeginTransactionAsync();var table=kind is "policy" or "generation"?"account":kind.StartsWith("profile")?"profile":"collection";await Hold(held,table,s,target,p);
        var reached=SignalSource();using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(15));var pending=new CollectionUpdateStore(new ProfileSetup.Factory(fixture,new Signal(reached,table))).UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,Input()),ct.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);
        if(kind=="policy")await held.Accounts.Where(x=>x.Id==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.PolicyRevision,a=>a.PolicyRevision+1));
        if(kind=="generation")await held.Accounts.Where(x=>x.Id==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.RevocationGeneration,a=>a.RevocationGeneration+1));
        if(kind=="link")await held.ProfileCollections.Where(x=>x.ProfileId==p.Id&&x.CollectionId==target.Id).ExecuteDeleteAsync();
        if(kind=="profile")await held.Profiles.Where(x=>x.Id==p.Id).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DeletedAt,DateTimeOffset.UtcNow));
        if(kind=="delete")await held.Collections.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.DeletedAt,DateTimeOffset.UtcNow));
        if(kind is "terminal" or "profileTerminal"){held.TerminalErasures.Add(new(){ResourceId=kind=="terminal"?target.PublicId:p.PublicId,ResourceKind=kind=="terminal"?"collection":"profile",DeletedAt=DateTimeOffset.UtcNow});await held.SaveChangesAsync();}
        await tx.CommitAsync();var before=await Snapshot(s);Assert.Equal(error,(await pending).Error);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData("expiry","vault_access_denied")][InlineData("link","not_found")]
    [InlineData("targetTerminal","not_found")][InlineData("actorTerminal","not_found")][InlineData("profileTerminal","vault_access_denied")]
    public async Task GivenAuthorityChangesImmediatelyBeforeWrite_WhenUpdating_ThenGuardReclassifiesCurrentState(string kind,string error)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);var p=await Profile(s,owner,key,[target.PublicId]);s=s with{Verifier=await Selected(s,p.Id)};
        var deadline=DateTimeOffset.UtcNow.AddSeconds(2);if(kind=="expiry"){await using var db=fixture.CreateContext();await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));}
        var original=await Row(target.Id);
        var store=new CollectionUpdateStore(new ProfileSetup.Factory(fixture,new BeforeWrite(async _=>
        {
            await using var db=fixture.CreateContext();if(kind=="expiry")await Past(deadline);
            if(kind=="link")await db.ProfileCollections.Where(x=>x.ProfileId==p.Id&&x.CollectionId==target.Id).ExecuteDeleteAsync();
            if(kind.EndsWith("Terminal")){db.TerminalErasures.Add(new(){ResourceId=kind=="targetTerminal"?target.PublicId:kind=="actorTerminal"?s.AccountId:p.PublicId,ResourceKind=kind=="targetTerminal"?"collection":kind=="actorTerminal"?"account":"profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        })));
        Assert.Equal(error,(await store.UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,Input()),default)).Error);Assert.Equal(JsonSerializer.Serialize(original),JsonSerializer.Serialize(await Row(target.Id)));
    }

    [FunctionalTheory][InlineData("revision")][InlineData("sequence")][InlineData("envelope")][InlineData("epoch")][InlineData("selection")]
    public async Task GivenGuardedValueChangesInsideTransaction_WhenUpdating_Then409AndRollback(string kind)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);var p=await Profile(s,owner,key,[target.PublicId]);var before=await Snapshot(s);
        var store=new CollectionUpdateStore(new ProfileSetup.Factory(fixture,new BeforeWrite(async command=>
        {
            await using var change=command.Connection!.CreateCommand();change.Transaction=command.Transaction;
            change.CommandText=kind switch{"revision"=>"UPDATE cerberus.collection SET revision=revision+1 WHERE id=@id","sequence"=>"UPDATE cerberus.collection SET server_sequence=server_sequence+1 WHERE id=@id","envelope"=>"UPDATE cerberus.collection SET envelope='bad'::bytea WHERE id=@id","epoch"=>"UPDATE cerberus.collection SET key_epoch=key_epoch+1 WHERE id=@id",_=>"UPDATE cerberus.vault_access_session SET profile_id=@profile WHERE handle_verifier=@verifier"};
            change.Parameters.Add(new NpgsqlParameter("id",target.Id));change.Parameters.Add(new NpgsqlParameter("profile",p.Id));change.Parameters.Add(new NpgsqlParameter("verifier",s.Verifier));await change.ExecuteNonQueryAsync();
        })));
        Assert.Equal("revision_conflict",(await store.UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,Input()),default)).Error);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenProtectionRotationAndUpdate_WhenCompeting_ThenSerializeAndPreserveWinner(bool rotationFirst)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);
        var change=new ProtectionChange(s.AccountId,1,1,"rotate-content",owner.Rewrap(),[new("account",s.AccountId,1,ProfileSetup.Envelope(2)),new("collection",target.PublicId,1,ProfileSetup.Envelope(2))]);
        var raw=Bytes(change);var challenge=(await new VaultProtectionStore(fixture).ChallengeAsync(s.Actor,"change-protection",ProtocolBinary.Encode(SHA256.HashData(raw)),default)).Data!;
        var request=new ProtectionChangeRequest(s.Actor,s.Verifier,challenge.ChallengeId,owner.Sign(challenge),raw,change);
        var reached=SignalSource();var release=SignalSource();var waiting=SignalSource();using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        if(rotationFirst)
        {
            var rotation=new VaultProtectionChangeStore(new ProfileSetup.Factory(fixture,new PauseRotation(reached,release))).ChangeAsync(request,ct.Token);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var update=new CollectionUpdateStore(new ProfileSetup.Factory(fixture,new Signal(waiting,"account"))).UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,Input()),ct.Token);
            try{await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(update.IsCompleted);}finally{release.TrySetResult();}
            // Rotation advances the account generation: session denial precedes the stale content revision.
            Assert.Null((await rotation).Error);var before=await Snapshot(s);Assert.Equal("vault_access_denied",(await update).Error);Assert.Equal(before,await Snapshot(s));Assert.Equal(2,(await Row(target.Id)).KeyEpoch);
        }
        else
        {
            var input=Input();var update=new CollectionUpdateStore(new ProfileSetup.Factory(fixture,new BeforeWrite(async _=>{reached.TrySetResult();await release.Task.WaitAsync(ct.Token);}))).UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,input),ct.Token);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var rotation=new VaultProtectionChangeStore(new ProfileSetup.Factory(fixture,new Signal(waiting,"account"))).ChangeAsync(request,ct.Token);
            try{await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(rotation.IsCompleted);}finally{release.TrySetResult();}
            await Success(target,input,await update);Assert.Equal("revision_conflict",(await rotation).Error);Assert.Equal(1,(await Row(target.Id)).KeyEpoch);
            await using var db=fixture.CreateContext();Assert.False((await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==challenge.ChallengeId)).Consumed);
        }
    }

    [FunctionalTheory][InlineData("failure")][InlineData("revision")][InlineData("time")][InlineData("envelope")][InlineData("epoch")][InlineData("stamp")]
    public async Task GivenPostWriteFaultOrUnexpectedResult_WhenUpdating_ThenRollbackAndRetrySucceeds(string kind)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);var before=await Snapshot(s);var input=Input();
        var store=new CollectionUpdateStore(new ProfileSetup.Factory(fixture,new AfterWrite(kind,target)));
        Assert.Equal("persistence_unavailable",(await store.UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,input),default)).Error);Assert.Equal(before,await Snapshot(s));await Success(target,input,await Update(s,target,input));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenExhaustedOrRegressedGlobalSequence_WhenUpdating_ThenRollback(bool exhausted)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);var before=await Snapshot(s);await using var db=fixture.CreateContext();
        var old=await db.Database.SqlQuery<long>($"SELECT nextval('cerberus.server_sequence') AS \"Value\"").SingleAsync();
        try{await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval('cerberus.server_sequence',{(exhausted?ProtocolBinary.MaxInteger:1)},true)");var r=await Update(s,target,Input());Assert.Equal(exhausted?"revision_conflict":"persistence_unavailable",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(s));}
        finally{await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval('cerberus.server_sequence',{old},true)");}
    }

    [FunctionalFact]
    public async Task GivenUnavailableDatabaseOrCallerCancellation_WhenUpdating_Then503OrPropagateCancellation()
    {
        var request=new CollectionUpdateRequest(Guid.NewGuid(),new string('a',64),Guid.NewGuid(),Input());
        var dead=new ProfileSetup.Factory(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=absent;Username=none;Timeout=1;Pooling=false").Options);
        Assert.Equal("persistence_unavailable",(await new CollectionUpdateStore(dead).UpdateAsync(request,default)).Error);using var ct=new CancellationTokenSource();ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Store().UpdateAsync(request,ct.Token));
    }

    [FunctionalTheory][InlineData("cancel")][InlineData("timeout")][InlineData("db")][InlineData("dbUpdate")][InlineData("json")][InlineData("format")][InlineData("wrapped")]
    public async Task GivenInternalProviderFailure_WhenUpdating_Then503WithoutLeakingException(string kind)
    {
        Exception failure=kind switch{"cancel"=>new OperationCanceledException("private"),"timeout"=>new TimeoutException("private"),"db"=>new NpgsqlException("private"),"dbUpdate"=>new DbUpdateException("private"),"json"=>new JsonException("private"),"format"=>new FormatException("private"),_=>new InvalidOperationException("private",new TimeoutException("private"))};
        var r=await new CollectionUpdateStore(new ThrowingFactory(failure)).UpdateAsync(new(Guid.NewGuid(),new string('a',64),Guid.NewGuid(),Input()),default);Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);
    }

    [FunctionalTheory][InlineData("account")][InlineData("profile")][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("grant")]
    public async Task GivenTerminalGuidCollision_WhenUpdating_ThenOnlyCollectionMarkerHidesTarget(string kind)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);
        await using(var db=fixture.CreateContext()){db.TerminalErasures.Add(new(){ResourceId=target.PublicId,ResourceKind=kind,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        var input=Input();var r=await Update(s,target,input);if(kind=="collection")Assert.Equal("not_found",r.Error);else await Success(target,input,r);
    }

    [FunctionalFact]
    public async Task GivenOneHundredUnrelatedCollections_WhenUpdating_ThenExplainOneCandidateAndLockOnlyOwnAuthorityAndTarget()
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var other=await ProfileSetup.Create(fixture,client);var target=await Collection(s);var p=await Profile(s,owner,key,[target.PublicId]);s=s with{Verifier=await Selected(s,p.Id)};
        await using(var db=fixture.CreateContext()){db.Collections.AddRange(Enumerable.Range(0,100).Select(i=>new VaultCollection{PublicId=Guid.NewGuid(),AccountId=i<50?s.InternalId:other.InternalId,Envelope=Bytes(ProfileSetup.Envelope()),EditedAt=DateTimeOffset.UnixEpoch}));await db.SaveChangesAsync();}
        var capture=new Capture();var input=Input();await Success(target,input,await new CollectionUpdateStore(new ProfileSetup.Factory(fixture,capture)).UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,input),default));
        Assert.Equal(4,capture.Locks.Count);Assert.Single(capture.Locks,x=>x.Contains("FROM cerberus.account "));Assert.Single(capture.Locks,x=>x.Contains("FROM cerberus.vault_access_session "));Assert.Single(capture.Locks,x=>x.Contains("FROM cerberus.profile "));
        var targetLock=Assert.Single(capture.Locks,x=>x.Contains("FROM cerberus.collection "));Assert.Contains("FOR NO KEY UPDATE",targetLock);Assert.Contains(target.PublicId,capture.LockValues);Assert.Equal(s.Actor,Assert.Single(capture.AccountLockValues));
        await using var read=fixture.CreateContext();await read.Database.OpenConnectionAsync();await using var command=read.Database.GetDbConnection().CreateCommand();command.CommandText="EXPLAIN (ANALYZE, FORMAT JSON) "+capture.Sql;foreach(var parameter in capture.Parameters)command.Parameters.Add(parameter.Clone());using var plan=JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        static IEnumerable<JsonElement> Nodes(JsonElement node){yield return node;if(node.TryGetProperty("Plans",out var children))foreach(var child in children.EnumerateArray())foreach(var n in Nodes(child))yield return n;}
        var nodes=Nodes(plan.RootElement[0].GetProperty("Plan")).ToArray();var candidate=Assert.Single(nodes,x=>x.TryGetProperty("Subplan Name",out var name)&&name.GetString()=="CTE target");Assert.Equal(1d,candidate.GetProperty("Actual Rows").GetDouble());
        Assert.Contains(nodes,x=>x.TryGetProperty("Relation Name",out var name)&&name.GetString()=="collection"&&x.GetProperty("Actual Rows").GetDouble()==1);
    }

    private CollectionUpdateStore Store()=>new(fixture);
    private Task<VaultResult<CollectionCreateDetails>> Update(ProfileSetup.State s,VaultCollection target,CollectionUpdateInput input)=>Store().UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,input),default);
    private static CollectionUpdateInput Input(long revision=1)=>new(revision,ProfileSetup.Envelope(),DateTimeOffset.Parse("2026-10-11T00:00:00Z").AddTicks(19));
    private async Task Success(VaultCollection original,CollectionUpdateInput input,VaultResult<CollectionCreateDetails> result)
    {
        Assert.Null(result.Error);var row=await Row(original.Id);Assert.Equal(original.PublicId,result.Data!.CollectionId);Assert.Equal(original.Revision+1,result.Data.Revision);Assert.True(result.Data.ServerSequence>original.ServerSequence);Assert.InRange(result.Data.ServerSequence,1,ProtocolBinary.MaxInteger);Assert.Equal(input.EditedAt.AddTicks(-(input.EditedAt.Ticks%10)),result.Data.EditedAt);
        Assert.Equal(input.Envelope,JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope,ProtectionFixture.Json));Assert.Equal(original.AccountId,row.AccountId);Assert.Equal(original.KeyEpoch,row.KeyEpoch);Assert.Equal(original.PublicId,row.PublicId);Assert.Equal(original.DeletedAt,row.DeletedAt);Assert.Equal(original.PurgeAt,row.PurgeAt);
        Assert.Equal(result.Data.Revision,row.Revision);Assert.Equal(result.Data.ServerSequence,row.ServerSequence);Assert.Equal(result.Data.EditedAt,row.EditedAt);Assert.NotEqual(original.ConcurrencyStamp,row.ConcurrencyStamp);
        using var json=JsonDocument.Parse(Bytes(result.Data));Assert.Equal(new[]{"collectionId","editedAt","revision","serverSequence"},json.RootElement.EnumerateObject().Select(x=>x.Name).Order().ToArray());
    }
    private async Task<VaultCollection> Row(long id){await using var db=fixture.CreateContext();return await db.Collections.AsNoTracking().SingleAsync(x=>x.Id==id);}
    private async Task<VaultCollection> Collection(ProfileSetup.State s){var row=new VaultCollection{PublicId=Guid.NewGuid(),AccountId=s.InternalId,Envelope=Bytes(ProfileSetup.Envelope()),EditedAt=DateTimeOffset.UnixEpoch};await using var db=fixture.CreateContext();db.Collections.Add(row);await db.SaveChangesAsync();return row;}
    private async Task<Profile> Profile(ProfileSetup.State s,ProtectionFixture owner,ProtectionFixture key,Guid[] collections){var input=ProfileSetup.Input(s,owner,key) with{CollectionIds=collections};Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,input),default)).Error);await using var db=fixture.CreateContext();return await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==input.ProfileId);}
    private async Task Members(ProfileSetup.State s,AssociationSetup.ResourceSet items){await using var db=fixture.CreateContext();db.CollectionRecords.Add(new(){AccountId=s.InternalId,CollectionId=items.Collection.Id,RecordId=items.Record.Id});db.CollectionFolders.Add(new(){AccountId=s.InternalId,CollectionId=items.Collection.Id,FolderId=items.Folder.Id});await db.SaveChangesAsync();}
    private async Task<string> Selected(ProfileSetup.State s,long profile){var verifier=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));await using var db=fixture.CreateContext();db.VaultAccessSessions.Add(new(){AccountId=s.InternalId,ProfileId=profile,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});await db.SaveChangesAsync();return verifier;}
    private async Task<string> Snapshot(ProfileSetup.State s,long? except=null)
    {
        await using var db=fixture.CreateContext();return JsonSerializer.Serialize(new{Account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.InternalId),Protection=await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==s.InternalId),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Records=await db.Records.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Folders=await db.Folders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Collections=await db.Collections.AsNoTracking().Where(x=>x.AccountId==s.InternalId&&x.Id!=except).OrderBy(x=>x.Id).ToArrayAsync(),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),ProfileRecords=await db.ProfileRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.RecordId).ToArrayAsync(),ProfileFolders=await db.ProfileFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.FolderId).ToArrayAsync(),ProfileCollections=await db.ProfileCollections.AsNoTracking().Where(x=>db.Profiles.Any(p=>p.Id==x.ProfileId&&p.AccountId==s.InternalId)).OrderBy(x=>x.ProfileId).ThenBy(x=>x.CollectionId).ToArrayAsync(),CollectionRecords=await db.CollectionRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.RecordId).ToArrayAsync(),CollectionFolders=await db.CollectionFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.FolderId).ToArrayAsync(),Grants=await db.CollectionGrants.AsNoTracking().Where(x=>x.RecipientAccountId==s.InternalId||db.Collections.Any(c=>c.Id==x.CollectionId&&c.AccountId==s.InternalId)).OrderBy(x=>x.Id).ToArrayAsync()});
    }
    private static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,ProtectionFixture.Json);
    private static byte[] Malformed(byte[] raw,string kind){var value=Encoding.UTF8.GetString(raw);return Encoding.UTF8.GetBytes(kind switch{"numericString"=>value.Replace("\"keyEpoch\":1","\"keyEpoch\":\"1\""),"unknown"=>value.Replace("{","{\"secret\":1,"),"duplicate"=>value.Replace("{","{\"keyEpoch\":1,"),_=>value.Replace("\"format\"","\"Format\"")});}
    private static TaskCompletionSource SignalSource()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Past(DateTimeOffset deadline){var delay=deadline-DateTimeOffset.UtcNow;if(delay>TimeSpan.Zero)await Task.Delay(delay+TimeSpan.FromMilliseconds(80));}
    private static Task Hold(AppDbContext db,string table,ProfileSetup.State s,VaultCollection c,Profile p)=>table switch{"account"=>db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE"),"profile"=>db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE id={p.Id} FOR UPDATE"),"vault_access_session"=>db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE handle_verifier={s.Verifier} FOR UPDATE"),_=>db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection WHERE id={c.Id} FOR UPDATE")};
    private static bool IsWrite(DbCommand command)=>command.CommandText.Contains("UPDATE cerberus.collection AS");
    private sealed class Signal(TaskCompletionSource reached,string table):DbCommandInterceptor
    {
        private void Check(DbCommand c){if(c.CommandText.Contains("FROM cerberus."+table+" ")&&(c.CommandText.Contains("FOR UPDATE")||c.CommandText.Contains("FOR NO KEY UPDATE")))reached.TrySetResult();}
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<int> r,CancellationToken ct=default){Check(c);return ValueTask.FromResult(r);}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<DbDataReader> r,CancellationToken ct=default){Check(c);return ValueTask.FromResult(r);}
    }
    private sealed class BeforeWrite(Func<DbCommand,Task> action):DbCommandInterceptor
    {private bool used;public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<int> r,CancellationToken ct=default){if(IsWrite(c)&&!used){used=true;await action(c);}return r;}}
    private sealed class AfterWrite(string kind,VaultCollection target):DbCommandInterceptor
    {
        private bool wrote;
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData e,int result,CancellationToken ct=default)
        {
            if(IsWrite(c)&&result>0)
            {
                wrote=true;if(kind!="failure")
                {
                    await using var change=c.Connection!.CreateCommand();change.Transaction=c.Transaction;change.CommandText=kind switch{"revision"=>"UPDATE cerberus.collection SET revision=revision+1 WHERE id=@id","time"=>"UPDATE cerberus.collection SET edited_at='2000-01-01' WHERE id=@id","envelope"=>"UPDATE cerberus.collection SET envelope='bad'::bytea WHERE id=@id","epoch"=>"UPDATE cerberus.collection SET key_epoch=2 WHERE id=@id",_=>"UPDATE cerberus.collection SET concurrency_stamp=@stamp WHERE id=@id"};change.Parameters.Add(new NpgsqlParameter("id",target.Id));change.Parameters.Add(new NpgsqlParameter("stamp",target.ConcurrencyStamp));await change.ExecuteNonQueryAsync(ct);
                }
            }
            return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<DbDataReader> r,CancellationToken ct=default){if(wrote&&kind=="failure")throw new TimeoutException("synthetic post-write outage");return ValueTask.FromResult(r);}
    }
    private sealed class PauseRotation(TaskCompletionSource reached,TaskCompletionSource release):DbCommandInterceptor
    {private bool used;public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<DbDataReader> r,CancellationToken ct=default){if(!used&&c.CommandText.Contains("FROM cerberus.collection ")&&c.CommandText.Contains("FOR UPDATE")){used=true;reached.TrySetResult();await release.Task.WaitAsync(ct);}return r;}}
    private sealed class ThrowingFactory(Exception exception):IDbContextFactory<AppDbContext>
    {public AppDbContext CreateDbContext()=>throw exception;public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct=default)=>Task.FromException<AppDbContext>(exception);}
    private sealed class Capture:DbCommandInterceptor
    {
        public string Sql{get;private set;}="";public NpgsqlParameter[] Parameters{get;private set;}=[];public List<string> Locks{get;}=[];public List<object> LockValues{get;}=[];public List<object> AccountLockValues{get;}=[];
        private void Note(DbCommand c){if(c.CommandText.Contains("FOR UPDATE")||c.CommandText.Contains("FOR NO KEY UPDATE")||c.CommandText.Contains("FOR SHARE")){Locks.Add(c.CommandText);LockValues.AddRange(c.Parameters.Cast<DbParameter>().Select(p=>p.Value!));if(c.CommandText.Contains("FROM cerberus.account "))AccountLockValues.AddRange(c.Parameters.Cast<DbParameter>().Select(p=>p.Value!));}}
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<int> r,CancellationToken ct=default){Note(c);return ValueTask.FromResult(r);}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<DbDataReader> r,CancellationToken ct=default){Note(c);if(Sql.Length==0&&c.CommandText.StartsWith("WITH actor")){Sql=c.CommandText;Parameters=c.Parameters.Cast<NpgsqlParameter>().Select(p=>p.Clone()).ToArray();}return ValueTask.FromResult(r);}
    }
}
