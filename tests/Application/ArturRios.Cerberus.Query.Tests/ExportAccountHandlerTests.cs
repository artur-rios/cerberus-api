using System.Runtime.CompilerServices;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Query.Accounts;
using Moq;

namespace ArturRios.Cerberus.Query.Tests;

public sealed class ExportAccountHandlerTests
{
    private const string Handle="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Verifier="66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925";
    private static readonly Guid Actor=Guid.Parse("527a1001-8ef5-4c9b-a565-222222222222");
    private static readonly Guid Account=Guid.Parse("527a1001-8ef5-4c9b-a565-333333333333");
    private static readonly Guid Foreign=Guid.Parse("527a1001-8ef5-4c9b-a565-444444444444");
    private static readonly DateTimeOffset Now=new(2026,10,15,12,0,0,TimeSpan.Zero);
    private static AccountExportHeader Header=>new(Account,Actor,3,2,4,true,86400,Now);
    private static ExportAccountQuery Query=>new(Actor,Now.ToUnixTimeSeconds(),Handle,3);

    [UnitTheory]
    [InlineData("actor","authentication_required")]
    [InlineData("missing","vault_access_required")]
    [InlineData("malformed","validation_failed")]
    [InlineData("padding","validation_failed")]
    [InlineData("noncanonical","validation_failed")]
    [InlineData("zeroIat","fresh_authentication_required")]
    [InlineData("negativeIat","fresh_authentication_required")]
    public async Task GivenInvalidAuthority_WhenExporting_ThenRejectBeforeOpeningPersistence(string invalid,string error)
    {
        // Given: a strict store makes any unauthorized persistence call observable.
        var store=new Mock<IAccountExportStore>(MockBehavior.Strict);
        var access=invalid switch{"missing"=>null,"malformed"=>"invalid","padding"=>Handle+"=","noncanonical"=>Handle[..^1]+"B",_=>Handle};
        var query=new ExportAccountQuery(invalid=="actor"?Guid.Empty:Actor,invalid=="zeroIat"?0:invalid=="negativeIat"?-1:Now.ToUnixTimeSeconds(),access,3);
        // When/Then: reject without producing an export lease.
        var result=await new ExportAccountHandler(store.Object).HandleAsync(query);
        Assert.Equal([error],result.Errors);Assert.Null(result.Data);
    }

    [UnitTheory][InlineData(0L)][InlineData(-1L)][InlineData(9007199254740992L)][InlineData(long.MaxValue)]
    public async Task GivenUnsafeExpectedRevision_WhenExporting_ThenRejectWithoutOpeningPersistence(long revision)
    {
        // Given/When: unsafe wire integers must not reach a reader.
        var result=await new ExportAccountHandler(new Mock<IAccountExportStore>(MockBehavior.Strict).Object)
            .HandleAsync(new(Actor,Now.ToUnixTimeSeconds(),Handle,revision));
        // Then: no archive is disclosed.
        Assert.Equal(["validation_failed"],result.Errors);Assert.Null(result.Data);
    }

