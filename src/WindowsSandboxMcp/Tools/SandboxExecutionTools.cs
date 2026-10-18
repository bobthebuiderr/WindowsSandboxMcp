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
        var result = await GuestRunner.RunAsync(command, timeoutSeconds, workingDirectory).ConfigureAwait(false);

        return System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
    }
}