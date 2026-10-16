using ArturRios.Cerberus.Shared.Configuration;

namespace ArturRios.Cerberus.Shared.Operations;

public static class OperationalGrants
{
    public static bool TryParse(string? value, out IReadOnlySet<Guid> subjects)
    {
        var ids = new HashSet<Guid>();
        subjects = ids;
        if (string.IsNullOrEmpty(value)) return true;
        foreach (var part in value.Split(','))
        {
            if (!Guid.TryParseExact(part, "D", out var id) || id == Guid.Empty
                || part != id.ToString("D") || !ids.Add(id)) return false;
        }
        return true;
    }

    public static bool IsValid(CerberusOptions options) =>
        TryParse(options.OperationalPersonIds, out _) && TryParse(options.OperationalApplicationIds, out _);
}
