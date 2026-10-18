using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsSandboxMcp.Internal;
using WindowsSandboxMcp.Models;
using WindowsSandboxMcp.Security;

namespace WindowsSandboxMcp.Tools;

[McpServerToolType]
public sealed class SandboxExecutionTools
{
    [McpServerTool]
    [Description("Runs a PowerShell command in the running Windows Sandbox and returns its output, exit code, duration and truncation status directly.")]
    public async Task<string> RunCommand(
        [Description("PowerShell command or script to execute inside the Sandbox.")] string command,
        [Description("Timeout in seconds, between 1 and 300.")] int timeoutSeconds = 120,
        [Description("Optional working directory inside the Sandbox.")] string? workingDirectory = null)
    {
        var result = await GuestRunner.RunAsync(
            command,
            timeoutSeconds,
            workingDirectory).ConfigureAwait(false);

        return Serialize(result);
    }

    [McpServerTool]
    [Description("Starts a PowerShell command in the running Windows Sandbox as a background job and returns a job ID. Use get_job or wait_job to retrieve its result.")]
    public async Task<string> RunCommandAsync(
        [Description("PowerShell command or script to execute inside the Sandbox.")] string command,
        [Description("Timeout in seconds, between 1 and 300.")] int timeoutSeconds = 300,
        [Description("Optional working directory inside the Sandbox.")] string? workingDirectory = null)
    {
        var job = JobManager.Create(command, timeoutSeconds, workingDirectory);

        return Serialize(new
        {
            job.JobId,
            job.Command,
            job.State,
            job.StartedAt,
            timeoutSeconds
        });
    }

    [McpServerTool]
    [Description("Gets the current state and result of a Sandbox command job.")]
    public async Task<string> GetJob(
        [Description("Job ID returned by run_command_async.")] string jobId)
    {
        var job = JobManager.Get(jobId);
        if (job is null)
            return Error(jobId, "Job not found.");

        if (!job.ExecutionTask.IsCompleted)
        {
            return Serialize(new
            {
                job.JobId,
                job.Command,
                job.State,
                job.StartedAt,
                elapsedSeconds = (DateTimeOffset.UtcNow - job.StartedAt).TotalSeconds
            });
        }

        var result = await job.ExecutionTask.ConfigureAwait(false);
        return Serialize(result);
    }

    [McpServerTool]
    [Description("Waits until a Sandbox command job completes or the timeout expires, then returns its result.")]
    public async Task<string> WaitJob(
        [Description("Job ID returned by run_command_async.")] string jobId,
        [Description("Maximum time to wait in seconds, between 1 and 300.")] int timeoutSeconds = 120)
    {
        var job = JobManager.Get(jobId);
        if (job is null)
            return Error(jobId, "Job not found.");

        if (timeoutSeconds < 1 || timeoutSeconds > 300)
            return Error(jobId, "timeoutSeconds must be between 1 and 300.");

        var completed = await Task.WhenAny(
            job.ExecutionTask,
            Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))).ConfigureAwait(false);

        if (completed != job.ExecutionTask)
        {
            return Serialize(new
            {
                job.JobId,
                job.State,
                timedOut = true,
                elapsedSeconds = (DateTimeOffset.UtcNow - job.StartedAt).TotalSeconds
            });
        }

        var result = await job.ExecutionTask.ConfigureAwait(false);
        return Serialize(result);
    }

    [McpServerTool]
    [Description("Requests cancellation of a running Sandbox command job.")]
    public async Task<string> StopJob(
        [Description("Job ID returned by run_command_async.")] string jobId)
    {
        var cancelled = JobManager.TryCancel(jobId);
        return cancelled
            ? $"Cancellation requested for job {jobId}."
            : $"Job {jobId} was not found.";
    }

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static string Error(string jobId, string message)
    {
        return Serialize(new
        {
            jobId,
            state = "error",
            error = message
        });
    }
}