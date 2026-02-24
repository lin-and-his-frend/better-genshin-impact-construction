using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Model.Ai;

namespace BetterGenshinImpact.Service.Remote;

public sealed class McpLocalClient
{
    private const int DefaultMaxToolResultChars = 20000;
    private const int DefaultMaxRawJsonChars = 8000;
    private const int DefaultMaxNonTextItemChars = 2000;
    private readonly IMcpRequestHandler _requestHandler;
    private int _nextId;

    public McpLocalClient(IMcpRequestHandler requestHandler)
    {
        _requestHandler = requestHandler;
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        var payload = BuildRequestJson("tools/list", new JsonObject());
        using var doc = await SendAsync(payload, ct).ConfigureAwait(false);
        if (TryGetError(doc.RootElement, out var error))
        {
            throw new InvalidOperationException(error);
        }

        if (!doc.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("tools", out var toolsElement) ||
            toolsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<McpToolInfo>();
        }

        var list = new List<McpToolInfo>();
        foreach (var tool in toolsElement.EnumerateArray())
        {
            var name = tool.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var description = tool.TryGetProperty("description", out var descElement) ? descElement.GetString() : string.Empty;
            var schema = tool.TryGetProperty("inputSchema", out var schemaElement) ? schemaElement.GetRawText() : string.Empty;
            list.Add(new McpToolInfo
            {
                Name = name ?? string.Empty,
                Description = description ?? string.Empty,
                InputSchema = schema ?? string.Empty
            });
        }

        return list;
    }

    public async Task<McpToolCallResult> CallToolAsync(string name, string? argumentsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new McpToolCallResult(true, "工具名为空");
        }

        JsonNode? argumentsNode;
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            argumentsNode = new JsonObject();
        }
        else
        {
            try
            {
                argumentsNode = JsonNode.Parse(argumentsJson);
            }
            catch (JsonException ex)
            {
                return new McpToolCallResult(true, $"参数 JSON 无效: {ex.Message}");
            }
        }

        var paramNode = new JsonObject
        {
            ["name"] = name,
            ["arguments"] = argumentsNode ?? new JsonObject()
        };

        var payload = BuildRequestJson("tools/call", paramNode);
        using var doc = await SendAsync(payload, ct).ConfigureAwait(false);
        if (TryGetError(doc.RootElement, out var error))
        {
            return new McpToolCallResult(true, error, doc.RootElement.GetRawText());
        }

        if (!doc.RootElement.TryGetProperty("result", out var result))
        {
            return new McpToolCallResult(true, "返回为空", doc.RootElement.GetRawText());
        }

        var isError = result.TryGetProperty("isError", out var isErrorElement) && isErrorElement.ValueKind == JsonValueKind.True;
        var contentText = ExtractContentText(result);
        var rawJson = isError ? Truncate(result.GetRawText(), DefaultMaxRawJsonChars) : null;
        return new McpToolCallResult(isError, contentText, rawJson);
    }

    private string BuildRequestJson(string method, JsonNode? paramNode)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _nextId),
            ["method"] = method
        };

        if (paramNode != null)
        {
            payload["params"] = paramNode;
        }

        return payload.ToJsonString(McpRequestHandler.JsonOptions);
    }

    private async Task<JsonDocument> SendAsync(string payloadJson, CancellationToken ct)
    {
        var pipeName = $"bgi-mcp-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);
            await _requestHandler.HandleConnectionAsync(server, cts.Token).ConfigureAwait(false);
        }, cts.Token);

        await client.ConnectAsync(cts.Token).ConfigureAwait(false);
        await WritePayloadAsync(client, payloadJson, cts.Token).ConfigureAwait(false);

        var reader = new McpMessageReader(client);
        var responseJson = await reader.ReadAsync(cts.Token).ConfigureAwait(false);
        cts.Cancel();

        try
        {
            await serverTask.ConfigureAwait(false);
        }
        catch
        {
            // ignore server cancellation exceptions
        }

        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return JsonDocument.Parse("{}");
        }

        return JsonDocument.Parse(responseJson);
    }

    private static async Task WritePayloadAsync(Stream stream, string json, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header, 0, header.Length, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, 0, body.Length, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static bool TryGetError(JsonElement root, out string message)
    {
        message = string.Empty;
        if (!root.TryGetProperty("error", out var errorElement))
        {
            return false;
        }

        if (errorElement.TryGetProperty("message", out var msgElement))
        {
            message = msgElement.GetString() ?? string.Empty;
        }
        else
        {
            message = errorElement.GetRawText();
        }

        return true;
    }

    private static string ExtractContentText(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.Array)
        {
            return Truncate(result.GetRawText(), DefaultMaxToolResultChars);
        }

        var builder = new StringBuilder();
        foreach (var item in contentElement.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;

            if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("text", out var textElement))
            {
                builder.AppendLine(textElement.GetString() ?? string.Empty);
            }
            else if (string.Equals(type, "image", StringComparison.OrdinalIgnoreCase))
            {
                var mimeType = item.TryGetProperty("mimeType", out var mimeTypeElement) && mimeTypeElement.ValueKind == JsonValueKind.String
                    ? mimeTypeElement.GetString()
                    : null;
                var dataLength = item.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.String
                    ? (dataElement.GetString() ?? string.Empty).Length
                    : 0;

                var hint = string.IsNullOrWhiteSpace(mimeType) ? "image" : mimeType;
                builder.AppendLine($"[{hint} payload omitted, base64 length={dataLength} chars]");
            }
            else
            {
                builder.AppendLine(Truncate(item.GetRawText(), DefaultMaxNonTextItemChars));
            }

            if (builder.Length >= DefaultMaxToolResultChars)
            {
                builder.AppendLine($"... (truncated, max {DefaultMaxToolResultChars} chars)");
                break;
            }
        }

        return Truncate(builder.ToString().TrimEnd(), DefaultMaxToolResultChars);
    }

    private static string Truncate(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (maxChars <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        var suffix = $"... (truncated {text.Length - maxChars} chars)";
        if (suffix.Length >= maxChars)
        {
            return text.Substring(0, maxChars);
        }

        var sliceLength = maxChars - suffix.Length;
        return text.Substring(0, sliceLength) + suffix;
    }
}
