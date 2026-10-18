namespace WindowsSandboxMcp.Security;

public static class LabPolicy
{
    public const string HostStagingRoot = @"C:\AI\lab-staging";
    public const string HostResultsRoot = @"C:\AI\lab-results";
    public const string GuestStagingRoot = @"C:\LabStaging";
    public const string GuestResultsRoot = @"C:\LabResults";

    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 300;
    public const int MaxOutputBytes = 256 * 1024;
    public const int MaxFileBytes = 50 * 1024 * 1024;
}