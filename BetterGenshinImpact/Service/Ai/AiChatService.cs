using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Helpers.Http;
using BetterGenshinImpact.Model.Ai;

namespace BetterGenshinImpact.Service.Ai;

public sealed class AiChatService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HttpClient _httpClient;

    public AiChatService()
    {
        _httpClient = HttpClientFactory.GetClient("ai-chat", () => new HttpClient());
    }

    public async Task<string> GetChatCompletionAsync(
        AiConfig config,
        IReadOnlyList<AiChatMessage> messages,
        CancellationToken ct,
        Func<string, Task>? onDelta = null)
    {
        var result = await GetChatCompletionResultAsync(config, messages, ct, null, onDelta).ConfigureAwait(false);
        return result.Content;
    }

    public async Task<AiChatCompletionResult> GetChatCompletionResultAsync(
        AiConfig config,
        IReadOnlyList<AiChatMessage> messages,
        CancellationToken ct,
        IReadOnlyList<AiToolDefinition>? tools = null,
        Func<string, Task>? onDelta = null)
    {
        if (config == null)
        {
            throw new InvalidOperationException("AI 配置为空");
        }

        var baseUrl = NormalizeBaseUrl(config.BaseUrl);
        if (string.IsNullOrWhiteSpace(config.Model))
        {
            throw new InvalidOperationException("模型未配置");
        }

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new InvalidOperationException("API Key 未配置");
        }

        var endpoint = $"{baseUrl}/chat/completions";
        var payloadMessages = messages
            .Where(m => m.IsUser || m.IsAssistant || m.IsSystem)
            .Select(m => new Dictionary<string, object?>
            {
                ["role"] = m.Role.ToLowerInvariant(),
                ["content"] = m.Content
            })
            .ToArray();
        var payloadTools = BuildPayloadTools(tools);

        var responseFormat = config.UseJsonMode
            ? new Dictionary<string, object?>
            {
                ["type"] = "json_object"
            }
            : null;

        async Task<(HttpStatusCode statusCode, string reasonPhrase, string body)> SendAsync(bool useJsonMode)
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = config.Model.Trim(),
                ["messages"] = payloadMessages
            };

            if (useJsonMode)
            {
                payload["response_format"] = responseFormat;
            }

            if (payloadTools.Count > 0)
            {
                payload["tools"] = payloadTools;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey.Trim());
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (response.StatusCode, response.ReasonPhrase ?? string.Empty, body);
        }

        async Task<(HttpStatusCode statusCode, string reasonPhrase, string body, AiChatCompletionResult result)> SendStreamAsync(bool useJsonMode)
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = config.Model.Trim(),
                ["messages"] = payloadMessages,
                ["stream"] = true
            };

            if (useJsonMode)
            {
                payload["response_format"] = responseFormat;
            }

            if (payloadTools.Count > 0)
            {
                payload["tools"] = payloadTools;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey.Trim());
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode >= 400)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return (response.StatusCode, response.ReasonPhrase ?? string.Empty, errorBody, new AiChatCompletionResult());
            }

            var builder = new StringBuilder();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var dataBuffer = new StringBuilder();
            while (true)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    if (dataBuffer.Length > 0)
                    {
                        var data = dataBuffer.ToString();
                        dataBuffer.Clear();
                        if (!await ProcessSseDataAsync(data).ConfigureAwait(false))
                        {
                            break;
                        }
                    }

                    continue;
                }

                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var data = line[5..].TrimStart();
                    if (dataBuffer.Length > 0)
                    {
                        dataBuffer.Append('\n');
                    }

                    dataBuffer.Append(data);
                }
            }

            if (dataBuffer.Length > 0)
            {
                _ = await ProcessSseDataAsync(dataBuffer.ToString()).ConfigureAwait(false);
            }

            return (response.StatusCode, response.ReasonPhrase ?? string.Empty, string.Empty, new AiChatCompletionResult
            {
                Content = builder.ToString()
            });

            async Task<bool> ProcessSseDataAsync(string data)
            {
                if (string.IsNullOrWhiteSpace(data))
                {
                    return true;
                }

                var payloadText = data.Trim();
                if (string.Equals(payloadText, "[DONE]", StringComparison.Ordinal))
                {
                    return false;
                }

                try
                {
                    using var doc = JsonDocument.Parse(payloadText);
                    if (doc.RootElement.TryGetProperty("error", out var error))
                    {
                        var message = error.TryGetProperty("message", out var msgElement)
                            ? msgElement.GetString()
                            : "未知错误";
                        throw new InvalidOperationException(message ?? "未知错误");
                    }

                    if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                        choices.ValueKind != JsonValueKind.Array ||
                        choices.GetArrayLength() == 0)
                    {
                        return true;
                    }

                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var delta))
                    {
                        var deltaText = ExtractDeltaContent(delta);
                        if (!string.IsNullOrEmpty(deltaText))
                        {
                            builder.Append(deltaText);
                            if (onDelta != null)
                            {
                                await onDelta(deltaText).ConfigureAwait(false);
                            }
                        }
                    }

                    if (choice.TryGetProperty("finish_reason", out var finishReasonElement) &&
                        finishReasonElement.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(finishReasonElement.GetString()))
                    {
                        return false;
                    }
                }
                catch (JsonException)
                {
                }

                return true;
            }
        }

        var useStreamingResponse = config.UseStreamingResponse && payloadTools.Count == 0;
        if (useStreamingResponse)
        {
            var streamResult = await SendStreamAsync(config.UseJsonMode).ConfigureAwait(false);
            if ((int)streamResult.statusCode >= 400 &&
                config.UseJsonMode &&
                ShouldRetryWithoutJsonMode(streamResult.statusCode, streamResult.body))
            {
                streamResult = await SendStreamAsync(false).ConfigureAwait(false);
            }

            if ((int)streamResult.statusCode >= 400)
            {
                throw new InvalidOperationException(BuildHttpFailureMessage(
                    streamResult.statusCode,
                    streamResult.reasonPhrase,
                    streamResult.body,
                    config.Model));
            }

            return streamResult.result;
        }

        var result = await SendAsync(config.UseJsonMode).ConfigureAwait(false);
        if ((int)result.statusCode >= 400 && config.UseJsonMode && ShouldRetryWithoutJsonMode(result.statusCode, result.body))
        {
            result = await SendAsync(false).ConfigureAwait(false);
        }

        var body = result.body;
        if ((int)result.statusCode >= 400)
        {
            throw new InvalidOperationException(BuildHttpFailureMessage(
                result.statusCode,
                result.reasonPhrase,
                body,
                config.Model));
        }

        return ParseChatCompletionResult(body);
    }

    private static string BuildHttpFailureMessage(HttpStatusCode statusCode, string reasonPhrase, string? body, string? model)
    {
        var trimmedBody = body?.Trim() ?? string.Empty;
        var statusText = $"{(int)statusCode} {reasonPhrase}".Trim();
        var modelName = model?.Trim();

        if (TryParseErrorPayload(trimmedBody, out var code, out var message))
        {
            if (statusCode == HttpStatusCode.Forbidden &&
                (code == 30003 || string.Equals(message, "Model disabled.", StringComparison.OrdinalIgnoreCase)))
            {
                return string.IsNullOrWhiteSpace(modelName)
                    ? "当前 AI 模型已被服务端禁用。请在 AI 设置中更换可用模型，或联系服务端管理员启用该模型。"
                    : $"当前 AI 模型“{modelName}”已被服务端禁用。请在 AI 设置中更换可用模型，或联系服务端管理员启用该模型。";
            }

            if (statusCode == HttpStatusCode.Unauthorized)
            {
                return "AI 接口鉴权失败。请检查 API Key 是否正确，或确认当前服务端是否允许此账号访问。";
            }

            if (statusCode == HttpStatusCode.Forbidden)
            {
                return string.IsNullOrWhiteSpace(message)
                    ? $"AI 请求被服务端拒绝（{statusText}）。请检查当前模型、账号权限或服务端策略。"
                    : $"AI 请求被服务端拒绝（{statusText}）：{message}";
            }

            return string.IsNullOrWhiteSpace(message)
                ? $"AI 请求失败（{statusText}）。"
                : $"AI 请求失败（{statusText}）：{message}";
        }

        return string.IsNullOrWhiteSpace(trimmedBody)
            ? $"AI 请求失败（{statusText}）。"
            : $"AI 请求失败（{statusText}）：{trimmedBody}";
    }

    private static bool TryParseErrorPayload(string body, out int? code, out string? message)
    {
        code = null;
        message = null;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (doc.RootElement.TryGetProperty("code", out var codeElement))
            {
                if (codeElement.ValueKind == JsonValueKind.Number && codeElement.TryGetInt32(out var intCode))
                {
                    code = intCode;
                }
                else if (codeElement.ValueKind == JsonValueKind.String && int.TryParse(codeElement.GetString(), out var parsedCode))
                {
                    code = parsedCode;
                }
            }

            if (doc.RootElement.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String)
            {
                message = messageElement.GetString()?.Trim();
            }

            return code.HasValue || !string.IsNullOrWhiteSpace(message);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<Dictionary<string, object?>> BuildPayloadTools(IReadOnlyList<AiToolDefinition>? tools)
    {
        if (tools == null || tools.Count == 0)
        {
            return Array.Empty<Dictionary<string, object?>>();
        }

        var payloadTools = new List<Dictionary<string, object?>>(tools.Count);
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name))
            {
                continue;
            }

            payloadTools.Add(new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = tool.Name.Trim(),
                    ["description"] = string.IsNullOrWhiteSpace(tool.Description) ? null : tool.Description.Trim(),
                    ["parameters"] = tool.Parameters ?? JsonSerializer.SerializeToNode(BuildDefaultToolParameters(), JsonOptions)
                }
            });
        }

        return payloadTools.Count == 0
            ? Array.Empty<Dictionary<string, object?>>()
            : payloadTools;
    }

    private static Dictionary<string, object?> BuildDefaultToolParameters()
    {
        return new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>()
        };
    }

    private static AiChatCompletionResult ParseChatCompletionResult(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("AI 返回为空");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var msgElement)
                ? msgElement.GetString()
                : "未知错误";
            throw new InvalidOperationException(message ?? "未知错误");
        }

        if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("AI 返回为空");
        }

        var messageElement = choices[0].GetProperty("message");
        return new AiChatCompletionResult
        {
            Content = ExtractMessageContent(messageElement),
            ToolCalls = ExtractToolCalls(messageElement)
        };
    }

    private static string ExtractMessageContent(JsonElement messageElement)
    {
        if (!messageElement.TryGetProperty("content", out var contentElement) ||
            contentElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return ExtractContentText(contentElement);
    }

    private static IReadOnlyList<AiToolCall> ExtractToolCalls(JsonElement messageElement)
    {
        if (!messageElement.TryGetProperty("tool_calls", out var toolCallsElement) ||
            toolCallsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AiToolCall>();
        }

        var calls = new List<AiToolCall>(toolCallsElement.GetArrayLength());
        foreach (var toolCallElement in toolCallsElement.EnumerateArray())
        {
            if (toolCallElement.ValueKind != JsonValueKind.Object ||
                !toolCallElement.TryGetProperty("function", out var functionElement) ||
                functionElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = functionElement.TryGetProperty("name", out var nameElement) &&
                       nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var argumentsJson = "{}";
            if (functionElement.TryGetProperty("arguments", out var argumentsElement))
            {
                argumentsJson = argumentsElement.ValueKind == JsonValueKind.String
                    ? argumentsElement.GetString() ?? "{}"
                    : argumentsElement.GetRawText();
                if (string.IsNullOrWhiteSpace(argumentsJson))
                {
                    argumentsJson = "{}";
                }
            }

            var id = toolCallElement.TryGetProperty("id", out var idElement) &&
                     idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : string.Empty;

            calls.Add(new AiToolCall
            {
                Id = id ?? string.Empty,
                Name = name.Trim(),
                ArgumentsJson = argumentsJson
            });
        }

        return calls.Count == 0 ? Array.Empty<AiToolCall>() : calls;
    }

    private static string ExtractContentText(JsonElement contentElement)
    {
        if (contentElement.ValueKind == JsonValueKind.String)
        {
            return contentElement.GetString() ?? string.Empty;
        }

        if (contentElement.ValueKind == JsonValueKind.Object &&
            contentElement.TryGetProperty("text", out var textElement) &&
            textElement.ValueKind == JsonValueKind.String)
        {
            return textElement.GetString() ?? string.Empty;
        }

        if (contentElement.ValueKind != JsonValueKind.Array)
        {
            return contentElement.GetRawText();
        }

        var builder = new StringBuilder();
        foreach (var item in contentElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                builder.Append(item.GetString());
                continue;
            }

            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("text", out var itemTextElement) &&
                itemTextElement.ValueKind == JsonValueKind.String)
            {
                builder.Append(itemTextElement.GetString());
            }
        }

        return builder.Length == 0 ? contentElement.GetRawText() : builder.ToString();
    }

    private static string ExtractDeltaContent(JsonElement deltaElement)
    {
        if (!deltaElement.TryGetProperty("content", out var contentElement))
        {
            return string.Empty;
        }

        return ExtractContentText(contentElement);
    }

    private static bool ShouldRetryWithoutJsonMode(HttpStatusCode statusCode, string body)
    {
        if (statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
        {
            var lowered = (body ?? string.Empty).ToLowerInvariant();
            if (lowered.Contains("response_format", StringComparison.Ordinal) ||
                lowered.Contains("json_mode", StringComparison.Ordinal) ||
                lowered.Contains("json mode", StringComparison.Ordinal) ||
                lowered.Contains("json_object", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        var url = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        url = url.TrimEnd('/');
        if (!url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            url += "/v1";
        }

        return url;
    }
}
