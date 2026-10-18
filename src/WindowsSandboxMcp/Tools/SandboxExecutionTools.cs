using System.ComponentModel;
using System.Security.Cryptography;
using ModelContextProtocol.Server;
using WindowsSandboxMcp.Models;
using WindowsSandboxMcp.Security;

namespace WindowsSandboxMcp.Tools;

[McpServerToolType]
public sealed class SandboxExecutionTools
{
    [McpServerTool]
    [Description("Executes a command in the running Windows Sandbox and returns its captured output. The Sandbox must already be running. Output is written to a dedicated host results folder and truncated if it exceeds the configured limit.")]
    public async Task<string> ExecuteCaptured(
        [Description("Command to execute inside the Sandbox.")] string command,
        [Description("Timeout in seconds, between 1 and 300.")] int timeoutSeconds = 120,
        [Description("Optional working directory inside the Sandbox.")] string? workingDirectory = null)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            if (string.IsNullOrWhiteSpace(command))
                return "Error: command cannot be empty.";

            if (timeoutSeconds < LabPolicy.MinTimeoutSeconds || timeoutSeconds > LabPolicy.MaxTimeoutSeconds)
                return $"Error: timeoutSeconds must be between {LabPolicy.MinTimeoutSeconds} and {LabPolicy.MaxTimeoutSeconds}.";

            if (!WindowsSandbox.CanUseSandboxCli())
                return "Error: Windows Sandbox CLI is not available on this system.";

            var sandboxId = await WindowsSandbox.GetSingleSandboxIdAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(sandboxId))
                return "Error: no running Sandbox found. Start a Sandbox first.";

            Directory.CreateDirectory(LabPolicy.HostResultsRoot);

            var hostOutputPath = Path.Combine(LabPolicy.HostResultsRoot, $"{jobId}.out");
            var guestOutputPath = $"{LabPolicy.GuestResultsRoot}\\{jobId}.out";

            var shareError = await WindowsSandbox.AddSharedFolderAsync(
                sandboxId,
                LabPolicy.HostResultsRoot,
                LabPolicy.GuestResultsRoot,
                true).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(shareError))
                return $"Error: failed to map the dedicated results folder: {shareError}";

            var wrappedCommand =
                $"cmd.exe /c ""{command}"" > ""{guestOutputPath}"" 2>&1";

            var executionTask = WindowsSandbox.ExecuteInSandboxAsync(
                sandboxId,
                wrappedCommand,
                SandboxRunningContext.ExistingLogin,
                workingDirectory);

            var completed = await Task.WhenAny(
                executionTask,
                Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))).ConfigureAwait(false);

            var timedOut = completed != executionTask;

            int? exitCode = null;
            string? executionError = null;

            if (!timedOut)
            {
                var (code, error) = await executionTask.ConfigureAwait(false);
                exitCode = code;
                executionError = error;
            }

            var output = string.Empty;
            if (File.Exists(hostOutputPath))
            {
                // Give the guest a brief grace period to finish flushing the mapped file.
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                output = await File.ReadAllTextAsync(hostOutputPath).ConfigureAwait(false);
            }

            var outputBytes = File.Exists(hostOutputPath)
                ? new FileInfo(hostOutputPath).Length
                : 0;

            var truncated = false;
            if (output.Length > LabPolicy.MaxOutputBytes)
            {
                output = output[..LabPolicy.MaxOutputBytes];
                truncated = true;
            }

            var outputHash = File.Exists(hostOutputPath)
                ? Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(hostOutputPath).ConfigureAwait(false)))
                : string.Empty;

            var duration = (DateTimeOffset.UtcNow - startedAt).TotalSeconds;

            var result = new CommandExecutionResult(
                JobId: jobId,
                ExitCode: exitCode,
                Output: output,
                TimedOut: timedOut,
                OutputTruncated: truncated,
                OutputBytes: outputBytes,
                OutputSha256: outputHash,
                DurationSeconds: duration,
                OutputHostPath: hostOutputPath,
                Error: executionError);

            return System.Text.Json.JsonSerializer.Serialize(result,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
        }
        catch (Exception ex)
        {
            return $"Error executing captured command: {ex.Message}";
        }
    }
}