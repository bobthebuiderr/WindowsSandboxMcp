namespace WindowsSandboxMcp.Security;

public static class LabPolicy
{
    public const string HostRoot = @"C:\Windows-sandbox-MCP";
    public const string HostStagingRoot = @"C:\Windows-sandbox-MCP\staging";
    public const string HostResultsRoot = @"C:\Windows-sandbox-MCP\results";
    public const string HostJobsRoot = @"C:\Windows-sandbox-MCP\jobs";
    public const string HostLogsRoot = @"C:\Windows-sandbox-MCP\logs";

    public const string GuestStagingRoot = @"C:\LabStaging";
    public const string GuestResultsRoot = @"C:\LabResults";

    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 300;
    public const int MaxOutputBytes = 256 * 1024;
    public const int MaxFileBytes = 50 * 1024 * 1024;
}