namespace FileExplorer.Api.Services.Jobs;

public interface IJobQueue
{
    ValueTask EnqueueAsync(Guid jobId, CancellationToken ct = default);

    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct);
}
