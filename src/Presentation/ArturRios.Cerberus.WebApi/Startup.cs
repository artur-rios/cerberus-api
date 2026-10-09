using ArturRios.Cerberus.Command.Profiles;
using ArturRios.Cerberus.Query.Profiles;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Command.Records;
using ArturRios.Cerberus.Query.Records;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Data;
using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Command.Accounts;
using ArturRios.Cerberus.Command.Authentication;
using ArturRios.Cerberus.Command.Identity;
using ArturRios.Cerberus.Domain.Identity;
using ArturRios.Cerberus.Data.Identity;
using ArturRios.Cerberus.Command.Protection;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Data.Protection;
using ArturRios.Cerberus.Query.Protection;
using ArturRios.Cerberus.Query.Accounts;
using ArturRios.Mediator.Query;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Command.Interfaces;
using FluentValidation;
using ArturRios.Cerberus.Data.Configuration;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Cerberus.Shared.Operations;
using ArturRios.Cerberus.WebApi.Configuration;
using ArturRios.Cerberus.WebApi.Middleware;
using ArturRios.Cerberus.WebApi.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace ArturRios.Cerberus.WebApi;

public static class Startup
{
    public static WebApplication CreateApplication(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var options = CerberusConfiguration.Load(builder.Configuration);
        var result = new CerberusOptionsValidator(production: !builder.Environment.IsEnvironment("local")).Validate(null, options);
        if (result.Failed) throw new OptionsValidationException(nameof(CerberusOptions), typeof(CerberusOptions), result.Failures);
        var database = PostgresConfiguration.Validate(options.ConnectionString);
        if (database.Failed) throw new OptionsValidationException(nameof(CerberusOptions), typeof(CerberusOptions), database.Failures);

        // Do not load arbitrary logging enrichers/sinks from request-controlled configuration.
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(configuration => configuration.MinimumLevel.Information().Enrich.FromLogContext()
            // Framework/HTTP/EF events can carry URLs, identifiers and exception payloads.
            // Only our deliberately redacted events enter the production sink.
            .Filter.ByIncludingOnly(log => log.Properties.TryGetValue("SourceContext", out var source)
                && source is ScalarValue { Value: string category } && category.StartsWith("ArturRios.Cerberus.", StringComparison.Ordinal))
            .WriteTo.Console(new JsonFormatter()));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<HeimdallTokenValidator>();
        builder.Services.AddScoped<CommandMediator>();
        builder.Services.AddScoped<QueryMediator>();
        builder.Services.AddScoped<IQueryHandlerAsync<GetAccountQuery, AccountOutput>, GetAccountHandler>();
        builder.Services.AddScoped<IAccountReadStore, AccountReadStore>();
        builder.Services.AddScoped<IAccountUpdateStore, AccountUpdateStore>();
        builder.Services.AddScoped<IRecordCreateStore, RecordCreateStore>();
        builder.Services.AddScoped<IRecordListStore, RecordListStore>();
        builder.Services.AddScoped<IRecordReadStore, RecordReadStore>();
        builder.Services.AddScoped<IQueryHandlerAsync<GetRecordQuery, RecordDetailsOutput>, GetRecordHandler>();
        builder.Services.AddSingleton<RecordListCursor>();
        builder.Services.AddScoped<IQueryHandlerAsync<ListRecordsQuery, RecordListOutput>, ListRecordsHandler>();
        builder.Services.AddScoped<IValidator<CreateRecordCommand>, CreateRecordValidator>();
        builder.Services.AddScoped<ICommandHandlerAsync<CreateRecordCommand, CreateRecordOutput>, CreateRecordHandler>();
        builder.Services.AddScoped<IProfileAccessStore, ProfileAccessStore>();
        builder.Services.AddScoped<IValidator<IssueProfileAccessChallengeCommand>, IssueProfileAccessChallengeValidator>();
        builder.Services.AddScoped<ICommandHandlerAsync<IssueProfileAccessChallengeCommand, IssueProfileAccessChallengeOutput>, IssueProfileAccessChallengeHandler>();
        builder.Services.AddScoped<IValidator<OpenProfileAccessCommand>, OpenProfileAccessValidator>();
        builder.Services.AddScoped<ICommandHandlerAsync<OpenProfileAccessCommand, OpenProfileAccessOutput>, OpenProfileAccessHandler>();
        builder.Services.AddScoped<IProfileAssociationStore, ProfileAssociationStore>();
        builder.Services.AddScoped<IValidator<SetProfileAssociationsCommand>, SetProfileAssociationsValidator>();
        builder.Services.AddScoped<ICommandHandlerAsync<SetProfileAssociationsCommand, SetProfileAssociationsOutput>, SetProfileAssociationsHandler>();

        builder.Services.AddScoped<IProfileTrashStore, ProfileTrashStore>();
        builder.Services.AddScoped<IValidator<DeleteProfileCommand>, DeleteProfileValidator>();
        builder.Services.AddScoped<ICommandHandlerAsync<DeleteProfileCommand, DeleteProfileOutput>, DeleteProfileHandler>();
        builder.Services.AddScoped<IProfileUpdateStore, ProfileUpdateStore>();
        builder.Services.AddScoped<IValidator<UpdateProfileCommand>, UpdateProfileValidator>();
        builder.Services.AddScoped<ICommandHandlerAsync<UpdateProfileCommand, UpdateProfileOutput>, UpdateProfileHandler>();
        builder.Services.AddScoped<IProfileReadStore, ProfileReadStore>();
        builder.Services.AddScoped<IQueryHandlerAsync<GetProfileQuery, ProfileDetailsOutput>, GetProfileHandler>();
        builder.Services.AddScoped<IProfileListStore, ProfileListStore>();
        builder.Services.AddSingleton<ProfileListCursor>();
        builder.Services.AddScoped<IQueryHandlerAsync<ListProfilesQuery, ProfileListOutput>, ListProfilesHandler>();
        builder.Services.AddScoped<IProfileCreateStore, ProfileCreateStore>();
        builder.Services.AddScoped<IValidator<CreateProfileCommand>, CreateProfileValidator>();
        builder.Services.AddScoped<ICommandHandlerAsync<CreateProfileCommand, CreateProfileOutput>, CreateProfileHandler>();
        builder.Services.AddScoped<IIdentityUpdateStore, IdentityUpdateStore>();
        builder.Services.AddScoped<IVaultProtectionStore, VaultProtectionStore>();
        builder.Services.AddScoped<IVaultProtectionChangeStore, VaultProtectionChangeStore>();
        builder.Services.AddScoped<IVaultRecoveryStore, VaultRecoveryStore>();
        builder.Services.AddScoped<IValidator<RecoverVaultCommand>, RecoverVaultValidator>();
        builder.Services.AddScoped<ICommandHandlerAsync<RecoverVaultCommand, RecoverVaultOutput>, RecoverVaultHandler>();
        builder.Services.AddScoped<IValidator<ChangeVaultProtectionCommand>, ChangeVaultProtectionValidator>();
        builder.Services.AddScoped<ICommandHandlerAsync<ChangeVaultProtectionCommand, ChangeVaultProtectionOutput>, ChangeVaultProtectionHandler>();
        builder.Services.AddScoped<IValidator<InitializeVaultCommand>, InitializeVaultValidator>();
        builder.Services.AddScoped<IValidator<IssueVaultChallengeCommand>, IssueVaultChallengeValidator>();
        builder.Services.AddScoped<IValidator<UnlockVaultCommand>, UnlockVaultValidator>();
        builder.Services.AddScoped<ICommandHandlerAsync<InitializeVaultCommand, VaultInitializationOutput>, InitializeVaultHandler>();
        builder.Services.AddScoped<ICommandHandlerAsync<IssueVaultChallengeCommand, VaultChallengeOutput>, IssueVaultChallengeHandler>();
        builder.Services.AddScoped<ICommandHandlerAsync<UnlockVaultCommand, VaultUnlockOutput>, UnlockVaultHandler>();
        builder.Services.AddScoped<IQueryHandlerAsync<GetVaultProtectionQuery, VaultProtectionOutput>, GetVaultProtectionHandler>();
        builder.Services.AddScoped<IValidator<UpdateIdentityCommand>, UpdateIdentityValidator>();
        builder.Services.AddScoped<ICommandHandlerAsync<UpdateIdentityCommand, UpdateIdentityOutput>, UpdateIdentityHandler>();
        builder.Services.AddScoped<IValidator<UpdateAccountCommand>, UpdateAccountValidator>();
        builder.Services.AddScoped<ICommandHandlerAsync<UpdateAccountCommand, UpdateAccountOutput>, UpdateAccountHandler>();
        builder.Services.AddScoped<IValidator<RegisterAccountCommand>, RegisterAccountValidator>();
        builder.Services.AddScoped<ICommandHandlerAsync<RegisterAccountCommand, RegisterAccountOutput>, RegisterAccountHandler>();
        builder.Services.AddScoped<IRegistrationStore, RegistrationStore>();
        builder.Services.AddScoped<IValidator<LoginCommand>, LoginValidator>();
        builder.Services.AddScoped<IValidator<VerifyChallengeCommand>, VerifyChallengeValidator>();
        builder.Services.AddScoped<ICommandHandlerAsync<LoginCommand, AuthenticationOutput>, AuthenticationHandler>();
        builder.Services.AddScoped<ICommandHandlerAsync<VerifyChallengeCommand, AuthenticationOutput>, AuthenticationHandler>();
        builder.Services.AddScoped<IAccountAuthenticationStore, AccountAuthenticationStore>();
        builder.Services.AddDbContextFactory<AppDbContext>(configuration => configuration.UseNpgsql(options.ConnectionString));
        builder.Services.AddHttpClient<IHeimdallClient, HeimdallClient>(client =>
            {
                client.BaseAddress = new Uri(options.HeimdallBaseUrl!);
                client.Timeout = TimeSpan.FromSeconds(10);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        var ledger = new FileErasureLedger(options.ErasureLedgerPath!);
        builder.Services.AddSingleton<IErasureLedger>(ledger);
        builder.Services.AddScoped<IRetentionWorkStore, RetentionWorkStore>();
        builder.Services.AddScoped<ITerminalErasureStore, TerminalErasureStore>();
        builder.Services.AddScoped<IRetentionExecutor, RetentionExecutor>();
        builder.Services.AddScoped<RetentionRunner>();
        builder.Services.AddSingleton<RestoreTrafficGate>();
        builder.Services.AddScoped<IRestoreAuthorizationVerifier, RestoreAuthorizationVerifier>();
        builder.Services.AddScoped<IRestoreReconciler, RestoreReconciler>();
        builder.Services.AddHostedService<RetentionWorker>();
        builder.Services.AddCerberusContracts();

        var application = builder.Build();
        application.UseMiddleware<NoStoreMiddleware>();
        application.UseMiddleware<SafeFailureMiddleware>();
        application.UseMiddleware<RequestLimitMiddleware>(options.MaxRequestBytes);
        application.UseRouting();
        if (options.RestoreRequired == true) application.UseMiddleware<RestoreBarrierMiddleware>();
        application.UseMiddleware<ProtectedEndpointMiddleware>();
        application.UseMiddleware<VaultProofBodyMiddleware>();
        application.MapControllers();
        return application;
    }
}
