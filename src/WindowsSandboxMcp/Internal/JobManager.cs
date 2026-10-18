using System.Collections.Concurrent;
using WindowsSandboxMcp.Models;

namespace WindowsSandboxMcp.Internal;

internal static class JobManager
{
    private static readonly ConcurrentDictionary<string, SandboxJob> Jobs = new();

    public static SandboxJob Create(string command, int timeoutSeconds, string? workingDirectory)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var cancellation = new CancellationTokenSource();

        var executionTask = GuestRunner.RunAsync(
            command,
            timeoutSeconds,
            workingDirectory,
            jobId,
            cancellation.Token);

        var job = new SandboxJob(
            jobId,
            command,
            DateTimeOffset.UtcNow,
            executionTask,
            cancellation);

        Jobs[jobId] = job;

        _ = Task.Run(async () =>
        {
            try
            {
                await executionTask.ConfigureAwait(false);
            }
            finally
            {
                if (Jobs.TryGetValue(jobId, out var completedJob) &&
                    completedJob.StartedAt < DateTimeOffset.UtcNow - TimeSpan.FromHours(1))
                {
                    Jobs.TryRemove(jobId, out _);
                }
            }
        }, CancellationToken.None);

        return job;
    }

    public static SandboxJob? Get(string jobId) =>
        Jobs.TryGetValue(jobId, out var job) ? job : null;

    public static IReadOnlyCollection<SandboxJob> List() =>
        Jobs.Values.ToArray();

    public static bool TryCancel(string jobId)
    {
        if (!Jobs.TryGetValue(jobId, out var job))
            return false;

        job.Cancellation.Cancel();
        return true;
    }
}