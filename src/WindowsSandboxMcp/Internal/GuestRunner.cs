using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowsSandboxMcp.Models;
using WindowsSandboxMcp.Security;

namespace WindowsSandboxMcp.Internal;

internal static class GuestRunner
{
    private const string GuestJobRoot = @"C:\WindowsSandboxMcpJobs";

    public static async Task<CommandExecutionResult> RunAsync(
        string command,
        int timeoutSeconds,
        string? workingDirectory,
        string? jobId = null,
        CancellationToken cancellationToken = default)
    {
        jobId ??= Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(command))
            return Failure(jobId, startedAt, "Command cannot be empty.");

        if (timeoutSeconds < LabPolicy.MinTimeoutSeconds || timeoutSeconds > LabPolicy.MaxTimeoutSeconds)
            return Failure(
                jobId,
                startedAt,
                $"Timeout must be between {LabPolicy.MinTimeoutSeconds} and {LabPolicy.MaxTimeoutSeconds} seconds.");

        if (!WindowsSandbox.CanUseSandboxCli())
            return Failure(jobId, startedAt, "Windows Sandbox CLI is not available on this system.");

        var sandboxId = await WindowsSandbox.GetSingleSandboxIdAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(sandboxId))
            return Failure(jobId, startedAt, "No running Sandbox found. Start a Sandbox first.");

        var hostJobDirectory = Path.Combine(
            LabPolicy.HostJobsRoot,
            jobId);

        var guestJobDirectory = $"{GuestJobRoot}\\{jobId}";

        Directory.CreateDirectory(hostJobDirectory);

        try
        {
            var shareError = await WindowsSandbox.AddSharedFolderAsync(
                sandboxId,
                hostJobDirectory,
                guestJobDirectory,
                true,
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(shareError))
                return Failure(jobId, startedAt, $"Failed to create the private job channel: {shareError}");

            var guestOutputPath = $"{guestJobDirectory}\\output.txt";
            var guestErrorPath = $"{guestJobDirectory}\\error.txt";
            var guestExitPath = $"{guestJobDirectory}\\exit.txt";
            var guestDonePath = $"{guestJobDirectory}\\done.txt";

            var encodedCommand = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));

            var guestScript = $"""
                $ErrorActionPreference = 'Continue'
                $outputPath = '{guestOutputPath}'
                $errorPath = '{guestErrorPath}'
                $exitPath = '{guestExitPath}'
                $donePath = '{guestDonePath}'

                $stdoutFile = [System.IO.File]::CreateText($outputPath)
                $stderrFile = [System.IO.File]::CreateText($errorPath)

                try
                {{
                    $commandText = [System.Text.Encoding]::UTF8.GetString(
                        [System.Convert]::FromBase64String('{encodedCommand}'))

                    $output = Invoke-Expression $commandText 2>&1

                    foreach ($item in $output)
                    {{
                        if ($item -is [System.Management.Automation.ErrorRecord])
                        {{
                            $stderrFile.WriteLine($item.ToString())
                        }}
                        else
                        {{
                            $stdoutFile.WriteLine($item.ToString())
                        }}
                    }}

                    $exitCode = 0
                    if ($null -ne $LASTEXITCODE)
                    {{
                        $exitCode = $LASTEXITCODE
                    }}
                    elseif (-not $?)
                    {{
                        $exitCode = 1
                    }}

                    $exitCode | Set-Content -LiteralPath $exitPath -Encoding ASCII
                }}
                catch
                {{
                    $stderrFile.WriteLine($_.ToString())
                    1 | Set-Content -LiteralPath $exitPath -Encoding ASCII
                }}
                finally
                {{
                    $stdoutFile.Flush()
                    $stdoutFile.Dispose()
                    $stderrFile.Flush()
                    $stderrFile.Dispose()

                    'completed' | Set-Content -LiteralPath $donePath -Encoding ASCII
                }}
                """;

            var encodedRunner = Convert.ToBase64String(Encoding.UTF8.GetBytes(guestScript));

            var runnerCommand =
                $"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedRunner}";

            var executionTask = WindowsSandbox.ExecuteInSandboxAsync(
                sandboxId,
                runnerCommand,
                SandboxRunningContext.ExistingLogin,
                workingDirectory,
                cancellationToken);

            var completedTask = await Task.WhenAny(
                executionTask,
                Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken)).ConfigureAwait(false);

            var timedOut = completedTask != executionTask;

            int? exitCode = null;
            string? executionError = null;

            if (!timedOut)
            {
                var (code, error) = await executionTask.ConfigureAwait(false);
                exitCode = code;
                executionError = error;
            }

            var hostOutputPath = Path.Combine(hostJobDirectory, "output.txt");
            var hostErrorPath = Path.Combine(hostJobDirectory, "error.txt");
            var hostExitPath = Path.Combine(hostJobDirectory, "exit.txt");
            var hostDonePath = Path.Combine(hostJobDirectory, "done.txt");

            if (!timedOut)
            {
                await WaitForFileAsync(hostDonePath, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }

            var stdout = await ReadExistingFileAsync(hostOutputPath, cancellationToken).ConfigureAwait(false);
            var stderr = await ReadExistingFileAsync(hostErrorPath, cancellationToken).ConfigureAwait(false);

            if (File.Exists(hostExitPath))
            {
                var exitText = (await ReadExistingFileAsync(hostExitPath, cancellationToken).ConfigureAwait(false)).Trim();
                if (int.TryParse(exitText, out var parsedExitCode))
                {
                    exitCode = parsedExitCode;
                }
            }

            var combined = stdout;
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                combined = string.IsNullOrWhiteSpace(combined)
                    ? stderr
                    : $"{combined}\n--- stderr ---\n{stderr}";
            }

            var truncated = false;
            if (combined.Length > LabPolicy.MaxOutputBytes)
            {
                combined = combined[..LabPolicy.MaxOutputBytes];
                truncated = true;
            }

            var outputBytes = File.Exists(hostOutputPath)
                ? new FileInfo(hostOutputPath).Length
                : 0;

            var outputHash = File.Exists(hostOutputPath)
                ? Convert.ToHexString(SHA256.HashData(
                    await File.ReadAllBytesAsync(hostOutputPath, cancellationToken).ConfigureAwait(false)))
                : string.Empty;

            var duration = (DateTimeOffset.UtcNow - startedAt).TotalSeconds;

            TryDeleteDirectory(hostJobDirectory);

            return new CommandExecutionResult(
                JobId: jobId,
                ExitCode: exitCode,
                Output: combined,
                TimedOut: timedOut,
                OutputTruncated: truncated,
                OutputBytes: outputBytes,
                OutputSha256: outputHash,
                DurationSeconds: duration,
                OutputHostPath: hostOutputPath,
                Error: executionError);
        }
        catch (OperationCanceledException)
        {
            return Failure(jobId, startedAt, "The command was cancelled.");
        }
        catch (Exception ex)
        {
            return Failure(jobId, startedAt, ex.Message);
        }
    }

    public static async Task<(CommandExecutionResult Result, string? HostArtifactPath)> RunWithArtifactAsync(
        string command,
        string artifactFileName,
        int timeoutSeconds,
        string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var result = await RunAsync(command, timeoutSeconds, workingDirectory, jobId, cancellationToken).ConfigureAwait(false);

        var hostArtifactPath = Path.Combine(
            LabPolicy.HostJobsRoot,
            jobId,
            artifactFileName);

        return (result, File.Exists(hostArtifactPath) ? hostArtifactPath : null);
    }

    private static CommandExecutionResult Failure(
        string jobId,
        DateTimeOffset startedAt,
        string error)
    {
        return new CommandExecutionResult(
            JobId: jobId,
            ExitCode: null,
            Output: string.Empty,
            TimedOut: false,
            OutputTruncated: false,
            OutputBytes: 0,
            OutputSha256: string.Empty,
            DurationSeconds: (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            OutputHostPath: string.Empty,
            Error: error);
    }

    private static async Task WaitForFileAsync(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (!File.Exists(path) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadExistingFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return string.Empty;

        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            return File.Exists(path)
                ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
                : string.Empty;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}