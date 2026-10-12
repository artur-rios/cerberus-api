using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Command.Profiles;
using ArturRios.Cerberus.Command.Records;
using ArturRios.Cerberus.Command.Collections;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Query.Records;
using ArturRios.Cerberus.Query.Collections;
using ArturRios.Cerberus.Query.Folders;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Configuration.Enums;
using ArturRios.Output;
using ArturRios.Util.Test.Functional;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArturRios.Cerberus.WebApi.Tests;

using System.Text.Json.Nodes;
using Npgsql;

[Collection("Registration host")]
public class CollectionUpdateHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{

    [FunctionalTheory][InlineData("wide")][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenNativeOwnedAccess_WhenReplacing_ThenOnlyEnvelopeAndFourMetadataFieldsChange(string mode)
    {
        using var owner = new ProtectionFixture(); using var key = new ProtectionFixture(); using var recipient = new ProtectionFixture();
        var s = await Setup(owner); var other = await Setup(recipient); var p = await Profile(s, owner, key, mode: mode == "wide" ? "Master" : mode);
        var folder = await Folder(s); var record = await Record(s, folder.Id);
        var target = await Create(s, [p.PublicId], [record.PublicId], [folder.PublicId]);
        await Grant(s, owner, other, recipient, target.CollectionId, CollectionGrantAccess.ReadWrite);
        var actor = mode == "wide" ? s : s with { Access = await Open(s, p, key) };
        var before = await Unrelated(s, target.CollectionId); var recipientBefore = await Snapshot(other);
        await using var db = fixture.Context(); var original = await db.Collections.AsNoTracking().SingleAsync(x => x.PublicId == target.CollectionId);
        var input = Replacement();
        using (var response = await Update(actor, target.CollectionId, input)) await Changed(s, target, input, await Success<UpdateCollectionOutput>(response, 200), original.ServerSequence);
        Assert.Equal(before, await Unrelated(s, target.CollectionId)); Assert.Equal(recipientBefore, await Snapshot(other));
        using var get = await Read(actor, "/api/collections/" + target.CollectionId); var detail = await Success<CollectionDetailsOutput>(get, 200);
        Assert.Equal(input.Envelope, detail.Envelope); Assert.Equal(new[] { p.PublicId }, detail.ProfileIds); Assert.Equal(new[] { record.PublicId }, detail.RecordIds); Assert.Equal(new[] { folder.PublicId }, detail.FolderIds);
        using var shared = await Read(other, "/api/collections/" + target.CollectionId); Assert.Equal(input.Envelope, (await Success<CollectionDetailsOutput>(shared, 200)).Envelope);
        var changed = await db.Collections.AsNoTracking().SingleAsync(x => x.Id == original.Id);
        Assert.NotEqual(original.ConcurrencyStamp, changed.ConcurrencyStamp); Assert.Equal(original.KeyEpoch, changed.KeyEpoch); Assert.Equal(original.DeletedAt, changed.DeletedAt); Assert.Equal(original.PurgeAt, changed.PurgeAt);
    }

    [FunctionalTheory][InlineData(CollectionGrantAccess.ReadOnly, "wide")][InlineData(CollectionGrantAccess.ReadWrite, "wide")]
    [InlineData(CollectionGrantAccess.ReadOnly, "Master")][InlineData(CollectionGrantAccess.ReadWrite, "Master")]
    [InlineData(CollectionGrantAccess.ReadOnly, "PerProfile")][InlineData(CollectionGrantAccess.ReadWrite, "PerProfile")]
    public async Task GivenVisibleNativeRecipient_WhenReplacingWithStaleRevision_ThenOwnerOnly403WithoutMutation(CollectionGrantAccess access, string mode)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var key = new ProtectionFixture();
        var o = await Setup(owner); var s = await Setup(recipient); var target = await Create(o); await Grant(o, owner, s, recipient, target.CollectionId, access);
        if (mode != "wide") { var p = await Profile(s, recipient, key, mode: mode, collections: [target.CollectionId]); s = s with { Access = await Open(s, p, key) }; }
        using (var get = await Read(s, "/api/collections/" + target.CollectionId)) await Success<CollectionDetailsOutput>(get, 200);
        var before = await Snapshot(o); var actorBefore = await Snapshot(s);
        using var response = await Update(s, target.CollectionId, Replacement(77)); await Failure(response, 403, "vault_access_denied");
        Assert.Equal(before, await Snapshot(o)); Assert.Equal(actorBefore, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("missing")][InlineData("foreign")][InlineData("unselected")][InlineData("deleted")]
    [InlineData("terminal")][InlineData("ownerClosing")][InlineData("ownerTerminal")][InlineData("revoked")][InlineData("grantTerminal")]
    public async Task GivenHiddenTargetWithCorruptEnvelope_WhenReplacing_Then404BeforeStaleRevisionOrEvidenceInspection(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var key = new ProtectionFixture();
        var o = await Setup(owner); var s = await Setup(recipient); var target = await Create(o);
        var grant = kind == "foreign" ? null : await Grant(o, owner, s, recipient, target.CollectionId);
        if (kind == "unselected") { var p = await Profile(s, recipient, key); s = s with { Access = await Open(s, p, key) }; }
        await using (var db = fixture.Context())
        {
            await db.Collections.Where(x => x.PublicId == target.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(c => c.Envelope, new byte[] { 1 }).SetProperty(c => c.DeletedAt, kind == "deleted" ? (DateTimeOffset?)DateTimeOffset.UtcNow : null));
            if (kind == "ownerClosing") await db.Accounts.Where(x => x.Id == o.InternalId).ExecuteUpdateAsync(x => x.SetProperty(a => a.State, AccountState.ClosurePending));
            if (kind == "revoked") await db.CollectionGrants.Where(x => x.Id == grant!.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.State, CollectionGrantState.Revoked));
            if (kind is "terminal" or "ownerTerminal" or "grantTerminal") { db.TerminalErasures.Add(new() { ResourceId = kind == "terminal" ? target.CollectionId : kind == "ownerTerminal" ? o.AccountId : grant!.PublicId, ResourceKind = kind == "terminal" ? "collection" : kind == "ownerTerminal" ? "account" : "grant", DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
        }
        var before = await Snapshot(o); var actorBefore = await Snapshot(s);
        using var response = await Update(s, kind == "missing" ? Guid.NewGuid() : target.CollectionId, Replacement(77)); await Failure(response, 404, "not_found");
        Assert.Equal(before, await Snapshot(o)); Assert.Equal(actorBefore, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("wrapper")][InlineData("ownerPins")][InlineData("recipientPins")][InlineData("epoch")][InlineData("revision")]
    public async Task GivenCorruptRelevantNativeEvidence_WhenReplacing_Then503BeforeOwnerOnlyDenial(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var o = await Setup(owner); var s = await Setup(recipient);
        var target = await Create(o); var grant = await Grant(o, owner, s, recipient, target.CollectionId, CollectionGrantAccess.ReadWrite);
        await using (var db = fixture.Context())
        {
            if (kind == "wrapper") await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.RecipientKeyEnvelope, new byte[] { 1 }));
            if (kind is "ownerPins" or "recipientPins") await db.VaultProtections.Where(x => x.AccountId == (kind == "ownerPins" ? o.InternalId : s.InternalId)).ExecuteUpdateAsync(x => x.SetProperty(v => v.Material, new byte[] { 1 }));
            if (kind == "epoch") await db.Collections.Where(x => x.PublicId == target.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(v => v.KeyEpoch, 2));
            if (kind == "revision") await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revision, 2));
        }
        var before = await Snapshot(o); var actorBefore = await Snapshot(s); using var response = await Update(s, target.CollectionId, Replacement(77));
        await Failure(response, 503, "persistence_unavailable"); Assert.Equal(before, await Snapshot(o)); Assert.Equal(actorBefore, await Snapshot(s));
    }

