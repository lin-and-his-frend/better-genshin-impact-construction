using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service.Remote;

public interface IMcpRequestHandler
{
    Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken);
    Task<string?> HandleRequestAsync(string payloadJson, bool isInternalCall, CancellationToken cancellationToken);
}

internal sealed class NullMcpRequestHandler : IMcpRequestHandler
{
    public Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes("{\"error\":\"mcp handler not configured\"}\n");
        return stream.WriteAsync(payload, 0, payload.Length, cancellationToken);
    }

    public Task<string?> HandleRequestAsync(string payloadJson, bool isInternalCall, CancellationToken cancellationToken)
    {
        return Task.FromResult<string?>("{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32603,\"message\":\"mcp handler not configured\"}}");
    }
}