    [UnitTheory][InlineData(1L)][InlineData(3L)][InlineData(9007199254740991L)]
    public async Task GivenCurrentOwner_WhenExporting_ThenBindHashedAccessAndTransferTheOwningLease(long revision)
    {
        // Given: an independently hand-checked SHA256 of the 32 zero handle bytes.
        var lease=new Lease(Header with{Revision=revision});var store=new Mock<IAccountExportStore>(MockBehavior.Strict);
        store.Setup(x=>x.OpenAsync(It.Is<AccountExportRequest>(r=>r.Actor==Actor&&r.AccessVerifier==Verifier
            &&r.AuthenticationIssuedAt==Now.ToUnixTimeSeconds()&&r.ExpectedRevision==revision),It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountExportOpenResult(lease));
        // When/Then: preserve ownership for the streaming consumer, without disposal.
        var result=await new ExportAccountHandler(store.Object).HandleAsync(new(Actor,Now.ToUnixTimeSeconds(),Handle,revision));
        Assert.True(result.Success);Assert.Same(lease,result.Data!.Lease);Assert.False(lease.Disposed);
    }

    [UnitTheory]
    [InlineData("authentication_required")][InlineData("vault_access_required")][InlineData("validation_failed")]
    [InlineData("fresh_authentication_required")][InlineData("vault_access_denied")][InlineData("not_found")]
    [InlineData("revision_conflict")][InlineData("persistence_unavailable")]
    public async Task GivenExpectedStoreRefusal_WhenExporting_ThenReturnOnlyTheSafeError(string error)
    {
        // Given/When: the store has not transferred a lease.
        var result=await Handler(new(Error:error)).HandleAsync(Query);
        // Then: no ciphertext or internal stream object accompanies the refusal.
        Assert.Equal([error],result.Errors);Assert.Null(result.Data);
    }

    [UnitTheory][InlineData("empty")][InlineData("unknown")][InlineData("mixed")]
    public async Task GivenIncoherentStoreResult_WhenExporting_ThenFailClosedAndDisposeAnyUnpublishedLease(string kind)
    {
        // Given: unknown dependency text or contradictory success/failure cannot escape.
        var lease=kind=="mixed"?new Lease(Header):null;
        var source=new AccountExportOpenResult(lease,kind=="empty"?null:kind=="mixed"?"not_found":"private-sql-credential");
        // When/Then: whitelist the failure and release any owned reader.
        var result=await Handler(source).HandleAsync(Query);
        Assert.Equal(["persistence_unavailable"],result.Errors);Assert.Null(result.Data);
        if(lease is not null)Assert.True(lease.Disposed);
    }

    [UnitTheory]
    [InlineData("foreignActor")][InlineData("emptyAccount")][InlineData("wrongRevision")]
    [InlineData("zeroPolicy")][InlineData("unsafeGeneration")][InlineData("enabledWithoutInterval")]
    [InlineData("disabledWithInterval")][InlineData("zeroInterval")][InlineData("unsafeInterval")]
    [InlineData("nonUtc")][InlineData("subMicrosecond")][InlineData("beforeEpoch")][InlineData("staleAuthentication")][InlineData("nullHeader")]
    public async Task GivenMalformedOrForeignCapturedHeader_WhenExporting_ThenWithholdTheArchiveAndReleaseTheLease(string invalid)
    {
        // Given: a producer must not turn malformed context into a usable archive.
        var header=invalid switch
        {
            "foreignActor"=>Header with{IdentityId=Foreign},"emptyAccount"=>Header with{AccountId=Guid.Empty},
            "wrongRevision"=>Header with{Revision=4},"zeroPolicy"=>Header with{PolicyRevision=0},
            "unsafeGeneration"=>Header with{RevocationGeneration=9007199254740992},
            "enabledWithoutInterval"=>Header with{RenewalIntervalSeconds=null},
            "disabledWithInterval"=>Header with{RenewalEnabled=false},"zeroInterval"=>Header with{RenewalIntervalSeconds=0},
            "unsafeInterval"=>Header with{RenewalIntervalSeconds=9007199254740992},
            "nonUtc"=>Header with{ServerTime=Now.ToOffset(TimeSpan.FromHours(1))},
            "subMicrosecond"=>Header with{ServerTime=Now.AddTicks(1)},"beforeEpoch"=>Header with{ServerTime=DateTimeOffset.UnixEpoch.AddTicks(-10)},"nullHeader"=>null,
            _=>Header with{ServerTime=Now.AddSeconds(60)}
        };
        var lease=new Lease(header!);
        // When/Then: fail without exposing even the foreign header and close the reader.
        var result=await Handler(new(lease)).HandleAsync(Query);
        Assert.Equal(["persistence_unavailable"],result.Errors);Assert.Null(result.Data);Assert.True(lease.Disposed);
    }

    [UnitFact]
    public async Task GivenExplicitlyDisabledRenewal_WhenExporting_ThenAcceptANullIntervalWithoutInventingAnExpiry()
    {
        // Given/When: disabled policy is a supported distinct state.
        var lease=new Lease(Header with{RenewalEnabled=false,RenewalIntervalSeconds=null});
        var result=await Handler(new(lease)).HandleAsync(Query);
        // Then: the original captured policy/reader is preserved.
        Assert.True(result.Success);Assert.Same(lease,result.Data!.Lease);Assert.False(lease.Disposed);
    }

    [UnitFact]
    public async Task GivenFailureDisposingAnUnpublishedLease_WhenRejectingTheHeader_ThenReturnUnavailableWithoutPrivateText()
    {
        // Given/When: cleanup failure cannot publish a foreign lease or dependency text.
        var lease=new Lease(Header with{IdentityId=Foreign}){ThrowOnDispose=true};
        var result=await Handler(new(lease)).HandleAsync(Query);
        // Then: stable failure and attempted resource release.
        Assert.Equal(["persistence_unavailable"],result.Errors);Assert.Null(result.Data);Assert.True(lease.Disposed);
    }

    [UnitFact]
    public async Task GivenCallerCancellation_WhenExporting_ThenPropagateItBeforeOpeningPersistence()
    {
        // Given/When/Then: caller cancellation must not become a successful empty archive.
        using var ct=new CancellationTokenSource();ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new ExportAccountHandler(new Mock<IAccountExportStore>(MockBehavior.Strict).Object).HandleAsync(Query,ct.Token));
    }

