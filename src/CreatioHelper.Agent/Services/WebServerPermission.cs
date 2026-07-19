namespace CreatioHelper.Agent.Services;

public static class WebServerPermission
{
    private static readonly string[] Signatures =
    {
        "повысить уровень процесса",
        "access is denied",
        "access denied",
        "requested registry access is not allowed",
        "unauthorizedaccess",
        "run this command as an administrator",
        "elevat",
        "requires elevation",
        "administrator privilege"
    };

    public static bool IsPermissionError(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return false;
        }

        var lowered = stderr.ToLowerInvariant();
        foreach (var signature in Signatures)
        {
            if (lowered.Contains(signature))
            {
                return true;
            }
        }

        return false;
    }
}