    [FunctionalFact]
    public async Task GivenOwnerWithCorruptGrantAndUnrelatedCiphertext_WhenReplacing_ThenUpdateIndependentlyOfGrants()
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var other = await Setup(recipient);
        var target = await Create(s); var unrelated = await Create(s); var grant = await Grant(s, owner, other, recipient, target.CollectionId);
        await using (var db = fixture.Context())
        {
            await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.RecipientKeyEnvelope, new byte[] { 1 }));
            await db.Collections.Where(x => x.PublicId == unrelated.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(v => v.Envelope, new byte[] { 1 }));
        }
        var before = await Unrelated(s, target.CollectionId); var input = Replacement(); using var response = await Update(s, target.CollectionId, input);
        await Changed(s, target, input, await Success<UpdateCollectionOutput>(response, 200)); Assert.Equal(before, await Unrelated(s, target.CollectionId));
    }

    [FunctionalTheory][InlineData("envelope")][InlineData("sequence")][InlineData("revision")][InlineData("outage")]
    public async Task GivenCorruptOwnedTargetOrDatabaseOutage_WhenReplacing_Then503WithoutMutation(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var target = await Create(s); await using var db = fixture.Context();
        if (kind == "envelope") await db.Collections.Where(x => x.PublicId == target.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(r => r.Envelope, new byte[] { 1 }));
        if (kind == "sequence") await db.Collections.Where(x => x.PublicId == target.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(r => r.ServerSequence, 0));
        if (kind == "revision") await db.Collections.Where(x => x.PublicId == target.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(r => r.Revision, 0));
        var before = await Snapshot(s);
        if (kind == "outage") await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.collection RENAME TO fixture_unavailable_collection");
        try { using var response = await Update(s, target.CollectionId, Replacement()); await Failure(response, 503, "persistence_unavailable"); }
        finally { if (kind == "outage") await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_unavailable_collection RENAME TO collection"); }
        Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("older")][InlineData("equal")][InlineData("pre2000")][InlineData("sameEnvelope")][InlineData("minimum")]
    public async Task GivenMatchingRevision_WhenReplacing_ThenAcceptRepresentableTimeAndIdenticalEnvelope(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var target = await Create(s);
        var input = Replacement() with { EditedAt = kind == "older" ? target.EditedAt.AddTicks(-11) : kind == "pre2000" ? new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(19) : kind == "minimum" ? new DateTimeOffset(10, TimeSpan.Zero) : target.EditedAt };
        if (kind == "sameEnvelope") input = input with { Envelope = target.Envelope };
        using (var response = await Update(s, target.CollectionId, input)) await Changed(s, target, input, await Success<UpdateCollectionOutput>(response, 200));
        using var get = await Read(s, "/api/collections/" + target.CollectionId); var result = await Success<CollectionDetailsOutput>(get, 200);
        Assert.Equal(2, result.Revision); Assert.Equal(Normalize(input.EditedAt), result.EditedAt); Assert.Equal(input.Envelope, result.Envelope);
    }

    [FunctionalFact]
    public async Task GivenLostSuccessfulResponse_WhenRetrying_Then409PreservesWinner()
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var target = await Create(s); var input = Replacement();
        using (var response = await Update(s, target.CollectionId, input)) await Changed(s, target, input, await Success<UpdateCollectionOutput>(response, 200));
        var before = await Snapshot(s); using var retry = await Update(s, target.CollectionId, input); await Failure(retry, 409, "revision_conflict");
        using var stale = await Update(s, target.CollectionId, Replacement()); await Failure(stale, 409, "revision_conflict"); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalFact]
    public async Task GivenConcurrentSameRevisionRequests_WhenReplacing_ThenExactlyOneWinner()
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var target = await Create(s); var first = Replacement(); var second = Replacement();
        var replies = await Task.WhenAll(Update(s, target.CollectionId, first), Update(s, target.CollectionId, second));
        try
        {
            var winner = Array.FindIndex(replies, r => r.StatusCode == HttpStatusCode.OK); Assert.InRange(winner, 0, 1);
            await Changed(s, target, winner == 0 ? first : second, await Success<UpdateCollectionOutput>(replies[winner], 200));
            await Failure(replies[1 - winner], 409, "revision_conflict");
        }
        finally { foreach (var response in replies) response.Dispose(); }
    }

    [FunctionalTheory][InlineData("policy",403)][InlineData("revoked",403)][InlineData("profile",403)][InlineData("link",404)][InlineData("deleted",404)][InlineData("terminal",404)]
    public async Task GivenAuthorityChangeWhileHttpRequestWaits_WhenReplacing_ThenCurrentStateWins(string kind, int status)
    {
        using var owner = new ProtectionFixture(); using var key = new ProtectionFixture(); var s = await Setup(owner); var target = await Create(s);
        var p = await Profile(s, owner, key, collections: [target.CollectionId]); var actor = s with { Access = await Open(s, p, key) };
        await using var held = fixture.Context(); await using var tx = await held.Database.BeginTransactionAsync();
        await held.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE id={s.InternalId} FOR NO KEY UPDATE");
        var pending = Update(actor, target.CollectionId, Replacement());
        await WaitForAccountLock(s); Assert.False(pending.IsCompleted);
        if (kind == "policy") await held.Accounts.Where(x => x.Id == s.InternalId).ExecuteUpdateAsync(x => x.SetProperty(a => a.PolicyRevision, a => a.PolicyRevision + 1));
        if (kind == "revoked") await held.VaultAccessSessions.Where(x => x.AccountId == s.InternalId).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revoked, true));
        if (kind == "profile") await held.Profiles.Where(x => x.Id == p.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.DeletedAt, DateTimeOffset.UtcNow));
        if (kind == "link") await held.ProfileCollections.Where(x => x.ProfileId == p.Id).ExecuteDeleteAsync();
        if (kind == "deleted") await held.Collections.Where(x => x.PublicId == target.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(v => v.DeletedAt, DateTimeOffset.UtcNow));
        if (kind == "terminal") { held.TerminalErasures.Add(new() { ResourceId = target.CollectionId, ResourceKind = "collection", DeletedAt = DateTimeOffset.UtcNow }); await held.SaveChangesAsync(); }
        await tx.CommitAsync(); var before = await Snapshot(s); using var response = await pending.WaitAsync(TimeSpan.FromSeconds(15));
        await Failure(response, status, status == 403 ? "vault_access_denied" : "not_found"); Assert.Equal(before, await Snapshot(s));
    }

    private async Task WaitForAccountLock(State s)
    {
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync(); var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%FROM cerberus.account %' AND query LIKE '%FOR NO KEY UPDATE%')", connection);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) return;
            await Task.Delay(20, timeout.Token);
        }
    }

    private static CollectionUpdateInput Replacement(long revision = 1) => new(revision, Envelope(), DateTimeOffset.Parse("2026-10-11T00:00:00Z").AddTicks(19));
    private static HttpRequestMessage Put(State s, string path, byte[] body) { var request = Request(s, body, path); request.Method = HttpMethod.Put; return request; }
    private async Task<HttpResponseMessage> Update(State s, Guid id, CollectionUpdateInput input) { using var request = Put(s, "/api/collections/" + id, Bytes(input)); return await Gateway.Client.SendAsync(request); }
    private async Task Changed(State s, CollectionCreateInput target, CollectionUpdateInput input, UpdateCollectionOutput output, long oldSequence = 0)
    {
        Assert.Equal(target.CollectionId, output.CollectionId); Assert.Equal(input.ExpectedRevision + 1, output.Revision); Assert.Equal(Normalize(input.EditedAt), output.EditedAt);
        Assert.InRange(output.ServerSequence, oldSequence + 1, ProtocolBinary.MaxInteger);
        await using var db = fixture.Context(); var row = await db.Collections.SingleAsync(x => x.PublicId == target.CollectionId);
        Assert.Equal(s.InternalId, row.AccountId); Assert.Equal(output.Revision, row.Revision); Assert.Equal(output.ServerSequence, row.ServerSequence);
        Assert.Equal(output.EditedAt, row.EditedAt); Assert.Equal(input.Envelope.KeyEpoch, row.KeyEpoch); Assert.Equal(input.Envelope, JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope, ProtectionFixture.Json));
    }
    private async Task<string> Unrelated(State s, Guid target)
    {
        var node = JsonNode.Parse(await Snapshot(s))!.AsObject(); var rows = node["Collections"]!.AsArray();
        node["Collections"] = new JsonArray(rows.Where(r => r!["PublicId"]!.GetValue<Guid>() != target).Select(r => r!.DeepClone()).ToArray()); return node.ToJsonString();
    }

    [FunctionalTheory][InlineData("bad")][InlineData("zero")][InlineData("uppercase")][InlineData("compact")][InlineData("braced")][InlineData("urn")]
    public async Task GivenNoncanonicalRoute_WhenReplacing_Then400WithoutMutation(string kind)
    {using var owner=new ProtectionFixture();var s=await Setup(owner);var g=Guid.Parse("abcdefab-1234-4321-abcd-123456789abc");var id=kind switch{"bad"=>"bad","zero"=>Guid.Empty.ToString(),"uppercase"=>g.ToString().ToUpperInvariant(),"compact"=>g.ToString("N"),"braced"=>g.ToString("B"),_=>"urn:uuid:"+g};var before=await Snapshot(s);using var req=Put(s,"/api/collections/"+id,Bytes(Replacement()));using var r=await Gateway.Client.SendAsync(req);await Failure(r,400,"validation_failed");Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("unknown")][InlineData("owner")][InlineData("parent")][InlineData("record")][InlineData("profile")][InlineData("folder")][InlineData("collection")][InlineData("name")][InlineData("duplicate")][InlineData("case")][InlineData("numericRevision")][InlineData("numericEpoch")][InlineData("missingRevision")][InlineData("missingEnvelope")][InlineData("missingTime")][InlineData("null")][InlineData("empty")][InlineData("format")][InlineData("epoch")][InlineData("revision")][InlineData("unsafeRevision")][InlineData("time")][InlineData("offset")][InlineData("saltReuse")][InlineData("nonceReuse")]
    public async Task GivenInvalidOrForgedBody_WhenReplacing_Then400AndNoChanges(string kind)
    {
        using var owner=new ProtectionFixture();var s=await Setup(owner);var target=await Create(s);var i=Replacement();if(kind=="format")i=i with{Envelope=i.Envelope with{Format="unknown"}};if(kind=="epoch")i=i with{Envelope=i.Envelope with{KeyEpoch=2}};if(kind=="revision")i=i with{ExpectedRevision=0};if(kind=="unsafeRevision")i=i with{ExpectedRevision=ProtocolBinary.MaxInteger+1};if(kind=="time")i=i with{EditedAt=new(9,TimeSpan.Zero)};if(kind=="offset")i=i with{EditedAt=i.EditedAt.ToOffset(TimeSpan.FromHours(1))};if(kind=="saltReuse")i=i with{Envelope=i.Envelope with{KeySalt=target.Envelope.KeySalt}};if(kind=="nonceReuse")i=i with{Envelope=i.Envelope with{Nonce=target.Envelope.Nonce}};
        var body=JsonSerializer.SerializeToNode(i,ProtectionFixture.Json)!.AsObject();if(kind=="missingRevision")body.Remove("expectedRevision");if(kind=="missingEnvelope")body.Remove("envelope");if(kind=="missingTime")body.Remove("editedAt");var field=kind switch{"unknown"=>"secret","owner"=>"ownerId","parent"=>"parentFolderId","record"=>"recordId","profile"=>"profileIds","folder"=>"folderId","collection"=>"collectionId","name"=>"name",_=>null};if(field is not null)body[field]=null;var raw=body.ToJsonString();raw=kind switch{"null"=>"null","empty"=>"{}","duplicate"=>raw.Insert(1,"\"expectedRevision\":1,"),"case"=>raw.Replace("expectedRevision","ExpectedRevision"),"numericRevision"=>raw.Replace("\"expectedRevision\":1","\"expectedRevision\":\"1\""),"numericEpoch"=>raw.Replace("\"keyEpoch\":1","\"keyEpoch\":\"1\""),_=>raw};
        var before=await Snapshot(s);using var request=Put(s,"/api/collections/"+target.CollectionId,Encoding.UTF8.GetBytes(raw));using var r=await Gateway.Client.SendAsync(request);await Failure(r,400);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("query",400)][InlineData("duplicate",400)][InlineData("bad",400)][InlineData("missing",401)][InlineData("utf8",400)][InlineData("bom",400)][InlineData("compression",400)][InlineData("charset",400)][InlineData("oversized",413)]
    public async Task GivenInvalidTransportOrAccess_WhenReplacing_ThenRejectWithoutMutation(string kind,int status)
    {
        using var owner=new ProtectionFixture();var s=await Setup(owner);var target=await Create(s);var before=await Snapshot(s);var raw=kind=="oversized"?Encoding.UTF8.GetBytes(new string(' ',65537)):kind=="utf8"?new byte[]{0xc3,0x28}:Bytes(Replacement());if(kind=="bom")raw=new byte[]{239,187,191}.Concat(raw).ToArray();using var req=Put(s,"/api/collections/"+target.CollectionId+(kind=="query"?"?ownerId=secret":""),raw);if(kind=="missing")req.Headers.Remove("X-Cerberus-Vault-Access");if(kind=="bad"){req.Headers.Remove("X-Cerberus-Vault-Access");req.Headers.Add("X-Cerberus-Vault-Access","bad");}if(kind=="duplicate")req.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access",s.Access);if(kind=="compression")req.Content!.Headers.ContentEncoding.Add("gzip");if(kind=="charset")req.Content!.Headers.ContentType!.CharSet="utf-16";using var r=await Gateway.Client.SendAsync(req);await Failure(r,status);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenRawEmptyAccessHeader_WhenReplacing_Then400()
    {using var owner=new ProtectionFixture();var s=await Setup(owner);using var host=new WebApplicationFactory<Program>();var raw=Bytes(Replacement());var context=await host.Server.SendAsync(c=>{c.Request.Method="PUT";c.Request.Path="/api/collections/"+Guid.NewGuid();c.Request.Headers.Authorization="Bearer "+RegistrationApiFixture.Token(s.Actor);c.Request.Headers["X-Cerberus-Vault-Access"]="";c.Request.ContentType="application/json";c.Request.ContentLength=raw.Length;c.Request.Body=new MemoryStream(raw);});Assert.Equal(400,context.Response.StatusCode);Assert.Contains("no-store",context.Response.Headers.CacheControl.ToString());}
    [FunctionalTheory][InlineData("missing",401)][InlineData("scope",401)][InlineData("deleted",401)][InlineData("unavailable",503)]
    public async Task GivenInvalidCurrentIdentity_WhenReplacing_ThenRejectWithoutPersistence(string kind,int status)
    {using var owner=new ProtectionFixture();var s=await Setup(owner);var target=await Create(s);if(kind is "deleted" or "unavailable")fixture.Users[s.Actor+"@example.test"]=new(s.Actor,"fixture-password",Deleted:kind=="deleted",IdentityUnavailable:kind=="unavailable");var before=await Snapshot(s);using var req=Put(s,"/api/collections/"+target.CollectionId,Bytes(Replacement()));if(kind=="missing")req.Headers.Authorization=null;if(kind=="scope")req.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor,Guid.NewGuid()));using var r=await Gateway.Client.SendAsync(req);await Failure(r,status);Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("closing",404)][InlineData("terminal",404)][InlineData("revoked",403)][InlineData("expired",403)][InlineData("future",403)][InlineData("policy",403)][InlineData("generation",403)][InlineData("dangling",403)]
    public async Task GivenInvalidCurrentActorState_WhenReplacing_ThenNoMutation(string kind,int status)
    {using var owner=new ProtectionFixture();var s=await Setup(owner);var target=await Create(s);await using(var db=fixture.Context()){var v=await db.VaultAccessSessions.SingleAsync(x=>x.AccountId==s.InternalId);if(kind=="revoked")v.Revoked=true;if(kind=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);if(kind=="future")v.IssuedAt=DateTimeOffset.UtcNow.AddMinutes(1);if(kind=="policy")v.PolicyRevision++;if(kind=="generation")v.RevocationGeneration++;if(kind=="dangling")v.ProfileId=long.MaxValue;if(kind=="closing")await db.Accounts.Where(x=>x.Id==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.State,AccountState.ClosurePending));if(kind=="terminal")db.TerminalErasures.Add(new(){ResourceId=s.AccountId,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}var before=await Snapshot(s);using var r=await Update(s,target.CollectionId,Replacement());await Failure(r,status);Assert.Equal(before,await Snapshot(s));}
    private async Task<CollectionGrant> Grant(State owner, ProtectionFixture author, State recipient, ProtectionFixture recipientKeys, Guid collectionId, CollectionGrantAccess access = CollectionGrantAccess.ReadOnly)
    {
        await using var db = fixture.Context(); var collection = await db.Collections.SingleAsync(x => x.PublicId == collectionId);
        var grant = new CollectionGrant { PublicId = Guid.NewGuid(), CollectionId = collection.Id, RecipientAccountId = recipient.InternalId, Access = access };
        grant.RecipientKeyEnvelope = Bytes(author.WrapGrant(owner.AccountId, collectionId, grant.PublicId, recipient.Actor, recipientKeys.Material.RecipientKey));
        db.CollectionGrants.Add(grant); await db.SaveChangesAsync(); return grant;
    }

    private async Task<CollectionCreateInput> Create(State s, Guid[]? profiles = null, Guid[]? records = null, Guid[]? folders = null)
    {
        var input = new CollectionCreateInput(Guid.NewGuid(), Envelope(), DateTimeOffset.UnixEpoch.AddTicks(19), profiles ?? [], records ?? [], folders ?? []);
        using var response = await Send(s, input); await Success<CreateCollectionOutput>(response, 201); return input;
    }
    private async Task<VaultRecord> Record(State s, long? folder = null)
    {
        var record = new VaultRecord { PublicId = Guid.NewGuid(), AccountId = s.InternalId, FolderId = folder, Envelope = Bytes(Envelope()), EditedAt = DateTimeOffset.UnixEpoch };
        await using var db = fixture.Context(); db.Records.Add(record); await db.SaveChangesAsync(); return record;
    }
    private sealed class BodyFeature:IHttpRequestBodyDetectionFeature{public bool CanHaveBody=>true;}
    private sealed record State(Guid Actor,Guid AccountId,long InternalId,string Access);
    private async Task<State> Setup(ProtectionFixture client)
    {var actor=Guid.NewGuid();fixture.Users[actor+"@example.test"]=new(actor,"fixture-password");await using var db=fixture.Context();var account=new Account{PublicId=Guid.NewGuid(),HeimdallPublicId=actor,DetailsEnvelope=Bytes(Envelope())};db.Accounts.Add(account);await db.SaveChangesAsync();var access=ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32));OpaqueAccessHandle.TryHash(access,out var verifier);db.VaultProtections.Add(new(){AccountId=account.Id,Material=Bytes(client.Material)});db.VaultAccessSessions.Add(new(){AccountId=account.Id,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});await db.SaveChangesAsync();return new(actor,account.PublicId,account.Id,access);}
    private async Task<VaultFolder> Folder(State s,long? parent=null){var f=new VaultFolder{PublicId=Guid.NewGuid(),AccountId=s.InternalId,ParentFolderId=parent,Envelope=Bytes(Envelope()),EditedAt=DateTimeOffset.UnixEpoch};await using var db=fixture.Context();db.Folders.Add(f);await db.SaveChangesAsync();return f;}
    private async Task<Profile> Profile(State s,ProtectionFixture owner,ProtectionFixture scoped,VaultFolder? folder=null,string mode="Master",Guid[]? collections=null,Guid[]? records=null)
    {var id=Guid.NewGuid();var input=new ProfileCreateInput(id,Envelope(),new(mode,scoped.Material.UnlockVerifier,owner.Wrap(s.AccountId,"profile",id,s.Actor),mode=="PerProfile"?scoped.Material.PasswordWrapper:null),DateTimeOffset.UnixEpoch,records??[],folder is null?[]:[folder.PublicId],collections??[]);using var request=Request(s,Bytes(input),"/api/profiles");using var r=await Gateway.Client.SendAsync(request);Assert.Equal(HttpStatusCode.Created,r.StatusCode);await using var db=fixture.Context();return await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==id);}
    private async Task<string> Open(State s,Profile p,ProtectionFixture scoped)
    {await using var db=fixture.Context();var revision=(await db.Profiles.AsNoTracking().SingleAsync(x=>x.Id==p.Id)).Revision;var raw=Bytes(new{expectedRevision=revision});using var challengeRequest=Request(s,Bytes(new{expectedRevision=revision,requestHash=ProtocolBinary.Encode(SHA256.HashData(raw))}),"/api/profiles/"+p.PublicId+"/access/challenges");challengeRequest.Headers.Remove("X-Cerberus-Vault-Access");using var issued=await Gateway.Client.SendAsync(challengeRequest);var challenge=(await Success<IssueProfileAccessChallengeOutput>(issued,201)).Challenge;using var open=Request(s,raw,"/api/profiles/"+p.PublicId+"/access");open.Headers.Remove("X-Cerberus-Vault-Access");open.Headers.Add("X-Cerberus-Challenge-Id",challenge.ChallengeId.ToString());open.Headers.Add("X-Cerberus-Proof",scoped.Sign(challenge));using var response=await Gateway.Client.SendAsync(open);return (await Success<OpenProfileAccessOutput>(response,200)).VaultAccess;}
    private static CollectionCreateInput Input(Guid[]? profiles=null)=>new(Guid.NewGuid(),Envelope(),DateTimeOffset.UnixEpoch.AddTicks(19),profiles??[],[],[]);
    private static EncryptedEnvelope Envelope(long epoch=1)=>new("cerberus-content-v1",epoch,ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32)),ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(12)),ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(73)),ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(16)));
    private static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,ProtectionFixture.Json);
    private static DateTimeOffset Normalize(DateTimeOffset value)=>value.AddTicks(-(value.Ticks%10));
    private static HttpRequestMessage Request(State s,byte[] body,string path="/api/collections"){var r=new HttpRequestMessage(HttpMethod.Post,path){Content=new ByteArrayContent(body)};r.Content.Headers.ContentType=new("application/json");r.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor));r.Headers.Add("X-Cerberus-Vault-Access",s.Access);return r;}
    private async Task<HttpResponseMessage> Send(State s,CollectionCreateInput input){using var request=Request(s,Bytes(input));return await Gateway.Client.SendAsync(request);}
    private async Task<HttpResponseMessage> Read(State s,string path){using var request=new HttpRequestMessage(HttpMethod.Get,path);request.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor));request.Headers.Add("X-Cerberus-Vault-Access",s.Access);return await Gateway.Client.SendAsync(request);}
    private async Task<string> Protected(State s){await using var db=fixture.Context();return JsonSerializer.Serialize(new{Account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.InternalId),Protection=await db.VaultProtections.AsNoTracking().SingleAsync(x=>x.AccountId==s.InternalId),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync()});}
    private static async Task<T> Success<T>(HttpResponseMessage response, int status) where T : class
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var text = await response.Content.ReadAsStringAsync(); using var document = JsonDocument.Parse(text);
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        if (typeof(T) == typeof(UpdateCollectionOutput)) {
            Assert.Equal(new[] { "collectionId", "editedAt", "revision", "serverSequence" }, document.RootElement.GetProperty("data").EnumerateObject().Select(x => x.Name).Order());
            Assert.Contains("collection_updated", document.RootElement.GetProperty("messages").EnumerateArray().Select(x => x.GetString()));
        }
        if (typeof(T) == typeof(CollectionDetailsOutput)) {
            var data = document.RootElement.GetProperty("data");
            Assert.Equal(new[] { "collectionId", "editedAt", "envelope", "folderIds", "profileIds", "recordIds", "revision", "serverSequence" }, data.EnumerateObject().Select(x => x.Name).Order());
            Assert.Contains("collection_found", document.RootElement.GetProperty("messages").EnumerateArray().Select(x => x.GetString()));
        }
        return Assert.IsType<T>(JsonSerializer.Deserialize<DataOutput<T>>(text, ProtectionFixture.Json)!.Data);
    }
    private static void Fields(object value,params string[] names){using var d=JsonDocument.Parse(JsonSerializer.Serialize(value,ProtectionFixture.Json));Assert.Equal(names.Order(),d.RootElement.EnumerateObject().Select(x=>x.Name).Order());}
    private static async Task Failure(HttpResponseMessage r,int status,string? error=null){Assert.Equal((HttpStatusCode)status,r.StatusCode);Assert.True(r.Headers.CacheControl?.NoStore);if(status==413){Assert.Equal("",await r.Content.ReadAsStringAsync());return;}using var d=JsonDocument.Parse(await r.Content.ReadAsStringAsync());Assert.False(d.RootElement.GetProperty("success").GetBoolean());if(d.RootElement.TryGetProperty("data",out var data))Assert.Equal(JsonValueKind.Null,data.ValueKind);if(error is not null)Assert.Contains(error,d.RootElement.GetProperty("errors").EnumerateArray().Select(x=>x.GetString()));}
    private async Task<string> Snapshot(State s,bool skipMembers=false){await using var db=fixture.Context();return JsonSerializer.Serialize(new{Protected=await Protected(s),Collections=await db.Collections.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Grants=await db.CollectionGrants.AsNoTracking().Where(x=>x.RecipientAccountId==s.InternalId || db.Collections.Any(c=>c.Id==x.CollectionId && c.AccountId==s.InternalId)).OrderBy(x=>x.Id).ToArrayAsync(),Members=await db.CollectionRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.RecordId).ToArrayAsync(),CollectionFolders=skipMembers?[]:await db.CollectionFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.FolderId).ToArrayAsync(),ProfileCollections=await db.ProfileCollections.AsNoTracking().Where(x=>db.Profiles.Any(p=>p.Id==x.ProfileId && p.AccountId==s.InternalId)).OrderBy(x=>x.ProfileId).ThenBy(x=>x.CollectionId).ToArrayAsync(),Records=await db.Records.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Folders=await db.Folders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),ProfileFolders=await db.ProfileFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.FolderId).ToArrayAsync(),ProfileRecords=await db.ProfileRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.RecordId).ToArrayAsync()});}
}
