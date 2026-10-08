using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class RegistrationStoreTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenNewRegistration_WhenBinding_ThenPersistOpaqueContentAndUniquePublicIdentity()
    {
        var request = Request();
        var identity = Guid.NewGuid();
        var result = await Store().RegisterAsync(request, _ => Verified(identity), default);
        Assert.Null(result.Error);
        Assert.False(result.Replayed);
        Assert.Equal(request.AccountId, result.AccountId);
        Assert.Equal(1, result.Revision);
        await using var context = fixture.CreateContext();
        var account = await context.Accounts.SingleAsync(x => x.PublicId == request.AccountId);
        Assert.Equal(identity, account.HeimdallPublicId);
        Assert.Equal(request.DetailsEnvelope, account.DetailsEnvelope);
        Assert.Equal(AccountState.Active, account.State);
        Assert.True(account.RenewalEnabled);
        Assert.Equal(TimeSpan.FromHours(24), account.RenewalInterval);
        var operation = await context.RegistrationOperations.SingleAsync(x => x.OperationId == request.OperationId);
        Assert.Equal(identity, operation.CompletedIdentityId);
    }

    [FunctionalFact]
    public async Task GivenCompletedOperation_WhenRetrying_ThenRequireCurrentProofAndPreserveOutcome()
    {
        var request = Request();
        var identity = Guid.NewGuid();
        var original = await Store().RegisterAsync(request, _ => Verified(identity), default);
        var denied = await Store().RegisterAsync(request, _ => Task.FromResult(new RegistrationIdentity(RegistrationIdentityStatus.Denied)), default);
        Assert.Equal("identity_proof_required", denied.Error);
        Assert.Null(denied.AccountId);
        var retry = await Store().RegisterAsync(request, _ => Verified(identity), default);
        Assert.True(retry.Replayed);
        Assert.Equal(original.AccountId, retry.AccountId);
        Assert.Equal(original.Revision, retry.Revision);
        await using var context = fixture.CreateContext();
        Assert.Equal(request.DetailsEnvelope, (await context.Accounts.SingleAsync(x => x.PublicId == request.AccountId)).DetailsEnvelope);
    }

    [FunctionalFact]
    public async Task GivenChangedOperationInput_WhenRetrying_ThenPreserveWinningCiphertext()
    {
        var request = Request();
        var identity = Guid.NewGuid();
        await Store().RegisterAsync(request, _ => Verified(identity), default);
        var result = await Store().RegisterAsync(request with { RequestFingerprint = new string('b', 64), DetailsEnvelope = [9, 9] },
            _ => throw new InvalidOperationException("Changed input must fail before the dependency call."), default);
        Assert.Equal("registration_conflict", result.Error);
        await using var context = fixture.CreateContext();
        Assert.Equal(request.DetailsEnvelope, (await context.Accounts.SingleAsync(x => x.PublicId == request.AccountId)).DetailsEnvelope);
    }

    [FunctionalFact]
    public async Task GivenConcurrentIdenticalRequests_WhenRegistering_ThenCommitOneAccount()
    {
        var request = Request();
        var identity = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Store().RegisterAsync(request, _ => Verified(identity), default)));
        Assert.All(results, result => Assert.Null(result.Error));
        Assert.Single(results, result => !result.Replayed);
        Assert.All(results, result => Assert.Equal(request.AccountId, result.AccountId));
        await using var context = fixture.CreateContext();
        Assert.Equal(1, await context.Accounts.CountAsync(x => x.HeimdallPublicId == identity));
        Assert.Equal(1, await context.RegistrationOperations.CountAsync(x => x.OperationId == request.OperationId));
    }

    [FunctionalFact]
    public async Task GivenConcurrentDifferentOperationsForIdentity_WhenRegistering_ThenKeepOnlyWinningAccount()
    {
        var first = Request();
        var second = Request() with { IdentityLock = first.IdentityLock };
        var identity = Guid.NewGuid();
        var results = await Task.WhenAll(new[] { first, second }.Select(request => Store().RegisterAsync(request, _ => Verified(identity), default)));
        Assert.Single(results, result => result.Error is null);
        Assert.Single(results, result => result.Error == "registration_conflict");
        await using var context = fixture.CreateContext();
        Assert.Equal(1, await context.Accounts.CountAsync(x => x.HeimdallPublicId == identity));
    }

    [FunctionalFact]
    public async Task GivenOtherIdentityUsingAccountGuid_WhenRegistering_ThenHideExistingAccount()
    {
        var request = Request();
        await Store().RegisterAsync(request, _ => Verified(Guid.NewGuid()), default);
        var result = await Store().RegisterAsync(Request() with { AccountId = request.AccountId }, _ => Verified(Guid.NewGuid()), default);
        Assert.Equal("not_found", result.Error);
        Assert.Null(result.AccountId);
    }

    [FunctionalFact]
    public async Task GivenErasedPublicIdentifier_WhenRegistering_ThenRejectWithoutCallingIdentity()
    {
        var request = Request();
        await using (var context = fixture.CreateContext())
        {
            context.TerminalErasures.Add(new TerminalErasure { ResourceId = request.AccountId, ResourceKind = "account", DeletedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
        }
        var result = await Store().RegisterAsync(request, _ => throw new InvalidOperationException("Erased identifiers cannot be recreated."), default);
        Assert.Equal("not_found", result.Error);
        await using var after = fixture.CreateContext();
        Assert.False(await after.Accounts.AnyAsync(x => x.PublicId == request.AccountId));
    }

    [FunctionalTheory]
    [InlineData(RegistrationIdentityStatus.Denied, "identity_proof_required")]
    [InlineData(RegistrationIdentityStatus.Forbidden, "registration_forbidden")]
    [InlineData(RegistrationIdentityStatus.NotFound, "not_found")]
    [InlineData(RegistrationIdentityStatus.Unavailable, "identity_unavailable")]
    public async Task GivenIdentityFailure_WhenRegistering_ThenCreateNoAccount(RegistrationIdentityStatus status, string expected)
    {
        var request = Request();
        var result = await Store().RegisterAsync(request, _ => Task.FromResult(new RegistrationIdentity(status)), default);
        Assert.Equal(expected, result.Error);
        await using var context = fixture.CreateContext();
        Assert.False(await context.Accounts.AnyAsync(x => x.PublicId == request.AccountId));
    }

    [FunctionalFact]
    public async Task GivenIdentityOutage_WhenRetrying_ThenResumeDurablePendingOperation()
    {
        var request = Request();
        var identity = Guid.NewGuid();
        Assert.Equal("identity_unavailable", (await Store().RegisterAsync(request,
            _ => Task.FromResult(new RegistrationIdentity(RegistrationIdentityStatus.Unavailable)), default)).Error);
        await using (var context = fixture.CreateContext())
        {
            Assert.Null((await context.RegistrationOperations.SingleAsync(x => x.OperationId == request.OperationId)).CompletedIdentityId);
            Assert.False(await context.Accounts.AnyAsync(x => x.PublicId == request.AccountId));
        }
        Assert.Null((await Store().RegisterAsync(request, _ => Verified(identity), default)).Error);
    }

    [FunctionalFact]
    public async Task GivenLocalFailureAfterUpstreamSuccess_WhenRetrying_ThenBindPendingOperationOnce()
    {
        var request = Request();
        var identity = Guid.NewGuid();
        var failed = await new RegistrationStore(new FaultFactory(fixture, new FailAccountSave())).RegisterAsync(request, _ => Verified(identity), default);
        Assert.Equal("persistence_unavailable", failed.Error);
        await using (var context = fixture.CreateContext())
        {
            Assert.False(await context.Accounts.AnyAsync(x => x.PublicId == request.AccountId));
            Assert.Null((await context.RegistrationOperations.SingleAsync(x => x.OperationId == request.OperationId)).CompletedIdentityId);
        }
        var resumed = await Store().RegisterAsync(request, _ => Verified(identity), default);
        Assert.Null(resumed.Error);
        var retry = await Store().RegisterAsync(request, _ => Verified(identity), default);
        Assert.True(retry.Replayed);
        await using var after = fixture.CreateContext();
        Assert.Equal(1, await after.Accounts.CountAsync(x => x.HeimdallPublicId == identity));
    }

    private RegistrationStore Store() => new(fixture);
    [FunctionalFact]
    public async Task GivenPostgresConnectionFailureAfterIdentitySuccess_WhenRegistering_ThenLeaveRetryableOperation()
    {
        var request = Request();
        await using var setup = fixture.CreateContext();
        await setup.Database.ExecuteSqlRawAsync("CREATE TABLE cerberus.fixture_failed_registration (public_id uuid NOT NULL); CREATE FUNCTION cerberus.fail_registration() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF EXISTS (SELECT 1 FROM cerberus.fixture_failed_registration WHERE public_id = NEW.public_id) THEN RAISE EXCEPTION 'synthetic persistence outage' USING ERRCODE = '08006'; END IF; RETURN NEW; END $$; CREATE TRIGGER fail_registration BEFORE INSERT ON cerberus.account FOR EACH ROW EXECUTE FUNCTION cerberus.fail_registration();");
        await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cerberus.fixture_failed_registration VALUES ({request.AccountId})");
        try
        {
            var result = await Store().RegisterAsync(request, _ => Verified(Guid.NewGuid()), default);
            Assert.Equal("persistence_unavailable", result.Error);
        }
        finally
        {
            await setup.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_registration ON cerberus.account; DROP FUNCTION cerberus.fail_registration(); DROP TABLE cerberus.fixture_failed_registration;");
        }
    }
    private static Task<RegistrationIdentity> Verified(Guid id) => Task.FromResult(new RegistrationIdentity(RegistrationIdentityStatus.Verified, id));
    private static RegistrationRequest Request() => new(Guid.NewGuid(), Guid.NewGuid(), [1, 2, 3, 4], new string('a', 64), Random.Shared.NextInt64());

    private sealed class FaultFactory(PostgresFixture source, SaveChangesInterceptor interceptor) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
        {
            using var context = source.CreateContext();
            return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(context.Database.GetConnectionString()).AddInterceptors(interceptor).Options);
        }
    }
    private sealed class FailAccountSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Account>().Any(x => x.State == EntityState.Added))
                throw new DbUpdateException("Synthetic local failure after upstream success.", new NpgsqlException("fixture"));
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
