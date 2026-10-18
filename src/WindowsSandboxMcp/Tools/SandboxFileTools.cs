using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsSandboxMcp.Internal;
using WindowsSandboxMcp.Security;

namespace WindowsSandboxMcp.Tools;

[McpServerToolType]
public sealed class SandboxFileTools
{
    [McpServerTool]
    [Description("Writes UTF-8 text to a file inside the Sandbox workspace at C:\\Lab. The Sandbox must be running.")]
    public async Task<string> WriteFile(
        [Description("Guest path under C:\\Lab.")] string guestPath,
        [Description("UTF-8 text to write.")] string content,
        [Description("Overwrite an existing file.")] bool overwrite = true)
    {
        var guestFilePath = GuestPath.ValidateWorkspaceFile(guestPath);
        var encodedContent = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));

        var script = $$"""
            $path = '{guestFilePath}'
            $directory = Split-Path -Path $path -Parent
            if (-not (Test-Path -LiteralPath $directory))
            {
                New-Item -ItemType Directory -Path $directory -Force | Out-Null
            }

            if ((Test-Path -LiteralPath $path) -and -not $overwrite)
            {
                Write-Error 'File already exists.'
                exit 1
            }

            $bytes = [System.Convert]::FromBase64String('{encodedContent}')
            [System.IO.File]::WriteAllBytes($path, $bytes)
            Write-Output "Wrote $((Get-Item -LiteralPath $path).Length) bytes to $path"
            """;

        var result = await GuestRunner.RunAsync(script, 60, null).ConfigureAwait(false);
        return GuestRunner.Serialize(result);
    }

    [McpServerTool]
    [Description("Reads a bounded UTF-8 text file from the Sandbox workspace at C:\\Lab.")]
    public async Task<string> ReadFile(
        [Description("Guest path under C:\\Lab.")] string guestPath,
        [Description("Maximum characters to return.")] int maxCharacters = 65536)
    {
        var guestFilePath = GuestPath.ValidateWorkspaceFile(guestPath);

        if (maxCharacters is < 1 or > LabPolicy.MaxOutputBytes)
            return "Error: maxCharacters must be between 1 and 262144.";

        var script = $$"""
            $path = '{guestFilePath}'
            if (-not (Test-Path -LiteralPath $path -PathType Leaf))
            {
                Write-Error 'File not found.'
                exit 1
            }

            $text = Get-Content -LiteralPath $path -Raw
            if ($text.Length -gt {maxCharacters})
            {
                $text.Substring(0, {maxCharacters})
                Write-Error 'Output truncated because the file exceeded maxCharacters.'
            }
            else
            {
                $text
            }
            """;

        var result = await GuestRunner.RunAsync(script, 60, null).ConfigureAwait(false);
        return GuestRunner.Serialize(result);
    }

    [McpServerTool]
    [Description("Lists files and directories under a path in the Sandbox workspace at C:\\Lab.")]
    public async Task<string> ListFiles(
        [Description("Guest directory under C:\\Lab. Defaults to C:\\Lab.")] string? guestPath = null,
        [Description("Maximum entries to return.")] int maxEntries = 200)
    {
        var guestDirectory = GuestPath.ValidateWorkspaceDirectory(guestPath ?? @"C:\Lab");

        if (maxEntries is < 1 or > 1000)
            return "Error: maxEntries must be between 1 and 1000.";

        var script = $$"""
            $path = '{guestDirectory}'
            if (-not (Test-Path -LiteralPath $path -PathType Container))
            {
                Write-Error 'Directory not found.'
                exit 1
            }

            Get-ChildItem -LiteralPath $path -Force |
                Select-Object -First {maxEntries} Name, Length, LastWriteTime, Mode |
                ConvertTo-Json -Depth 3
            """;

        var result = await GuestRunner.RunAsync(script, 60, null).ConfigureAwait(false);
        return GuestRunner.Serialize(result);
    }

    [McpServerTool]
    [Description("Uploads a file from the host staging folder into the Sandbox workspace at C:\\Lab.")]
    public async Task<string> UploadFile(
        [Description("Host file path under C:\\Windows-sandbox-MCP\\staging.")] string hostFilePath,
        [Description("Destination guest path under C:\\Lab.")] string guestPath)
    {
        var sourcePath = PathValidator.RequireUnder(LabPolicy.HostStagingRoot, hostFilePath);
        var destinationPath = GuestPath.ValidateWorkspaceFile(guestPath);

        if (!File.Exists(sourcePath))
            return $"Error: host file not found: {sourcePath}";

        var fileInfo = new FileInfo(sourcePath);
        if (fileInfo.Length > LabPolicy.MaxFileBytes)
            return $"Error: file exceeds the {LabPolicy.MaxFileBytes} byte limit.";

        var script = $$"""
            $destination = '{destinationPath}'
            $directory = Split-Path -Path $destination -Parent
            if (-not (Test-Path -LiteralPath $directory))
            {
                New-Item -ItemType Directory -Path $directory -Force | Out-Null
            }

            Copy-Item -LiteralPath 'C:\LabStaging\{fileInfo.Name}' -Destination $destination -Force
            Write-Output "Uploaded $(Split-Path -Path $destination -Leaf)"
            """;

        var result = await GuestRunner.RunAsync(script, 120, null).ConfigureAwait(false);

        if (result.ExitCode == 0)
        {
            return JsonSerializer.Serialize(new
            {
                hostFilePath = sourcePath,
                guestFilePath = destinationPath,
                sizeBytes = fileInfo.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))),
                output = result.Output,
                error = result.Error
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        return GuestRunner.Serialize(result);
    }

    [McpServerTool]
    [Description("Downloads a file from the Sandbox workspace to the host results folder.")]
    public async Task<string> DownloadFile(
        [Description("Guest file path under C:\\Lab.")] string guestPath,
        [Description("Optional host file name. Defaults to the guest file name.")] string? hostFileName = null)
    {
        var guestFilePath = GuestPath.ValidateWorkspaceFile(guestPath);
        var safeFileName = Path.GetFileName(guestFilePath);

        if (string.IsNullOrWhiteSpace(hostFileName))
            hostFileName = safeFileName;

        var hostDestination = Path.Combine(LabPolicy.HostResultsRoot, hostFileName);
        PathValidator.RequireUnder(LabPolicy.HostResultsRoot, hostDestination);

        Directory.CreateDirectory(LabPolicy.HostResultsRoot);

        var script = $$"""
            Copy-Item -LiteralPath '{guestFilePath}' -Destination 'C:\LabResults\{hostFileName}' -Force
            Write-Output "Downloaded {hostFileName}"
            """;

        var result = await GuestRunner.RunAsync(script, 120, null).ConfigureAwait(false);

        if (result.ExitCode != 0)
            return GuestRunner.Serialize(result);

        var hostPath = Path.Combine(LabPolicy.HostResultsRoot, hostFileName);

        return JsonSerializer.Serialize(new
        {
            guestFilePath,
            hostFilePath = hostPath,
            sizeBytes = File.Exists(hostPath) ? new FileInfo(hostPath).Length : 0,
            sha256 = File.Exists(hostPath)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(hostPath)))
                : null,
            output = result.Output,
            error = result.Error
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool]
    [Description("Deletes a file or directory inside the Sandbox workspace at C:\\Lab.")]
    public async Task<string> DeleteFile(
        [Description("Guest path under C:\\Lab.")] string guestPath,
        [Description("Allow deletion of a non-empty directory.")] bool recursive = false)
    {
        var guestFilePath = GuestPath.ValidateWorkspaceFile(guestPath);

        var script = $$"""
            $path = '{guestFilePath}'
            if (-not (Test-Path -LiteralPath $path))
            {
                Write-Error 'Path not found.'
                exit 1
            }

            Remove-Item -LiteralPath $path -Recurse:$recursive -Force
            Write-Output "Deleted $path"
            """;

        var result = await GuestRunner.RunAsync(script, 60, null).ConfigureAwait(false);
        return GuestRunner.Serialize(result);
    }
}