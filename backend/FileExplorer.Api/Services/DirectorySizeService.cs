using System.Collections.Concurrent;
using System.Diagnostics;
using FileExplorer.Api.Models.Dtos;
using FileExplorer.Api.Services.Jobs;

namespace FileExplorer.Api.Services;

/// <summary>Computes directory sizes on demand in the background, using the native `du` command on Linux (far
/// faster than a managed recursive walk, especially over network mounts). Bounded to a handful of concurrent scans
/// so browsing stays responsive even when many folders are measured at once (e.g. the explorer's "show folder
/// sizes" toggle), and results are cached briefly so re-opening the same folder doesn't re-scan immediately.</summary>
public class DirectorySizeService(IPathResolver pathResolver, ILogger<DirectorySizeService> logger) : IDirectorySizeService
{
    private const int MaxConcurrentScans = 3;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _throttle = new(MaxConcurrentScans, MaxConcurrentScans);
    private readonly ConcurrentDictionary<string, (long Bytes, DateTimeOffset At)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, JobState> _jobs = new();

    private sealed class JobState
    {
        public required string VirtualPath { get; init; }
        public required string PhysicalPath { get; init; }
        public DirectorySizeStatus Status = DirectorySizeStatus.Running;
        public long Bytes;
        public readonly CancellationTokenSource Cts = new();
        public Process? Process;
    }

    public DirectorySizeJobDto Start(string virtualPath)
    {
        var physicalPath = pathResolver.ToPhysicalPath(virtualPath);

        if (_cache.TryGetValue(physicalPath, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheTtl)
        {
            return new DirectorySizeJobDto(Guid.NewGuid(), virtualPath, DirectorySizeStatus.Completed, cached.Bytes);
        }

        var jobId = Guid.NewGuid();
        var state = new JobState { VirtualPath = virtualPath, PhysicalPath = physicalPath };
        _jobs[jobId] = state;

        _ = RunAsync(state);

        return new DirectorySizeJobDto(jobId, virtualPath, DirectorySizeStatus.Running, 0);
    }

    public DirectorySizeJobDto? Get(Guid jobId) =>
        _jobs.TryGetValue(jobId, out var state)
            ? new DirectorySizeJobDto(jobId, state.VirtualPath, state.Status, state.Bytes)
            : null;

    public bool Cancel(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var state))
        {
            return false;
        }

        state.Cts.Cancel();
        try
        {
            state.Process?.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited - nothing to kill.
        }

        return true;
    }

    private async Task RunAsync(JobState state)
    {
        try
        {
            await _throttle.WaitAsync(state.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            state.Status = DirectorySizeStatus.Cancelled;
            return;
        }

        try
        {
            var bytes = await ComputeAsync(state).ConfigureAwait(false);
            state.Bytes = bytes;
            state.Status = DirectorySizeStatus.Completed;
            _cache[state.PhysicalPath] = (bytes, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            state.Status = DirectorySizeStatus.Cancelled;
        }
        catch (Exception ex)
        {
            state.Status = DirectorySizeStatus.Failed;
            logger.LogWarning(ex, "Failed to compute size for {Path}", state.VirtualPath);
        }
        finally
        {
            _throttle.Release();
        }
    }

    private async Task<long> ComputeAsync(JobState state)
    {
        if (!OperatingSystem.IsLinux())
        {
            return FileTreeOperations.Measure(state.PhysicalPath).Bytes;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "du",
                ArgumentList = { "-sb", "--", state.PhysicalPath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        state.Process = process;
        process.Start();

        // Drain both streams concurrently with waiting for exit - du can print enough permission-denied
        // warnings on stderr to fill the pipe buffer and deadlock the child if nothing reads it.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(state.Cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(state.Cts.Token);

        try
        {
            await process.WaitForExitAsync(state.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            throw;
        }

        var output = await stdoutTask.ConfigureAwait(false);
        await stderrTask.ConfigureAwait(false);

        var trimmed = output.AsSpan().TrimStart();
        var separatorIndex = trimmed.IndexOfAny(' ', '\t');
        var digits = separatorIndex >= 0 ? trimmed[..separatorIndex] : trimmed;

        return long.TryParse(digits, out var bytes) ? bytes : 0;
    }
}
