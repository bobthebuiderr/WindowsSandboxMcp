using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsSandboxMcp.Internal;

namespace WindowsSandboxMcp.Tools;

[McpServerToolType]
public sealed class SandboxGuiTools
{
    [McpServerTool]
    [Description("Lists visible top-level windows in the Sandbox.")]
    public async Task<string> ListWindows()
    {
        var script = """
            Get-Process |
                Where-Object { $_.MainWindowTitle } |
                Select-Object Id, ProcessName, MainWindowTitle |
                ConvertTo-Json -Depth 3
            """;

        return await GuestRunner.RunAsync(script, 60, null).ConfigureAwait(false);
    }

    [McpServerTool]
    [Description("Waits until a window with the specified title is visible in the Sandbox.")]
    public async Task<string> WaitForWindow(
        [Description("Window title to wait for. Supports PowerShell wildcard patterns.")] string windowTitle,
        [Description("Timeout in seconds, between 1 and 300.")] int timeoutSeconds = 30)
    {
        if (timeoutSeconds is < 1 or > 300)
            return "Error: timeoutSeconds must be between 1 and 300.";

        var safeTitle = windowTitle.Replace("'", "''");

        var script = $$"""
            $deadline = (Get-Date).AddSeconds({timeoutSeconds})
            while ((Get-Date) -lt $deadline)
            {
                $window = Get-Process |
                    Where-Object { $_.MainWindowTitle -like '{safeTitle}' } |
                    Select-Object -First 1

                if ($window)
                {
                    $window | Select-Object Id, ProcessName, MainWindowTitle | ConvertTo-Json -Depth 3
                    exit 0
                }

                Start-Sleep -Milliseconds 500
            }

            Write-Error 'Timed out waiting for window.'
            exit 1
            """;

        return await GuestRunner.RunAsync(script, timeoutSeconds + 10, null).ConfigureAwait(false);
    }

    [McpServerTool]
    [Description("Sends keystrokes to the currently focused Sandbox window. Use sparingly and verify the focused window first.")]
    public async Task<string> PressKey(
        [Description("Keys to send, using Windows Forms SendKeys syntax.")] string keys)
    {
        if (string.IsNullOrWhiteSpace(keys))
            return "Error: keys cannot be empty.";

        var safeKeys = keys.Replace("'", "''");

        var script = $$"""
            Add-Type -AssemblyName System.Windows.Forms
            [System.Windows.Forms.SendKeys]::SendWait('{safeKeys}')
            Write-Output 'Keys sent.'
            """;

        return await GuestRunner.RunAsync(script, 30, null).ConfigureAwait(false);
    }

    [McpServerTool]
    [Description("Types text into the currently focused Sandbox window.")]
    public async Task<string> TypeText(
        [Description("Text to type into the focused window.")] string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Error: text cannot be empty.";

        var safeText = text.Replace("'", "''");

        var script = $$"""
            Add-Type -AssemblyName System.Windows.Forms
            [System.Windows.Forms.SendKeys]::SendWait('{safeText}')
            Write-Output 'Text sent.'
            """;

        return await GuestRunner.RunAsync(script, 30, null).ConfigureAwait(false);
    }

    [McpServerTool]
    [Description("Inspects a window by title and returns basic window/process information.")]
    public async Task<string> InspectWindow(
        [Description("Window title to inspect. Supports PowerShell wildcard patterns.")] string windowTitle)
    {
        var safeTitle = windowTitle.Replace("'", "''");

        var script = $$"""
            $window = Get-Process |
                Where-Object { $_.MainWindowTitle -like '{safeTitle}' } |
                Select-Object -First 1

            if (-not $window)
            {
                Write-Error 'Window not found.'
                exit 1
            }

            $window | Select-Object Id, ProcessName, MainWindowTitle, MainWindowHandle |
                ConvertTo-Json -Depth 3
            """;

        return await GuestRunner.RunAsync(script, 30, null).ConfigureAwait(false);
    }
}