namespace ArturRios.Cerberus.Command.Authentication;

public static class AuthenticationMessages
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        ["authenticated"] = 200, ["authentication_challenge_required"] = 200,
        ["validation_failed"] = 400, ["authentication_required"] = 401,
        ["authentication_forbidden"] = 403, ["identity_unavailable"] = 503, ["persistence_unavailable"] = 503
    };
}
