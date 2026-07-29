using System.Diagnostics;
using FileExplorer.Api.Models.Dtos;

namespace FileExplorer.Api.Services;

public class PermissionsService(IPathResolver pathResolver) : IPermissionsService
{
    public async Task<PermissionsDto> GetAsync(string virtualPath, CancellationToken ct = default)
    {
        var physical = pathResolver.ToPhysicalPath(virtualPath);

        if (!OperatingSystem.IsLinux())
        {
            return new PermissionsDto("", "", "", "", false);
        }

        var (exitCode, output, error) = await RunAsync("stat", ["-c", "%U %G %a", physical], ct);
        if (exitCode != 0)
        {
            throw new IOException($"Could not read permissions for '{virtualPath}': {error}");
        }

        var parts = output.Trim().Split(' ', 3);
        var owner = parts.ElementAtOrDefault(0) ?? "";
        var group = parts.ElementAtOrDefault(1) ?? "";
        var octal = parts.ElementAtOrDefault(2) ?? "";
        var symbolic = UnixPermissionFormatter.TryFormat(physical) ?? "";

        return new PermissionsDto(owner, group, octal, symbolic, true);
    }

    public async Task UpdateAsync(string virtualPath, string? octalMode, string? owner, string? group, CancellationToken ct = default)
    {
        var physical = pathResolver.ToPhysicalPath(virtualPath);

        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Changing permissions is only supported when the API runs on Linux.");
        }

        if (!string.IsNullOrWhiteSpace(octalMode))
        {
            var (exitCode, _, error) = await RunAsync("chmod", [octalMode, physical], ct);
            if (exitCode != 0)
            {
                throw new IOException($"chmod failed: {error}");
            }
        }

        if (!string.IsNullOrWhiteSpace(owner) || !string.IsNullOrWhiteSpace(group))
        {
            var spec = string.IsNullOrWhiteSpace(group) ? owner! : $"{owner}:{group}";
            var (exitCode, _, error) = await RunAsync("chown", [spec, physical], ct);
            if (exitCode != 0)
            {
                throw new IOException($"chown failed: {error}");
            }
        }
    }

    public Task<PrincipalsResponse> ListPrincipalsAsync(CancellationToken ct = default) =>
        Task.FromResult(new PrincipalsResponse(ParsePrincipals("/etc/passwd"), ParsePrincipals("/etc/group")));

    private static IReadOnlyList<PrincipalDto> ParsePrincipals(string file)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(file))
        {
            return [];
        }

        var results = new List<PrincipalDto>();
        foreach (var line in File.ReadAllLines(file))
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split(':');
            if (parts.Length < 3)
            {
                continue;
            }

            results.Add(new PrincipalDto(parts[0], parts[2]));
        }

        return results.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, output, error);
    }
}
