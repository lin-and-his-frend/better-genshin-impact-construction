using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.IO.Pipes;
using System.Windows;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.Core.Script.Project;
using BetterGenshinImpact.Core.Script.Utils;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.Helpers.Http;
using BetterGenshinImpact.Helpers.Win32;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.ViewModel.Pages;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.Text.RegularExpressions;

namespace BetterGenshinImpact.Service.Remote;

internal sealed class McpRequestHandler : IMcpRequestHandler
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IConfigService _configService;
    private readonly ITranslationService _translationService;
    private readonly ILogger<McpRequestHandler> _logger;
    private readonly OfficialSiteKnowledgeService _officialSiteKnowledgeService;
    private readonly GenshinCharacterKnowledgeService _genshinCharacterKnowledgeService;
    private static readonly string InstanceTag = $"pid={Process.GetCurrentProcess().Id}";
    private static readonly HttpClient WebSearchHttpClient = HttpClientFactory.GetClient(
        "mcp-web-search",
        () => new HttpClient { Timeout = TimeSpan.FromSeconds(12) });
    private static readonly HttpClient VectorEmbeddingHttpClient = HttpClientFactory.GetClient(
        "mcp-vector-embedding",
        () => new HttpClient { Timeout = TimeSpan.FromSeconds(18) });
    private static readonly HttpClient RemoteScriptRepoHttpClient = HttpClientFactory.GetClient(
        "mcp-script-remote-repo",
        () => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
    private static readonly HttpClient LanguageResourceHttpClient = HttpClientFactory.GetClient(
        "mcp-ui-language-resource",
        () => new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
    private const int DefaultScriptListLimit = 80;
    private const int MaxScriptListLimit = 200;
    private const int DefaultScriptSearchLimit = 50;
    private const int MaxScriptSearchLimit = 200;
    private const string RemoteScriptRepoIndexUrl = "https://raw.githubusercontent.com/babalae/bettergi-scripts-list/refs/heads/release/repo.json";
    private static readonly string[] UiLanguageMirrorUrls =
    [
        "https://raw.githubusercontent.com/babalae/bettergi-i18n/refs/heads/main/i18n/{0}.json",
        "https://cnb.cool/bettergi/bettergi-i18n/-/git/raw/main/i18n/{0}.json"
    ];
    private static readonly TimeSpan RemoteScriptRepoCacheTtl = TimeSpan.FromMinutes(10);
    private const int MaxVectorCandidateCount = 240;
    private const int VectorEmbeddingBatchSize = 32;
    private const int MaxEmbeddingInputLength = 900;
    private const int MaxRerankCandidateCount = 96;
    private const int MaxRerankInputLength = 1200;
    private const int MaxScriptSearchTextLength = 420;
    private const int MaxPathingSummaryTokens = 72;
    private const int MaxPathingSummaryLength = 520;
    private const int MaxReadmeSummaryLength = 220;
    private const int ParallelSearchThreshold = 600;
    private const int ParallelFileIoThreshold = 64;
    private static readonly int SearchMaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8);
    private const string ReadmeSummaryCacheMiss = "\u0001";
    private static readonly string[] ReadmeFileNames =
    [
        "README.md",
        "readme.md",
        "Readme.md",
        "README.MD"
    ];
    private static readonly ConcurrentDictionary<string, string> ReadmeSummaryCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex MarkdownCodeBlockRegex = new(@"```[\s\S]*?```", RegexOptions.Compiled);
    private static readonly Regex MarkdownImageRegex = new(@"!\[[^\]]*\]\([^\)]*\)", RegexOptions.Compiled);
    private static readonly Regex MarkdownLinkRegex = new(@"\[(?<text>[^\]]+)\]\([^\)]*\)", RegexOptions.Compiled);
    private static readonly Regex MarkdownPrefixRegex = new(@"^\s{0,3}(#{1,6}\s*|[-*+]\s+|>\s+)", RegexOptions.Compiled);
    private static readonly string[] PreferredScriptTypeOrder = ["pathing", "js", "group", "keymouse"];
    private static readonly SemaphoreSlim ScriptRunSemaphore = new(1, 1);
    private static readonly SemaphoreSlim RemoteScriptRepoCacheSemaphore = new(1, 1);
    private static IReadOnlyList<RemoteScriptRepoEntry> _remoteScriptRepoEntriesCache = [];
    private static DateTimeOffset _remoteScriptRepoEntriesUpdatedUtc = DateTimeOffset.MinValue;
    private static readonly string[] ConfigReadBlockedPrefixes =
    [
        "AiConfig.ApiKey",
        "AiConfig.VectorApiKey",
        "CommonConfig.OssAccessKeyId",
        "CommonConfig.OssAccessKeySecret",
        "CommonConfig.WebDavUsername",
        "CommonConfig.WebDavPassword",
        "WebRemoteConfig.Password",
        "WebRemoteConfig.ClusterApiToken"
    ];
    private static readonly string[] ConfigWriteAllowedPrefixes =
    [
        "OtherConfig.UiCultureInfoName",
        "OtherConfig.GameCultureInfoName",
        "McpConfig.WebSearchEnabled",
        "McpConfig.WebSearchProvider",
        "McpConfig.WebSearchBaseUrl",
        "McpConfig.WebSearchMaxResults",
        "McpConfig.WebSearchLanguage"
    ];

    public McpRequestHandler(
        IConfigService configService,
        ITranslationService translationService,
        ILogger<McpRequestHandler> logger)
    {
        _configService = configService;
        _translationService = translationService;
        _logger = logger;
        _officialSiteKnowledgeService = new OfficialSiteKnowledgeService(logger);
        _genshinCharacterKnowledgeService = new GenshinCharacterKnowledgeService(logger);
        _officialSiteKnowledgeService.WarmupInBackground();
    }

    public async Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        var isInternalCall = stream is NamedPipeServerStream;
        var reader = new McpMessageReader(stream);
        var writer = new McpMessageWriter(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            string? json;
            try
            {
                json = await reader.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MCP 读取请求失败");
                return;
            }

            if (json == null)
            {
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("method", out var methodElement))
                {
                    ConsoleHelper.WriteLine($"[MCP {InstanceTag}] invalid request (missing method), len={json.Length}");
                    await writer.WriteErrorAsync(ExtractId(root), -32600, "Invalid Request", cancellationToken);
                    continue;
                }

                var method = methodElement.GetString() ?? string.Empty;
                var id = ExtractId(root);
                if (string.Equals(method, "tools/call", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("params", out var paramElement) &&
                    paramElement.TryGetProperty("name", out var nameElement))
                {
                    var toolName = nameElement.GetString() ?? string.Empty;
                    ConsoleHelper.WriteLine($"[MCP {InstanceTag}] call {toolName} (id={id ?? "null"})");
                }
                else
                {
                    ConsoleHelper.WriteLine($"[MCP {InstanceTag}] {method} (id={id ?? "null"})");
                }

                switch (method)
                {
                    case "initialize":
                        await writer.WriteResultAsync(id, BuildInitializeResult(), cancellationToken);
                        break;
                    case "ping":
                        await writer.WriteResultAsync(id, new { }, cancellationToken);
                        break;
                    case "tools/list":
                        await writer.WriteResultAsync(id, new { tools = BuildToolsList() }, cancellationToken);
                        break;
                    case "tools/call":
                        await HandleToolCallAsync(root, id, writer, cancellationToken, isInternalCall);
                        break;
                    case "notifications/initialized":
                        break;
                    default:
                        if (id == null)
                        {
                            break;
                        }

                        await writer.WriteErrorAsync(id, -32601, $"Method not found: {method}", cancellationToken);
                        break;
                }
            }
            catch (JsonException)
            {
                ConsoleHelper.WriteLine($"[MCP {InstanceTag}] parse error, len={json.Length}");
                await writer.WriteErrorAsync(null, -32700, "Parse error", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MCP 处理请求失败");
                await writer.WriteErrorAsync(null, -32603, "Internal error", cancellationToken);
            }
        }
    }

    private async Task HandleToolCallAsync(JsonElement root, object? id, McpMessageWriter writer, CancellationToken ct, bool isInternalCall)
    {
        if (!root.TryGetProperty("params", out var paramsElement))
        {
            await writer.WriteErrorAsync(id, -32602, "Missing params", ct);
            return;
        }

        var name = paramsElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            await writer.WriteErrorAsync(id, -32602, "Missing tool name", ct);
            return;
        }

        var argsElement = paramsElement.TryGetProperty("arguments", out var args) ? args : default;

        switch (name)
        {
            case "bgi.get_status":
                await writer.WriteResultAsync(id, ToolTextResult(JsonSerializer.Serialize(BuildStatus(), JsonOptions)), ct);
                return;
            case "bgi.get_features":
                var featureState = BuildBasicFeatureState();
                ConsoleHelper.WriteLine($"[MCP {InstanceTag}] get_features -> {JsonSerializer.Serialize(featureState, JsonOptions)}");
                await writer.WriteResultAsync(id, ToolTextResult(JsonSerializer.Serialize(featureState, JsonOptions)), ct);
                return;
            case "bgi.set_features":
                var patch = ParseFeaturePatch(argsElement);
                if (patch == null)
                {
                    await writer.WriteResultAsync(id, ToolTextResult("Invalid arguments", true), ct);
                    return;
                }
                if (IsEmptyPatch(patch))
                {
                    ConsoleHelper.WriteLine($"[MCP {InstanceTag}] set_features ignored: empty patch");
                    await writer.WriteResultAsync(id, ToolTextResult("No fields to update. Provide at least one boolean field, e.g. {\"autoPick\": true}.", true), ct);
                    return;
                }
                ApplyBasicFeaturePatch(patch);
                var updatedState = BuildBasicFeatureState();
                ConsoleHelper.WriteLine($"[MCP {InstanceTag}] set_features {JsonSerializer.Serialize(patch, JsonOptions)} -> {JsonSerializer.Serialize(updatedState, JsonOptions)}");
                await writer.WriteResultAsync(id, ToolTextResult(JsonSerializer.Serialize(updatedState, JsonOptions)), ct);
                return;
            case "bgi.language.get":
                await writer.WriteResultAsync(id, ToolTextResult(GetLanguageState()), ct);
                return;
            case "bgi.language.set":
                var languageSetResult = await SetLanguageStateAsync(argsElement, ct);
                await writer.WriteResultAsync(id, ToolTextResult(languageSetResult.text, languageSetResult.isError), ct);
                return;
            case "bgi.config.get":
                await writer.WriteResultAsync(id, ToolTextResult(GetConfigValue(argsElement, isInternalCall)), ct);
                return;
            case "bgi.config.set":
                var setResult = SetConfigValue(argsElement, isInternalCall);
                await writer.WriteResultAsync(id, ToolTextResult(setResult.text, setResult.isError), ct);
                return;
            case "bgi.config.reload":
                var reloaded = _configService.ReloadFromStorage();
                await writer.WriteResultAsync(id, ToolTextResult(JsonSerializer.Serialize(new { ok = reloaded }, JsonOptions), !reloaded), ct);
                return;
            case "bgi.get_logs":
                await writer.WriteResultAsync(id, ToolTextResult(GetLogs(argsElement)), ct);
                return;
            case "bgi.capture_screen":
                await writer.WriteResultAsync(id, await CaptureScreenResultAsync(argsElement, ct), ct);
                return;
            case "bgi.web.search":
                var webSearchResult = await WebSearchAsync(argsElement, ct);
                await writer.WriteResultAsync(id, ToolTextResult(webSearchResult.text, webSearchResult.isError), ct);
                return;
            case "search_docs":
                var searchDocsResult = await SearchDocsAsync(argsElement, ct);
                await writer.WriteResultAsync(id, ToolTextResult(searchDocsResult.text, searchDocsResult.isError), ct);
                return;
            case "get_feature_detail":
                var featureDetailResult = await GetFeatureDetailAsync(argsElement, ct);
                await writer.WriteResultAsync(id, ToolTextResult(featureDetailResult.text, featureDetailResult.isError), ct);
                return;
            case "get_download_info":
                var downloadInfoResult = await GetDownloadInfoAsync(argsElement, ct);
                await writer.WriteResultAsync(id, ToolTextResult(downloadInfoResult.text, downloadInfoResult.isError), ct);
                return;
            case "search_scripts":
                var searchScriptsResult = await SearchCommunityScriptsAsync(argsElement, ct);
                await writer.WriteResultAsync(id, ToolTextResult(searchScriptsResult.text, searchScriptsResult.isError), ct);
                return;
            case "get_faq":
                var faqResult = await GetFaqAsync(argsElement, ct);
                await writer.WriteResultAsync(id, ToolTextResult(faqResult.text, faqResult.isError), ct);
                return;
            case "get_quickstart":
                var quickstartResult = await GetQuickstartAsync(argsElement, ct);
                await writer.WriteResultAsync(id, ToolTextResult(quickstartResult.text, quickstartResult.isError), ct);
                return;
            case "bgi.script.groups":
                await writer.WriteResultAsync(id, ToolTextResult(GetScriptGroups(argsElement)), ct);
                return;
            case "bgi.script.list":
                await writer.WriteResultAsync(id, ToolTextResult(await GetScriptListAsync(argsElement, ct)), ct);
                return;
            case "bgi.script.search":
                var searchResult = await SearchScriptsAsync(argsElement, ct);
                await writer.WriteResultAsync(id, ToolTextResult(searchResult.text, searchResult.isError), ct);
                return;
            case "bgi.script.detail":
                var detailResult = await GetScriptDetailAsync(argsElement, ct);
                await writer.WriteResultAsync(id, ToolTextResult(detailResult.text, detailResult.isError), ct);
                return;
            case "bgi.script.run":
                var runResult = await RunScriptGroupsAsync(argsElement);
                await writer.WriteResultAsync(id, ToolTextResult(runResult.text, runResult.isError), ct);
                return;
            case "bgi.script.subscribe":
                var subscribeResult = await SubscribeScriptsAsync(argsElement, ct, isInternalCall);
                await writer.WriteResultAsync(id, ToolTextResult(subscribeResult.text, subscribeResult.isError), ct);
                return;
            case "bgi.one_dragon.list":
                await writer.WriteResultAsync(id, ToolTextResult(GetOneDragonConfigs()), ct);
                return;
            case "bgi.one_dragon.run":
                var dragonResult = await RunOneDragonAsync(argsElement);
                await writer.WriteResultAsync(id, ToolTextResult(dragonResult.text, dragonResult.isError), ct);
                return;
            case "bgi.task.cancel":
                CancellationContext.Instance.ManualCancel();
                await writer.WriteResultAsync(id, ToolTextResult("{\"ok\":true}"), ct);
                return;
            case "bgi.task.pause":
                RunnerContext.Instance.IsSuspend = true;
                await writer.WriteResultAsync(id, ToolTextResult("{\"ok\":true}"), ct);
                return;
            case "bgi.task.resume":
                RunnerContext.Instance.IsSuspend = false;
                await writer.WriteResultAsync(id, ToolTextResult("{\"ok\":true}"), ct);
                return;
            case "bgi.action.start_dispatcher":
                await StartDispatcherAsync();
                await writer.WriteResultAsync(id, ToolTextResult("{\"ok\":true}"), ct);
                return;
            case "bgi.action.stop_dispatcher":
                StopDispatcher();
                await writer.WriteResultAsync(id, ToolTextResult("{\"ok\":true}"), ct);
                return;
            case "bgi.action.start_game":
                if (!isInternalCall && !_configService.Get().McpConfig.AllowStartGameAction)
                {
                    await writer.WriteResultAsync(id, ToolTextResult("start_game disabled. Enable \"允许 MCP 启动游戏动作\" in settings first.", true), ct);
                    return;
                }
                await StartGameAsync();
                await writer.WriteResultAsync(id, ToolTextResult("{\"ok\":true}"), ct);
                return;
            case "bgi.task.status":
                await writer.WriteResultAsync(id, ToolTextResult(GetTaskStatus()), ct);
                return;
            default:
                await writer.WriteResultAsync(id, ToolTextResult($"Unknown tool: {name}", true), ct);
                return;
        }
    }

    private static object ToolTextResult(string text, bool isError = false)
    {
        return new
        {
            content = new[]
            {
                new { type = "text", text }
            },
            isError
        };
    }

    private async Task<object> CaptureScreenResultAsync(JsonElement argsElement, CancellationToken ct)
    {
        var config = _configService.Get().WebRemoteConfig;
        if (!config.ScreenStreamEnabled)
        {
            return ToolTextResult("Screen stream disabled", true);
        }

        var includeData = true;
        if (argsElement.ValueKind == JsonValueKind.Object &&
            argsElement.TryGetProperty("includeData", out var includeDataElement) &&
            (includeDataElement.ValueKind == JsonValueKind.True || includeDataElement.ValueKind == JsonValueKind.False))
        {
            includeData = includeDataElement.GetBoolean();
        }

        var bytes = await Task.Run(CaptureScreenPng, ct);
        if (bytes == null || bytes.Length == 0)
        {
            return ToolTextResult("Screen capture unavailable", true);
        }

        if (!includeData)
        {
            var payload = new
            {
                ok = true,
                mimeType = "image/png",
                bytes = bytes.Length,
                note = "Image data omitted. Set includeData=true to receive base64 payload."
            };
            return ToolTextResult(JsonSerializer.Serialize(payload, JsonOptions));
        }

        var base64 = Convert.ToBase64String(bytes);
        return new
        {
            content = new[]
            {
                new
                {
                    type = "image",
                    data = base64,
                    mimeType = "image/png"
                }
            }
        };
    }

    private static byte[]? CaptureScreenPng()
    {
        if (!TaskContext.Instance().IsInitialized)
        {
            return null;
        }

        Mat? mat = null;
        try
        {
            mat = TaskControl.CaptureGameImageNoRetry(TaskTriggerDispatcher.GlobalGameCapture);
            if (mat == null || mat.Empty())
            {
                return null;
            }

            if (!Cv2.ImEncode(".png", mat, out var buffer))
            {
                return null;
            }

            return buffer;
        }
        catch
        {
            return null;
        }
        finally
        {
            mat?.Dispose();
        }
    }

    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);

    private async Task<(string text, bool isError)> WebSearchAsync(JsonElement argsElement, CancellationToken ct)
    {
        var config = _configService.Get().McpConfig;
        if (!config.WebSearchEnabled)
        {
            return ("Web search disabled. Enable it in Settings -> MCP 接口 -> 允许 MCP 联网搜索。", true);
        }

        var query = ParseWebSearchQuery(argsElement);
        if (string.IsNullOrWhiteSpace(query))
        {
            return ("Missing query. Provide a keyword to search.", true);
        }

        query = SanitizeWebSearchQuery(query, out var warning);
        if (!string.IsNullOrEmpty(warning))
        {
            _logger.LogDebug("[MCP {Tag}] web.search query sanitized: {Warning}", InstanceTag, warning);
        }

        if (LooksSensitive(query, out var sensitiveReason))
        {
            return ($"Query rejected: {sensitiveReason}", true);
        }

        var provider = NormalizeWebSearchProvider(ParseWebSearchProvider(argsElement, config));
        var language = ParseWebSearchLanguage(argsElement, config);
        var maxResults = ParseWebSearchMaxResults(argsElement, config);

        var errors = new List<string>(3);
        var usedProvider = provider;
        List<object> results = [];

        var localCharacterSearch = await _genshinCharacterKnowledgeService.SearchAsync(query, maxResults, ct).ConfigureAwait(false);
        if (localCharacterSearch.matches.Count > 0)
        {
            warning = string.IsNullOrWhiteSpace(localCharacterSearch.note)
                ? warning
                : string.IsNullOrWhiteSpace(warning)
                    ? localCharacterSearch.note
                    : $"{warning} {localCharacterSearch.note}";
            return (BuildWebSearchPayload("honeyhunter_character_data", query, localCharacterSearch.matches, warning, errors), false);
        }

        if (provider == "auto")
        {
            var preferDuckDuckGo = ContainsCjk(query);
            var providerTasks = new List<Task<(string provider, bool ok, List<object> results, string? error)>>(3)
            {
                RunAutoProviderSearchAsync("fandom", token => SearchFandomAsync(query, maxResults, token), ct),
                RunAutoProviderSearchAsync("duckduckgo", token => SearchDuckDuckGoAsync(query, maxResults, token), ct)
            };
            if (!string.IsNullOrWhiteSpace(config.WebSearchBaseUrl))
            {
                providerTasks.Add(RunAutoProviderSearchAsync(
                    "searxng",
                    token => SearchSearxngAsync(config.WebSearchBaseUrl, query, language, maxResults, token),
                    ct));
            }

            var providerResults = await Task.WhenAll(providerTasks).ConfigureAwait(false);
            var scoredCandidates = new List<(string provider, List<object> matches, int relevance, int priority)>(providerResults.Length);
            foreach (var candidate in providerResults)
            {
                if (candidate.ok)
                {
                    if (candidate.results.Count <= 0)
                    {
                        continue;
                    }

                    scoredCandidates.Add((
                        candidate.provider,
                        candidate.results,
                        EvaluateWebSearchRelevance(query, candidate.results),
                        GetAutoProviderPriority(candidate.provider, preferDuckDuckGo)));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(candidate.error))
                {
                    errors.Add($"{candidate.provider}: {candidate.error}");
                }
            }

            if (scoredCandidates.Count > 0)
            {
                var positiveCandidate = scoredCandidates
                    .Where(item => item.relevance > 0)
                    .OrderByDescending(item => item.relevance)
                    .ThenBy(item => item.priority)
                    .ThenByDescending(item => item.matches.Count)
                    .FirstOrDefault();
                if (positiveCandidate.matches != null)
                {
                    return (BuildWebSearchPayload(positiveCandidate.provider, query, positiveCandidate.matches, warning, errors), false);
                }

                var fallbackCandidate = scoredCandidates
                    .OrderBy(item => item.priority)
                    .ThenByDescending(item => item.matches.Count)
                    .First();
                return (BuildWebSearchPayload(fallbackCandidate.provider, query, fallbackCandidate.matches, warning, errors), false);
            }

            return (BuildWebSearchPayload("auto", query, results, warning, errors), errors.Count > 0);
        }

        if (provider == "searxng")
        {
            if (string.IsNullOrWhiteSpace(config.WebSearchBaseUrl))
            {
                return ("SearXNG baseUrl missing. Configure it in Settings -> MCP 接口 -> 联网搜索基础地址。", true);
            }

            var searx = await SearchSearxngAsync(config.WebSearchBaseUrl, query, language, maxResults, ct);
            usedProvider = "searxng";
            results = searx.results;
            if (!searx.ok)
            {
                var providerErrors = new List<string>(errors);
                if (!string.IsNullOrWhiteSpace(searx.error))
                {
                    providerErrors.Add(searx.error);
                }
                return (BuildWebSearchPayload(usedProvider, query, results, warning, providerErrors), true);
            }

            return (BuildWebSearchPayload(usedProvider, query, results, warning, errors), false);
        }

        if (provider == "fandom")
        {
            var fandom = await SearchFandomAsync(query, maxResults, ct);
            usedProvider = "fandom";
            results = fandom.results;
            if (!fandom.ok)
            {
                var providerErrors = new List<string>(errors);
                if (!string.IsNullOrWhiteSpace(fandom.error))
                {
                    providerErrors.Add(fandom.error);
                }
                return (BuildWebSearchPayload(usedProvider, query, results, warning, providerErrors), true);
            }

            return (BuildWebSearchPayload(usedProvider, query, results, warning, errors), false);
        }

        if (provider == "duckduckgo")
        {
            var ddg = await SearchDuckDuckGoAsync(query, maxResults, ct);
            usedProvider = "duckduckgo";
            results = ddg.results;
            if (!ddg.ok)
            {
                var providerErrors = new List<string>(errors);
                if (!string.IsNullOrWhiteSpace(ddg.error))
                {
                    providerErrors.Add(ddg.error);
                }
                return (BuildWebSearchPayload(usedProvider, query, results, warning, providerErrors), true);
            }

            return (BuildWebSearchPayload(usedProvider, query, results, warning, errors), false);
        }

        return ($"Unsupported provider: {provider}. Supported: auto / searxng / fandom / duckduckgo.", true);
    }

    private static string BuildWebSearchPayload(string provider, string query, IReadOnlyList<object> results, string? warning, IReadOnlyList<string> errors)
    {
        var payload = new
        {
            provider,
            query,
            count = results.Count,
            results,
            warning,
            errors = errors.Count == 0 ? null : errors
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string ParseWebSearchQuery(JsonElement argsElement)
    {
        if (argsElement.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (argsElement.TryGetProperty("query", out var queryElement) && queryElement.ValueKind == JsonValueKind.String)
        {
            return queryElement.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static int ParseWebSearchMaxResults(JsonElement argsElement, McpConfig config)
    {
        var maxResults = config.WebSearchMaxResults;
        if (argsElement.ValueKind == JsonValueKind.Object &&
            argsElement.TryGetProperty("maxResults", out var maxResultsElement) &&
            maxResultsElement.ValueKind == JsonValueKind.Number &&
            maxResultsElement.TryGetInt32(out var parsed))
        {
            maxResults = parsed;
        }

        return Math.Clamp(maxResults, 1, 10);
    }

    private static string ParseWebSearchProvider(JsonElement argsElement, McpConfig config)
    {
        var provider = config.WebSearchProvider;
        if (argsElement.ValueKind == JsonValueKind.Object &&
            argsElement.TryGetProperty("provider", out var providerElement) &&
            providerElement.ValueKind == JsonValueKind.String)
        {
            provider = providerElement.GetString() ?? provider;
        }

        return provider ?? string.Empty;
    }

    private static string ParseWebSearchLanguage(JsonElement argsElement, McpConfig config)
    {
        var language = config.WebSearchLanguage;
        if (argsElement.ValueKind == JsonValueKind.Object &&
            argsElement.TryGetProperty("language", out var langElement) &&
            langElement.ValueKind == JsonValueKind.String)
        {
            language = langElement.GetString() ?? language;
        }

        return string.IsNullOrWhiteSpace(language) ? "zh-CN" : language.Trim();
    }

    private static string NormalizeWebSearchProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return "auto";
        }

        var normalized = provider.Trim().ToLowerInvariant();
        return normalized switch
        {
            "auto" => "auto",
            "searx" => "searxng",
            "searxng" => "searxng",
            "ddg" => "duckduckgo",
            "duckduckgo" => "duckduckgo",
            "fandom" => "fandom",
            _ => normalized
        };
    }

    private static string SanitizeWebSearchQuery(string query, out string? warning)
    {
        warning = null;
        var trimmed = (query ?? string.Empty).Trim();
        trimmed = trimmed.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        while (trimmed.Contains("  ", StringComparison.Ordinal))
        {
            trimmed = trimmed.Replace("  ", " ", StringComparison.Ordinal);
        }

        const int maxLen = 200;
        if (trimmed.Length > maxLen)
        {
            warning = $"Query truncated to {maxLen} chars.";
            trimmed = trimmed.Substring(0, maxLen);
        }

        return trimmed;
    }

    private static bool LooksSensitive(string query, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        var lowered = query.ToLowerInvariant();
        if (lowered.Contains("bearer ", StringComparison.Ordinal) ||
            lowered.Contains("api_key", StringComparison.Ordinal) ||
            lowered.Contains("apikey", StringComparison.Ordinal) ||
            lowered.Contains("sk-", StringComparison.Ordinal))
        {
            reason = "possible credential/token detected in query";
            return true;
        }

        return false;
    }

    private static async Task<(bool ok, List<object> results, string? error)> SearchSearxngAsync(
        string baseUrl,
        string query,
        string language,
        int maxResults,
        CancellationToken ct)
    {
        if (!TryBuildSearxngUri(baseUrl, query, language, out var uri, out var uriError))
        {
            return (false, [], uriError);
        }

        try
        {
            using var request = CreateWebSearchRequest(uri, language);
            using var resp = await WebSearchHttpClient.SendAsync(request, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, [], $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("results", out var resultsElement) || resultsElement.ValueKind != JsonValueKind.Array)
            {
                return (true, [], null);
            }

            var list = new List<object>(Math.Min(maxResults, 10));
            foreach (var item in resultsElement.EnumerateArray())
            {
                if (list.Count >= maxResults)
                {
                    break;
                }

                var title = item.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
                    ? titleElement.GetString()
                    : null;
                var url = item.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
                    ? urlElement.GetString()
                    : null;
                var content = item.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
                    ? contentElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                list.Add(new
                {
                    title = (title ?? string.Empty).Trim(),
                    url = url.Trim(),
                    snippet = Truncate((content ?? string.Empty).Trim(), 240)
                });
            }

            return (true, list, null);
        }
        catch (OperationCanceledException)
        {
            return (false, [], "timeout/canceled");
        }
        catch (Exception ex)
        {
            return (false, [], ex.Message);
        }
    }

    private static bool TryBuildSearxngUri(string baseUrl, string query, string language, out Uri uri, out string? error)
    {
        uri = null!;
        error = null;

        var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            error = "baseUrl empty";
            return false;
        }

        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "https://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var root))
        {
            error = "invalid baseUrl";
            return false;
        }

        var queryString = $"q={Uri.EscapeDataString(query)}&format=json&language={Uri.EscapeDataString(language)}&safesearch=0&categories=general";
        uri = new UriBuilder(new Uri(root, "/search"))
        {
            Query = queryString
        }.Uri;

        return true;
    }

    private static async Task<(bool ok, List<object> results, string? error)> SearchFandomAsync(string query, int maxResults, CancellationToken ct)
    {
        // Genshin Impact Wiki (Fandom) - MediaWiki API
        var queryString = $"action=query&list=search&srsearch={Uri.EscapeDataString(query)}&srlimit={maxResults}&utf8=1&format=json";
        var uri = new UriBuilder("https://genshin-impact.fandom.com/api.php") { Query = queryString }.Uri;

        try
        {
            using var request = CreateWebSearchRequest(uri, "en-US");
            using var resp = await WebSearchHttpClient.SendAsync(request, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, [], $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("query", out var queryElement) ||
                !queryElement.TryGetProperty("search", out var searchElement) ||
                searchElement.ValueKind != JsonValueKind.Array)
            {
                return (true, [], null);
            }

            var list = new List<object>(Math.Min(maxResults, 10));
            foreach (var item in searchElement.EnumerateArray())
            {
                if (list.Count >= maxResults)
                {
                    break;
                }

                var title = item.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
                    ? titleElement.GetString()
                    : null;
                var snippet = item.TryGetProperty("snippet", out var snippetElement) && snippetElement.ValueKind == JsonValueKind.String
                    ? snippetElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var urlTitle = title.Replace(' ', '_');
                var url = $"https://genshin-impact.fandom.com/wiki/{Uri.EscapeDataString(urlTitle)}";

                list.Add(new
                {
                    title = title.Trim(),
                    url,
                    snippet = Truncate(StripHtml(snippet ?? string.Empty), 240)
                });
            }

            return (true, list, null);
        }
        catch (OperationCanceledException)
        {
            return (false, [], "timeout/canceled");
        }
        catch (Exception ex)
        {
            return (false, [], ex.Message);
        }
    }

    private static async Task<(bool ok, List<object> results, string? error)> SearchDuckDuckGoAsync(string query, int maxResults, CancellationToken ct)
    {
        var queryString = $"q={Uri.EscapeDataString(query)}&format=json&no_redirect=1&no_html=1&skip_disambig=1";
        var uri = new UriBuilder("https://api.duckduckgo.com/") { Query = queryString }.Uri;

        try
        {
            using var request = CreateWebSearchRequest(uri, "en-US");
            using var resp = await WebSearchHttpClient.SendAsync(request, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, [], $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            }

            using var doc = JsonDocument.Parse(body);
            var list = new List<object>(Math.Min(maxResults, 10));

            var heading = doc.RootElement.TryGetProperty("Heading", out var headingElement) && headingElement.ValueKind == JsonValueKind.String
                ? headingElement.GetString()
                : null;
            var abstractText = doc.RootElement.TryGetProperty("AbstractText", out var abstractElement) && abstractElement.ValueKind == JsonValueKind.String
                ? abstractElement.GetString()
                : null;
            var abstractUrl = doc.RootElement.TryGetProperty("AbstractURL", out var abstractUrlElement) && abstractUrlElement.ValueKind == JsonValueKind.String
                ? abstractUrlElement.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(abstractText))
            {
                list.Add(new
                {
                    title = string.IsNullOrWhiteSpace(heading) ? query : heading.Trim(),
                    url = abstractUrl ?? string.Empty,
                    snippet = Truncate(abstractText.Trim(), 240)
                });
            }

            if (doc.RootElement.TryGetProperty("RelatedTopics", out var relatedElement) &&
                relatedElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in relatedElement.EnumerateArray())
                {
                    if (list.Count >= maxResults)
                    {
                        break;
                    }

                    if (TryAddDuckDuckGoTopic(list, item))
                    {
                        continue;
                    }

                    if (item.TryGetProperty("Topics", out var topicsElement) && topicsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var nested in topicsElement.EnumerateArray())
                        {
                            if (list.Count >= maxResults)
                            {
                                break;
                            }

                            TryAddDuckDuckGoTopic(list, nested);
                        }
                    }
                }
            }

            return (true, list, null);
        }
        catch (OperationCanceledException)
        {
            return (false, [], "timeout/canceled");
        }
        catch (Exception ex)
        {
            return (false, [], ex.Message);
        }
    }

    private static bool TryAddDuckDuckGoTopic(List<object> list, JsonElement topic)
    {
        if (!topic.TryGetProperty("Text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = textElement.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var url = topic.TryGetProperty("FirstURL", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString()
            : string.Empty;

        list.Add(new
        {
            title = Truncate(text.Trim(), 80),
            url = url ?? string.Empty,
            snippet = Truncate(text.Trim(), 240)
        });
        return true;
    }

    private static HttpRequestMessage CreateWebSearchRequest(Uri uri, string language)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("accept-language", string.IsNullOrWhiteSpace(language) ? "zh-CN,zh;q=0.9" : language);
        request.Headers.UserAgent.ParseAdd("BetterGI-MCP/1.0");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36");
        return request;
    }

    private static string StripHtml(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var noTags = HtmlTagRegex.Replace(text, string.Empty);
        return WebUtility.HtmlDecode(noTags).Trim();
    }

    private static string Truncate(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        var suffix = $"... (+{text.Length - maxChars} chars)";
        if (suffix.Length >= maxChars)
        {
            return text.Substring(0, maxChars);
        }

        return text.Substring(0, maxChars - suffix.Length) + suffix;
    }

    private static bool ContainsCjk(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (ch is >= '\u3400' and <= '\u4DBF' ||
                ch is >= '\u4E00' and <= '\u9FFF')
            {
                return true;
            }
        }

        return false;
    }

    private static int EvaluateWebSearchRelevance(string query, IReadOnlyList<object> results)
    {
        if (results.Count == 0)
        {
            return -1;
        }

        var keywords = ExtractWebSearchKeywords(query);
        if (keywords.Count == 0)
        {
            return 1;
        }

        var score = 0;
        foreach (var item in results)
        {
            var title = ReadWebSearchResultField(item, "title");
            var snippet = ReadWebSearchResultField(item, "snippet");
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(snippet))
            {
                continue;
            }

            var text = $"{title} {snippet}";
            var hits = 0;
            foreach (var keyword in keywords)
            {
                if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    hits++;
                }
            }

            if (hits > 0)
            {
                score += 1 + hits;
            }
        }

        return score;
    }

    private static IReadOnlyList<string> ExtractWebSearchKeywords(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var normalized = query;
        var noiseWords = new[]
        {
            "原神",
            "genshin impact",
            "genshin",
            "impact",
            "wiki",
            "角色",
            "材料",
            "培养",
            "升级",
            "突破",
            "天赋",
            "需要",
            "所需",
            "要什么",
            "什么",
            "如何",
            "怎么",
            "请问",
            "一下",
            "list",
            "列表"
        };

        foreach (var noise in noiseWords)
        {
            normalized = normalized.Replace(noise, " ", StringComparison.OrdinalIgnoreCase);
        }

        normalized = normalized
            .Replace("，", " ", StringComparison.Ordinal)
            .Replace("。", " ", StringComparison.Ordinal)
            .Replace("、", " ", StringComparison.Ordinal)
            .Replace(",", " ", StringComparison.Ordinal)
            .Replace(".", " ", StringComparison.Ordinal)
            .Replace("：", " ", StringComparison.Ordinal)
            .Replace(":", " ", StringComparison.Ordinal)
            .Replace("！", " ", StringComparison.Ordinal)
            .Replace("!", " ", StringComparison.Ordinal)
            .Replace("？", " ", StringComparison.Ordinal)
            .Replace("?", " ", StringComparison.Ordinal)
            .Replace("（", " ", StringComparison.Ordinal)
            .Replace("）", " ", StringComparison.Ordinal)
            .Replace("(", " ", StringComparison.Ordinal)
            .Replace(")", " ", StringComparison.Ordinal)
            .Replace("/", " ", StringComparison.Ordinal)
            .Replace("\\", " ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal)
            .Trim();

        while (normalized.Contains("  ", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        }

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        if (tokens.Count > 0)
        {
            return tokens;
        }

        var cjk = new string(query.Where(ch => ch is >= '\u3400' and <= '\u4DBF' || ch is >= '\u4E00' and <= '\u9FFF').ToArray());
        if (cjk.Length >= 2)
        {
            return [cjk];
        }

        return [];
    }

    private static string ReadWebSearchResultField(object item, string fieldName)
    {
        if (item == null)
        {
            return string.Empty;
        }

        var property = item.GetType().GetProperty(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.IgnoreCase);
        if (property == null)
        {
            return string.Empty;
        }

        var value = property.GetValue(item);
        return value?.ToString() ?? string.Empty;
    }

    private string GetLogs(JsonElement argsElement)
    {
        var config = _configService.Get().WebRemoteConfig;
        if (!config.LogStreamEnabled)
        {
            return "Log stream disabled";
        }

        var maxLines = 100;
        if (argsElement.ValueKind == JsonValueKind.Object &&
            argsElement.TryGetProperty("maxLines", out var maxLinesElement) &&
            maxLinesElement.ValueKind == JsonValueKind.Number &&
            maxLinesElement.TryGetInt32(out var parsed))
        {
            maxLines = Math.Clamp(parsed, 1, 500);
        }

        var lines = LogRelayHub.GetSnapshot(maxLines);
        return JsonSerializer.Serialize(lines, JsonOptions);
    }

    private string GetConfigValue(JsonElement argsElement, bool isInternalCall)
    {
        var path = argsElement.ValueKind == JsonValueKind.Object && argsElement.TryGetProperty("path", out var pathElement)
            ? pathElement.GetString()
            : null;

        var config = _configService.Get();
        if (isInternalCall && string.IsNullOrWhiteSpace(path))
        {
            return JsonSerializer.Serialize(config, JsonOptions);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return JsonSerializer.Serialize(new { error = "Missing path" }, JsonOptions);
        }

        if (!TryNormalizeConfigPath(path, out var normalizedPath, out var normalizeError))
        {
            return JsonSerializer.Serialize(new { error = normalizeError ?? "Invalid path" }, JsonOptions);
        }

        if (!isInternalCall && !IsConfigPathAllowedForRead(normalizedPath, out var policyError))
        {
            return JsonSerializer.Serialize(new { error = policyError ?? "Config path not allowed" }, JsonOptions);
        }

        if (!ConfigPathAccessor.TryGetValue(config, normalizedPath, out var value, out var error))
        {
            return JsonSerializer.Serialize(new { error }, JsonOptions);
        }

        return JsonSerializer.Serialize(value ?? new { }, JsonOptions);
    }

    private string GetLanguageState()
    {
        var otherConfig = _configService.Get().OtherConfig;
        var currentUiCulture = _translationService.GetCurrentCulture().Name;
        var payload = new
        {
            uiCulture = NormalizeCultureForOutput(otherConfig.UiCultureInfoName),
            gameCulture = NormalizeCultureForOutput(otherConfig.GameCultureInfoName),
            currentUiCulture = string.IsNullOrWhiteSpace(currentUiCulture) ? "zh-Hans" : currentUiCulture,
            knownUiCultures = new[] { "zh-Hans", "zh-Hant", "en" }
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static int GetAutoProviderPriority(string provider, bool preferDuckDuckGo)
    {
        if (string.Equals(provider, "searxng", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (preferDuckDuckGo)
        {
            return string.Equals(provider, "duckduckgo", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        }

        return string.Equals(provider, "fandom", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private static async Task<(string provider, bool ok, List<object> results, string? error)> RunAutoProviderSearchAsync(
        string provider,
        Func<CancellationToken, Task<(bool ok, List<object> results, string? error)>> searchFactory,
        CancellationToken ct)
    {
        var result = await searchFactory(ct).ConfigureAwait(false);
        return (provider, result.ok, result.results, result.error);
    }

    private async Task<(string text, bool isError)> SetLanguageStateAsync(JsonElement argsElement, CancellationToken ct)
    {
        if (argsElement.ValueKind != JsonValueKind.Object)
        {
            return ("Invalid arguments. Provide {\"uiCulture\":\"zh-Hans\"} or {\"gameCulture\":\"zh-Hans\"}.", true);
        }

        var uiInput = ParseStringArgument(argsElement, "uiCulture") ?? ParseStringArgument(argsElement, "ui");
        var gameInput = ParseStringArgument(argsElement, "gameCulture") ?? ParseStringArgument(argsElement, "game");

        var normalizedUi = NormalizeCultureName(uiInput);
        var normalizedGame = NormalizeCultureName(gameInput);

        if (normalizedUi == null && normalizedGame == null)
        {
            return ("No language field to update. Provide uiCulture/ui or gameCulture/game.", true);
        }

        var uiChanged = false;
        var gameChanged = false;
        RunOnUiThread(() =>
        {
            var config = _configService.Get();
            if (!string.IsNullOrWhiteSpace(normalizedUi) &&
                !string.Equals(config.OtherConfig.UiCultureInfoName, normalizedUi, StringComparison.OrdinalIgnoreCase))
            {
                config.OtherConfig.UiCultureInfoName = normalizedUi;
                uiChanged = true;
            }

            if (!string.IsNullOrWhiteSpace(normalizedGame) &&
                !string.Equals(config.OtherConfig.GameCultureInfoName, normalizedGame, StringComparison.OrdinalIgnoreCase))
            {
                config.OtherConfig.GameCultureInfoName = normalizedGame;
                gameChanged = true;
            }
        });

        var uiLanguageUpdated = false;
        var uiLanguageUpdateMessage = string.Empty;
        var docsIndexRefreshTriggered = false;
        if (uiChanged)
        {
            var updateResult = await TryUpdateUiLanguageFileAsync(normalizedUi ?? string.Empty, ct);
            uiLanguageUpdated = updateResult.updated;
            uiLanguageUpdateMessage = updateResult.message;

            try
            {
                _translationService.Reload();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MCP 触发语言重载失败");
            }

            _officialSiteKnowledgeService.ForceRefreshInBackground();
            docsIndexRefreshTriggered = true;
        }

        var payload = new
        {
            ok = true,
            uiChanged,
            gameChanged,
            uiLanguageUpdated,
            uiLanguageUpdateMessage,
            docsIndexRefreshTriggered,
            state = JsonSerializer.Deserialize<object>(GetLanguageState(), JsonOptions)
        };
        return (JsonSerializer.Serialize(payload, JsonOptions), false);
    }

    private async Task<(bool updated, string message)> TryUpdateUiLanguageFileAsync(string cultureName, CancellationToken ct)
    {
        var normalizedCulture = NormalizeCultureName(cultureName);
        if (string.IsNullOrWhiteSpace(normalizedCulture))
        {
            return (false, "uiCulture 为空，跳过语言包更新");
        }

        if (string.Equals(normalizedCulture, "zh-Hans", StringComparison.OrdinalIgnoreCase))
        {
            return (true, "zh-Hans 为内置语言，无需更新语言包");
        }

        byte[]? bytes = null;
        string? lastError = null;
        foreach (var template in UiLanguageMirrorUrls)
        {
            var url = string.Format(template, normalizedCulture);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.UserAgent.ParseAdd("BetterGI-MCP/1.0");
                using var response = await LanguageResourceHttpClient.SendAsync(request, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    lastError = $"404: {url}";
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    lastError = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {url}";
                    continue;
                }

                bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                using var jsonDoc = JsonDocument.Parse(bytes);
                if (jsonDoc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    lastError = $"语言文件结构无效: {url}";
                    bytes = null;
                    continue;
                }

                break;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        if (bytes == null)
        {
            return (false, $"语言包更新失败: {lastError ?? "未找到可用镜像"}");
        }

        try
        {
            var dir = Global.Absolute(@"User\I18n");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{normalizedCulture}.json");
            var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);

            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }

            return (true, "已触发语言包更新");
        }
        catch (Exception ex)
        {
            return (false, $"语言包写入失败: {ex.Message}");
        }
    }

    private async Task<(string text, bool isError)> SearchDocsAsync(JsonElement argsElement, CancellationToken ct)
    {
        var query = ParseStringArgument(argsElement, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return ("Missing query. Provide keyword to search BetterGI official docs.", true);
        }

        var limit = ParseIntArgument(argsElement, "limit", 5, 1, 20);
        var normalizedQuery = query.Trim();
        var docsTask = _officialSiteKnowledgeService.SearchAsync(normalizedQuery, limit, ct);
        var faqTask = _officialSiteKnowledgeService.GetFaqAsync(normalizedQuery, limit, ct);
        await Task.WhenAll(docsTask, faqTask).ConfigureAwait(false);

        var docsResult = docsTask.Result;
        var faqResult = faqTask.Result;
        var hits = docsResult.hits.ToList();
        var warning = docsResult.warning;
        var faqFallback = false;

        if (hits.Count == 0)
        {
            if (faqResult.hits.Count > 0)
            {
                hits = faqResult.hits.ToList();
                warning = faqResult.warning;
                faqFallback = true;
            }
        }

        List<object>? fallbackWeb = null;
        string? fallbackWarning = null;
        var needsFallback = hits.Count == 0 || hits.Count < Math.Min(limit, 2);
        if (needsFallback)
        {
            var ddg = await SearchDuckDuckGoAsync($"site:bettergi.com {normalizedQuery}", limit, ct);
            if (ddg.ok && ddg.results.Count > 0)
            {
                fallbackWeb = ddg.results;
            }
            else if (!ddg.ok && !string.IsNullOrWhiteSpace(ddg.error))
            {
                fallbackWarning = ddg.error;
            }
        }

        var payload = new
        {
            query = normalizedQuery,
            limit,
            returned = hits.Count,
            pageCount = docsResult.pageCount,
            warning,
            faqFallback,
            fallbackWarning,
            fallbackWeb,
            matches = hits.Select(hit => new
            {
                title = hit.Title,
                url = hit.Url,
                snippet = hit.Snippet,
                score = Math.Round(hit.Score, 3, MidpointRounding.AwayFromZero)
            })
        };

        return (JsonSerializer.Serialize(payload, JsonOptions), false);
    }

    private async Task<(string text, bool isError)> GetFeatureDetailAsync(JsonElement argsElement, CancellationToken ct)
    {
        var feature = ParseStringArgument(argsElement, "feature") ??
                      ParseStringArgument(argsElement, "query") ??
                      ParseStringArgument(argsElement, "keyword");
        if (string.IsNullOrWhiteSpace(feature))
        {
            return ("Missing feature. Provide feature keyword, e.g. {\"feature\":\"自动邀约\"}.", true);
        }

        var limit = ParseIntArgument(argsElement, "limit", 4, 1, 10);
        var result = await _officialSiteKnowledgeService.GetFeatureDetailAsync(feature.Trim(), limit, ct);
        var payload = new
        {
            feature = feature.Trim(),
            returned = result.hits.Count,
            pageCount = result.pageCount,
            warning = result.warning,
            details = result.hits.Select(hit => new
            {
                title = hit.Title,
                url = hit.Url,
                snippet = hit.Snippet,
                score = Math.Round(hit.Score, 3, MidpointRounding.AwayFromZero)
            })
        };
        return (JsonSerializer.Serialize(payload, JsonOptions), false);
    }

    private async Task<(string text, bool isError)> GetDownloadInfoAsync(JsonElement argsElement, CancellationToken ct)
    {
        var limit = ParseIntArgument(argsElement, "limit", 12, 1, 30);
        var result = await _officialSiteKnowledgeService.GetDownloadInfoAsync(limit, ct);
        var payload = new
        {
            returned = result.links.Count,
            pageCount = result.pageCount,
            warning = result.warning,
            downloads = result.links.Select(link => new
            {
                url = link.Url,
                fromPageTitle = link.PageTitle,
                fromPageUrl = link.PageUrl
            }),
            relatedPages = result.relatedPages.Select(hit => new
            {
                title = hit.Title,
                url = hit.Url,
                snippet = hit.Snippet
            })
        };
        return (JsonSerializer.Serialize(payload, JsonOptions), false);
    }

    private async Task<(string text, bool isError)> SearchCommunityScriptsAsync(JsonElement argsElement, CancellationToken ct)
    {
        var query = ParseStringArgument(argsElement, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return ("Missing query. Provide keyword to search pathing scripts.", true);
        }

        var limit = ParseIntArgument(argsElement, "limit", 8, 1, 30);
        var result = await SearchRemoteScriptsForSubscriptionAsync(query.Trim(), "pathing", limit, ct);
        var payload = new
        {
            query = query.Trim(),
            source = RemoteScriptRepoIndexUrl,
            type = "pathing",
            returned = result.matches.Count,
            candidateTotal = result.candidateTotal,
            truncated = result.truncated,
            warning = result.error,
            matches = result.matches
        };
        return (JsonSerializer.Serialize(payload, JsonOptions), false);
    }

    private async Task<(string text, bool isError)> SubscribeScriptsAsync(JsonElement argsElement, CancellationToken ct, bool isInternalCall)
    {
        var names = ParseScriptNames(argsElement);
        var query = ParseScriptQuery(argsElement)?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            var inferredQuery = TryInferSubscribeQueryFromNames(names);
            if (!string.IsNullOrWhiteSpace(inferredQuery))
            {
                query = inferredQuery;
                names.Clear();
            }
        }

        var limit = ParseIntArgument(argsElement, "limit", 20, 1, 100);
        var importNowRequested = ParseBoolArgument(argsElement, "importNow", isInternalCall);
        if (ParseBoolArgument(argsElement, "previewOnly", false) || ParseBoolArgument(argsElement, "dryRun", false))
        {
            importNowRequested = false;
        }

        var allowImportNow = isInternalCall || _configService.Get().ScriptConfig.AllowMcpImportNow;
        var importNow = importNowRequested && allowImportNow;
        var importPolicyWarning = !isInternalCall && importNowRequested && !allowImportNow
            ? "importNow blocked. Enable \"允许 MCP 立即导入脚本\" in settings first."
            : null;

        if (names.Count == 0 && string.IsNullOrWhiteSpace(query))
        {
            return ("Missing names/query. Provide {\"names\":[\"脚本名\"]} or {\"query\":\"关键词\"}.", true);
        }

        if (!string.IsNullOrWhiteSpace(query) && names.Count == 0)
        {
            var queryResult = await SearchRemoteScriptsForSubscriptionAsync(query!, "pathing", limit, ct);
            var importTargets = ExtractScriptPathsFromMatches(queryResult.matches);
            var importResult = importNow
                ? await TryImportSubscribedScriptsAsync(importTargets, ct)
                : (ok: true, importedCount: 0, error: (string?)null, importedPaths: new List<string>());

            var queryPayload = new
            {
                query,
                source = RemoteScriptRepoIndexUrl,
                returned = queryResult.matches.Count,
                candidateTotal = queryResult.candidateTotal,
                truncated = queryResult.truncated,
                warning = queryResult.error,
                importWarning = importPolicyWarning,
                importNow,
                imported = importResult.importedCount,
                importError = importResult.error,
                importedPaths = importResult.importedPaths,
                matches = queryResult.matches
            };
            var isImportError = importNow && importTargets.Count > 0 && !importResult.ok;
            return (JsonSerializer.Serialize(queryPayload, JsonOptions), isImportError);
        }

        var (entries, loadError) = await LoadRemoteScriptRepoEntriesAsync(ct);
        if (entries.Count == 0)
        {
            return ($"远程脚本仓库不可用：{loadError ?? "索引为空"}", true);
        }

        var unmatched = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = new List<object>();

        foreach (var requested in names)
        {
            if (!TryFindBestRemoteEntryByName(entries, requested, out var entry))
            {
                unmatched.Add(requested);
                continue;
            }

            if (!seenPaths.Add(entry.Path))
            {
                continue;
            }

            matches.Add(new
            {
                name = entry.Name,
                folder = entry.Folder,
                path = entry.Path,
                subscribeUri = BuildScriptSubscribeUri(entry.Path),
                description = ResolveRemoteScriptDescription(entry)
            });
        }

        var importPaths = ExtractScriptPathsFromMatches(matches);
        var importResultByName = importNow
            ? await TryImportSubscribedScriptsAsync(importPaths, ct)
            : (ok: true, importedCount: 0, error: (string?)null, importedPaths: new List<string>());

        var payload = new
        {
            source = RemoteScriptRepoIndexUrl,
            requested = names,
            returned = matches.Count,
            unmatched,
            importWarning = importPolicyWarning,
            importNow,
            imported = importResultByName.importedCount,
            importError = importResultByName.error,
            importedPaths = importResultByName.importedPaths,
            matches
        };
        var isError = importNow && importPaths.Count > 0 && !importResultByName.ok;
        return (JsonSerializer.Serialize(payload, JsonOptions), isError);
    }

    private async Task<(bool ok, int importedCount, string? error, List<string> importedPaths)> TryImportSubscribedScriptsAsync(
        IEnumerable<string> paths,
        CancellationToken ct)
    {
        var importedPaths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in paths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            if (!TryNormalizeRemoteScriptPath(rawPath, out var normalizedPath, out var normalizeError))
            {
                return (false, 0, $"Invalid script path '{rawPath}': {normalizeError}", importedPaths);
            }

            if (seen.Add(normalizedPath))
            {
                importedPaths.Add(normalizedPath);
            }
        }

        if (importedPaths.Count == 0)
        {
            return (true, 0, null, []);
        }

        var repoRoot = TryGetCenterRepoContentRoot();
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
        {
            return (false, 0, "本地脚本仓库不存在，请先在脚本仓库页更新仓库后再订阅。", importedPaths);
        }

        ct.ThrowIfCancellationRequested();
        try
        {
            var pathJson = JsonSerializer.Serialize(importedPaths, JsonOptions);
            await RunOnUiThreadAsync(() => ScriptRepoUpdater.Instance.ImportScriptFromPathJson(pathJson));
            return (true, importedPaths.Count, null, importedPaths);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[MCP {Tag}] subscribe import failed", InstanceTag);
            return (false, 0, ex.Message, importedPaths);
        }
    }

    private static List<string> ExtractScriptPathsFromMatches(IEnumerable<object> matches)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var match in matches)
        {
            if (match == null)
            {
                continue;
            }

            string? path = null;
            if (match is JsonElement jsonElement)
            {
                path = TryGetJsonString(jsonElement, "path");
            }
            else
            {
                var matchType = match.GetType();
                var property = matchType.GetProperty("path") ?? matchType.GetProperty("Path");
                if (property?.GetValue(match) is string pathValue)
                {
                    path = pathValue;
                }
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var normalized = path.Trim();
            if (seen.Add(normalized))
            {
                paths.Add(normalized);
            }
        }

        return paths;
    }

    private async Task<(string text, bool isError)> GetFaqAsync(JsonElement argsElement, CancellationToken ct)
    {
        var query = ParseStringArgument(argsElement, "query");
        var limit = ParseIntArgument(argsElement, "limit", 6, 1, 20);
        var result = await _officialSiteKnowledgeService.GetFaqAsync(query, limit, ct);
        var payload = new
        {
            query = query?.Trim(),
            returned = result.hits.Count,
            pageCount = result.pageCount,
            warning = result.warning,
            faq = result.hits.Select(hit => new
            {
                title = hit.Title,
                url = hit.Url,
                snippet = hit.Snippet,
                score = Math.Round(hit.Score, 3, MidpointRounding.AwayFromZero)
            })
        };
        return (JsonSerializer.Serialize(payload, JsonOptions), false);
    }

    private async Task<(string text, bool isError)> GetQuickstartAsync(JsonElement argsElement, CancellationToken ct)
    {
        var limit = ParseIntArgument(argsElement, "limit", 6, 1, 20);
        var result = await _officialSiteKnowledgeService.GetQuickstartAsync(limit, ct);
        var payload = new
        {
            returned = result.hits.Count,
            pageCount = result.pageCount,
            warning = result.warning,
            quickstart = result.hits.Select(hit => new
            {
                title = hit.Title,
                url = hit.Url,
                snippet = hit.Snippet,
                score = Math.Round(hit.Score, 3, MidpointRounding.AwayFromZero)
            })
        };
        return (JsonSerializer.Serialize(payload, JsonOptions), false);
    }

    private static int ParseIntArgument(JsonElement argsElement, string propertyName, int defaultValue, int min, int max)
    {
        if (argsElement.ValueKind == JsonValueKind.Object &&
            argsElement.TryGetProperty(propertyName, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out var parsed))
        {
            return Math.Clamp(parsed, min, max);
        }

        return Math.Clamp(defaultValue, min, max);
    }

    private static string? ParseStringArgument(JsonElement argsElement, string propertyName)
    {
        return argsElement.ValueKind == JsonValueKind.Object &&
               argsElement.TryGetProperty(propertyName, out var element) &&
               element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static bool ParseBoolArgument(JsonElement argsElement, string propertyName, bool defaultValue)
    {
        if (argsElement.ValueKind != JsonValueKind.Object ||
            !argsElement.TryGetProperty(propertyName, out var element))
        {
            return defaultValue;
        }

        if (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False)
        {
            return element.GetBoolean();
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            return number != 0;
        }

        if (element.ValueKind == JsonValueKind.String &&
            bool.TryParse(element.GetString(), out var parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static bool TryNormalizeConfigPath(string rawPath, out string normalizedPath, out string? error)
    {
        normalizedPath = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            error = "Path is required";
            return false;
        }

        var segments = rawPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            error = "Path is invalid";
            return false;
        }

        var normalizedSegments = new List<string>(segments.Length);
        foreach (var rawSegment in segments)
        {
            var segment = rawSegment.Trim();
            if (string.IsNullOrWhiteSpace(segment))
            {
                error = "Path contains empty segment";
                return false;
            }

            if (segment.Contains('/') || segment.Contains('\\') || segment.Contains(':'))
            {
                error = "Path contains invalid separators";
                return false;
            }

            var bracketStart = segment.IndexOf('[');
            if (bracketStart >= 0)
            {
                if (!segment.EndsWith("]", StringComparison.Ordinal))
                {
                    error = $"Invalid index expression in '{segment}'";
                    return false;
                }

                var propertyName = segment[..bracketStart];
                if (string.IsNullOrWhiteSpace(propertyName))
                {
                    error = $"Invalid index segment '{segment}'";
                    return false;
                }

                var indexText = segment[(bracketStart + 1)..^1];
                if (!int.TryParse(indexText, out var parsedIndex) || parsedIndex < 0)
                {
                    error = $"Invalid index in '{segment}'";
                    return false;
                }
            }

            normalizedSegments.Add(segment);
        }

        normalizedPath = string.Join('.', normalizedSegments);
        return true;
    }

    private static bool IsConfigPathAllowedForRead(string normalizedPath, out string? error)
    {
        error = null;
        var canonicalPath = CanonicalizeConfigPathForPolicy(normalizedPath);
        foreach (var blockedPrefix in ConfigReadBlockedPrefixes)
        {
            if (PathMatchesPrefix(canonicalPath, blockedPrefix))
            {
                error = $"Read blocked for sensitive config path: {blockedPrefix}";
                return false;
            }
        }

        return true;
    }

    private static bool IsConfigPathAllowedForWrite(string normalizedPath, out string? error)
    {
        error = null;
        var canonicalPath = CanonicalizeConfigPathForPolicy(normalizedPath);
        foreach (var blockedPrefix in ConfigReadBlockedPrefixes)
        {
            if (PathMatchesPrefix(canonicalPath, blockedPrefix))
            {
                error = $"Write blocked for sensitive config path: {blockedPrefix}";
                return false;
            }
        }

        if (ConfigWriteAllowedPrefixes.Any(prefix => PathMatchesPrefix(canonicalPath, prefix)))
        {
            return true;
        }

        error = "Path is not in MCP writable allowlist";
        return false;
    }

    private static string CanonicalizeConfigPathForPolicy(string normalizedPath)
    {
        var segments = normalizedPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var canonical = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            var token = segment.Trim();
            var bracketStart = token.IndexOf('[');
            if (bracketStart >= 0)
            {
                token = token[..bracketStart];
            }

            if (!string.IsNullOrWhiteSpace(token))
            {
                canonical.Add(token);
            }
        }

        return string.Join('.', canonical);
    }

    private static bool PathMatchesPrefix(string path, string prefix)
    {
        if (string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeRemoteScriptPath(string rawPath, out string normalizedPath, out string? error)
    {
        normalizedPath = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            error = "Path is empty";
            return false;
        }

        var trimmed = rawPath.Trim().Replace('\\', '/');
        if (trimmed.StartsWith("/", StringComparison.Ordinal) ||
            trimmed.StartsWith("\\", StringComparison.Ordinal) ||
            trimmed.Contains(':') ||
            Path.IsPathRooted(trimmed))
        {
            error = "Absolute path is not allowed";
            return false;
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            error = "Path is empty";
            return false;
        }

        var normalizedSegments = new List<string>(segments.Length);
        foreach (var rawSegment in segments)
        {
            var segment = rawSegment.Trim();
            if (!IsSafePathSegment(segment))
            {
                error = $"Invalid path segment '{segment}'";
                return false;
            }

            normalizedSegments.Add(segment);
        }

        normalizedPath = string.Join('/', normalizedSegments);
        return true;
    }

    private static bool IsSafePathSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment is "." or "..")
        {
            return false;
        }

        if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        if (segment.Contains('/') || segment.Contains('\\') || segment.Contains(':'))
        {
            return false;
        }

        return true;
    }

    private static string? NormalizeCultureName(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return null;
        }

        var trimmed = cultureName.Trim();
        var lowered = trimmed.ToLowerInvariant();
        switch (lowered)
        {
            case "zh":
            case "zh-cn":
            case "zh-sg":
            case "zh-hans":
            case "zh-hans-cn":
                return "zh-Hans";
            case "zh-tw":
            case "zh-hk":
            case "zh-mo":
            case "zh-hant":
                return "zh-Hant";
            case "en":
            case "en-us":
            case "en-gb":
                return "en";
        }

        try
        {
            return CultureInfo.GetCultureInfo(trimmed).Name;
        }
        catch
        {
            return trimmed;
        }
    }

    private static string NormalizeCultureForOutput(string? cultureName)
    {
        return string.IsNullOrWhiteSpace(cultureName) ? "zh-Hans" : cultureName.Trim();
    }

    private static string GetScriptGroups(JsonElement argsElement)
    {
        var query = ParseScriptQuery(argsElement);
        var groups = FilterScriptGroups(LoadScriptGroupInfos(), query);
        return JsonSerializer.Serialize(new { count = groups.Count, groups }, JsonOptions);
    }

    private async Task<string> GetScriptListAsync(JsonElement argsElement, CancellationToken ct)
    {
        var type = NormalizeScriptType(ParseScriptType(argsElement));
        var query = ParseScriptQuery(argsElement);
        var limit = ParseScriptListLimit(argsElement);

        var includeGroups = type == null || type == "group";
        var includeJs = type == null || type == "js";
        var includeKeyMouse = type == null || type == "keymouse";
        var includePathing = type == null || type == "pathing";

        var groupsTask = includeGroups
            ? Task.Run(() => FilterScriptGroups(LoadScriptGroupInfos(), query), ct)
            : Task.FromResult(new List<ScriptGroupInfo>());
        var jsTask = includeJs
            ? Task.Run(() => FilterJsScripts(LoadJsScriptInfos(), query), ct)
            : Task.FromResult(new List<JsScriptInfo>());
        var keyMouseTask = includeKeyMouse
            ? Task.Run(() => FilterKeyMouseScripts(LoadKeyMouseScriptInfos(), query), ct)
            : Task.FromResult(new List<KeyMouseScriptInfo>());
        var pathingTask = includePathing
            ? Task.Run(() => FilterPathingScripts(LoadPathingScriptInfos(), query), ct)
            : Task.FromResult(new List<PathingScriptInfo>());
        await Task.WhenAll(groupsTask, jsTask, keyMouseTask, pathingTask).ConfigureAwait(false);

        var groups = groupsTask.Result;
        var jsScripts = jsTask.Result;
        var keyMouseScripts = keyMouseTask.Result;
        var pathingScripts = pathingTask.Result;

        var counts = new
        {
            groups = groups.Count,
            js = jsScripts.Count,
            keyMouse = keyMouseScripts.Count,
            pathing = pathingScripts.Count,
            total = groups.Count + jsScripts.Count + keyMouseScripts.Count + pathingScripts.Count
        };

        var candidates = BuildScriptCandidates(groups, jsScripts, keyMouseScripts, pathingScripts);
        var matches = BuildMatchesFromCandidates(candidates, limit);
        var payload = new
        {
            type = type ?? "auto",
            query = query?.Trim(),
            limit,
            counts,
            returned = matches.Count,
            truncated = matches.Count < candidates.Count,
            matches
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private async Task<(string text, bool isError)> SearchScriptsAsync(JsonElement argsElement, CancellationToken ct)
    {
        var query = ParseScriptQuery(argsElement);
        if (string.IsNullOrWhiteSpace(query))
        {
            return ("Missing query. Provide a keyword to search scripts.", true);
        }

        query = query.Trim();
        var type = NormalizeScriptType(ParseScriptType(argsElement));
        var limit = ParseScriptSearchLimit(argsElement);

        var includeGroups = type == null || type == "group";
        var includeJs = type == null || type == "js";
        var includeKeyMouse = type == null || type == "keymouse";
        var includePathing = type == null || type == "pathing";

        var allGroupsTask = includeGroups
            ? Task.Run(LoadScriptGroupInfos, ct)
            : Task.FromResult<IReadOnlyList<ScriptGroupInfo>>(Array.Empty<ScriptGroupInfo>());
        var allJsScriptsTask = includeJs
            ? Task.Run(LoadJsScriptInfos, ct)
            : Task.FromResult<IReadOnlyList<JsScriptInfo>>(Array.Empty<JsScriptInfo>());
        var allKeyMouseScriptsTask = includeKeyMouse
            ? Task.Run(LoadKeyMouseScriptInfos, ct)
            : Task.FromResult<IReadOnlyList<KeyMouseScriptInfo>>(Array.Empty<KeyMouseScriptInfo>());
        var allPathingScriptsTask = includePathing
            ? Task.Run(LoadPathingScriptInfos, ct)
            : Task.FromResult<IReadOnlyList<PathingScriptInfo>>(Array.Empty<PathingScriptInfo>());
        await Task.WhenAll(allGroupsTask, allJsScriptsTask, allKeyMouseScriptsTask, allPathingScriptsTask).ConfigureAwait(false);

        var allGroups = allGroupsTask.Result;
        var allJsScripts = allJsScriptsTask.Result;
        var allKeyMouseScripts = allKeyMouseScriptsTask.Result;
        var allPathingScripts = allPathingScriptsTask.Result;
        ct.ThrowIfCancellationRequested();

        var keywordGroupsTask = includeGroups
            ? Task.Run(() => FilterScriptGroups(allGroups, query), ct)
            : Task.FromResult(new List<ScriptGroupInfo>());
        var keywordJsScriptsTask = includeJs
            ? Task.Run(() => FilterJsScripts(allJsScripts, query), ct)
            : Task.FromResult(new List<JsScriptInfo>());
        var keywordKeyMouseScriptsTask = includeKeyMouse
            ? Task.Run(() => FilterKeyMouseScripts(allKeyMouseScripts, query), ct)
            : Task.FromResult(new List<KeyMouseScriptInfo>());
        var keywordPathingScriptsTask = includePathing
            ? Task.Run(() => FilterPathingScripts(allPathingScripts, query), ct)
            : Task.FromResult(new List<PathingScriptInfo>());
        await Task.WhenAll(keywordGroupsTask, keywordJsScriptsTask, keywordKeyMouseScriptsTask, keywordPathingScriptsTask).ConfigureAwait(false);

        var keywordGroups = keywordGroupsTask.Result;
        var keywordJsScripts = keywordJsScriptsTask.Result;
        var keywordKeyMouseScripts = keywordKeyMouseScriptsTask.Result;
        var keywordPathingScripts = keywordPathingScriptsTask.Result;

        var counts = new
        {
            groups = keywordGroups.Count,
            js = keywordJsScripts.Count,
            keyMouse = keywordKeyMouseScripts.Count,
            pathing = keywordPathingScripts.Count,
            total = keywordGroups.Count + keywordJsScripts.Count + keywordKeyMouseScripts.Count + keywordPathingScripts.Count
        };

        var allCandidates = BuildScriptCandidates(allGroups, allJsScripts, allKeyMouseScripts, allPathingScripts);
        var keywordCandidates = BuildScriptCandidates(keywordGroups, keywordJsScripts, keywordKeyMouseScripts, keywordPathingScripts);
        var hasLocalKeywordHit = keywordCandidates.Count > 0;
        var orderedCandidates = hasLocalKeywordHit ? keywordCandidates : [];
        var mode = hasLocalKeywordHit ? "keyword" : "remote_prefetch";
        var noteParts = new List<string>();
        var vectorCandidateCount = 0;

        if (!hasLocalKeywordHit)
        {
            noteParts.Add("本地脚本未命中关键词，已优先检索远程仓库订阅脚本。");
        }

        if (hasLocalKeywordHit && _configService.Get().AiConfig.VectorSearchEnabled && allCandidates.Count > 0)
        {
            var vectorCandidates = SelectVectorCandidatePool(allCandidates, keywordCandidates, query);
            vectorCandidateCount = vectorCandidates.Count;
            if (vectorCandidates.Count > 0)
            {
                var vectorResult = await TryRankScriptCandidatesByVectorAsync(query, vectorCandidates, ct);
                if (vectorResult.rankedCandidates.Count > 0)
                {
                    orderedCandidates = vectorResult.rankedCandidates;
                    mode = vectorResult.mode;
                    if (!string.IsNullOrWhiteSpace(vectorResult.endpointLabel))
                    {
                        noteParts.Add($"向量检索来源：{vectorResult.endpointLabel}");
                    }
                    if (!string.IsNullOrWhiteSpace(vectorResult.rerankInfo))
                    {
                        noteParts.Add(vectorResult.rerankInfo);
                    }
                    if (vectorCandidates.Count < allCandidates.Count)
                    {
                        noteParts.Add($"向量检索候选池已限制为 {vectorCandidates.Count}/{allCandidates.Count} 条。");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(vectorResult.error))
                {
                    noteParts.Add($"向量检索不可用，已回退关键词匹配：{vectorResult.error}");
                }
            }
        }

        var matches = BuildMatchesFromCandidates(orderedCandidates, limit);
        object? remote = null;
        if (matches.Count == 0)
        {
            var remoteSearchResult = await SearchRemoteScriptsForSubscriptionAsync(query, type, limit, ct);
            if (remoteSearchResult.matches.Count > 0)
            {
                mode = "remote";
                noteParts.Add("本地未匹配到脚本，已返回远程仓库订阅候选（使用 subscribeUri 可一键订阅）。");
                remote = new
                {
                    source = RemoteScriptRepoIndexUrl,
                    returned = remoteSearchResult.matches.Count,
                    truncated = remoteSearchResult.truncated,
                    candidateTotal = remoteSearchResult.candidateTotal,
                    matches = remoteSearchResult.matches
                };
            }
            else if (!string.IsNullOrWhiteSpace(remoteSearchResult.error))
            {
                noteParts.Add($"远程脚本仓库检索失败：{remoteSearchResult.error}");
            }
        }

        var payload = new
        {
            query,
            type = type ?? "auto",
            limit,
            mode,
            counts,
            candidateTotal = allCandidates.Count,
            vectorCandidateCount,
            returned = matches.Count,
            truncated = matches.Count < orderedCandidates.Count,
            note = noteParts.Count == 0 ? string.Empty : string.Join(" ", noteParts),
            remote,
            matches
        };

        return (JsonSerializer.Serialize(payload, JsonOptions), false);
    }

    private async Task<(string text, bool isError)> GetScriptDetailAsync(JsonElement argsElement, CancellationToken ct)
    {
        var names = ParseScriptNames(argsElement)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var query = ParseScriptQuery(argsElement)?.Trim();
        var type = NormalizeScriptType(ParseScriptType(argsElement));
        var limit = ParseIntArgument(argsElement, "limit", 5, 1, 20);

        if (names.Count == 0 && string.IsNullOrWhiteSpace(query))
        {
            return ("Missing name(s)/query. Provide {\"name\":\"脚本名\"}、{\"names\":[...]} or {\"query\":\"关键词\"}.", true);
        }

        if (!string.IsNullOrWhiteSpace(query) && names.Count == 0)
        {
            var searchResult = await SearchScriptsAsync(argsElement, ct);
            if (searchResult.isError)
            {
                return searchResult;
            }

            var details = BuildScriptDetailsFromSearchPayload(searchResult.text, limit);
            var payload = new
            {
                query,
                type = type ?? "auto",
                returned = details.Count,
                details
            };
            return (JsonSerializer.Serialize(payload, JsonOptions), false);
        }

        var detailsByName = new List<object>();
        var unmatched = new List<object>();
        var includeGroups = type == null || type == "group";
        var includeJs = type == null || type == "js";
        var includeKeyMouse = type == null || type == "keymouse";
        var includePathing = type == null || type == "pathing";
        var localGroupsTask = includeGroups
            ? Task.Run(LoadScriptGroupInfos, ct)
            : Task.FromResult<IReadOnlyList<ScriptGroupInfo>>(Array.Empty<ScriptGroupInfo>());
        var localJsTask = includeJs
            ? Task.Run(LoadJsScriptInfos, ct)
            : Task.FromResult<IReadOnlyList<JsScriptInfo>>(Array.Empty<JsScriptInfo>());
        var localKeyMouseTask = includeKeyMouse
            ? Task.Run(LoadKeyMouseScriptInfos, ct)
            : Task.FromResult<IReadOnlyList<KeyMouseScriptInfo>>(Array.Empty<KeyMouseScriptInfo>());
        var localPathingTask = includePathing
            ? Task.Run(LoadPathingScriptInfos, ct)
            : Task.FromResult<IReadOnlyList<PathingScriptInfo>>(Array.Empty<PathingScriptInfo>());
        await Task.WhenAll(localGroupsTask, localJsTask, localKeyMouseTask, localPathingTask).ConfigureAwait(false);

        var localGroups = localGroupsTask.Result;
        var localJsScripts = localJsTask.Result;
        var localKeyMouseScripts = localKeyMouseTask.Result;
        var localPathingScripts = localPathingTask.Result;
        foreach (var requestedName in names)
        {
            if (detailsByName.Count >= limit)
            {
                break;
            }

            if (TryBuildLocalScriptDetailByName(
                    requestedName,
                    type,
                    localGroups,
                    localJsScripts,
                    localKeyMouseScripts,
                    localPathingScripts,
                    out var localDetail))
            {
                detailsByName.Add(localDetail);
                continue;
            }

            var remoteDetail = await TryBuildRemoteScriptDetailByNameAsync(requestedName, type, ct);
            if (remoteDetail != null)
            {
                detailsByName.Add(remoteDetail);
                continue;
            }

            unmatched.Add(new
            {
                name = requestedName,
                suggestion = BuildScriptSuggestion(requestedName)
            });
        }

        var output = new
        {
            requested = names,
            type = type ?? "auto",
            returned = detailsByName.Count,
            unmatched,
            details = detailsByName
        };
        return (JsonSerializer.Serialize(output, JsonOptions), false);
    }

    private static List<object> BuildScriptDetailsFromSearchPayload(string payloadText, int limit)
    {
        var details = new List<object>();
        if (string.IsNullOrWhiteSpace(payloadText))
        {
            return details;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(payloadText);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return details;
            }

            if (doc.RootElement.TryGetProperty("matches", out var matchesElement) &&
                matchesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var match in matchesElement.EnumerateArray())
                {
                    if (!TryBuildScriptDetailFromMatchElement(match, out var detail, out var key))
                    {
                        continue;
                    }

                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    details.Add(detail!);
                    if (details.Count >= limit)
                    {
                        return details;
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("remote", out var remoteElement) &&
                remoteElement.ValueKind == JsonValueKind.Object &&
                remoteElement.TryGetProperty("matches", out var remoteMatches) &&
                remoteMatches.ValueKind == JsonValueKind.Array)
            {
                foreach (var match in remoteMatches.EnumerateArray())
                {
                    if (!TryBuildScriptDetailFromMatchElement(match, out var detail, out var key))
                    {
                        continue;
                    }

                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    details.Add(detail!);
                    if (details.Count >= limit)
                    {
                        return details;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return details;
        }

        return details;
    }

    private static bool TryBuildScriptDetailFromMatchElement(JsonElement element, out object? detail, out string key)
    {
        detail = null;
        key = string.Empty;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var type = TryGetJsonString(element, "type");
        var name = TryGetJsonString(element, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var folder = TryGetJsonString(element, "folder");
        var description = NormalizeReadmeSummary(TryGetJsonString(element, "description"));
        var subscribeUri = TryGetJsonString(element, "subscribeUri");
        var path = TryGetJsonString(element, "path");
        var normalizedType = NormalizeScriptType(type) ?? type?.Trim().ToLowerInvariant() ?? "auto";
        key = $"{normalizedType}|{name}|{folder}|{path}";

        if (string.Equals(normalizedType, "remote", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(subscribeUri))
        {
            detail = new
            {
                source = "remote",
                type = "pathing",
                name,
                folder,
                path,
                description,
                action = new
                {
                    tool = "bgi.script.subscribe",
                    arguments = new { names = new[] { name } },
                    subscribeUri
                }
            };
            return true;
        }

        var typeHint = string.IsNullOrWhiteSpace(normalizedType) || normalizedType == "auto"
            ? "pathing"
            : normalizedType;
        detail = new
        {
            source = "local",
            type = typeHint,
            name,
            folder,
            description,
            action = new
            {
                tool = "bgi.script.run",
                arguments = new { name, type = typeHint }
            }
        };
        return true;
    }

    private static bool TryBuildLocalScriptDetailByName(
        string requestedName,
        string? typeHint,
        IReadOnlyList<ScriptGroupInfo> groups,
        IReadOnlyList<JsScriptInfo> jsScripts,
        IReadOnlyList<KeyMouseScriptInfo> keyMouseScripts,
        IReadOnlyList<PathingScriptInfo> pathingScripts,
        out object detail)
    {
        detail = null!;
        var type = NormalizeScriptType(typeHint);

        if (type == null || type == "group")
        {
            var group = groups.FirstOrDefault(item =>
                IsNameMatch(requestedName, item.Name) ||
                item.Name.Contains(requestedName, StringComparison.OrdinalIgnoreCase));
            if (group != null)
            {
                detail = new
                {
                    source = "local",
                    type = "group",
                    name = group.Name,
                    description = $"脚本配置组，共 {group.ProjectCount} 个项目。",
                    action = new
                    {
                        tool = "bgi.script.run",
                        arguments = new { name = group.Name, type = "group" }
                    }
                };
                return true;
            }
        }

        if (type == null || type == "js")
        {
            var js = jsScripts.FirstOrDefault(item =>
                IsNameMatch(requestedName, item.Name) ||
                IsNameMatch(requestedName, item.Folder) ||
                item.Name.Contains(requestedName, StringComparison.OrdinalIgnoreCase) ||
                item.Folder.Contains(requestedName, StringComparison.OrdinalIgnoreCase));
            if (js != null)
            {
                detail = new
                {
                    source = "local",
                    type = "js",
                    name = js.Name,
                    folder = js.Folder,
                    description = NormalizeReadmeSummary(js.Description),
                    action = new
                    {
                        tool = "bgi.script.run",
                        arguments = new { name = js.Name, type = "js" }
                    }
                };
                return true;
            }
        }

        if (type == null || type == "keymouse")
        {
            var keymouse = keyMouseScripts.FirstOrDefault(item =>
                IsNameMatch(requestedName, item.Name) ||
                item.Name.Contains(requestedName, StringComparison.OrdinalIgnoreCase));
            if (keymouse != null)
            {
                detail = new
                {
                    source = "local",
                    type = "keymouse",
                    name = keymouse.Name,
                    description = "键鼠录制脚本，用于回放固定操作序列。",
                    action = new
                    {
                        tool = "bgi.script.run",
                        arguments = new { name = keymouse.Name, type = "keymouse" }
                    }
                };
                return true;
            }
        }

        if (type == null || type == "pathing")
        {
            var pathing = pathingScripts.FirstOrDefault(item =>
                IsNameMatch(requestedName, item.Name) ||
                item.Name.Contains(requestedName, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(item.Folder) &&
                 item.Folder.Contains(requestedName, StringComparison.OrdinalIgnoreCase)));
            if (pathing != null)
            {
                detail = new
                {
                    source = "local",
                    type = "pathing",
                    name = pathing.Name,
                    folder = pathing.Folder,
                    description = NormalizeReadmeSummary(pathing.Description),
                    action = new
                    {
                        tool = "bgi.script.run",
                        arguments = new { name = pathing.Name, type = "pathing" }
                    }
                };
                return true;
            }
        }

        return false;
    }

    private async Task<object?> TryBuildRemoteScriptDetailByNameAsync(string requestedName, string? typeHint, CancellationToken ct)
    {
        var (entries, _) = await LoadRemoteScriptRepoEntriesAsync(ct);
        if (entries.Count == 0)
        {
            return null;
        }

        var normalizedType = NormalizeScriptType(typeHint);
        var filtered = entries
            .Where(entry => IsRemoteScriptTypeMatch(entry.Root, normalizedType))
            .ToList();
        if (filtered.Count == 0)
        {
            filtered = entries.ToList();
        }

        if (!TryFindBestRemoteEntryByName(filtered, requestedName, out var entry))
        {
            return null;
        }

        return new
        {
            source = "remote",
            type = entry.Root,
            name = entry.Name,
            folder = entry.Folder,
            path = entry.Path,
            description = ResolveRemoteScriptDescription(entry),
            action = new
            {
                tool = "bgi.script.subscribe",
                arguments = new { names = new[] { entry.Name } },
                subscribeUri = BuildScriptSubscribeUri(entry.Path)
            }
        };
    }

    private static string? TryGetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString()?.Trim();
    }

    private async Task<(List<object> matches, int candidateTotal, bool truncated, string? error)> SearchRemoteScriptsForSubscriptionAsync(
        string query,
        string? type,
        int limit,
        CancellationToken ct)
    {
        var (entries, loadError) = await LoadRemoteScriptRepoEntriesAsync(ct);
        if (entries.Count == 0)
        {
            return ([], 0, false, loadError ?? "远程仓库没有可用脚本索引");
        }

        var filteredEntries = entries
            .Where(entry => IsRemoteScriptTypeMatch(entry.Root, type))
            .ToList();
        if (filteredEntries.Count == 0)
        {
            return ([], 0, false, loadError);
        }

        var normalizedQuery = NormalizeQuery(query);
        var queryTokens = ExtractQueryTokens(query)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        List<(RemoteScriptRepoEntry entry, double score)> scored;
        if (filteredEntries.Count >= ParallelSearchThreshold)
        {
            var scoredBag = new ConcurrentBag<(RemoteScriptRepoEntry entry, double score)>();
            var options = new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = SearchMaxDegreeOfParallelism
            };
            Parallel.ForEach(filteredEntries, options, entry =>
            {
                var score = ComputeRemoteScriptScore(normalizedQuery, queryTokens, entry);
                if (score > 0d)
                {
                    scoredBag.Add((entry, score));
                }
            });
            scored = scoredBag.ToList();
        }
        else
        {
            scored = new List<(RemoteScriptRepoEntry entry, double score)>(filteredEntries.Count);
            foreach (var entry in filteredEntries)
            {
                ct.ThrowIfCancellationRequested();
                var score = ComputeRemoteScriptScore(normalizedQuery, queryTokens, entry);
                if (score <= 0d)
                {
                    continue;
                }

                scored.Add((entry, score));
            }
        }

        if (scored.Count == 0)
        {
            return ([], 0, false, loadError);
        }

        var ordered = scored
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.entry.Path, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.entry)
            .ToList();

        var size = Math.Min(limit, ordered.Count);
        var matches = new List<object>(size);
        for (var i = 0; i < ordered.Count && matches.Count < limit; i++)
        {
            var entry = ordered[i];
            var description = ResolveRemoteScriptDescription(entry);
            matches.Add(new
            {
                type = "remote",
                name = entry.Name,
                folder = entry.Folder,
                path = entry.Path,
                subscribeUri = BuildScriptSubscribeUri(entry.Path),
                description
            });
        }

        return (matches, ordered.Count, matches.Count < ordered.Count, loadError);
    }

    private async Task<(IReadOnlyList<RemoteScriptRepoEntry> entries, string? error)> LoadRemoteScriptRepoEntriesAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_remoteScriptRepoEntriesCache.Count > 0 &&
            now - _remoteScriptRepoEntriesUpdatedUtc < RemoteScriptRepoCacheTtl)
        {
            return (_remoteScriptRepoEntriesCache, null);
        }

        await RemoteScriptRepoCacheSemaphore.WaitAsync(ct);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_remoteScriptRepoEntriesCache.Count > 0 &&
                now - _remoteScriptRepoEntriesUpdatedUtc < RemoteScriptRepoCacheTtl)
            {
                return (_remoteScriptRepoEntriesCache, null);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, RemoteScriptRepoIndexUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await RemoteScriptRepoHttpClient.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = body.Length > 240 ? $"{body[..240]}..." : body;
                return (_remoteScriptRepoEntriesCache, $"{(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
            }

            var parsed = ParseRemoteScriptRepoEntries(body);
            if (parsed.Count == 0)
            {
                return (_remoteScriptRepoEntriesCache, "repo.json 中未解析到可订阅脚本路径");
            }

            _remoteScriptRepoEntriesCache = parsed;
            _remoteScriptRepoEntriesUpdatedUtc = now;
            return (_remoteScriptRepoEntriesCache, null);
        }
        catch (Exception ex)
        {
            return (_remoteScriptRepoEntriesCache, ex.Message);
        }
        finally
        {
            RemoteScriptRepoCacheSemaphore.Release();
        }
    }

    private static List<RemoteScriptRepoEntry> ParseRemoteScriptRepoEntries(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("indexes", out var indexes) ||
            indexes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<RemoteScriptRepoEntry>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectRemoteScriptRepoEntries(indexes, string.Empty, result, seenPaths);
        return result;
    }

    private static void CollectRemoteScriptRepoEntries(
        JsonElement nodes,
        string currentPath,
        List<RemoteScriptRepoEntry> result,
        HashSet<string> seenPaths)
    {
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!node.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = nameElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var candidatePath = string.IsNullOrWhiteSpace(currentPath) ? name : $"{currentPath}/{name}";
            if (!TryNormalizeRemoteScriptPath(candidatePath, out var path, out _))
            {
                continue;
            }

            if (node.TryGetProperty("children", out var children) &&
                children.ValueKind == JsonValueKind.Array &&
                children.GetArrayLength() > 0)
            {
                CollectRemoteScriptRepoEntries(children, path, result, seenPaths);
            }

            var slashIndex = path.IndexOf('/');
            if (slashIndex <= 0)
            {
                continue;
            }

            var root = path[..slashIndex].Trim().ToLowerInvariant();
            if (!IsRemoteSupportedRoot(root))
            {
                continue;
            }

            if (!seenPaths.Add(path))
            {
                continue;
            }

            var lastSlash = path.LastIndexOf('/');
            var folder = lastSlash > 0 ? path[..lastSlash] : null;
            var leafName = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
            var title = TryGetRemoteString(node, "title");
            var description = TryGetRemoteString(node, "description");
            var searchText = NormalizeSearchText(string.Join(" ", new[] { path, title, description }.Where(v => !string.IsNullOrWhiteSpace(v))), MaxScriptSearchTextLength);

            result.Add(new RemoteScriptRepoEntry
            {
                Path = path,
                Root = root,
                Name = leafName,
                Folder = folder,
                Title = title,
                Description = description,
                SearchText = searchText
            });
        }
    }

    private static string? TryGetRemoteString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool IsRemoteSupportedRoot(string root)
    {
        return root is "pathing" or "js" or "combat" or "tcg";
    }

    private static bool IsRemoteScriptTypeMatch(string root, string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return IsRemoteSupportedRoot(root);
        }

        return type switch
        {
            "pathing" => string.Equals(root, "pathing", StringComparison.OrdinalIgnoreCase),
            "js" => root is "js" or "combat" or "tcg",
            _ => false
        };
    }

    private static double ComputeRemoteScriptScore(
        string normalizedQuery,
        IReadOnlyList<string> queryTokens,
        RemoteScriptRepoEntry entry)
    {
        var searchText = entry.SearchText;
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return 0d;
        }

        var compactSearchText = searchText.Replace(" ", string.Empty, StringComparison.Ordinal);
        var score = 0d;

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            if (compactSearchText.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            {
                score += 10d;
            }
            else if (entry.Path.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            {
                score += 6d;
            }
        }

        foreach (var token in queryTokens)
        {
            if (ContainsKeywordAny(token, entry.Name, entry.Folder, entry.SearchText))
            {
                score += 3d;
            }
            else if (compactSearchText.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 1d;
            }
        }

        if (entry.Root == "pathing")
        {
            score += 0.2d;
        }

        return score;
    }

    private static bool TryFindBestRemoteEntryByName(IReadOnlyList<RemoteScriptRepoEntry> entries, string requestedName, out RemoteScriptRepoEntry entry)
    {
        entry = null!;
        if (entries.Count == 0 || string.IsNullOrWhiteSpace(requestedName))
        {
            return false;
        }

        var trimmed = requestedName.Trim();
        var exact = entries.FirstOrDefault(item => string.Equals(item.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            entry = exact;
            return true;
        }

        var normalized = NormalizeQuery(trimmed);
        var tokens = ExtractQueryTokens(trimmed)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var bestScore = double.MinValue;
        RemoteScriptRepoEntry? best = null;
        foreach (var candidate in entries)
        {
            var baseScore = ComputeRemoteScriptScore(normalized, tokens, candidate);
            if (baseScore <= 0d)
            {
                continue;
            }

            var score = baseScore;
            if (candidate.Root == "pathing")
            {
                score += 0.2d;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        if (best == null)
        {
            return false;
        }

        entry = best;
        return true;
    }

    private static string BuildScriptSubscribeUri(string scriptPath)
    {
        var payloadJson = JsonSerializer.Serialize(new[] { scriptPath }, JsonOptions);
        var encodedJson = WebUtility.UrlEncode(payloadJson);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(encodedJson));
        return $"bettergi://script?import={WebUtility.UrlEncode(base64)}";
    }

    private static List<ScriptSearchCandidate> BuildScriptCandidates(
        IReadOnlyList<ScriptGroupInfo> groups,
        IReadOnlyList<JsScriptInfo> jsScripts,
        IReadOnlyList<KeyMouseScriptInfo> keyMouseScripts,
        IReadOnlyList<PathingScriptInfo> pathingScripts)
    {
        var totalCount = groups.Count + jsScripts.Count + keyMouseScripts.Count + pathingScripts.Count;
        if (totalCount == 0)
        {
            return [];
        }

        if (totalCount < ParallelSearchThreshold)
        {
            var list = new List<ScriptSearchCandidate>(totalCount);
            AddScriptCandidates(list, groups, "group", static item => item.Name, static _ => null, static _ => null, static _ => null);
            AddScriptCandidates(list, jsScripts, "js", static item => item.Name, static item => item.Folder, static item => item.Description, static item => item.SearchText);
            AddScriptCandidates(list, keyMouseScripts, "keymouse", static item => item.Name, static _ => null, static _ => null, static _ => null);
            AddScriptCandidates(list, pathingScripts, "pathing", static item => item.Name, static item => item.Folder, static item => item.Description, static item => item.SearchText);
            return list;
        }

        var groupTask = Task.Run(() => BuildScriptCandidatesChunk(groups, "group", static item => item.Name, static _ => null, static _ => null, static _ => null));
        var jsTask = Task.Run(() => BuildScriptCandidatesChunk(jsScripts, "js", static item => item.Name, static item => item.Folder, static item => item.Description, static item => item.SearchText));
        var keyMouseTask = Task.Run(() => BuildScriptCandidatesChunk(keyMouseScripts, "keymouse", static item => item.Name, static _ => null, static _ => null, static _ => null));
        var pathingTask = Task.Run(() => BuildScriptCandidatesChunk(pathingScripts, "pathing", static item => item.Name, static item => item.Folder, static item => item.Description, static item => item.SearchText));
        Task.WaitAll(groupTask, jsTask, keyMouseTask, pathingTask);

        var merged = new List<ScriptSearchCandidate>(totalCount);
        merged.AddRange(groupTask.Result);
        merged.AddRange(jsTask.Result);
        merged.AddRange(keyMouseTask.Result);
        merged.AddRange(pathingTask.Result);
        return merged;
    }

    private static List<ScriptSearchCandidate> BuildScriptCandidatesChunk<T>(
        IReadOnlyList<T> items,
        string type,
        Func<T, string?> nameSelector,
        Func<T, string?> folderSelector,
        Func<T, string?> descriptionSelector,
        Func<T, string?> searchTextSelector)
    {
        var chunk = new List<ScriptSearchCandidate>(items.Count);
        AddScriptCandidates(chunk, items, type, nameSelector, folderSelector, descriptionSelector, searchTextSelector);
        return chunk;
    }

    private static void AddScriptCandidates<T>(
        List<ScriptSearchCandidate> target,
        IReadOnlyList<T> items,
        string type,
        Func<T, string?> nameSelector,
        Func<T, string?> folderSelector,
        Func<T, string?> descriptionSelector,
        Func<T, string?> searchTextSelector)
    {
        foreach (var item in items)
        {
            var name = nameSelector(item);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            target.Add(new ScriptSearchCandidate
            {
                Type = type,
                Name = name.Trim(),
                Folder = folderSelector(item)?.Trim(),
                Description = NormalizeReadmeSummary(descriptionSelector(item)),
                SearchText = NormalizeSearchText(searchTextSelector(item), MaxScriptSearchTextLength)
            });
        }
    }

    private static List<object> BuildMatchesFromCandidates(IReadOnlyList<ScriptSearchCandidate> candidates, int limit)
    {
        var size = Math.Min(limit, candidates.Count);
        var matches = new List<object>(size);
        for (var i = 0; i < candidates.Count && matches.Count < limit; i++)
        {
            var candidate = candidates[i];
            matches.Add(new
            {
                type = candidate.Type,
                name = candidate.Name,
                folder = string.IsNullOrWhiteSpace(candidate.Folder) ? null : candidate.Folder,
                description = string.IsNullOrWhiteSpace(candidate.Description) ? null : candidate.Description
            });
        }

        return matches;
    }

    private static List<ScriptSearchCandidate> SelectVectorCandidatePool(
        IReadOnlyList<ScriptSearchCandidate> allCandidates,
        IReadOnlyList<ScriptSearchCandidate> keywordCandidates,
        string query)
    {
        var targetCount = Math.Min(MaxVectorCandidateCount, allCandidates.Count);
        if (targetCount <= 0)
        {
            return [];
        }

        var result = new List<ScriptSearchCandidate>(targetCount);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AppendCandidates(IEnumerable<ScriptSearchCandidate> source)
        {
            foreach (var candidate in EnumerateCandidatesByTypeBalance(source))
            {
                if (result.Count >= targetCount)
                {
                    break;
                }

                var key = BuildScriptCandidateKey(candidate);
                if (!seen.Add(key))
                {
                    continue;
                }

                result.Add(candidate);
            }
        }

        AppendCandidates(keywordCandidates);
        if (result.Count > 0)
        {
            return result;
        }

        if (result.Count < targetCount)
        {
            var normalized = NormalizeQuery(query);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                AppendCandidates(allCandidates.Where(candidate =>
                    ContainsKeyword(candidate.Name, normalized) ||
                    ContainsKeyword(candidate.Folder, normalized) ||
                    ContainsKeyword(candidate.SearchText, normalized)));
            }
        }

        if (result.Count < targetCount)
        {
            AppendCandidates(allCandidates);
        }

        return result;
    }

    private static IEnumerable<ScriptSearchCandidate> EnumerateCandidatesByTypeBalance(IEnumerable<ScriptSearchCandidate> source)
    {
        var grouped = source
            .GroupBy(candidate => string.IsNullOrWhiteSpace(candidate.Type) ? "unknown" : candidate.Type.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => new Queue<ScriptSearchCandidate>(group), StringComparer.OrdinalIgnoreCase);
        if (grouped.Count == 0)
        {
            yield break;
        }

        var typeOrder = BuildBalancedTypeOrder(grouped.Keys);
        while (grouped.Count > 0)
        {
            var emitted = false;
            foreach (var type in typeOrder)
            {
                if (!grouped.TryGetValue(type, out var queue) || queue.Count == 0)
                {
                    continue;
                }

                emitted = true;
                yield return queue.Dequeue();
                if (queue.Count == 0)
                {
                    grouped.Remove(type);
                }
            }

            if (!emitted)
            {
                yield break;
            }
        }
    }

    private static List<string> BuildBalancedTypeOrder(IEnumerable<string> availableTypes)
    {
        var order = new List<string>();
        var distinctTypes = availableTypes
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(type => type.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var preferred in PreferredScriptTypeOrder)
        {
            if (distinctTypes.Contains(preferred, StringComparer.OrdinalIgnoreCase))
            {
                order.Add(preferred);
            }
        }

        foreach (var type in distinctTypes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!order.Contains(type, StringComparer.OrdinalIgnoreCase))
            {
                order.Add(type);
            }
        }

        return order;
    }

    private static string BuildScriptCandidateKey(ScriptSearchCandidate candidate)
    {
        return $"{candidate.Type}|{candidate.Name}|{candidate.Folder}";
    }

    private async Task<(List<ScriptSearchCandidate> rankedCandidates, string? error, string? endpointLabel, string mode, string? rerankInfo)> TryRankScriptCandidatesByVectorAsync(
        string query,
        IReadOnlyList<ScriptSearchCandidate> candidates,
        CancellationToken ct)
    {
        if (candidates.Count == 0)
        {
            return ([], "候选为空", null, "keyword", null);
        }

        try
        {
            var aiConfig = _configService.Get().AiConfig;
            var embeddingModel = string.IsNullOrWhiteSpace(aiConfig.VectorModel) ? "BAAI/bge-m3" : aiConfig.VectorModel.Trim();
            var rerankEnabled = aiConfig.VectorRerankEnabled;
            var rerankModel = string.IsNullOrWhiteSpace(aiConfig.VectorRerankModel) ? "BAAI/bge-reranker-v2-m3" : aiConfig.VectorRerankModel.Trim();
            var endpoints = BuildVectorEndpointOptions(aiConfig);
            if (endpoints.Count == 0)
            {
                return ([], "缺少向量 API Key", null, "keyword", null);
            }

            var errors = new List<string>();
            foreach (var endpoint in endpoints)
            {
                var attempt = await RankCandidatesByEmbeddingEndpointAsync(
                    query,
                    candidates,
                    endpoint,
                    embeddingModel,
                    ct);
                if (attempt.rankedCandidates.Count > 0)
                {
                    var ranked = attempt.rankedCandidates;
                    var mode = "vector";
                    string? rerankInfo = null;

                    if (rerankEnabled)
                    {
                        var rerankAttempt = await TryRerankCandidatesByEndpointAsync(
                            query,
                            ranked,
                            endpoint,
                            rerankModel,
                            ct);
                        if (rerankAttempt.rankedCandidates.Count > 0)
                        {
                            ranked = rerankAttempt.rankedCandidates;
                            mode = "vector_rerank";
                            rerankInfo = $"重排模型：{rerankModel}";
                        }
                        else if (!string.IsNullOrWhiteSpace(rerankAttempt.error))
                        {
                            rerankInfo = $"重排不可用，已回退向量排序：{rerankAttempt.error}";
                        }
                    }

                    return (ranked, null, endpoint.Label, mode, rerankInfo);
                }

                if (!string.IsNullOrWhiteSpace(attempt.error))
                {
                    errors.Add($"{endpoint.Label}: {attempt.error}");
                }
            }

            var error = errors.Count == 0
                ? "向量接口不可用"
                : string.Join(" | ", errors.Take(3));
            return ([], error, null, "keyword", null);
        }
        catch (OperationCanceledException)
        {
            return ([], "向量请求超时", null, "keyword", null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[MCP {Tag}] vector search failed", InstanceTag);
            return ([], ex.Message, null, "keyword", null);
        }
    }

    private async Task<(List<ScriptSearchCandidate> rankedCandidates, string? error)> RankCandidatesByEmbeddingEndpointAsync(
        string query,
        IReadOnlyList<ScriptSearchCandidate> candidates,
        VectorEndpointOption endpoint,
        string model,
        CancellationToken ct)
    {
        var queryEmbeddingResult = await RequestEmbeddingsAsync(
            endpoint.EmbeddingsEndpoint,
            endpoint.ApiKey,
            model,
            [query.Trim()],
            ct);
        if (!string.IsNullOrWhiteSpace(queryEmbeddingResult.error))
        {
            return ([], queryEmbeddingResult.error);
        }

        if (queryEmbeddingResult.embeddings.Count == 0)
        {
            return ([], "向量接口未返回 query embedding");
        }

        var queryEmbedding = queryEmbeddingResult.embeddings[0];
        var scored = new List<(ScriptSearchCandidate candidate, double score)>(candidates.Count);
        for (var start = 0; start < candidates.Count; start += VectorEmbeddingBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(VectorEmbeddingBatchSize, candidates.Count - start);
            var batchCandidates = new List<ScriptSearchCandidate>(count);
            var batchInputs = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var candidate = candidates[start + i];
                batchCandidates.Add(candidate);
                batchInputs.Add(BuildScriptEmbeddingInput(candidate));
            }

            var batchEmbeddingResult = await RequestEmbeddingsAsync(
                endpoint.EmbeddingsEndpoint,
                endpoint.ApiKey,
                model,
                batchInputs,
                ct);
            if (!string.IsNullOrWhiteSpace(batchEmbeddingResult.error))
            {
                return ([], batchEmbeddingResult.error);
            }

            if (batchEmbeddingResult.embeddings.Count != batchCandidates.Count)
            {
                return ([], "向量接口返回数量与请求不一致");
            }

            for (var i = 0; i < batchCandidates.Count; i++)
            {
                var score = CosineSimilarity(queryEmbedding, batchEmbeddingResult.embeddings[i]) +
                            ComputeScriptKeywordBoost(query, batchCandidates[i]);
                scored.Add((batchCandidates[i], score));
            }
        }

        var ranked = scored
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.candidate)
            .ToList();

        return (ranked, null);
    }

    private async Task<(List<ScriptSearchCandidate> rankedCandidates, string? error)> TryRerankCandidatesByEndpointAsync(
        string query,
        IReadOnlyList<ScriptSearchCandidate> candidates,
        VectorEndpointOption endpoint,
        string model,
        CancellationToken ct)
    {
        if (candidates.Count == 0)
        {
            return ([], "候选为空");
        }

        var rerankCount = Math.Min(MaxRerankCandidateCount, candidates.Count);
        var topCandidates = candidates.Take(rerankCount).ToList();
        var documents = topCandidates
            .Select(BuildScriptRerankInput)
            .ToList();
        var rerankResult = await RequestRerankAsync(
            endpoint.RerankEndpoint,
            endpoint.ApiKey,
            model,
            query,
            documents,
            rerankCount,
            ct);
        if (!string.IsNullOrWhiteSpace(rerankResult.error))
        {
            return ([], rerankResult.error);
        }

        if (rerankResult.items.Count == 0)
        {
            return ([], "重排接口未返回结果");
        }

        var seenIndices = new HashSet<int>();
        var orderedIndices = rerankResult.items
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.index)
            .Select(item => item.index)
            .Where(index => index >= 0 && index < topCandidates.Count && seenIndices.Add(index))
            .ToList();
        if (orderedIndices.Count == 0)
        {
            return ([], "重排接口返回的索引无效");
        }

        var reranked = new List<ScriptSearchCandidate>(candidates.Count);
        foreach (var index in orderedIndices)
        {
            reranked.Add(topCandidates[index]);
        }

        for (var index = 0; index < topCandidates.Count; index++)
        {
            if (seenIndices.Add(index))
            {
                reranked.Add(topCandidates[index]);
            }
        }

        if (rerankCount < candidates.Count)
        {
            reranked.AddRange(candidates.Skip(rerankCount));
        }

        return (reranked, null);
    }

    private async Task<(List<(int index, double score)> items, string? error)> RequestRerankAsync(
        string endpoint,
        string apiKey,
        string model,
        string query,
        IReadOnlyList<string> documents,
        int topN,
        CancellationToken ct)
    {
        if (documents.Count == 0)
        {
            return ([], null);
        }

        var payload = new
        {
            model,
            query = query.Trim(),
            documents,
            top_n = Math.Clamp(topN, 1, documents.Count)
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await VectorEmbeddingHttpClient.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = body.Length > 240 ? $"{body[..240]}..." : body;
            return ([], $"{(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var errorElement))
        {
            var message = errorElement.TryGetProperty("message", out var messageElement) &&
                          messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : errorElement.GetRawText();
            return ([], string.IsNullOrWhiteSpace(message) ? "重排接口返回错误" : message);
        }

        JsonElement itemsElement;
        if (doc.RootElement.TryGetProperty("results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array)
        {
            itemsElement = resultsElement;
        }
        else if (doc.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
        {
            itemsElement = dataElement;
        }
        else
        {
            return ([], "重排接口缺少 results/data 字段");
        }

        var items = new List<(int index, double score)>(itemsElement.GetArrayLength());
        foreach (var item in itemsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !TryGetRerankIndex(item, out var index))
            {
                continue;
            }

            var score = TryGetRerankScore(item, out var parsedScore) ? parsedScore : 0d;
            items.Add((index, score));
        }

        if (items.Count == 0)
        {
            return ([], "重排接口返回为空");
        }

        return (items, null);
    }

    private static bool TryGetRerankIndex(JsonElement element, out int index)
    {
        index = -1;
        if (element.TryGetProperty("index", out var indexElement) &&
            indexElement.ValueKind == JsonValueKind.Number &&
            indexElement.TryGetInt32(out index))
        {
            return true;
        }

        if (element.TryGetProperty("document_index", out var documentIndexElement) &&
            documentIndexElement.ValueKind == JsonValueKind.Number &&
            documentIndexElement.TryGetInt32(out index))
        {
            return true;
        }

        if (element.TryGetProperty("doc_index", out var docIndexElement) &&
            docIndexElement.ValueKind == JsonValueKind.Number &&
            docIndexElement.TryGetInt32(out index))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetRerankScore(JsonElement element, out double score)
    {
        score = 0d;
        if (element.TryGetProperty("relevance_score", out var relevanceElement) &&
            relevanceElement.ValueKind == JsonValueKind.Number &&
            relevanceElement.TryGetDouble(out score))
        {
            return true;
        }

        if (element.TryGetProperty("score", out var scoreElement) &&
            scoreElement.ValueKind == JsonValueKind.Number &&
            scoreElement.TryGetDouble(out score))
        {
            return true;
        }

        return false;
    }

    private static List<VectorEndpointOption> BuildVectorEndpointOptions(AiConfig aiConfig)
    {
        var options = new List<VectorEndpointOption>(2);
        var sharedApiKey = aiConfig.ApiKey;
        var vectorApiKey = string.IsNullOrWhiteSpace(aiConfig.VectorApiKey) ? aiConfig.ApiKey : aiConfig.VectorApiKey;

        if (aiConfig.VectorUseSameApiSite)
        {
            AddVectorEndpointOption(options, "同站点 API", aiConfig.BaseUrl, "https://api.openai.com/v1", sharedApiKey);
            AddVectorEndpointOption(options, "向量专用 API", aiConfig.VectorBaseUrl, "https://api.siliconflow.cn/v1", vectorApiKey);
        }
        else
        {
            AddVectorEndpointOption(options, "向量专用 API", aiConfig.VectorBaseUrl, "https://api.siliconflow.cn/v1", vectorApiKey);
            AddVectorEndpointOption(options, "同站点 API", aiConfig.BaseUrl, "https://api.openai.com/v1", sharedApiKey);
        }

        return options;
    }

    private static void AddVectorEndpointOption(
        List<VectorEndpointOption> options,
        string label,
        string? baseUrl,
        string fallbackBaseUrl,
        string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        var normalizedApiKey = apiKey.Trim();
        var baseEndpoint = NormalizeOpenAiBaseUrl(baseUrl, fallbackBaseUrl);
        if (options.Any(item => string.Equals(item.BaseEndpoint, baseEndpoint, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(item.ApiKey, normalizedApiKey, StringComparison.Ordinal)))
        {
            return;
        }

        var displayLabel = label;
        if (Uri.TryCreate(baseEndpoint, UriKind.Absolute, out var endpointUri) && !string.IsNullOrWhiteSpace(endpointUri.Host))
        {
            displayLabel = $"{label}({endpointUri.Host})";
        }

        options.Add(new VectorEndpointOption
        {
            Label = displayLabel,
            BaseEndpoint = baseEndpoint,
            ApiKey = normalizedApiKey
        });
    }

    private async Task<(List<double[]> embeddings, string? error)> RequestEmbeddingsAsync(
        string endpoint,
        string apiKey,
        string model,
        IReadOnlyList<string> inputs,
        CancellationToken ct)
    {
        if (inputs.Count == 0)
        {
            return ([], null);
        }

        var payload = new
        {
            model,
            input = inputs
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await VectorEmbeddingHttpClient.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = body.Length > 240 ? $"{body[..240]}..." : body;
            return ([], $"{(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var errorElement))
        {
            var message = errorElement.TryGetProperty("message", out var messageElement) &&
                          messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : errorElement.GetRawText();
            return ([], string.IsNullOrWhiteSpace(message) ? "向量接口返回错误" : message);
        }

        if (!doc.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
        {
            return ([], "向量接口缺少 data 字段");
        }

        var vectors = new double[inputs.Count][];
        var fallbackIndex = 0;
        foreach (var item in dataElement.EnumerateArray())
        {
            var index = fallbackIndex;
            if (item.TryGetProperty("index", out var indexElement) &&
                indexElement.ValueKind == JsonValueKind.Number &&
                indexElement.TryGetInt32(out var parsedIndex))
            {
                index = parsedIndex;
            }

            fallbackIndex++;
            if (index < 0 || index >= vectors.Length)
            {
                continue;
            }

            if (!item.TryGetProperty("embedding", out var embeddingElement) || embeddingElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            vectors[index] = ParseEmbeddingArray(embeddingElement);
        }

        var list = new List<double[]>(inputs.Count);
        for (var i = 0; i < vectors.Length; i++)
        {
            if (vectors[i] == null || vectors[i].Length == 0)
            {
                return ([], "向量接口返回了空 embedding");
            }

            list.Add(vectors[i]);
        }

        return (list, null);
    }

    private static string NormalizeOpenAiBaseUrl(string? baseUrl, string fallback)
    {
        var url = string.IsNullOrWhiteSpace(baseUrl) ? fallback : baseUrl.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
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

    private static string BuildScriptEmbeddingInput(ScriptSearchCandidate candidate)
    {
        var typeLabel = candidate.Type switch
        {
            "group" => "配置组",
            "js" => "JS脚本",
            "keymouse" => "键鼠脚本",
            "pathing" => "地图追踪",
            _ => candidate.Type
        };

        var parts = new List<string>(5)
        {
            typeLabel,
            candidate.Type,
            candidate.Folder ?? string.Empty,
            candidate.Name
        };
        if (!string.IsNullOrWhiteSpace(candidate.SearchText))
        {
            parts.Add(candidate.SearchText);
        }

        var input = NormalizeSearchText(string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part))), MaxEmbeddingInputLength);
        return string.IsNullOrWhiteSpace(input) ? $"{candidate.Type} {candidate.Name}" : input;
    }

    private static string BuildScriptRerankInput(ScriptSearchCandidate candidate)
    {
        var typeLabel = candidate.Type switch
        {
            "group" => "配置组",
            "js" => "JS脚本",
            "keymouse" => "键鼠脚本",
            "pathing" => "地图追踪",
            _ => candidate.Type
        };

        var parts = new List<string>(5)
        {
            typeLabel,
            candidate.Name,
            candidate.Folder ?? string.Empty,
            candidate.Description ?? string.Empty,
            candidate.SearchText ?? string.Empty
        };

        var input = NormalizeSearchText(string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part))), MaxRerankInputLength);
        return string.IsNullOrWhiteSpace(input) ? $"{candidate.Type} {candidate.Name}" : input;
    }

    private static double ComputeScriptKeywordBoost(string query, ScriptSearchCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 0d;
        }

        var keyword = query.Trim();
        var normalized = NormalizeQuery(keyword);
        var keywordNoExt = Path.GetFileNameWithoutExtension(keyword);
        if (IsNameMatch(keywordNoExt, candidate.Name))
        {
            return 0.24d;
        }

        if (!string.IsNullOrWhiteSpace(normalized) && IsNameMatch(normalized, candidate.Name))
        {
            return 0.18d;
        }

        if (ContainsKeyword(candidate.Name, keyword) ||
            (!string.IsNullOrWhiteSpace(normalized) && ContainsKeyword(candidate.Name, normalized)))
        {
            return 0.12d;
        }

        if (ContainsKeyword(candidate.Folder, keyword) ||
            ContainsKeyword(candidate.SearchText, keyword) ||
            (!string.IsNullOrWhiteSpace(normalized) &&
             (ContainsKeyword(candidate.Folder, normalized) || ContainsKeyword(candidate.SearchText, normalized))))
        {
            return 0.06d;
        }

        foreach (var token in ExtractQueryTokens(keyword))
        {
            if (ContainsKeyword(candidate.Name, token) ||
                ContainsKeyword(candidate.Folder, token) ||
                ContainsKeyword(candidate.SearchText, token))
            {
                return 0.04d;
            }
        }

        return 0d;
    }

    private static double[] ParseEmbeddingArray(JsonElement embeddingElement)
    {
        var values = new List<double>(embeddingElement.GetArrayLength());
        foreach (var item in embeddingElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var value))
            {
                values.Add(value);
            }
        }

        return values.ToArray();
    }

    private static double CosineSimilarity(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        var length = Math.Min(left.Count, right.Count);
        if (length <= 0)
        {
            return -1d;
        }

        var dot = 0d;
        var leftNorm = 0d;
        var rightNorm = 0d;
        for (var i = 0; i < length; i++)
        {
            var lv = left[i];
            var rv = right[i];
            dot += lv * rv;
            leftNorm += lv * lv;
            rightNorm += rv * rv;
        }

        if (leftNorm <= 0d || rightNorm <= 0d)
        {
            return -1d;
        }

        return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }

    private async Task<(string text, bool isError)> RunScriptGroupsAsync(JsonElement argsElement)
    {
        var names = ParseScriptNames(argsElement);
        if (names.Count == 0)
        {
            return ("Missing script names", true);
        }

        var typeHint = NormalizeScriptType(ParseScriptType(argsElement));
        var results = new List<ScriptRunResult>();

        if (typeHint == "group")
        {
            var groupResults = await RunScriptGroupsByNamesAsync(names);
            foreach (var result in groupResults)
            {
                if (result.Ok || !string.Equals(result.Message, "配置组不存在", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(result);
                    continue;
                }

                var fallback = await RunScriptByNameAsync(result.Name, null);
                if (fallback.Ok)
                {
                    results.Add(fallback);
                    continue;
                }

                var hint = BuildScriptSuggestion(result.Name);
                var message = $"{result.Message}; {fallback.Message}{hint}";
                results.Add(new ScriptRunResult(result.Name, "group", false, message));
            }

            var groupError = results.Any(r => !r.Ok);
            return (JsonSerializer.Serialize(new { results }, JsonOptions), groupError);
        }

        var groupNames = new List<string>();
        var scriptNames = new List<string>();
        foreach (var name in names)
        {
            if (typeHint == null && ScriptGroupExists(name))
            {
                groupNames.Add(name);
            }
            else
            {
                scriptNames.Add(name);
            }
        }

        if (groupNames.Count > 0)
        {
            results.AddRange(await RunScriptGroupsByNamesAsync(groupNames));
        }

        foreach (var scriptName in scriptNames)
        {
            results.Add(await RunScriptByNameAsync(scriptName, typeHint));
        }

        var isError = results.Any(r => !r.Ok);
        return (JsonSerializer.Serialize(new { results }, JsonOptions), isError);
    }

    private static string GetOneDragonConfigs()
    {
        var configs = OneDragonConfigStore.LoadAll();
        var names = configs.Select(c => c.Name).ToArray();
        var selected = TaskContext.Instance().Config.SelectedOneDragonFlowConfigName;
        if (string.IsNullOrWhiteSpace(selected) && names.Length > 0)
        {
            selected = names[0];
        }

        return JsonSerializer.Serialize(new { selected, configs = names }, JsonOptions);
    }

    private async Task<(string text, bool isError)> RunOneDragonAsync(JsonElement argsElement)
    {
        string? name = null;
        if (argsElement.ValueKind == JsonValueKind.Object &&
            argsElement.TryGetProperty("name", out var nameElement) &&
            nameElement.ValueKind == JsonValueKind.String)
        {
            name = nameElement.GetString();
        }

        var config = OneDragonConfigStore.LoadByName(name);
        if (config == null)
        {
            var available = OneDragonConfigStore.ListNames();
            return (JsonSerializer.Serialize(new { ok = false, error = "配置不存在", available }, JsonOptions), true);
        }

        await RunOnUiThreadAsync(() =>
        {
            var vm = new OneDragonFlowViewModel
            {
                EnableHotReload = false
            };
            vm.OnNavigatedTo();
            vm.SelectedConfig = config;
            vm.SetSomeSelectedConfig(config);
            TaskContext.Instance().Config.SelectedOneDragonFlowConfigName = config.Name;
            StartBackgroundTask(() => vm.OnOneKeyExecute());
            return Task.CompletedTask;
        });

        return (JsonSerializer.Serialize(new { ok = true, name = config.Name }, JsonOptions), false);
    }

    private static List<string> ParseScriptNames(JsonElement argsElement)
    {
        var names = new List<string>();
        if (argsElement.ValueKind != JsonValueKind.Object)
        {
            return names;
        }

        if (argsElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
        {
            var name = nameElement.GetString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        if (argsElement.TryGetProperty("names", out var namesElement) && namesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in namesElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var name = item.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name);
                    }
                }
            }
        }

        return names;
    }

    private static string? TryInferSubscribeQueryFromNames(IReadOnlyList<string> names)
    {
        if (names.Count != 1)
        {
            return null;
        }

        var raw = names[0]?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (raw.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains('/', StringComparison.Ordinal) ||
            raw.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        var query = raw
            .Replace("脚本", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("订阅", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("导入", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("添加", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (query.Length < 2 || string.Equals(query, raw, StringComparison.Ordinal))
        {
            return null;
        }

        return query;
    }

    private static string? ParseScriptType(JsonElement argsElement)
    {
        if (argsElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (argsElement.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
        {
            return typeElement.GetString();
        }

        return null;
    }

    private static string? ParseScriptQuery(JsonElement argsElement)
    {
        if (argsElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (argsElement.TryGetProperty("query", out var queryElement) && queryElement.ValueKind == JsonValueKind.String)
        {
            return queryElement.GetString();
        }

        return null;
    }

    private static int ParseScriptListLimit(JsonElement argsElement)
    {
        return ParsePositiveInt(argsElement, "limit", DefaultScriptListLimit, MaxScriptListLimit);
    }

    private static int ParseScriptSearchLimit(JsonElement argsElement)
    {
        return ParsePositiveInt(argsElement, "limit", DefaultScriptSearchLimit, MaxScriptSearchLimit);
    }

    private static int ParsePositiveInt(JsonElement argsElement, string key, int defaultValue, int maxValue)
    {
        if (argsElement.ValueKind != JsonValueKind.Object)
        {
            return defaultValue;
        }

        if (!argsElement.TryGetProperty(key, out var valueElement))
        {
            return defaultValue;
        }

        if (valueElement.ValueKind != JsonValueKind.Number || !valueElement.TryGetInt32(out var value))
        {
            return defaultValue;
        }

        if (value <= 0)
        {
            return defaultValue;
        }

        return Math.Min(value, maxValue);
    }

    private static string? NormalizeScriptType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        var value = type.Trim().ToLowerInvariant();
        return value switch
        {
            "group" or "groups" or "config" => "group",
            "js" or "javascript" => "js",
            "keymouse" or "key_mouse" or "key-mouse" => "keymouse",
            "pathing" or "autopathing" or "auto_pathing" => "pathing",
            "auto" or "all" => null,
            _ => null
        };
    }

    private static List<ScriptGroupInfo> FilterScriptGroups(IReadOnlyList<ScriptGroupInfo> groups, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return groups.ToList();
        }

        if (groups.Count < ParallelSearchThreshold)
        {
            return groups.Where(group => IsQueryMatch(query, group.Name, null)).ToList();
        }

        return groups.AsParallel()
            .AsOrdered()
            .WithDegreeOfParallelism(SearchMaxDegreeOfParallelism)
            .Where(group => IsQueryMatch(query, group.Name, null))
            .ToList();
    }

    private static List<JsScriptInfo> FilterJsScripts(IReadOnlyList<JsScriptInfo> scripts, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return scripts.ToList();
        }

        if (scripts.Count < ParallelSearchThreshold)
        {
            return scripts.Where(script => IsQueryMatch(query, script.Name, script.Folder, script.SearchText)).ToList();
        }

        return scripts.AsParallel()
            .AsOrdered()
            .WithDegreeOfParallelism(SearchMaxDegreeOfParallelism)
            .Where(script => IsQueryMatch(query, script.Name, script.Folder, script.SearchText))
            .ToList();
    }

    private static List<KeyMouseScriptInfo> FilterKeyMouseScripts(IReadOnlyList<KeyMouseScriptInfo> scripts, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return scripts.ToList();
        }

        if (scripts.Count < ParallelSearchThreshold)
        {
            return scripts.Where(script => IsQueryMatch(query, script.Name, null)).ToList();
        }

        return scripts.AsParallel()
            .AsOrdered()
            .WithDegreeOfParallelism(SearchMaxDegreeOfParallelism)
            .Where(script => IsQueryMatch(query, script.Name, null))
            .ToList();
    }

    private static List<PathingScriptInfo> FilterPathingScripts(IReadOnlyList<PathingScriptInfo> scripts, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return scripts.ToList();
        }

        if (scripts.Count < ParallelSearchThreshold)
        {
            return scripts.Where(script => IsQueryMatch(query, script.Name, script.Folder, script.SearchText)).ToList();
        }

        return scripts.AsParallel()
            .AsOrdered()
            .WithDegreeOfParallelism(SearchMaxDegreeOfParallelism)
            .Where(script => IsQueryMatch(query, script.Name, script.Folder, script.SearchText))
            .ToList();
    }

    private static bool IsQueryMatch(string? query, string? name, string? folder, string? searchText = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var keyword = query.Trim();
        if (ContainsKeywordAny(keyword, name, folder, searchText))
        {
            return true;
        }

        var normalized = NormalizeQuery(keyword);
        if (!string.IsNullOrWhiteSpace(normalized) &&
            !string.Equals(normalized, keyword, StringComparison.OrdinalIgnoreCase) &&
            ContainsKeywordAny(normalized, name, folder, searchText))
        {
            return true;
        }

        foreach (var token in ExtractQueryTokens(keyword))
        {
            if (ContainsKeywordAny(token, name, folder, searchText))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(normalized))
        {
            foreach (var token in ExtractQueryTokens(normalized))
            {
                if (ContainsKeywordAny(token, name, folder, searchText))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsKeywordAny(string keyword, string? name, string? folder, string? searchText)
    {
        return ContainsKeyword(name, keyword) ||
               ContainsKeyword(folder, keyword) ||
               ContainsKeyword(searchText, keyword);
    }

    private static bool ContainsKeyword(string? text, string keyword)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(keyword))
        {
            return false;
        }

        return text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSearchText(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(text.Trim(), @"\s+", " ");
        if (normalized.Length > maxLength)
        {
            normalized = normalized[..maxLength];
        }

        return normalized;
    }

    private static string NormalizeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(query.Trim(), @"\s+", string.Empty);
        foreach (var phrase in QueryStopPhrases)
        {
            if (normalized.Length <= phrase.Length)
            {
                continue;
            }

            var replaced = normalized.Replace(phrase, string.Empty, StringComparison.OrdinalIgnoreCase);
            if (replaced.Length >= 2)
            {
                normalized = replaced;
            }
        }

        return normalized.Trim();
    }

    private static IEnumerable<string> ExtractQueryTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            yield break;
        }

        foreach (Match match in Regex.Matches(query, @"[\p{IsCJKUnifiedIdeographs}]{2,}|[A-Za-z0-9]{2,}"))
        {
            var token = match.Value.Trim();
            if (token.Length >= 2)
            {
                yield return token;
            }
        }
    }

    private static bool ScriptGroupExists(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var folder = Global.Absolute(@"User\ScriptGroup");
        var path = Path.Combine(folder, $"{name}.json");
        return File.Exists(path);
    }

    private async Task<IReadOnlyList<ScriptRunResult>> RunScriptGroupsByNamesAsync(IReadOnlyList<string> names)
    {
        var results = new List<ScriptRunResult>();
        var existing = new List<string>();
        foreach (var name in names)
        {
            if (ScriptGroupExists(name))
            {
                existing.Add(name);
            }
            else
            {
                results.Add(new ScriptRunResult(name, "group", false, "配置组不存在"));
            }
        }

        if (existing.Count == 0)
        {
            return results;
        }

        var started = false;
        try
        {
            await RunOnUiThreadAsync(async () =>
            {
                var vm = App.GetService<ScriptControlViewModel>();
                if (vm != null)
                {
                    await vm.OnStartMultiScriptGroupWithNamesAsync(existing.ToArray());
                    started = true;
                }
            });
        }
        catch (Exception ex)
        {
            foreach (var name in existing)
            {
                results.Add(new ScriptRunResult(name, "group", false, ex.Message));
            }
            return results;
        }

        foreach (var name in existing)
        {
            results.Add(new ScriptRunResult(name, "group", started, started ? "started" : "脚本控制页未初始化"));
        }

        return results;
    }

    private async Task<ScriptRunResult> RunScriptByNameAsync(string name, string? typeHint)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new ScriptRunResult(string.Empty, "auto", false, "脚本名为空");
        }

        var scriptService = App.GetService<IScriptService>();
        if (scriptService == null)
        {
            return new ScriptRunResult(name, typeHint ?? "auto", false, "脚本服务不可用");
        }

        var normalizedType = NormalizeScriptType(typeHint);

        if (normalizedType == null || normalizedType == "js")
        {
            if (TryFindJsScript(name, out var project))
            {
                try
                {
                    await RunScriptProjectSerialAsync(scriptService, new ScriptGroupProject(project!));
                    return new ScriptRunResult(name, "js", true, "started");
                }
                catch (Exception ex)
                {
                    return new ScriptRunResult(name, "js", false, ex.Message);
                }
            }

            if (normalizedType == "js")
            {
                return new ScriptRunResult(name, "js", false, "未找到 JS 脚本");
            }
        }

        if (normalizedType == null || normalizedType == "keymouse")
        {
            if (TryFindKeyMouseScript(name, out var keyMouseName))
            {
                try
                {
                    await RunScriptProjectSerialAsync(scriptService, ScriptGroupProject.BuildKeyMouseProject(keyMouseName));
                    return new ScriptRunResult(name, "keymouse", true, "started");
                }
                catch (Exception ex)
                {
                    return new ScriptRunResult(name, "keymouse", false, ex.Message);
                }
            }

            if (normalizedType == "keymouse")
            {
                return new ScriptRunResult(name, "keymouse", false, "未找到键鼠脚本");
            }
        }

        if (normalizedType == null || normalizedType == "pathing")
        {
            if (TryFindPathingScript(name, out var pathingFile, out var pathingFolder))
            {
                try
                {
                    await RunScriptProjectSerialAsync(scriptService, ScriptGroupProject.BuildPathingProject(pathingFile, pathingFolder));
                    return new ScriptRunResult(name, "pathing", true, "started");
                }
                catch (Exception ex)
                {
                    return new ScriptRunResult(name, "pathing", false, ex.Message);
                }
            }

            if (normalizedType == "pathing")
            {
                return new ScriptRunResult(name, "pathing", false, "未找到地图追踪脚本");
            }
        }

        return new ScriptRunResult(name, normalizedType ?? "auto", false, "未找到匹配脚本");
    }

    private static async Task RunScriptProjectSerialAsync(IScriptService scriptService, ScriptGroupProject project)
    {
        await ScriptRunSemaphore.WaitAsync();
        try
        {
            await RunOnUiThreadAsync(() => scriptService.RunMulti([project]));
        }
        finally
        {
            ScriptRunSemaphore.Release();
        }
    }

    private static bool TryFindJsScript(string name, out ScriptProject? project)
    {
        project = null;
        var root = Global.ScriptPath();
        if (!Directory.Exists(root))
        {
            return false;
        }

        foreach (var dir in Directory.GetDirectories(root))
        {
            var folder = Path.GetFileName(dir) ?? string.Empty;
            if (IsNameMatch(name, folder))
            {
                try
                {
                    project = new ScriptProject(folder);
                    return true;
                }
                catch
                {
                    continue;
                }
            }

            try
            {
                var candidate = new ScriptProject(folder);
                if (IsNameMatch(name, candidate.Manifest.Name))
                {
                    project = candidate;
                    return true;
                }
            }
            catch
            {
                // ignore invalid script folders
            }
        }

        return false;
    }

    private static bool TryFindKeyMouseScript(string name, out string relativePath)
    {
        relativePath = string.Empty;
        var root = Global.Absolute(@"User\KeyMouseScript");
        if (!Directory.Exists(root))
        {
            return false;
        }

        foreach (var file in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
        {
            var rel = GetRelativePathSafe(root, file);
            var fileName = Path.GetFileName(file);
            if (IsNameMatch(name, rel) || IsNameMatch(name, fileName))
            {
                relativePath = rel;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindPathingScript(string name, out string fileName, out string folder)
    {
        fileName = string.Empty;
        folder = string.Empty;

        var root = MapPathingViewModel.PathJsonPath;
        if (!Directory.Exists(root))
        {
            return false;
        }

        foreach (var file in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
        {
            var rel = GetRelativePathSafe(root, file);
            var candidateFile = Path.GetFileName(file);
            if (IsNameMatch(name, rel) || IsNameMatch(name, candidateFile))
            {
                fileName = candidateFile;
                var dir = Path.GetDirectoryName(file) ?? string.Empty;
                var relFolder = GetRelativePathSafe(root, dir);
                folder = relFolder == "." ? string.Empty : relFolder;
                return true;
            }
        }

        return false;
    }

    private static bool IsNameMatch(string input, string candidate)
    {
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var left = input.Trim();
        var right = candidate.Trim();
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leftNoExt = Path.GetFileNameWithoutExtension(left);
        var rightNoExt = Path.GetFileNameWithoutExtension(right);
        return string.Equals(leftNoExt, rightNoExt, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildScriptSuggestion(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var hints = new List<string>();
        var groupMatches = FindSimilarNames(name, LoadScriptGroupInfos().Select(g => g.Name));
        if (groupMatches.Count > 0)
        {
            hints.Add($"配置组: {string.Join(", ", groupMatches)}");
        }

        var jsMatches = FindSimilarNames(name, LoadJsScriptInfos().Select(s => s.Name));
        if (jsMatches.Count > 0)
        {
            hints.Add($"JS: {string.Join(", ", jsMatches)}");
        }

        var keyMouseMatches = FindSimilarNames(name, LoadKeyMouseScriptInfos().Select(s => s.Name));
        if (keyMouseMatches.Count > 0)
        {
            hints.Add($"键鼠: {string.Join(", ", keyMouseMatches)}");
        }

        var pathingMatches = FindSimilarNames(name, LoadPathingScriptInfos().Select(s => s.Name));
        if (pathingMatches.Count > 0)
        {
            hints.Add($"路径: {string.Join(", ", pathingMatches)}");
        }

        if (name.Contains("一条龙", StringComparison.OrdinalIgnoreCase))
        {
            var dragonConfigs = OneDragonConfigStore.ListNames();
            if (dragonConfigs.Count > 0)
            {
                hints.Add($"一条龙配置: {string.Join(", ", dragonConfigs)}");
            }
        }

        if (hints.Count == 0)
        {
            return string.Empty;
        }

        return "；候选=" + string.Join(" | ", hints);
    }

    private static List<string> FindSimilarNames(string name, IEnumerable<string> candidates, int max = 5)
    {
        var list = new List<string>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (IsNameMatch(name, candidate) ||
                candidate.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                if (!list.Any(item => string.Equals(item, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(candidate);
                    if (list.Count >= max)
                    {
                        break;
                    }
                }
            }
        }

        return list;
    }

    private static string GetRelativePathSafe(string root, string path)
    {
        try
        {
            return Path.GetRelativePath(root, path);
        }
        catch
        {
            return path;
        }
    }

    private (string text, bool isError) SetConfigValue(JsonElement argsElement, bool isInternalCall)
    {
        if (argsElement.ValueKind != JsonValueKind.Object)
        {
            return ("Invalid arguments", true);
        }

        if (!argsElement.TryGetProperty("path", out var pathElement))
        {
            return ("Missing path", true);
        }

        var path = pathElement.GetString();
        if (string.IsNullOrWhiteSpace(path))
        {
            return ("Missing path", true);
        }

        if (!TryNormalizeConfigPath(path, out var normalizedPath, out var normalizeError))
        {
            return (normalizeError ?? "Invalid path", true);
        }

        if (!isInternalCall && !_configService.Get().McpConfig.AllowConfigSet)
        {
            return ("config.set disabled. Enable \"允许 MCP 修改配置\" in settings first.", true);
        }

        if (!isInternalCall && !IsConfigPathAllowedForWrite(normalizedPath, out var policyError))
        {
            return (policyError ?? "Config path is not writable via MCP", true);
        }

        if (!argsElement.TryGetProperty("value", out var valueElement))
        {
            return ("Missing value", true);
        }

        var success = false;
        string? error = null;
        RunOnUiThread(() =>
        {
            var config = _configService.Get();
            success = ConfigPathAccessor.TrySetValue(config, normalizedPath, valueElement, out error);
        });

        if (!success)
        {
            return (error ?? "Set failed", true);
        }

        return ("{\"ok\":true}", false);
    }

    private Task StartDispatcherAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(async () =>
        {
            try
            {
                var homeVm = App.GetService<BetterGenshinImpact.ViewModel.Pages.HomePageViewModel>();
                if (homeVm != null)
                {
                    await homeVm.OnStartTriggerAsync();
                }
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private Task StartGameAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(async () =>
        {
            try
            {
                await BetterGenshinImpact.Service.ScriptService.StartGameTask(false);
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private void StopDispatcher()
    {
        RunOnUiThread(() =>
        {
            var homeVm = App.GetService<BetterGenshinImpact.ViewModel.Pages.HomePageViewModel>();
            homeVm?.StopTriggerCommand.Execute(null);
        });
    }

    private static string GetTaskStatus()
    {
        var running = TaskControl.TaskSemaphore.CurrentCount == 0;
        var payload = new
        {
            isRunning = running,
            isSuspended = RunnerContext.Instance.IsSuspend,
            taskProgress = RunnerContext.Instance.taskProgress
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static object[] BuildToolsList()
    {
        return
        [
            new
            {
                name = "bgi.get_status",
                description = "获取 BetterGI 当前运行状态",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.get_features",
                description = "获取基础任务开关状态",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.set_features",
                description = "设置基础任务开关状态（至少提供一个字段，值为 true/false）",
                inputSchema = new
                {
                    type = "object",
                    minProperties = 1,
                    properties = new
                    {
                        autoPick = new { type = "boolean" },
                        autoSkip = new { type = "boolean" },
                        autoHangout = new { type = "boolean" },
                        autoFishing = new { type = "boolean" },
                        autoCook = new { type = "boolean" },
                        autoEat = new { type = "boolean" },
                        quickTeleport = new { type = "boolean" },
                        mapMask = new { type = "boolean" },
                        skillCd = new { type = "boolean" }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.language.get",
                description = "获取软件多语言配置（UI语言/游戏语言）",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.language.set",
                description = "设置软件多语言配置（uiCulture/gameCulture，支持 zh-Hans/zh-Hant/en 等 culture 名）",
                inputSchema = new
                {
                    type = "object",
                    minProperties = 1,
                    properties = new
                    {
                        uiCulture = new { type = "string", description = "UI 语言，如 zh-Hans / zh-Hant / en" },
                        gameCulture = new { type = "string", description = "游戏语言，如 zh-Hans / zh-Hant / en" }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.config.get",
                description = "读取配置（必须提供 path，且敏感字段会被拦截）",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string", description = "配置路径（必填）" }
                    },
                    required = new[] { "path" },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.config.set",
                description = "设置配置（需在设置中开启“允许 MCP 修改配置”，且仅允许白名单路径）",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string" },
                        value = new { }
                    },
                    required = new[] { "path", "value" },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.config.reload",
                description = "从数据库重新加载配置到内存",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.get_logs",
                description = "获取遮罩日志内容",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        maxLines = new { type = "integer", minimum = 1, maximum = 500 }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.script.groups",
                description = "列出脚本配置组",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string", description = "可选关键词过滤" }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.script.list",
                description = "列出可用脚本（默认限流，避免一次性返回完整脚本清单）",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        type = new { type = "string", @enum = new[] { "auto", "group", "js", "keymouse", "pathing" } },
                        query = new { type = "string", description = "可选关键词过滤" },
                        limit = new { type = "integer", minimum = 1, maximum = 200, description = "最多返回条数（默认 80）" }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.script.search",
                description = "搜索脚本（优先少量结果；启用向量检索时会进行语义重排；本地无结果时会附带远程仓库订阅链接）",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string", description = "关键词（必填）" },
                        type = new { type = "string", @enum = new[] { "auto", "group", "js", "keymouse", "pathing" } },
                        limit = new { type = "integer", minimum = 1, maximum = 200, description = "返回的最大匹配数量" }
                    },
                    required = new[] { "query" },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.script.detail",
                description = "获取脚本详细介绍（优先读取 README 摘要，返回可执行/可订阅建议）",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "脚本名（可选）" },
                        names = new { type = "array", items = new { type = "string" }, description = "批量脚本名（可选）" },
                        query = new { type = "string", description = "关键词检索（可选）" },
                        type = new { type = "string", @enum = new[] { "auto", "group", "js", "keymouse", "pathing" } },
                        limit = new { type = "integer", minimum = 1, maximum = 20, description = "返回条数（默认 5）" }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.script.run",
                description = "执行脚本（配置组或单脚本）",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string" },
                        names = new { type = "array", items = new { type = "string" } },
                        type = new { type = "string", @enum = new[] { "auto", "all", "group", "js", "keymouse", "pathing" } }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.script.subscribe",
                description = "订阅远程脚本（默认仅返回订阅信息；importNow 需手动开启总闸）",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "脚本名（可选）" },
                        names = new { type = "array", items = new { type = "string" }, description = "批量脚本名（可选）" },
                        query = new { type = "string", description = "关键词检索（可选）" },
                        limit = new { type = "integer", minimum = 1, maximum = 100, description = "query 模式返回条数（默认 20）" },
                        importNow = new { type = "boolean", description = "是否立即导入到本地（默认 false，且需设置里允许）" },
                        previewOnly = new { type = "boolean", description = "仅预览订阅链接，不执行导入（等价于 importNow=false）" }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.one_dragon.list",
                description = "列出一条龙配置",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.one_dragon.run",
                description = "运行一条龙配置",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "配置名称（可选）" }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.task.cancel",
                description = "取消当前任务",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.task.pause",
                description = "暂停当前任务",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.task.resume",
                description = "继续当前任务",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.task.status",
                description = "获取任务运行状态",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.action.start_dispatcher",
                description = "启动截图器与遮罩（等同于点击开始）",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.action.stop_dispatcher",
                description = "停止截图器与遮罩",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.action.start_game",
                description = "启动原神并开启截图器（高风险，需在设置中开启）",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.capture_screen",
                description = "获取当前画面截图（PNG）",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        includeData = new
                        {
                            type = "boolean",
                            description = "是否返回 base64 图片数据（默认 true）。false 时仅返回截图元信息，避免大 payload。"
                        }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.web.search",
                description = "联网搜索（用于查询原神术语/机制等，默认开启，可在设置中关闭）",
                inputSchema = new
                {
                    type = "object",
                    required = new[] { "query" },
                    properties = new
                    {
                        query = new
                        {
                            type = "string",
                            description = "搜索关键词（建议包含“原神”以便消歧）"
                        },
                        maxResults = new
                        {
                            type = "integer",
                            minimum = 1,
                            maximum = 10,
                            description = "返回条数（默认使用设置中的值）"
                        },
                        provider = new
                        {
                            type = "string",
                            description = "auto / searxng / fandom / duckduckgo（默认 auto）"
                        },
                        language = new
                        {
                            type = "string",
                            description = "语言，例如 zh-CN / en-US（部分 provider 会忽略）"
                        }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "search_docs",
                description = "搜索 BetterGI 官网文档（MCP 启动后会自动预抓取 sitemap 建立内存索引）",
                inputSchema = new
                {
                    type = "object",
                    required = new[] { "query" },
                    properties = new
                    {
                        query = new { type = "string", description = "文档关键词或报错信息" },
                        limit = new { type = "integer", minimum = 1, maximum = 20, description = "返回条数（默认 5）" }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "get_feature_detail",
                description = "获取某个功能的详细说明（基于官网文档索引）",
                inputSchema = new
                {
                    type = "object",
                    required = new[] { "feature" },
                    properties = new
                    {
                        feature = new { type = "string", description = "功能名，如 自动邀约 / 地图追踪 / MCP" },
                        limit = new { type = "integer", minimum = 1, maximum = 10, description = "返回条数（默认 4）" }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "get_download_info",
                description = "获取官网中最新版本下载信息与入口",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        limit = new { type = "integer", minimum = 1, maximum = 30, description = "最多返回下载链接条数（默认 12）" }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "search_scripts",
                description = "搜索社区路径追踪脚本（远程仓库，返回订阅链接）",
                inputSchema = new
                {
                    type = "object",
                    required = new[] { "query" },
                    properties = new
                    {
                        query = new { type = "string", description = "脚本关键词，如 薄荷 采集 / 丘丘王 讨伐" },
                        limit = new { type = "integer", minimum = 1, maximum = 30, description = "返回条数（默认 8）" }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "get_faq",
                description = "查询官网 FAQ / 常见问题",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string", description = "可选关键词；为空时默认检索 FAQ" },
                        limit = new { type = "integer", minimum = 1, maximum = 20, description = "返回条数（默认 6）" }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "get_quickstart",
                description = "获取官网快速上手步骤与入口",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        limit = new { type = "integer", minimum = 1, maximum = 20, description = "返回条数（默认 6）" }
                    },
                    additionalProperties = false
                }
            }
        ];
    }

    private static object BuildInitializeResult()
    {
        return new
        {
            protocolVersion = "2024-11-05",
            serverInfo = new
            {
                name = "BetterGI",
                version = Global.Version
            },
            capabilities = new
            {
                tools = new { listChanged = false },
                resources = new { },
                prompts = new { }
            }
        };
    }

    private object BuildStatus()
    {
        return new
        {
            version = Global.Version,
            serverTime = DateTimeOffset.Now,
            isInitialized = TaskContext.Instance().IsInitialized,
            isSuspended = RunnerContext.Instance.IsSuspend
        };
    }

    private BasicFeatureState BuildBasicFeatureState()
    {
        var config = _configService.Get();
        return new BasicFeatureState
        {
            AutoPick = config.AutoPickConfig.Enabled,
            AutoSkip = config.AutoSkipConfig.Enabled,
            AutoHangout = config.AutoSkipConfig.AutoHangoutEventEnabled,
            AutoFishing = config.AutoFishingConfig.Enabled,
            AutoCook = config.AutoCookConfig.Enabled,
            AutoEat = config.AutoEatConfig.Enabled,
            QuickTeleport = config.QuickTeleportConfig.Enabled,
            MapMask = config.MapMaskConfig.Enabled,
            SkillCd = config.SkillCdConfig.Enabled
        };
    }

    private void ApplyBasicFeaturePatch(BasicFeaturePatch patch)
    {
        RunOnUiThread(() =>
        {
            var config = _configService.Get();

            if (patch.AutoPick.HasValue)
            {
                config.AutoPickConfig.Enabled = patch.AutoPick.Value;
            }

            if (patch.AutoSkip.HasValue)
            {
                config.AutoSkipConfig.Enabled = patch.AutoSkip.Value;
            }

            if (patch.AutoHangout.HasValue)
            {
                config.AutoSkipConfig.AutoHangoutEventEnabled = patch.AutoHangout.Value;
            }

            if (patch.AutoFishing.HasValue)
            {
                config.AutoFishingConfig.Enabled = patch.AutoFishing.Value;
            }

            if (patch.AutoCook.HasValue)
            {
                config.AutoCookConfig.Enabled = patch.AutoCook.Value;
            }

            if (patch.AutoEat.HasValue)
            {
                config.AutoEatConfig.Enabled = patch.AutoEat.Value;
            }

            if (patch.QuickTeleport.HasValue)
            {
                config.QuickTeleportConfig.Enabled = patch.QuickTeleport.Value;
            }

            if (patch.MapMask.HasValue)
            {
                config.MapMaskConfig.Enabled = patch.MapMask.Value;
            }

            if (patch.SkillCd.HasValue)
            {
                config.SkillCdConfig.Enabled = patch.SkillCd.Value;
            }
        });
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    private static Task RunOnUiThreadAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(async () =>
        {
            try
            {
                await action();
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private static void StartBackgroundTask(Func<Task> action)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch
            {
                // ignore background errors
            }
        });
    }

    private static readonly string[] QueryStopPhrases =
    [
        "采集",
        "收集",
        "获取",
        "寻找",
        "查找",
        "搜索",
        "挖矿",
        "挖",
        "刷取",
        "刷",
        "跑图",
        "跑",
        "自动",
        "脚本",
        "路线",
        "任务"
    ];

    private static readonly HashSet<string> PathingSearchFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Name",
        "Description",
        "Author",
        "Type",
        "MapName",
        "Action",
        "ActionParams",
        "Material",
        "Monster",
        "Tags"
    };

    private static BasicFeaturePatch? ParseFeaturePatch(JsonElement argsElement)
    {
        if (argsElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return JsonSerializer.Deserialize<BasicFeaturePatch>(argsElement.GetRawText(), JsonOptions);
    }

    private static bool IsEmptyPatch(BasicFeaturePatch patch)
    {
        return !patch.AutoPick.HasValue &&
               !patch.AutoSkip.HasValue &&
               !patch.AutoHangout.HasValue &&
               !patch.AutoFishing.HasValue &&
               !patch.AutoCook.HasValue &&
               !patch.AutoEat.HasValue &&
               !patch.QuickTeleport.HasValue &&
               !patch.MapMask.HasValue &&
               !patch.SkillCd.HasValue;
    }

    private static object? ExtractId(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idElement))
        {
            return null;
        }

        return idElement.ValueKind switch
        {
            JsonValueKind.String => idElement.GetString(),
            JsonValueKind.Number => idElement.TryGetInt64(out var v) ? v : idElement.GetDouble(),
            JsonValueKind.Null => null,
            _ => null
        };
    }

    private sealed class BasicFeatureState
    {
        public bool AutoPick { get; set; }
        public bool AutoSkip { get; set; }
        public bool AutoHangout { get; set; }
        public bool AutoFishing { get; set; }
        public bool AutoCook { get; set; }
        public bool AutoEat { get; set; }
        public bool QuickTeleport { get; set; }
        public bool MapMask { get; set; }
        public bool SkillCd { get; set; }
    }

    private sealed class BasicFeaturePatch
    {
        public bool? AutoPick { get; set; }
        public bool? AutoSkip { get; set; }
        public bool? AutoHangout { get; set; }
        public bool? AutoFishing { get; set; }
        public bool? AutoCook { get; set; }
        public bool? AutoEat { get; set; }
        public bool? QuickTeleport { get; set; }
        public bool? MapMask { get; set; }
        public bool? SkillCd { get; set; }
    }

    private sealed class ScriptGroupInfo
    {
        public string Name { get; set; } = string.Empty;
        public int Index { get; set; }
        public int ProjectCount { get; set; }
    }

    private sealed class ScriptRunResult
    {
        public ScriptRunResult(string name, string type, bool ok, string message)
        {
            Name = name;
            Type = type;
            Ok = ok;
            Message = message;
        }

        public string Name { get; }
        public string Type { get; }
        public bool Ok { get; }
        public string Message { get; }
    }

    private sealed class ScriptSearchCandidate
    {
        public string Type { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Folder { get; set; }
        public string? Description { get; set; }
        public string? SearchText { get; set; }
    }

    private sealed class RemoteScriptRepoEntry
    {
        public string Root { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Folder { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string SearchText { get; set; } = string.Empty;
    }

    private sealed class JsScriptInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Folder { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string SearchText { get; set; } = string.Empty;
    }

    private sealed class KeyMouseScriptInfo
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class PathingScriptInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Folder { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string SearchText { get; set; } = string.Empty;
    }

    private sealed class VectorEndpointOption
    {
        public string Label { get; set; } = string.Empty;
        public string BaseEndpoint { get; set; } = string.Empty;
        public string EmbeddingsEndpoint => $"{BaseEndpoint}/embeddings";
        public string RerankEndpoint => $"{BaseEndpoint}/rerank";
        public string ApiKey { get; set; } = string.Empty;
    }

    private static string BuildJsScriptSearchText(Manifest manifest)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(manifest.Description))
        {
            parts.Add(manifest.Description);
        }

        if (manifest.Authors is { Count: > 0 })
        {
            var authorNames = manifest.Authors
                .Select(author => author.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (authorNames.Count > 0)
            {
                parts.Add(string.Join(" ", authorNames));
            }
        }

        return NormalizeSearchText(string.Join(" ", parts), MaxScriptSearchTextLength);
    }

    private static string BuildPathingScriptSearchText(string filePath, string fileName, string folder)
    {
        var parts = new List<string>(4);
        var fileNameNoExtension = Path.GetFileNameWithoutExtension(fileName);
        if (!string.IsNullOrWhiteSpace(fileNameNoExtension))
        {
            parts.Add(fileNameNoExtension);
        }

        if (!string.IsNullOrWhiteSpace(folder))
        {
            parts.Add(folder);
        }

        try
        {
            var mergedJson = JsonMerger.getMergePathingJson(filePath);
            if (!string.IsNullOrWhiteSpace(mergedJson))
            {
                using var doc = JsonDocument.Parse(mergedJson);
                var tokens = new List<string>(32);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectPathingSearchTokens(doc.RootElement, null, tokens, seen);
                if (tokens.Count > 0)
                {
                    parts.Add(string.Join(" ", tokens));
                }
            }
        }
        catch
        {
            // keep minimal metadata when pathing content parse fails
        }

        return NormalizeSearchText(string.Join(" ", parts), MaxPathingSummaryLength);
    }

    private static string? ResolveRemoteScriptDescription(RemoteScriptRepoEntry entry)
    {
        var summary = NormalizeReadmeSummary(entry.Description);
        if (!string.IsNullOrWhiteSpace(summary))
        {
            return summary;
        }

        var readmeSummary = TryResolveCenterRepoReadmeSummary(entry.Path);
        summary = NormalizeReadmeSummary(readmeSummary);
        if (!string.IsNullOrWhiteSpace(summary))
        {
            return summary;
        }

        return NormalizeReadmeSummary(entry.Title);
    }

    private static string? TryResolveCenterRepoReadmeSummary(string repoRelativePath)
    {
        if (string.IsNullOrWhiteSpace(repoRelativePath))
        {
            return null;
        }

        var repoRoot = TryGetCenterRepoContentRoot();
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
        {
            return null;
        }

        var relative = repoRelativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var fullPath = Path.Combine(repoRoot, relative);
        var directory = Directory.Exists(fullPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        return TryGetReadmeSummaryFromDirectory(directory, repoRoot);
    }

    private static string? TryGetCenterRepoContentRoot()
    {
        try
        {
            var centerPath = ScriptRepoUpdater.CenterRepoPath;
            if (string.IsNullOrWhiteSpace(centerPath) || !Directory.Exists(centerPath))
            {
                return null;
            }

            var repoSubfolder = Path.Combine(centerPath, "repo");
            return Directory.Exists(repoSubfolder) ? repoSubfolder : centerPath;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetReadmeSummaryFromDirectory(string? directory, string? stopDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var current = Path.GetFullPath(directory);
        var stop = string.IsNullOrWhiteSpace(stopDirectory) || !Directory.Exists(stopDirectory)
            ? null
            : Path.GetFullPath(stopDirectory);

        while (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            if (TryGetCachedReadmeSummary(current, out var cachedSummary))
            {
                if (!string.IsNullOrWhiteSpace(cachedSummary))
                {
                    return cachedSummary;
                }
            }
            else
            {
                var summary = TryReadReadmeSummaryInDirectory(current);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    ReadmeSummaryCache[current] = summary;
                    return summary;
                }

                ReadmeSummaryCache[current] = ReadmeSummaryCacheMiss;
            }

            if (!string.IsNullOrWhiteSpace(stop) && string.Equals(current, stop, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return null;
    }

    private static bool TryGetCachedReadmeSummary(string directory, out string? summary)
    {
        summary = null;
        if (!ReadmeSummaryCache.TryGetValue(directory, out var cached))
        {
            return false;
        }

        summary = string.Equals(cached, ReadmeSummaryCacheMiss, StringComparison.Ordinal)
            ? null
            : cached;
        return true;
    }

    private static string? TryReadReadmeSummaryInDirectory(string directory)
    {
        foreach (var fileName in ReadmeFileNames)
        {
            var filePath = Path.Combine(directory, fileName);
            if (!File.Exists(filePath))
            {
                continue;
            }

            try
            {
                var content = File.ReadAllText(filePath);
                var summary = ExtractReadmeSummary(content);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    return summary;
                }
            }
            catch
            {
                // ignore invalid markdown files
            }
        }

        return null;
    }

    private static string? ExtractReadmeSummary(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        text = MarkdownCodeBlockRegex.Replace(text, " ");
        text = MarkdownImageRegex.Replace(text, " ");
        text = MarkdownLinkRegex.Replace(text, "$1");

        var lines = text.Split('\n');
        var snippets = new List<string>(3);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            line = MarkdownPrefixRegex.Replace(line, string.Empty).Trim();
            if (line.Length < 2)
            {
                continue;
            }

            snippets.Add(line);
            if (snippets.Count >= 2)
            {
                break;
            }
        }

        if (snippets.Count == 0)
        {
            return null;
        }

        return NormalizeReadmeSummary(string.Join(" ", snippets));
    }

    private static string? NormalizeReadmeSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var normalized = NormalizeSearchText(summary, MaxReadmeSummaryLength);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized;
    }

    private static void CollectPathingSearchTokens(JsonElement element, string? propertyName, List<string> tokens, HashSet<string> seen)
    {
        if (tokens.Count >= MaxPathingSummaryTokens)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectPathingSearchTokens(property.Value, property.Name, tokens, seen);
                    if (tokens.Count >= MaxPathingSummaryTokens)
                    {
                        break;
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectPathingSearchTokens(item, propertyName, tokens, seen);
                    if (tokens.Count >= MaxPathingSummaryTokens)
                    {
                        break;
                    }
                }
                break;
            case JsonValueKind.String:
                var value = element.GetString();
                if (ShouldIncludePathingToken(propertyName, value))
                {
                    AddPathingToken(tokens, seen, value!);
                }
                break;
        }
    }

    private static bool ShouldIncludePathingToken(string? propertyName, string? value)
    {
        if (string.IsNullOrWhiteSpace(propertyName) ||
            !PathingSearchFieldNames.Contains(propertyName) ||
            string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = NormalizeSearchText(value, 120);
        if (normalized.Length < 2)
        {
            return false;
        }

        return !normalized.All(ch => char.IsDigit(ch) || ch is '.' or '-' or '_' or '/');
    }

    private static void AddPathingToken(List<string> tokens, HashSet<string> seen, string value)
    {
        var token = NormalizeSearchText(value, 96);
        if (token.Length < 2 || !seen.Add(token))
        {
            return;
        }

        tokens.Add(token);
    }

    private static IReadOnlyList<ScriptGroupInfo> LoadScriptGroupInfos()
    {
        try
        {
            var folder = Global.Absolute(@"User\ScriptGroup");
            if (!Directory.Exists(folder))
            {
                return Array.Empty<ScriptGroupInfo>();
            }

            var files = Directory.GetFiles(folder, "*.json");
            if (files.Length == 0)
            {
                return Array.Empty<ScriptGroupInfo>();
            }

            var list = new List<ScriptGroupInfo>(files.Length);
            if (files.Length < ParallelFileIoThreshold)
            {
                foreach (var file in files)
                {
                    if (TryLoadScriptGroupInfo(file, out var groupInfo))
                    {
                        list.Add(groupInfo);
                    }
                }
            }
            else
            {
                var bag = new ConcurrentBag<ScriptGroupInfo>();
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = SearchMaxDegreeOfParallelism
                };
                Parallel.ForEach(files, options, file =>
                {
                    if (TryLoadScriptGroupInfo(file, out var groupInfo))
                    {
                        bag.Add(groupInfo);
                    }
                });
                list.AddRange(bag);
            }

            return list.OrderBy(g => g.Index).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return Array.Empty<ScriptGroupInfo>();
        }
    }

    private static bool TryLoadScriptGroupInfo(string file, out ScriptGroupInfo info)
    {
        info = null!;
        try
        {
            var json = File.ReadAllText(file);
            var group = ScriptGroup.FromJson(json);
            info = new ScriptGroupInfo
            {
                Name = group.Name,
                Index = group.Index,
                ProjectCount = group.Projects?.Count ?? 0
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<JsScriptInfo> LoadJsScriptInfos()
    {
        var list = new List<JsScriptInfo>();
        try
        {
            var root = Global.ScriptPath();
            if (!Directory.Exists(root))
            {
                return list;
            }

            var directories = Directory.GetDirectories(root);
            if (directories.Length == 0)
            {
                return list;
            }

            if (directories.Length < ParallelFileIoThreshold)
            {
                foreach (var dir in directories)
                {
                    if (TryBuildJsScriptInfo(root, dir, out var scriptInfo))
                    {
                        list.Add(scriptInfo);
                    }
                }
            }
            else
            {
                var bag = new ConcurrentBag<JsScriptInfo>();
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = SearchMaxDegreeOfParallelism
                };
                Parallel.ForEach(directories, options, dir =>
                {
                    if (TryBuildJsScriptInfo(root, dir, out var scriptInfo))
                    {
                        bag.Add(scriptInfo);
                    }
                });
                list.AddRange(bag);
            }
        }
        catch
        {
            return list;
        }

        return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<KeyMouseScriptInfo> LoadKeyMouseScriptInfos()
    {
        var list = new List<KeyMouseScriptInfo>();
        try
        {
            var root = Global.Absolute(@"User\KeyMouseScript");
            if (!Directory.Exists(root))
            {
                return list;
            }

            foreach (var file in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
            {
                var rel = GetRelativePathSafe(root, file);
                if (string.IsNullOrWhiteSpace(rel))
                {
                    continue;
                }

                list.Add(new KeyMouseScriptInfo { Name = rel });
            }
        }
        catch
        {
            return list;
        }

        return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<PathingScriptInfo> LoadPathingScriptInfos()
    {
        var list = new List<PathingScriptInfo>();
        try
        {
            var root = MapPathingViewModel.PathJsonPath;
            if (!Directory.Exists(root))
            {
                return list;
            }

            var files = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                return list;
            }

            var folderReadmeCache = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (files.Length < ParallelFileIoThreshold)
            {
                foreach (var file in files)
                {
                    if (TryBuildPathingScriptInfo(root, file, folderReadmeCache, out var info))
                    {
                        list.Add(info);
                    }
                }
            }
            else
            {
                var bag = new ConcurrentBag<PathingScriptInfo>();
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = SearchMaxDegreeOfParallelism
                };
                Parallel.ForEach(files, options, file =>
                {
                    if (TryBuildPathingScriptInfo(root, file, folderReadmeCache, out var info))
                    {
                        bag.Add(info);
                    }
                });
                list.AddRange(bag);
            }
        }
        catch
        {
            return list;
        }

        return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool TryBuildJsScriptInfo(string scriptRoot, string scriptDirectory, out JsScriptInfo info)
    {
        info = null!;
        var folder = Path.GetFileName(scriptDirectory) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        try
        {
            var project = new ScriptProject(folder);
            var manifestName = string.IsNullOrWhiteSpace(project.Manifest.Name)
                ? folder
                : project.Manifest.Name.Trim();
            var readmeSummary = TryGetReadmeSummaryFromDirectory(scriptDirectory, scriptRoot);
            if (string.IsNullOrWhiteSpace(readmeSummary))
            {
                readmeSummary = TryResolveCenterRepoReadmeSummary($"js/{folder}");
            }

            info = new JsScriptInfo
            {
                Name = manifestName,
                Folder = folder,
                Description = NormalizeReadmeSummary(readmeSummary),
                SearchText = NormalizeSearchText(
                    string.Join(" ", new[] { BuildJsScriptSearchText(project.Manifest), readmeSummary }
                        .Where(v => !string.IsNullOrWhiteSpace(v))),
                    MaxScriptSearchTextLength)
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryBuildPathingScriptInfo(
        string root,
        string file,
        ConcurrentDictionary<string, string?> folderReadmeCache,
        out PathingScriptInfo info)
    {
        info = null!;
        var fileName = Path.GetFileName(file);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var dir = Path.GetDirectoryName(file) ?? string.Empty;
        var relFolder = GetRelativePathSafe(root, dir);
        if (relFolder == ".")
        {
            relFolder = string.Empty;
        }

        var readmeSummary = folderReadmeCache.GetOrAdd(relFolder, folderKey =>
        {
            var summary = string.IsNullOrWhiteSpace(folderKey)
                ? TryGetReadmeSummaryFromDirectory(root, root)
                : TryGetReadmeSummaryFromDirectory(Path.Combine(root, folderKey), root);
            if (string.IsNullOrWhiteSpace(summary))
            {
                var repoRelativePath = string.IsNullOrWhiteSpace(folderKey)
                    ? "pathing"
                    : $"pathing/{folderKey.Replace('\\', '/')}";
                summary = TryResolveCenterRepoReadmeSummary(repoRelativePath);
            }

            return summary;
        });

        var pathingSearchText = BuildPathingScriptSearchText(file, fileName, relFolder);
        var mergedSearchText = NormalizeSearchText(
            string.Join(" ", new[] { pathingSearchText, readmeSummary }.Where(v => !string.IsNullOrWhiteSpace(v))),
            MaxPathingSummaryLength);

        info = new PathingScriptInfo
        {
            Name = fileName,
            Folder = relFolder,
            Description = NormalizeReadmeSummary(readmeSummary),
            SearchText = mergedSearchText
        };
        return true;
    }
}

internal sealed class McpMessageReader
{
    private readonly Stream _stream;
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxPayloadBytes = 64 * 1024 * 1024;

    public McpMessageReader(Stream stream)
    {
        _stream = stream;
    }

    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        var headerBytes = new MemoryStream();
        var buffer = new byte[1];
        while (true)
        {
            var read = await _stream.ReadAsync(buffer, 0, 1, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            headerBytes.WriteByte(buffer[0]);
            if (headerBytes.Length > MaxHeaderBytes)
            {
                throw new InvalidOperationException($"MCP header too large (>{MaxHeaderBytes} bytes).");
            }

            if (headerBytes.Length < 4)
            {
                continue;
            }

            var data = headerBytes.GetBuffer();
            var len = (int)headerBytes.Length;
            if (data[len - 4] == '\r' && data[len - 3] == '\n' && data[len - 2] == '\r' && data[len - 1] == '\n')
            {
                break;
            }
        }

        var headerText = Encoding.ASCII.GetString(headerBytes.GetBuffer(), 0, (int)headerBytes.Length - 4);
        var contentLength = ParseContentLength(headerText);
        if (contentLength <= 0)
        {
            return null;
        }

        if (contentLength > MaxPayloadBytes)
        {
            throw new InvalidOperationException($"MCP payload too large ({contentLength} bytes, max {MaxPayloadBytes} bytes).");
        }

        var payload = new byte[contentLength];
        var offset = 0;
        while (offset < contentLength)
        {
            var read = await _stream.ReadAsync(payload, offset, contentLength - offset, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            offset += read;
        }

        return Encoding.UTF8.GetString(payload, 0, payload.Length);
    }

    private static int ParseContentLength(string headerText)
    {
        var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var parts = line.Split(':', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            if (!parts[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(parts[1].Trim(), out var len))
            {
                return len;
            }
        }

        return -1;
    }
}

internal sealed class McpMessageWriter
{
    private readonly Stream _stream;

    public McpMessageWriter(Stream stream)
    {
        _stream = stream;
    }

    public Task WriteResultAsync(object? id, object result, CancellationToken ct)
    {
        var payload = new
        {
            jsonrpc = "2.0",
            id,
            result
        };

        return WritePayloadAsync(payload, ct);
    }

    public Task WriteErrorAsync(object? id, int code, string message, CancellationToken ct)
    {
        var payload = new
        {
            jsonrpc = "2.0",
            id,
            error = new
            {
                code,
                message
            }
        };

        return WritePayloadAsync(payload, ct);
    }

    private async Task WritePayloadAsync(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, McpRequestHandler.JsonOptions);
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _stream.WriteAsync(header, 0, header.Length, ct);
        await _stream.WriteAsync(body, 0, body.Length, ct);
        await _stream.FlushAsync(ct);
    }
}
