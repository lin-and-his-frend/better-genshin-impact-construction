using System;
using System.Collections.Generic;
using System.Linq;
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

    public async Task<string> GetChatCompletionAsync(AiConfig config, IReadOnlyList<AiChatMessage> messages, CancellationToken ct)
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
            .Select(m => new { role = m.Role.ToLowerInvariant(), content = m.Content })
            .ToArray();

        var payload = new
        {
            model = config.Model.Trim(),
            messages = payloadMessages
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey.Trim());
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"请求失败: {(int)response.StatusCode} {response.ReasonPhrase}\n{body}");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var msgElement)
                ? msgElement.GetString()
                : "未知错误";
            throw new InvalidOperationException(message ?? "未知错误");
        }

        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("AI 返回为空");
        }

        var messageElement = choices[0].GetProperty("message");
        if (messageElement.TryGetProperty("content", out var contentElement))
        {
            if (contentElement.ValueKind == JsonValueKind.String)
            {
                return contentElement.GetString() ?? string.Empty;
            }

            return contentElement.GetRawText();
        }

        return string.Empty;
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
