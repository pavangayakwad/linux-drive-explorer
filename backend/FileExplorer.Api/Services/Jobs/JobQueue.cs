using System.Threading.Channels;

namespace FileExplorer.Api.Services.Jobs;

public class JobQueue : IJobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    public ValueTask EnqueueAsync(Guid jobId, CancellationToken ct = default) => _channel.Writer.WriteAsync(jobId, ct);

    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
