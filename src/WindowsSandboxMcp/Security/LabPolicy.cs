namespace WindowsSandboxMcp.Security;

public static class LabPolicy
{
    public const string HostResultsRoot = @"C:\AI\lab-results";
    public const string GuestResultsRoot = @"C:\LabResults";

    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 300;
    public const int MaxOutputBytes = 256 * 1024;

    public static bool IsOfflineSandboxSafe(bool? enableNetworking) =>
        enableNetworking is null or false;
}