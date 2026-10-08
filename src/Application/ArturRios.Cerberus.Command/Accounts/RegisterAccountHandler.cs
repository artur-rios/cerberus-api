using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Accounts;

public sealed class RegisterAccountHandler(IValidator<RegisterAccountCommand> validator, IRegistrationStore store,
    IHeimdallClient identity, CerberusOptions options) : ICommandHandlerAsync<RegisterAccountCommand, RegisterAccountOutput>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<DataOutput<RegisterAccountOutput?>> HandleAsync(RegisterAccountCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<RegisterAccountOutput?>.New;
        if (!(await validator.ValidateAsync(command, cancellationToken)).IsValid) return output.WithError("validation_failed");
        var key = Encoding.UTF8.GetBytes(options.RegistrationFingerprintKey!);
        var input = JsonSerializer.SerializeToUtf8Bytes(new object[]
        {
            "cerberus-registration-input-v1", options.HeimdallScopeId, command.AccountId,
            command.IdempotencyKey, command.Identity, command.Details
        }, Json);
        RegistrationRequest request;
        try
        {
            var fingerprint = Convert.ToHexStringLower(HMACSHA256.HashData(key, input));
            var address = JsonSerializer.SerializeToUtf8Bytes(new[]
            {
                "cerberus-registration-lock-v1", options.HeimdallScopeId.ToString("D"), command.Identity.Email.Trim().ToLowerInvariant()
            });
            try
            {
                var identityLock = BinaryPrimitives.ReadInt64BigEndian(HMACSHA256.HashData(key, address));
                request = new(command.IdempotencyKey, command.AccountId,
                    JsonSerializer.SerializeToUtf8Bytes(command.Details, Json), fingerprint, identityLock);
            }
            finally { CryptographicOperations.ZeroMemory(address); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(key);
        }
        var result = await store.RegisterAsync(request,
            token => identity.EstablishRegistrationIdentityAsync(command.Identity, command.IdentityToken, token), cancellationToken);
        if (result.Error is not null) return output.WithError(result.Error);
        return output.WithData(new RegisterAccountOutput { Id = result.AccountId!.Value, Revision = result.Revision, Replayed = result.Replayed })
            .WithMessage(result.Replayed ? "account_registration_replayed" : "account_registered");
    }
}
