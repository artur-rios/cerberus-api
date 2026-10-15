using System.Text.Json.Serialization;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Authentication;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ClosureRecoveryLoginCommand:BaseCommand
{
    [JsonRequired]public string Email{get;set;}=string.Empty;
    [JsonRequired]public string Password{get;set;}=string.Empty;
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ClosureRecoveryVerifyChallengeCommand:BaseCommand
{
    [JsonRequired]public string ChallengeToken{get;set;}=string.Empty;
    public string? Code{get;set;}
    public string? RecoveryCode{get;set;}
}
