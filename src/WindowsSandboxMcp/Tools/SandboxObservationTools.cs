using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsSandboxMcp.Internal;
using WindowsSandboxMcp.Security;

namespace WindowsSandboxMcp.Tools;

[McpServerToolType]
public sealed class SandboxObservationTools
{
    [McpServerTool]
    [Description("Lists running processes in the Sandbox, optionally filtered by process name.")]
    public async Task<string> GetProcesses(
        [Description("Optional process-name filter.")] string? processName = null,
        [Description("Maximum processes to return.")] int maxResults = 100)
    {
        if (maxResults is < 1 or > 500)
            return "Error: maxResults must be between 1 and 500.";

        var safeFilter = (processName ?? string.Empty).Replace("'", "''");
        var filter = string.IsNullOrWhiteSpace(processName)
            ? string.Empty
            : $"Where-Object {{ $_.ProcessName -like '{safeFilter}' }} |\n                ";

        var script = $$"""
            Get-Process |
                {filter}Sort-Object CPU -Descending |
                Select-Object -First {maxResults} Id, ProcessName, CPU, WorkingSet64, Path |
                ConvertTo-Json -Depth 3
            """;

        var result = await GuestRunner.RunAsync(script, 60, null).ConfigureAwait(false);
        return GuestRunner.Serialize(result);
    }

    [McpServerTool]
    [Description("Lists services in the Sandbox, optionally filtered by service name or display name.")]
    public async Task<string> GetServices(
        [Description("Optional service-name filter.")] string? serviceName = null,
        [Description("Maximum services to return.")] int maxResults = 100)
    {
        if (maxResults is < 1 or > 500)
            return "Error: maxResults must be between 1 and 500.";

        var safeFilter = (serviceName ?? string.Empty).Replace("'", "''");
        var filter = string.IsNullOrWhiteSpace(serviceName)
            ? string.Empty
            : $"Where-Object {{ $_.Name -like '{safeFilter}' -or $_.DisplayName -like '{safeFilter}' }} |\n                ";

        var script = $$"""
            Get-Service |
                {filter}Select-Object -First {maxResults} Name, DisplayName, Status, StartType |
                ConvertTo-Json -Depth 3
            """;

        var result = await GuestRunner.RunAsync(script, 60, null).ConfigureAwait(false);
        return GuestRunner.Serialize(result);
    }

    [McpServerTool]
    [Description("Lists scheduled tasks in the Sandbox.")]
    public async Task<string> GetScheduledTasks(
        [Description("Optional task-name filter.")] string? taskName = null,
        [Description("Maximum tasks to return.")] int maxResults = 100)
    {
        if (maxResults is < 1 or > 500)
            return "Error: maxResults must be between 1 and 500.";

        var safeFilter = (taskName ?? string.Empty).Replace("'", "''");
        var filter = string.IsNullOrWhiteSpace(taskName)
            ? string.Empty
            : $"Where-Object {{ $_.TaskName -like '{safeFilter}' }} |\n                ";

        var script = $$"""
            Get-ScheduledTask |
                {filter}Select-Object -First {maxResults} TaskName, TaskPath, State |
                ConvertTo-Json -Depth 3
            """;

        var result = await GuestRunner.RunAsync(script, 60, null).ConfigureAwait(false);
        return GuestRunner.Serialize(result);
    }

    [McpServerTool]
    [Description("Lists active TCP connections in the Sandbox.")]
    public async Task<string> GetNetworkConnections(
        [Description("Maximum connections to return.")] int maxResults = 100)
    {
        if (maxResults is < 1 or > 500)
            return "Error: maxResults must be between 1 and 500.";

        var script = $$"""
            Get-NetTCPConnection |
                Select-Object -First {maxResults} LocalAddress, LocalPort, RemoteAddress, RemotePort, State, OwningProcess |
                ConvertTo-Json -Depth 3
            """;

        var result = await GuestRunner.RunAsync(script, 60, null).ConfigureAwait(false);
        return GuestRunner.Serialize(result);
    }

    [McpServerTool]
    [Description("Reads recent events from a Windows event log in the Sandbox.")]
    public async Task<string> GetEventLogs(
        [Description("Event log name. Defaults to System.")] string logName = "System",
        [Description("Maximum events to return.")] int maxEvents = 50)
    {
        if (maxEvents is < 1 or > 500)
            return "Error: maxEvents must be between 1 and 500.";

        var safeLogName = logName.Replace("'", "''");

        var script = $$"""
            Get-WinEvent -LogName '{safeLogName}' -MaxEvents {maxEvents} |
                Select-Object TimeCreated, ProviderName, LevelDisplayName, Id, Message |
                ConvertTo-Json -Depth 3
            """;

        var result = await GuestRunner.RunAsync(script, 60, null).ConfigureAwait(false);
        return GuestRunner.Serialize(result);
    }

    [McpServerTool]
    [Description("Returns compact operating-system and hardware information for the Sandbox.")]
    public async Task<string> GetSystemInfo()
    {
        var script = """
            [PSCustomObject]@{
                ComputerName = $env:COMPUTERNAME
                OsVersion = [Environment]::OSVersion.VersionString
                Is64Bit = [Environment]::Is64BitOperatingSystem
                ProcessorCount = [Environment]::ProcessorCount
                MachineName = [Environment]::MachineName
                User = [Environment]::UserName
                Uptime = (Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
            } | ConvertTo-Json -Depth 3
            """;

        var result = await GuestRunner.RunAsync(script, 60, null).ConfigureAwait(false);
        return GuestRunner.Serialize(result);
    }

    [McpServerTool]
    [Description("Captures a PNG screenshot of the Sandbox display and saves it to the host results folder. Returns the host file path.")]
    public async Task<string> TakeScreenshot()
    {
        Directory.CreateDirectory(LabPolicy.HostResultsRoot);

        var fileName = $"sandbox-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.png";
        var hostPath = Path.Combine(LabPolicy.HostResultsRoot, fileName);

        var script = $$"""
            Add-Type -AssemblyName System.Windows.Forms
            Add-Type -AssemblyName System.Drawing

            $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
            $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
            $bitmap.Save('C:\LabResults\{fileName}', [System.Drawing.Imaging.ImageFormat]::Png)
            $graphics.Dispose()
            $bitmap.Dispose()

            Write-Output "Screenshot saved: C:\LabResults\{fileName}"
            """;

        var result = await GuestRunner.RunAsync(script, 60, null).ConfigureAwait(false);

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            hostFilePath = hostPath,
            output = result.Output,
            exitCode = result.ExitCode,
            error = result.Error
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}