namespace WindowsSandboxMcp.Security;

public static class GuestPath
{
    public const string WorkspaceRoot = @"C:\Lab";
    public const string StagingRoot = @"C:\LabStaging";
    public const string ResultsRoot = @"C:\LabResults";

    public static string ValidateWorkspaceFile(string path) =>
        Validate(WorkspaceRoot, path, requireFile: true);

    public static string ValidateWorkspaceDirectory(string path) =>
        Validate(WorkspaceRoot, path, requireFile: false);

    private static string Validate(string root, string path, bool requireFile)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Guest path cannot be empty.");

        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);

        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Guest path must be under {root}: {path}");

        if (requireFile && fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A file path is required.");

        return fullPath;
    }
}