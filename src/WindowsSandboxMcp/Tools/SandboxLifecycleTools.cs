using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsSandboxMcp.Internal;
using WindowsSandboxMcp.Security;

namespace WindowsSandboxMcp.Tools;

[McpServerToolType]
public sealed class SandboxLifecycleTools
{
    [McpServerTool]
    [Description("Starts a Windows Sandbox using a fixed, approved isolation profile. Profiles: offline (default), analysis, gui-test, networked.")]
    public async Task<string> StartSandbox(
        [Description("Isolation profile: offline, analysis, gui-test or networked.")] string profile = "offline",
        [Description("Sandbox memory in MB. If omitted, Windows chooses the default.")] int? memoryInMB = null)
    {
        var normalizedProfile = profile.Trim().ToLowerInvariant();

        if (memoryInMB is < 512 or > 32768)
            return "Error: memoryInMB must be between 512 and 32768.";

        if (!WindowsSandbox.CanUseSandboxCli())
            return "Error: Windows Sandbox CLI is not available on this system.";

        var existingId = await WindowsSandbox.GetSingleSandboxIdAsync().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(existingId))
            return "A Sandbox is already running. Stop it before starting another one.";

        var config = new WindowsSandboxConfiguration
        {
            EnableNetworking = normalizedProfile == "networked",
            EnableClipboardRedirection = false,
            EnablePrinterRedirection = false,
            EnableAudioInput = false,
            EnableVideoInput = false,
            EnableProtectedClient = true,
            EnableVGpu = normalizedProfile == "gui-test",
            MemoryInMB = memoryInMB
        };

        if (normalizedProfile == "analysis")
        {
            Directory.CreateDirectory(LabPolicy.HostStagingRoot);
            Directory.CreateDirectory(LabPolicy.HostResultsRoot);

            config.MappedFolders.Add(new MappedFolderConfiguration
            {
                HostFolderPath = LabPolicy.HostStagingRoot,
                SandboxFolderAbsolutePath = LabPolicy.GuestStagingRoot,
                ReadOnly = true
            });

            config.MappedFolders.Add(new MappedFolderConfiguration
            {
                HostFolderPath = LabPolicy.HostResultsRoot,
                SandboxFolderAbsolutePath = LabPolicy.GuestResultsRoot,
                ReadOnly = false
            });
        }
        else if (normalizedProfile != "offline" && normalizedProfile != "gui-test" && normalizedProfile != "networked")
        {
            return "Error: profile must be offline, analysis, gui-test or networked.";
        }

        var (sandboxId, error) = await WindowsSandbox.StartSandboxAsync(config).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(error))
            return $"Error starting Sandbox: {error}";

        if (string.IsNullOrEmpty(sandboxId))
            return "Error: Sandbox started, but no Sandbox ID was returned.";

        return JsonSerializer.Serialize(new
        {
            sandboxId,
            profile = normalizedProfile,
            networking = normalizedProfile == "networked",
            vgpu = normalizedProfile == "gui-test",
            analysisFoldersMapped = normalizedProfile == "analysis"
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool]
    [Description("Returns the current Sandbox state, active profile estimate, running job count and network information when available.")]
    public async Task<string> SandboxStatus()
    {
        if (!WindowsSandbox.CanUseSandboxCli())
            return "Error: Windows Sandbox CLI is not available on this system.";

        var sandboxId = await WindowsSandbox.GetSingleSandboxIdAsync().ConfigureAwait(false);
        var jobs = JobManager.List();

        if (string.IsNullOrEmpty(sandboxId))
        {
            return JsonSerializer.Serialize(new
            {
                running = false,
                runningJobs = jobs.Count(j => !j.ExecutionTask.IsCompleted)
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        var (networks, networkError) = await WindowsSandbox.GetSandboxNetworkAsync(sandboxId).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            running = true,
            sandboxId,
            remoteSessionOpen = WindowsSandbox.GetActiveSandboxRemoteSession(sandboxId),
            runningJobs = jobs.Count(j => !j.ExecutionTask.IsCompleted),
            totalJobs = jobs.Count,
            network = networks.Any() ? networks.First().IPv4Address : null,
            networkError
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool]
    [Description("Stops the running Windows Sandbox and discards its state.")]
    public async Task<string> StopSandbox()
    {
        if (!WindowsSandbox.CanUseSandboxCli())
            return "Error: Windows Sandbox CLI is not available on this system.";

        var sandboxId = await WindowsSandbox.GetSingleSandboxIdAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(sandboxId))
            return "No running Sandbox found.";

        var error = await WindowsSandbox.StopSandboxAsync(sandboxId).ConfigureAwait(false);
        return string.IsNullOrEmpty(error)
            ? "Sandbox stopped successfully."
            : $"Error stopping Sandbox: {error}";
    }

    [McpServerTool]
    [Description("Stops the current Sandbox and starts a clean Sandbox using the selected profile.")]
    public async Task<string> RestartSandbox(
        [Description("Isolation profile: offline, analysis, gui-test or networked.")] string profile = "offline",
        [Description("Sandbox memory in MB.")] int? memoryInMB = null)
    {
        var sandboxId = await WindowsSandbox.GetSingleSandboxIdAsync().ConfigureAwait(false);

        if (!string.IsNullOrEmpty(sandboxId))
        {
            var stopError = await WindowsSandbox.StopSandboxAsync(sandboxId).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(stopError))
                return $"Error stopping the previous Sandbox: {stopError}";

            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        return await StartSandbox(profile, memoryInMB).ConfigureAwait(false);
    }
}