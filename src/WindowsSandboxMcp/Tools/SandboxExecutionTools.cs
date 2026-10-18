using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using WindowsSandboxMcp.Security;

namespace WindowsSandboxMcp.Tools;

[McpServerToolType]
public sealed class SandboxExecutionTools
{
    private const string GuestJobRoot = @"C:\WindowsSandboxMcpJobs";

    [McpServerTool]
    [Description("Runs a PowerShell command inside the running Windows Sandbox and returns its output directly, like a normal shell tool. Returns JSON containing output, exit code, duration and truncation status. The Sandbox must already be running.")]
    public async Task<string> RunCommand(
        [Description("PowerShell command or script to execute inside the Sandbox.")] string command,
        [Description("Timeout in seconds, between 1 and 300.")] int timeoutSeconds = 120,
        [Description("Optional working directory inside the Sandbox.")] string? workingDirectory = null)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            if (string.IsNullOrWhiteSpace(command))
                return Error(jobId, "Command cannot be empty.");

            if (timeoutSeconds < LabPolicy.MinTimeoutSeconds || timeoutSeconds > LabPolicy.MaxTimeoutSeconds)
                return Error(jobId, $"timeoutSeconds must be between {LabPolicy.MinTimeoutSeconds} and {LabPolicy.MaxTimeoutSeconds}.");

            if (!WindowsSandbox.CanUseSandboxCli())
                return Error(jobId, "Windows Sandbox CLI is not available on this system.");

            var sandboxId = await WindowsSandbox.GetSingleSandboxIdAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(sandboxId))
                return Error(jobId, "No running Sandbox found. Start a Sandbox first.");

            var hostJobDirectory = Path.Combine(
                Path.GetTempPath(),
                "WindowsSandboxMcp",
                "runs",
                jobId);

            var guestJobDirectory = $"{GuestJobRoot}\\{jobId}";

            Directory.CreateDirectory(hostJobDirectory);

            var shareError = await WindowsSandbox.AddSharedFolderAsync(
                sandboxId,
                hostJobDirectory,
                guestJobDirectory,
                true).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(shareError))
                return Error(jobId, $"Failed to create the private job channel: {shareError}");

            var guestOutputPath = $"{guestJobDirectory}\\output.txt";
            var guestExitPath = $"{guestJobDirectory}\\exit.txt";
            var guestDonePath = $"{guestJobDirectory}\\done.txt";

            var encodedCommand = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));

            var guestScript = $"""
                $ErrorActionPreference = 'Continue'
                $outputPath = '{guestOutputPath}'
                $exitPath = '{guestExitPath}'
                $donePath = '{guestDonePath}'

                try
                {{
                    $command = [System.Text.Encoding]::UTF8.GetString(
                        [System.Convert]::FromBase64String('{encodedCommand}'))

                    $result = Invoke-Expression $command 2>&1
                    $result | Out-String -Width 4096 |
                        Set-Content -LiteralPath $outputPath -Encoding UTF8

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
                    $_ | Out-String |
                        Set-Content -LiteralPath $outputPath -Encoding UTF8
                    1 | Set-Content -LiteralPath $exitPath -Encoding ASCII
                }}

                'completed' | Set-Content -LiteralPath $donePath -Encoding ASCII
                """;

            var encodedRunner = Convert.ToBase64String(Encoding.UTF8.GetBytes(guestScript));

            var runnerCommand =
                $"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedRunner}";

            var executionTask = WindowsSandbox.ExecuteInSandboxAsync(
                sandboxId,
                runnerCommand,
                SandboxRunningContext.ExistingLogin,
                workingDirectory);

            var completedTask = await Task.WhenAny(
                executionTask,
                Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))).ConfigureAwait(false);

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
            var hostExitPath = Path.Combine(hostJobDirectory, "exit.txt");
            var hostDonePath = Path.Combine(hostJobDirectory, "done.txt");

            if (!timedOut)
            {
                await WaitForFileAsync(hostDonePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }

            var output = await ReadExistingFileAsync(hostOutputPath).ConfigureAwait(false);

            if (File.Exists(hostExitPath))
            {
                var exitText = (await ReadExistingFileAsync(hostExitPath).ConfigureAwait(false)).Trim();
                if (int.TryParse(exitText, out var parsedExitCode))
                {
                    exitCode = parsedExitCode;
                }
            }

            var truncated = false;
            if (output.Length > LabPolicy.MaxOutputBytes)
            {
                output = output[..LabPolicy.MaxOutputBytes];
                truncated = true;
            }

            var duration = (DateTimeOffset.UtcNow - startedAt).TotalSeconds;

            TryDeleteDirectory(hostJobDirectory);

            return Serialize(new
            {
                jobId,
                output,
                exitCode,
                timedOut,
                truncated,
                durationSeconds = Math.Round(duration, 3),
                error = executionError
            });
        }
        catch (Exception ex)
        {
            return Error(jobId, ex.Message);
        }
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (!File.Exists(path) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadExistingFileAsync(string path)
    {
        if (!File.Exists(path))
            return string.Empty;

        try
        {
            return await File.ReadAllTextAsync(path).ConfigureAwait(false);
        }
        catch (IOException)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            return File.Exists(path) ? await File.ReadAllTextAsync(path).ConfigureAwait(false) : string.Empty;
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
            // The Sandbox may still hold the mapped folder open. Temporary job folders
            // under %TEMP% can be cleaned later without affecting Sandbox operation.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Serialize<T>(T value)
    {
        return System.Text.Json.JsonSerializer.Serialize(
            value,
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
    }

    private static string Error(string jobId, string message)
    {
        return Serialize(new
        {
            jobId,
            output = string.Empty,
            exitCode = (int?)null,
            timedOut = false,
            truncated = false,
            durationSeconds = 0.0,
            error = message
        });
    }
}