namespace WindowsSandboxMcp.Models;

public sealed record SandboxJob(
    string JobId,
    string Command,
    DateTimeOffset StartedAt,
    Task<CommandExecutionResult> ExecutionTask,
    CancellationTokenSource Cancellation)
{
    public string State =>
        ExecutionTask.IsCompleted ? "completed" : "running";
}