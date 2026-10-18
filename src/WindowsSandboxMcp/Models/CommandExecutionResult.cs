namespace WindowsSandboxMcp.Models;

public sealed record CommandExecutionResult(
    string JobId,
    int? ExitCode,
    string Output,
    bool TimedOut,
    bool OutputTruncated,
    long OutputBytes,
    string OutputSha256,
    double DurationSeconds,
    string OutputHostPath,
    string? Error);