    [UnitFact]
    public async Task GivenCancellationWhileOpening_WhenExporting_ThenPropagateTheOriginalCancellation()
    {
        // Given: the persistence boundary cancels the in-flight request.
        using var ct=new CancellationTokenSource();var store=new Mock<IAccountExportStore>(MockBehavior.Strict);
        store.Setup(x=>x.OpenAsync(It.IsAny<AccountExportRequest>(),ct.Token)).Returns(()=>{ct.Cancel();return Task.FromCanceled<AccountExportOpenResult>(ct.Token);});
        // When/Then: the cancellation remains visible to request lifetime management.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new ExportAccountHandler(store.Object).HandleAsync(Query,ct.Token));
    }

    [UnitFact]
    public async Task GivenCancellationAtLeaseHandoff_WhenExporting_ThenDisposeTheUnpublishedReaderAndPropagate()
    {
        // Given: cancellation wins immediately after the store transfers ownership.
        using var ct=new CancellationTokenSource();var lease=new Lease(Header);var store=new Mock<IAccountExportStore>(MockBehavior.Strict);
        store.Setup(x=>x.OpenAsync(It.IsAny<AccountExportRequest>(),ct.Token)).Returns(()=>{ct.Cancel();return Task.FromResult(new AccountExportOpenResult(lease));});
        // When/Then: a canceled request must neither publish nor leak that reader.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new ExportAccountHandler(store.Object).HandleAsync(Query,ct.Token));
        Assert.True(lease.Disposed);
    }

    private static ExportAccountHandler Handler(AccountExportOpenResult result)
    {
        var store=new Mock<IAccountExportStore>(MockBehavior.Strict);
        store.Setup(x=>x.OpenAsync(It.Is<AccountExportRequest>(r=>r.Actor==Actor&&r.AccessVerifier==Verifier&&r.ExpectedRevision==3),It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return new(store.Object);
    }
    private sealed class Lease(AccountExportHeader header):IAccountExportLease
    {
        public AccountExportHeader Header{get;}=header;
        public bool Disposed{get;private set;}
        public bool ThrowOnDispose{get;init;}
        public async IAsyncEnumerable<AccountExportEntry> ReadAsync([EnumeratorCancellation]CancellationToken ct)
        {await Task.CompletedTask;ct.ThrowIfCancellationRequested();yield break;}
        public ValueTask DisposeAsync(){Disposed=true;if(ThrowOnDispose)throw new IOException("private-sql-credential");return ValueTask.CompletedTask;}
    }
}
