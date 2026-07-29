using System.Collections.Concurrent;

namespace FileExplorer.Api.Services.Jobs;

/// <summary>
/// Tracks a live CancellationTokenSource per running job so the API layer can request
/// cancellation of a job that's executing on the background worker.
/// </summary>
public class JobCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _tokens = new();

    public CancellationTokenSource Register(Guid jobId, CancellationToken linkedTo)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(linkedTo);
        _tokens[jobId] = cts;
        return cts;
    }

    public bool TryCancel(Guid jobId)
    {
        if (_tokens.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    public void Unregister(Guid jobId)
    {
        if (_tokens.TryRemove(jobId, out var cts))
        {
            cts.Dispose();
        }
    }
}
