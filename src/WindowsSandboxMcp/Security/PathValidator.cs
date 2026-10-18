using System.IO;

namespace WindowsSandboxMcp.Security;

public static class PathValidator
{
    public static string RequireUnder(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            throw new ArgumentException("Path cannot be empty.", nameof(candidate));

        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(candidate);

        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Path is outside the permitted lab root: {candidate}");

        return fullPath;
    }
}