using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service.Remote;

public interface IMcpRequestHandler
{
    Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken);
}

internal sealed class NullMcpRequestHandler : IMcpRequestHandler
{
    public Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes("{\"error\":\"mcp handler not configured\"}\n");
        return stream.WriteAsync(payload, 0, payload.Length, cancellationToken);
    }
}
