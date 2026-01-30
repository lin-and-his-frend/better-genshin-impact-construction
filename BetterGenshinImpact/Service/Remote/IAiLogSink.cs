using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service.Remote;

internal interface IAiLogSink
{
    Task PublishAsync(LogLine line, CancellationToken cancellationToken);
}

internal sealed class NullAiLogSink : IAiLogSink
{
    public Task PublishAsync(LogLine line, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
