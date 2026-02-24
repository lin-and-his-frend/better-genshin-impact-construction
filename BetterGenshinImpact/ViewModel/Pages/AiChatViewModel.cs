using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Helpers.Win32;
using BetterGenshinImpact.Model.Ai;
using BetterGenshinImpact.Service.Ai;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.Service.Remote;
using BetterGenshinImpact.View.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterGenshinImpact.ViewModel.Pages;

public partial class AiChatViewModel : ViewModel
{
    private const string DefaultSystemPrompt =
        "你是 BetterGI 内置 AI 助手。你可以通过 MCP 工具读取或控制软件状态。\n" +
        "当你需要调用 MCP 工具时，仅输出一个或多个 ```mcp 代码块，每个代码块是 JSON：\n" +
        "{\"name\":\"工具名\",\"arguments\":{...}}。\n" +
        "不要在同一回复中输出其他文字。收到 MCP_RESULT 后再用自然语言回复用户。\n" +
        "当你遇到原神专有名词/机制/角色/武器/圣遗物等不确定含义时，优先调用 bgi.web.search 联网搜索（query 建议包含“原神”以便消歧，maxResults 建议 3-5）。\n" +
        "如果 MCP 返回 web search disabled，提示用户到 设置 -> MCP 接口 开启“允许 MCP 联网搜索”。\n" +
        "当用户要查找脚本或名称不明确时，优先调用 bgi.script.search 并提供 query 关键词，避免返回大量脚本。\n" +
        "当用户表达采集材料、跑图、刷怪、打怪、讨伐等需求时，优先调用 bgi.script.search，且 arguments.type 必须设为 pathing。\n" +
        "调用 bgi.script.run 时，必须直接复制 bgi.script.search 返回的 name 原文，不要翻译、音译或改写文件名。\n" +
        "不要在没有 query 的情况下调用 bgi.script.list。\n" +
        "用户提到“一条龙”相关操作时使用 bgi.one_dragon.list / bgi.one_dragon.run。\n" +
        "调用 bgi.set_features 时 arguments 必须至少包含一个字段，值为 true/false，不要发送空对象或 null。\n" +
        "示例：{\"name\":\"bgi.set_features\",\"arguments\":{\"autoPick\":true}}。\n" +
        "用户说“关闭/关掉/禁用/停用”时应将对应字段设为 false；“打开/开启/启用/启动”时设为 true。\n" +
        "如果用户只说“关闭/打开”但未明确功能，先追问，不要调用工具。\n" +
        "用户要求“查询状态/查看开关”时必须调用 bgi.get_features。\n" +
        "用户说“全部/所有实时功能/全部开关/全开/全关”时，调用 bgi.set_features 并同时设置全部字段（autoPick/autoSkip/autoFishing/autoCook/autoEat/quickTeleport/mapMask）。\n" +
        "MCP 工具返回会以 \"MCP_RESULT:\" 开头的系统消息提供给你。";

    private const int MaxAutoToolRounds = 3;
    private const int MaxAutoToolCallsPerRound = 5;
    private const int DefaultMaxContextChars = 80000;
    private const int DefaultMaxMcpResultChars = 8000;
    private const int DefaultMaxChatMessageChars = 20000;
    private static readonly TimeSpan DefaultAiRequestTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DefaultMcpRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex McpBlockRegex = new(@"```mcp\s*(?<json>[\s\S]*?)```", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UnicodeEscapeRegex = new(@"\\u(?<hex>[0-9a-fA-F]{4})", RegexOptions.Compiled);
    private static readonly Regex ScriptNameHintRegex = new(@"[A-Za-z]+\d+|\d{2,}|[\u4e00-\u9fff]{2,}", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions McpPrettyJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly JsonSerializerOptions McpCompactJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly TimeSpan ScriptCandidateCacheTtl = TimeSpan.FromMinutes(5);

    private readonly AiChatService _chatService;
    private readonly McpLocalClient _mcpClient;
    private string? _lastFeatureFocus;
    private List<string> _recentScriptCandidates = [];
    private DateTimeOffset _recentScriptCandidatesUpdatedUtc = DateTimeOffset.MinValue;

    private static readonly (string Key, string[] Aliases)[] FeatureAliases =
    [
        ("autoPick", new[] { "自动拾取", "拾取" }),
        ("autoSkip", new[] { "自动剧情", "剧情跳过", "跳过剧情", "自动跳过" }),
        ("autoFishing", new[] { "自动钓鱼", "钓鱼" }),
        ("autoCook", new[] { "自动烹饪", "烹饪" }),
        ("autoEat", new[] { "自动吃药", "吃药" }),
        ("quickTeleport", new[] { "快捷传送", "快速传送", "快传" }),
        ("mapMask", new[] { "地图遮罩", "遮罩" })
    ];

    private static readonly string[] AllFeatureKeywords = ["全部", "所有", "全关", "全开", "全都", "一键"];
    private static readonly string[] FeatureScopeKeywords = ["功能", "开关", "实时", "触发", "配置", "自动"];
    private static readonly string[] StatusKeywords = ["状态", "配置", "开关", "是否"];
    private static readonly string[] StatusVerbs = ["查询", "查看", "检查", "确认", "了解"];
    private static readonly string[] PathingPriorityKeywords =
    [
        "采集", "收集", "跑图", "路线", "点位", "材料", "特产", "挖矿", "矿", "薄荷",
        "打怪", "刷怪", "清怪", "击杀", "讨伐", "怪物", "精英怪", "boss", "BOSS"
    ];
    private static readonly string[] PathingQueryNoisePhrases =
    [
        "我想要", "我想", "帮我", "请帮我", "麻烦", "请", "可以", "能不能", "一下", "帮忙",
        "自动", "脚本", "用脚本", "用地图追踪", "地图追踪", "运行", "执行", "安排"
    ];

    public AiConfig Config { get; }

    public ObservableCollection<AiChatMessage> Messages { get; } = new();

    public ObservableCollection<McpToolInfo> McpTools { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CallMcpToolCommand))]
    private McpToolInfo? _selectedMcpTool;

    [ObservableProperty]
    private string _mcpArguments = "{}";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CallMcpToolCommand))]
    private bool _mcpBusy;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private string _mcpStatus = "内置 MCP 未连接";

    public AiChatViewModel(IConfigService configService, AiChatService chatService, McpLocalClient mcpClient)
    {
        _chatService = chatService;
        _mcpClient = mcpClient;
        Config = configService.Get().AiConfig;
    }

    public override async Task OnNavigatedToAsync()
    {
        await RefreshMcpToolsAsync();
        await base.OnNavigatedToAsync();
    }

    private bool CanSendMessage()
    {
        return !IsBusy && !string.IsNullOrWhiteSpace(InputText);
    }

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task SendMessageAsync()
    {
        var content = InputText.Trim();
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        InputText = string.Empty;
        AddChatMessage("user", content);
        LogChat("user", content);
        UpdateFeatureFocusFromText(content);

        IsBusy = true;
        StatusText = "AI 正在思考...";
        try
        {
            var payloadMessages = BuildPayloadMessages();
            var reply = await GetAiReplyAsync(payloadMessages);
            LogChat("assistant_raw", reply);
            var round = 0;
            var toolCalls = ParseMcpToolCalls(reply);
            toolCalls = CoerceRealtimeFeatureToolCalls(content, toolCalls, out var coerceNotice);
            if (!string.IsNullOrWhiteSpace(coerceNotice))
            {
                AddChatMessage("system", coerceNotice);
                LogChat("system", coerceNotice);
            }

            toolCalls = CoercePathingPriorityToolCalls(content, toolCalls, out var pathingNotice);
            if (!string.IsNullOrWhiteSpace(pathingNotice))
            {
                AddChatMessage("system", pathingNotice);
                LogChat("system", pathingNotice);
            }

            toolCalls = LimitToolCalls(toolCalls, out var toolLimitNotice);
            if (!string.IsNullOrWhiteSpace(toolLimitNotice))
            {
                AddChatMessage("system", toolLimitNotice);
                LogChat("system", toolLimitNotice);
            }

            toolCalls = ExpandScriptRunCallsForSerial(toolCalls, out var serialRunNotice);
            if (!string.IsNullOrWhiteSpace(serialRunNotice))
            {
                AddChatMessage("system", serialRunNotice);
                LogChat("system", serialRunNotice);
            }

            if (toolCalls.Count == 0 && TryBuildFallbackToolCalls(content, out var fallbackCalls))
            {
                toolCalls = fallbackCalls;
                LogChat("system", $"检测到明确操作意图，已自动调用 {fallbackCalls[0].Name}。");
            }

            while (toolCalls.Count > 0 && round < MaxAutoToolRounds)
            {
                await ExecuteMcpToolCallsAsync(toolCalls);
                round++;

                StatusText = "AI 正在思考...";
                payloadMessages = BuildPayloadMessages();
                reply = await GetAiReplyAsync(payloadMessages);
                LogChat("assistant_raw", reply);
                toolCalls = ParseMcpToolCalls(reply);
                toolCalls = CoerceRealtimeFeatureToolCalls(content, toolCalls, out coerceNotice);
                if (!string.IsNullOrWhiteSpace(coerceNotice))
                {
                    AddChatMessage("system", coerceNotice);
                    LogChat("system", coerceNotice);
                }

                toolCalls = CoercePathingPriorityToolCalls(content, toolCalls, out pathingNotice);
                if (!string.IsNullOrWhiteSpace(pathingNotice))
                {
                    AddChatMessage("system", pathingNotice);
                    LogChat("system", pathingNotice);
                }

                toolCalls = LimitToolCalls(toolCalls, out toolLimitNotice);
                if (!string.IsNullOrWhiteSpace(toolLimitNotice))
                {
                    AddChatMessage("system", toolLimitNotice);
                    LogChat("system", toolLimitNotice);
                }

                toolCalls = ExpandScriptRunCallsForSerial(toolCalls, out serialRunNotice);
                if (!string.IsNullOrWhiteSpace(serialRunNotice))
                {
                    AddChatMessage("system", serialRunNotice);
                    LogChat("system", serialRunNotice);
                }
            }

            if (toolCalls.Count > 0)
            {
                var notice = "MCP 自动调用次数达到上限，已停止自动调用。";
                AddChatMessage("system", notice);
                LogChat("system", notice);
            }

            reply = SanitizeAssistantReply(reply);
            if (string.IsNullOrWhiteSpace(reply))
            {
                reply = "（无回复）";
            }

            AddChatMessage("assistant", reply);
            LogChat("assistant", reply);
            StatusText = "完成";
        }
        catch (OperationCanceledException)
        {
            var notice = "请求超时，请检查 AI 接口是否可用，或稍后重试。";
            AddChatMessage("system", notice);
            LogChat("system", notice);
            StatusText = "请求超时";
        }
        catch (Exception ex)
        {
            var notice = $"请求失败: {ex.Message}";
            AddChatMessage("system", notice);
            LogChat("system", notice);
            StatusText = "请求失败";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var window = new AiSettingsWindow
        {
            Owner = UIDispatcherHelper.MainWindow,
            DataContext = this
        };
        window.ShowDialog();
    }

    [RelayCommand]
    private async Task RefreshMcpToolsAsync()
    {
        McpBusy = true;
        McpStatus = "正在连接内置 MCP...";
        try
        {
            using var cts = new CancellationTokenSource(DefaultMcpRequestTimeout);
            var tools = await _mcpClient.ListToolsAsync(cts.Token);
            McpTools.Clear();
            foreach (var tool in tools)
            {
                McpTools.Add(tool);
            }

            SelectedMcpTool ??= McpTools.Count > 0 ? McpTools[0] : null;
            McpStatus = $"已连接 · {McpTools.Count} 个工具";
        }
        catch (Exception ex)
        {
            McpStatus = $"连接失败: {ex.Message}";
        }
        finally
        {
            McpBusy = false;
        }
    }

    private bool CanCallMcpTool()
    {
        return !McpBusy && SelectedMcpTool != null;
    }

    [RelayCommand(CanExecute = nameof(CanCallMcpTool))]
    private async Task CallMcpToolAsync()
    {
        if (SelectedMcpTool == null)
        {
            return;
        }

        McpBusy = true;
        StatusText = $"调用 MCP: {SelectedMcpTool.Name}";
        try
        {
            using var cts = new CancellationTokenSource(DefaultMcpRequestTimeout);
            var argumentsJson = SelectedMcpTool.Name.Equals("bgi.capture_screen", StringComparison.OrdinalIgnoreCase)
                ? EnsureCaptureScreenArguments(McpArguments)
                : McpArguments;
            var result = await _mcpClient.CallToolAsync(SelectedMcpTool.Name, argumentsJson, cts.Token);
            var prefix = result.IsError ? "调用失败" : "调用成功";
            var formattedResult = FormatMcpResultForDisplay(result.Content);
            AddChatMessage("mcp", $"{prefix} · {SelectedMcpTool.Name}\n{formattedResult}", DefaultMaxChatMessageChars);
            StatusText = result.IsError ? "MCP 调用失败" : "MCP 调用完成";
        }
        catch (Exception ex)
        {
            AddChatMessage("system", $"MCP 调用失败: {ex.Message}");
            StatusText = "MCP 调用失败";
        }
        finally
        {
            McpBusy = false;
        }
    }

    private async Task ExecuteMcpToolCallsAsync(IReadOnlyList<McpToolCall> toolCalls)
    {
        if (toolCalls.Count == 0)
        {
            return;
        }

        McpBusy = true;
        try
        {
            foreach (var call in toolCalls)
            {
                StatusText = $"调用 MCP: {call.Name}";
                try
                {
                    var argumentsJson = call.ArgumentsJson;
                    if (string.Equals(call.Name, "bgi.set_features", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryNormalizeSetFeaturesArguments(argumentsJson, out argumentsJson, out var error))
                        {
                            var notice = $"MCP 调用失败: {error}";
                            AddChatMessage("system", notice);
                            LogChat("system", notice);
                            continue;
                        }
                    }
                    else if (string.Equals(call.Name, "bgi.capture_screen", StringComparison.OrdinalIgnoreCase))
                    {
                        argumentsJson = EnsureCaptureScreenArguments(argumentsJson);
                    }
                    else if (string.Equals(call.Name, "bgi.script.run", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryNormalizeScriptRunArguments(argumentsJson, out var normalizedRunArguments, out var runNotice))
                        {
                            argumentsJson = normalizedRunArguments;
                            if (!string.IsNullOrWhiteSpace(runNotice))
                            {
                                AddChatMessage("system", runNotice);
                                LogChat("system", runNotice);
                            }
                        }
                    }

                    using var cts = new CancellationTokenSource(DefaultMcpRequestTimeout);
                    var result = await _mcpClient.CallToolAsync(call.Name, argumentsJson, cts.Token);
                    var prefix = result.IsError ? "调用失败" : "调用成功";
                    var formattedResult = FormatMcpResultForDisplay(result.Content);
                    var message = $"{prefix} · {call.Name}\n{formattedResult}";
                    AddChatMessage("mcp", message, DefaultMaxChatMessageChars);
                    LogChat($"mcp:{call.Name}", message);
                    UpdateScriptCandidateCache(call.Name, result.Content);
                    UpdateFeatureFocusFromToolCall(call.Name, argumentsJson);
                }
                catch (Exception ex)
                {
                    var notice = $"MCP 调用失败: {ex.Message}";
                    AddChatMessage("system", notice);
                    LogChat("system", notice);
                }
            }
        }
        finally
        {
            McpBusy = false;
        }
    }

    private IReadOnlyList<McpToolCall> ParseMcpToolCalls(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return Array.Empty<McpToolCall>();
        }

        var matches = McpBlockRegex.Matches(reply);
        if (matches.Count == 0)
        {
            return ParseFallbackToolCalls(reply);
        }

        var calls = new List<McpToolCall>(matches.Count);
        foreach (Match match in matches)
        {
            var jsonText = match.Groups["json"].Value.Trim();
            if (string.IsNullOrWhiteSpace(jsonText))
            {
                continue;
            }

            if (TryParseToolCallJson(jsonText, out var call))
            {
                calls.Add(call!);
            }
        }

        return calls;
    }

    private void UpdateScriptCandidateCache(string toolName, string content)
    {
        if (!string.Equals(toolName, "bgi.script.search", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(toolName, "bgi.script.list", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var names = TryExtractScriptNames(content);
        if (names.Count == 0)
        {
            return;
        }

        _recentScriptCandidates = names;
        _recentScriptCandidatesUpdatedUtc = DateTimeOffset.UtcNow;
    }

    private static List<string> TryExtractScriptNames(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        try
        {
            var normalized = DecodeUnicodeEscapes(content).Trim();
            if (JsonNode.Parse(normalized) is not JsonObject obj)
            {
                return [];
            }

            if (!obj.TryGetPropertyValue("matches", out var matchesNode) || matchesNode is not JsonArray matches)
            {
                return [];
            }

            var names = new List<string>(matches.Count);
            foreach (var match in matches)
            {
                if (match is not JsonObject matchObj)
                {
                    continue;
                }

                if (!matchObj.TryGetPropertyValue("name", out var nameNode) || nameNode == null || nameNode.GetValueKind() != JsonValueKind.String)
                {
                    continue;
                }

                var name = nameNode.GetValue<string>()?.Trim();
                if (!string.IsNullOrWhiteSpace(name) &&
                    !names.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
                {
                    names.Add(name);
                }
            }

            return names;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private bool TryNormalizeScriptRunArguments(string argumentsJson, out string normalizedArgumentsJson, out string? notice)
    {
        normalizedArgumentsJson = argumentsJson;
        notice = null;
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return false;
        }

        var candidates = GetRecentScriptCandidates();
        if (candidates.Count == 0)
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(argumentsJson) is not JsonObject obj)
            {
                return false;
            }

            var changed = false;
            var replaced = new List<string>(2);

            if (obj.TryGetPropertyValue("name", out var nameNode) &&
                nameNode != null &&
                nameNode.GetValueKind() == JsonValueKind.String)
            {
                var original = nameNode.GetValue<string>()?.Trim();
                if (!string.IsNullOrWhiteSpace(original) &&
                    TryResolveScriptName(original, candidates, out var resolved) &&
                    !string.Equals(original, resolved, StringComparison.Ordinal))
                {
                    obj["name"] = resolved;
                    changed = true;
                    replaced.Add($"“{original}”→“{resolved}”");
                }
            }

            if (obj.TryGetPropertyValue("names", out var namesNode) && namesNode is JsonArray namesArray)
            {
                for (var i = 0; i < namesArray.Count; i++)
                {
                    if (namesArray[i] == null || namesArray[i]!.GetValueKind() != JsonValueKind.String)
                    {
                        continue;
                    }

                    var original = namesArray[i]!.GetValue<string>()?.Trim();
                    if (string.IsNullOrWhiteSpace(original) ||
                        !TryResolveScriptName(original, candidates, out var resolved) ||
                        string.Equals(original, resolved, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    namesArray[i] = resolved;
                    changed = true;
                    replaced.Add($"“{original}”→“{resolved}”");
                    if (replaced.Count >= 3)
                    {
                        break;
                    }
                }
            }

            if (!changed)
            {
                return false;
            }

            normalizedArgumentsJson = obj.ToJsonString();
            if (replaced.Count > 0)
            {
                notice = $"检测到脚本名与最近搜索结果不一致，已自动纠正：{string.Join("，", replaced)}。";
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private IReadOnlyList<string> GetRecentScriptCandidates()
    {
        if (_recentScriptCandidates.Count == 0)
        {
            return [];
        }

        if (DateTimeOffset.UtcNow - _recentScriptCandidatesUpdatedUtc > ScriptCandidateCacheTtl)
        {
            _recentScriptCandidates = [];
            return [];
        }

        return _recentScriptCandidates;
    }

    private static bool TryResolveScriptName(string name, IReadOnlyList<string> candidates, out string resolved)
    {
        resolved = name;
        if (candidates.Count == 0 || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();
        var exact = candidates.FirstOrDefault(candidate => string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exact))
        {
            resolved = exact;
            return true;
        }

        if (candidates.Count == 1)
        {
            resolved = candidates[0];
            return true;
        }

        var normalizedInput = NormalizeScriptNameKey(trimmed);
        if (normalizedInput.Length == 0)
        {
            return false;
        }

        var normalizedMatch = candidates.FirstOrDefault(candidate =>
            string.Equals(NormalizeScriptNameKey(candidate), normalizedInput, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(normalizedMatch))
        {
            resolved = normalizedMatch;
            return true;
        }

        var hints = ExtractScriptNameHints(trimmed);
        double bestScore = double.MinValue;
        double secondScore = double.MinValue;
        string? bestCandidate = null;
        foreach (var candidate in candidates)
        {
            var score = ComputeScriptNameScore(trimmed, normalizedInput, hints, candidate);
            if (score > bestScore)
            {
                secondScore = bestScore;
                bestScore = score;
                bestCandidate = candidate;
            }
            else if (score > secondScore)
            {
                secondScore = score;
            }
        }

        if (string.IsNullOrWhiteSpace(bestCandidate))
        {
            return false;
        }

        if (bestScore >= 9d && bestScore >= secondScore + 3d)
        {
            resolved = bestCandidate;
            return true;
        }

        return false;
    }

    private static double ComputeScriptNameScore(string rawInput, string normalizedInput, IReadOnlyList<string> hints, string candidate)
    {
        var score = 0d;
        var normalizedCandidate = NormalizeScriptNameKey(candidate);
        if (normalizedCandidate.Length == 0)
        {
            return score;
        }

        if (string.Equals(normalizedInput, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return 100d;
        }

        if (normalizedCandidate.Contains(normalizedInput, StringComparison.OrdinalIgnoreCase))
        {
            score += 12d;
        }
        else if (normalizedInput.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            score += 6d;
        }

        foreach (var hint in hints)
        {
            if (candidate.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                score += 5d;
                continue;
            }

            var normalizedHint = NormalizeScriptNameKey(hint);
            if (normalizedHint.Length > 0 &&
                normalizedCandidate.Contains(normalizedHint, StringComparison.OrdinalIgnoreCase))
            {
                score += 3d;
            }
        }

        if (rawInput.Length >= 2 &&
            rawInput.Contains('-', StringComparison.Ordinal) &&
            candidate.Contains('-', StringComparison.Ordinal))
        {
            score += 0.5d;
        }

        return score;
    }

    private static List<string> ExtractScriptNameHints(string text)
    {
        var hints = new List<string>(4);
        if (string.IsNullOrWhiteSpace(text))
        {
            return hints;
        }

        foreach (Match match in ScriptNameHintRegex.Matches(text))
        {
            var value = match.Value.Trim();
            if (value.Length < 2 || hints.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            hints.Add(value);
            if (hints.Count >= 6)
            {
                break;
            }
        }

        return hints;
    }

    private static string NormalizeScriptNameKey(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || IsCjk(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static bool IsCjk(char c)
    {
        return c is >= '\u3400' and <= '\u9FFF';
    }

    private IReadOnlyList<McpToolCall> ParseFallbackToolCalls(string reply)
    {
        var trimmed = reply.Trim();
        if (TryParseToolCallJson(trimmed, out var directCall))
        {
            return new[] { directCall! };
        }

        var jsonCalls = ExtractJsonToolCalls(trimmed);
        if (jsonCalls.Count > 0)
        {
            return jsonCalls;
        }

        var toolNames = BuildToolNameSet();

        var lines = trimmed.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var calls = new List<McpToolCall>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("MCP_RESULT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("• "))
            {
                line = line[2..].TrimStart();
            }

            if (TryParseInlineToolCall(line, toolNames, out var inlineCall))
            {
                calls.Add(inlineCall!);
                continue;
            }

            if (TryParseEmbeddedToolCall(line, toolNames, out var embeddedName, out var embeddedArgs))
            {
                if (!string.IsNullOrWhiteSpace(embeddedArgs))
                {
                    calls.Add(new McpToolCall(embeddedName!, embeddedArgs));
                    continue;
                }

                var j = i + 1;
                while (j < lines.Length && string.IsNullOrWhiteSpace(lines[j]))
                {
                    j++;
                }

                if (j < lines.Length)
                {
                    var argLine = lines[j].Trim();
                    if (argLine.StartsWith("{", StringComparison.Ordinal) && argLine.EndsWith("}", StringComparison.Ordinal))
                    {
                        calls.Add(new McpToolCall(embeddedName!, argLine));
                        i = j;
                        continue;
                    }
                }

                calls.Add(new McpToolCall(embeddedName!, "{}"));
                continue;
            }

            if (IsToolName(line, toolNames))
            {
                var argsJson = "{}";
                var j = i + 1;
                while (j < lines.Length && string.IsNullOrWhiteSpace(lines[j]))
                {
                    j++;
                }

                if (j < lines.Length)
                {
                    var nextLine = NormalizeJsonPrefix(lines[j].Trim());
                    if (string.Equals(nextLine, "json", StringComparison.OrdinalIgnoreCase) && j + 1 < lines.Length)
                    {
                        nextLine = NormalizeJsonPrefix(lines[j + 1].Trim());
                    }

                    if (nextLine.StartsWith("{", StringComparison.Ordinal) && nextLine.EndsWith("}", StringComparison.Ordinal))
                    {
                        argsJson = nextLine;
                    }
                }

                calls.Add(new McpToolCall(line, argsJson));
                continue;
            }

            if (TryParseToolLine(line, toolNames, out var call))
            {
                calls.Add(call!);
            }
        }

        return calls;
    }

    private IReadOnlyList<McpToolCall> ExtractJsonToolCalls(string reply)
    {
        var calls = new List<McpToolCall>();
        foreach (var json in ExtractJsonObjects(reply))
        {
            if (TryParseToolCallJson(json, out var call))
            {
                calls.Add(call!);
            }
        }

        return calls;
    }

    private static bool TryParseToolLine(string line, HashSet<string> toolNames, out McpToolCall? call)
    {
        call = null;
        if (IsToolName(line, toolNames))
        {
            call = new McpToolCall(line, "{}");
            return true;
        }

        foreach (var name in toolNames)
        {
            if (!line.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = line[name.Length..].Trim();
            if (rest.Length == 0)
            {
                call = new McpToolCall(name, "{}");
                return true;
            }

            if (rest.StartsWith(":"))
            {
                rest = rest[1..].Trim();
            }

            if (rest.StartsWith("{") && rest.EndsWith("}"))
            {
                call = new McpToolCall(name, rest);
                return true;
            }
        }

        return false;
    }

    private static bool TryParseInlineToolCall(string line, HashSet<string> toolNames, out McpToolCall? call)
    {
        call = null;
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("bgi.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var firstBrace = trimmed.IndexOf('{');
        var firstSpace = trimmed.IndexOf(' ');
        var firstColon = trimmed.IndexOf(':');

        var cut = trimmed.Length;
        if (firstBrace >= 0)
        {
            cut = Math.Min(cut, firstBrace);
        }

        if (firstSpace >= 0)
        {
            cut = Math.Min(cut, firstSpace);
        }

        if (firstColon >= 0)
        {
            cut = Math.Min(cut, firstColon);
        }

        var name = trimmed.Substring(0, cut).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (toolNames.Count > 0 && !toolNames.Contains(name))
        {
            return false;
        }

        var rest = trimmed[cut..].Trim();
        if (rest.StartsWith(":"))
        {
            rest = rest[1..].Trim();
        }

        if (rest.Length == 0)
        {
            return false;
        }

        if (rest.StartsWith("{", StringComparison.Ordinal) && rest.EndsWith("}", StringComparison.Ordinal))
        {
            call = new McpToolCall(name, rest);
            return true;
        }

        return false;
    }

    private static bool TryParseEmbeddedToolCall(string line, HashSet<string> toolNames, out string? name, out string? argumentsJson)
    {
        name = null;
        argumentsJson = null;

        var searchIndex = 0;
        while (true)
        {
            var index = line.IndexOf("bgi.", searchIndex, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            var slice = line[index..].TrimStart();
            if (TryParseInlineToolCall(slice, toolNames, out var call))
            {
                name = call!.Name;
                argumentsJson = call.ArgumentsJson;
                return true;
            }

            var end = index;
            while (end < line.Length && !char.IsWhiteSpace(line[end]) && line[end] != ':' && line[end] != '，' && line[end] != '。' && line[end] != ';')
            {
                end++;
            }

            var candidate = line.Substring(index, end - index).TrimEnd('.', '。', '，', ';');
            if (!string.IsNullOrWhiteSpace(candidate) && (toolNames.Count == 0 || toolNames.Contains(candidate)))
            {
                name = candidate;
                return true;
            }

            searchIndex = index + 4;
        }
    }

    private static bool IsToolName(string line, HashSet<string> toolNames)
    {
        if (toolNames.Contains(line))
        {
            return true;
        }

        if (!line.StartsWith("bgi.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (line.Contains(' ') || line.Contains('{') || line.Contains(':'))
        {
            return false;
        }

        return true;
    }

    private bool TryParseToolCallJson(string jsonText, out McpToolCall? call)
    {
        call = null;
        var normalized = NormalizeJsonPrefix(jsonText);
        if (string.IsNullOrWhiteSpace(normalized) || !normalized.TrimStart().StartsWith("{"))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(normalized);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!doc.RootElement.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var argumentsJson = "{}";
            if (doc.RootElement.TryGetProperty("arguments", out var argsElement))
            {
                argumentsJson = argsElement.GetRawText();
            }

            call = new McpToolCall(name!, argumentsJson);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryNormalizeSetFeaturesArguments(string argumentsJson, out string normalizedJson, out string? error)
    {
        normalizedJson = argumentsJson;
        error = null;

        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            error = "bgi.set_features arguments 为空";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            var filtered = new JsonObject();
            var hasBoolean = false;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                {
                    filtered[prop.Name] = prop.Value.GetBoolean();
                    hasBoolean = true;
                }
            }

            if (filtered.Count == 0 || !hasBoolean)
            {
                error = "bgi.set_features 仅支持布尔字段（autoPick/autoSkip/autoFishing/autoCook/autoEat/quickTeleport/mapMask）";
                normalizedJson = "{}";
                return false;
            }

            normalizedJson = filtered.ToJsonString();
            return true;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private bool TryBuildFallbackToolCalls(string userText, out IReadOnlyList<McpToolCall> calls)
    {
        calls = Array.Empty<McpToolCall>();
        if (IsPathingPriorityIntent(userText))
        {
            var query = BuildPathingSearchQuery(userText);
            if (!string.IsNullOrWhiteSpace(query))
            {
                var searchArgs = new JsonObject
                {
                    ["query"] = query,
                    ["type"] = "pathing",
                    ["limit"] = 5
                };
                calls = new[] { new McpToolCall("bgi.script.search", searchArgs.ToJsonString()) };
                return true;
            }
        }

        if (IsStatusQuery(userText))
        {
            calls = new[] { new McpToolCall("bgi.get_features", "{}") };
            return true;
        }

        if (!TryParseDesiredValue(userText, out var value))
        {
            return false;
        }

        if (IsAllFeaturesRequest(userText))
        {
            var allArgs = BuildAllFeaturesArgs(value);
            calls = new[] { new McpToolCall("bgi.set_features", allArgs.ToJsonString()) };
            return true;
        }

        var featureKey = TryGetFeatureKey(userText);
        if (featureKey == null)
        {
            featureKey = _lastFeatureFocus;
        }

        if (string.IsNullOrWhiteSpace(featureKey))
        {
            return false;
        }

        var featureArgs = new JsonObject
        {
            [featureKey] = value
        };

        calls = new[] { new McpToolCall("bgi.set_features", featureArgs.ToJsonString()) };
        return true;
    }

    private static bool IsStatusQuery(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (TryParseDesiredValue(text, out _) && !ContainsQuestionMarker(text))
        {
            return false;
        }

        foreach (var keyword in StatusKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var hasVerb = false;
        foreach (var verb in StatusVerbs)
        {
            if (text.Contains(verb, StringComparison.OrdinalIgnoreCase))
            {
                hasVerb = true;
                break;
            }
        }

        if (!hasVerb)
        {
            return false;
        }

        if (TryGetFeatureKey(text) != null)
        {
            return true;
        }

        foreach (var scope in FeatureScopeKeywords)
        {
            if (text.Contains(scope, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsQuestionMarker(string text)
    {
        return text.Contains("吗", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("？", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("?", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("是否", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("有没有", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("是不是", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("可否", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("能否", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("请问", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllFeaturesRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Contains("全开", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("全关", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hasAll = false;
        foreach (var keyword in AllFeatureKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                hasAll = true;
                break;
            }
        }

        if (!hasAll)
        {
            return false;
        }

        if (text.Contains("实时触发", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var scope in FeatureScopeKeywords)
        {
            if (text.Contains(scope, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (TryGetFeatureKey(text) != null)
        {
            return true;
        }

        return true;
    }

    private static bool IsRealtimeFeatureQuery(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("实时触发", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("实时功能", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("实时开关", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("实时配置", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathingPriorityIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (IsStatusQuery(text))
        {
            return false;
        }

        if (TryParseDesiredValue(text, out _) && TryGetFeatureKey(text) != null)
        {
            return false;
        }

        foreach (var keyword in PathingPriorityKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildPathingSearchQuery(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var query = text.Trim();
        foreach (var noise in PathingQueryNoisePhrases)
        {
            query = query.Replace(noise, " ", StringComparison.OrdinalIgnoreCase);
        }

        query = Regex.Replace(query, @"[，。！？,.!?:：；;（）()\[\]{}""'`]+", " ");
        query = Regex.Replace(query, @"\s+", " ").Trim();
        if (query.Length > 48)
        {
            query = query[..48].Trim();
        }

        return query;
    }

    private IReadOnlyList<McpToolCall> CoerceRealtimeFeatureToolCalls(string userText, IReadOnlyList<McpToolCall> toolCalls, out string? notice)
    {
        notice = null;
        if (toolCalls.Count == 0 || !IsRealtimeFeatureQuery(userText))
        {
            return toolCalls;
        }

        foreach (var call in toolCalls)
        {
            if (!string.Equals(call.Name, "bgi.config.get", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryConfigPathContainsRealtime(call.ArgumentsJson))
            {
                notice = "已将实时触发查询改为 bgi.get_features，避免读取整套配置。";
                return new[] { new McpToolCall("bgi.get_features", "{}") };
            }
        }

        return toolCalls;
    }

    private IReadOnlyList<McpToolCall> CoercePathingPriorityToolCalls(string userText, IReadOnlyList<McpToolCall> toolCalls, out string? notice)
    {
        notice = null;
        if (toolCalls.Count == 0 || !IsPathingPriorityIntent(userText))
        {
            return toolCalls;
        }

        var hasScriptToolCall = false;
        foreach (var call in toolCalls)
        {
            if (string.Equals(call.Name, "bgi.script.search", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(call.Name, "bgi.script.list", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(call.Name, "bgi.script.run", StringComparison.OrdinalIgnoreCase))
            {
                hasScriptToolCall = true;
                break;
            }
        }

        if (!hasScriptToolCall)
        {
            var query = BuildPathingSearchQuery(userText);
            if (string.IsNullOrWhiteSpace(query))
            {
                query = userText.Trim();
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                var searchArgs = new JsonObject
                {
                    ["query"] = query,
                    ["type"] = "pathing",
                    ["limit"] = 5
                };
                notice = "检测到采集/打怪意图，已改为优先搜索 pathing（地图追踪）脚本。";
                return new[] { new McpToolCall("bgi.script.search", searchArgs.ToJsonString()) };
            }
        }

        var rewritten = new List<McpToolCall>(toolCalls.Count);
        var changed = false;
        foreach (var call in toolCalls)
        {
            if (string.Equals(call.Name, "bgi.script.search", StringComparison.OrdinalIgnoreCase))
            {
                if (TryForcePathingSearchArguments(call.ArgumentsJson, userText, out var normalizedArgs, out var argsChanged))
                {
                    rewritten.Add(new McpToolCall(call.Name, normalizedArgs));
                    changed |= argsChanged;
                }
                else
                {
                    rewritten.Add(call);
                }

                continue;
            }

            if (string.Equals(call.Name, "bgi.script.list", StringComparison.OrdinalIgnoreCase))
            {
                if (TryForcePathingListArguments(call.ArgumentsJson, userText, out var normalizedArgs, out var argsChanged))
                {
                    rewritten.Add(new McpToolCall(call.Name, normalizedArgs));
                    changed |= argsChanged;
                }
                else
                {
                    rewritten.Add(call);
                }

                continue;
            }

            if (string.Equals(call.Name, "bgi.script.run", StringComparison.OrdinalIgnoreCase))
            {
                if (TryForcePathingRunArguments(call.ArgumentsJson, out var normalizedArgs, out var argsChanged))
                {
                    rewritten.Add(new McpToolCall(call.Name, normalizedArgs));
                    changed |= argsChanged;
                }
                else
                {
                    rewritten.Add(call);
                }

                continue;
            }

            rewritten.Add(call);
        }

        if (!changed)
        {
            return toolCalls;
        }

        notice = "检测到采集/打怪意图，已优先限定为 pathing（地图追踪）脚本。";
        return rewritten;
    }

    private static bool TryForcePathingListArguments(string argumentsJson, string userText, out string normalizedJson, out bool changed)
    {
        changed = false;
        JsonObject args;
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            args = new JsonObject();
            changed = true;
        }
        else
        {
            try
            {
                var node = JsonNode.Parse(argumentsJson);
                if (node is not JsonObject obj)
                {
                    normalizedJson = argumentsJson;
                    return false;
                }

                args = obj;
            }
            catch (JsonException)
            {
                normalizedJson = argumentsJson;
                return false;
            }
        }

        if (!args.TryGetPropertyValue("type", out var typeNode) ||
            typeNode == null ||
            typeNode.GetValueKind() == JsonValueKind.Null)
        {
            args["type"] = "pathing";
            changed = true;
        }
        else if (typeNode.GetValueKind() == JsonValueKind.String)
        {
            var currentType = typeNode.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(currentType) ||
                currentType.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                currentType.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                !currentType.Equals("pathing", StringComparison.OrdinalIgnoreCase))
            {
                args["type"] = "pathing";
                changed = true;
            }
        }
        else
        {
            args["type"] = "pathing";
            changed = true;
        }

        if (!args.TryGetPropertyValue("query", out var queryNode) ||
            queryNode == null ||
            queryNode.GetValueKind() != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(queryNode.GetValue<string>()))
        {
            var queryText = BuildPathingSearchQuery(userText);
            if (!string.IsNullOrWhiteSpace(queryText))
            {
                args["query"] = queryText;
                changed = true;
            }
        }

        normalizedJson = args.ToJsonString();
        return true;
    }

    private static bool TryForcePathingSearchArguments(string argumentsJson, string userText, out string normalizedJson, out bool changed)
    {
        changed = false;
        JsonObject args;
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            args = new JsonObject();
            changed = true;
        }
        else
        {
            try
            {
                var node = JsonNode.Parse(argumentsJson);
                if (node is not JsonObject obj)
                {
                    normalizedJson = argumentsJson;
                    return false;
                }

                args = obj;
            }
            catch (JsonException)
            {
                normalizedJson = argumentsJson;
                return false;
            }
        }

        if (!args.TryGetPropertyValue("type", out var typeNode) ||
            typeNode == null ||
            typeNode.GetValueKind() == JsonValueKind.Null)
        {
            args["type"] = "pathing";
            changed = true;
        }
        else if (typeNode.GetValueKind() == JsonValueKind.String)
        {
            var currentType = typeNode.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(currentType) ||
                currentType.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                currentType.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                !currentType.Equals("pathing", StringComparison.OrdinalIgnoreCase))
            {
                args["type"] = "pathing";
                changed = true;
            }
        }
        else
        {
            args["type"] = "pathing";
            changed = true;
        }

        var queryText = BuildPathingSearchQuery(userText);
        if (string.IsNullOrWhiteSpace(queryText))
        {
            queryText = userText?.Trim() ?? string.Empty;
        }

        if (!args.TryGetPropertyValue("query", out var queryNode) ||
            queryNode == null ||
            queryNode.GetValueKind() != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(queryNode.GetValue<string>()))
        {
            if (!string.IsNullOrWhiteSpace(queryText))
            {
                args["query"] = queryText;
                changed = true;
            }
        }

        if (!args.TryGetPropertyValue("limit", out var limitNode) ||
            limitNode == null ||
            limitNode.GetValueKind() == JsonValueKind.Null ||
            limitNode.GetValueKind() == JsonValueKind.Undefined)
        {
            args["limit"] = 5;
            changed = true;
        }

        normalizedJson = args.ToJsonString();
        return true;
    }

    private static bool TryForcePathingRunArguments(string argumentsJson, out string normalizedJson, out bool changed)
    {
        changed = false;
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            var args = new JsonObject
            {
                ["type"] = "pathing"
            };
            normalizedJson = args.ToJsonString();
            changed = true;
            return true;
        }

        try
        {
            var node = JsonNode.Parse(argumentsJson);
            if (node is not JsonObject obj)
            {
                normalizedJson = argumentsJson;
                return false;
            }

            if (!obj.TryGetPropertyValue("type", out var typeNode) ||
                typeNode == null ||
                typeNode.GetValueKind() == JsonValueKind.Null)
            {
                obj["type"] = "pathing";
                changed = true;
            }
            else if (typeNode.GetValueKind() == JsonValueKind.String)
            {
                var type = typeNode.GetValue<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(type) ||
                    type.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                    type.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    obj["type"] = "pathing";
                    changed = true;
                }
            }
            else
            {
                obj["type"] = "pathing";
                changed = true;
            }

            normalizedJson = obj.ToJsonString();
            return true;
        }
        catch (JsonException)
        {
            normalizedJson = argumentsJson;
            return false;
        }
    }

    private static bool TryConfigPathContainsRealtime(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!doc.RootElement.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var path = pathElement.GetString();
            return !string.IsNullOrWhiteSpace(path) &&
                   path.Contains("realtime", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return argumentsJson.Contains("realtime", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static JsonObject BuildAllFeaturesArgs(bool value)
    {
        var args = new JsonObject();
        foreach (var (key, _) in FeatureAliases)
        {
            args[key] = value;
        }

        return args;
    }

    private void UpdateFeatureFocusFromText(string text)
    {
        var key = TryGetFeatureKey(text);
        if (!string.IsNullOrWhiteSpace(key))
        {
            _lastFeatureFocus = key;
        }
    }

    private void UpdateFeatureFocusFromToolCall(string name, string argumentsJson)
    {
        if (!string.Equals(name, "bgi.set_features", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                {
                    _lastFeatureFocus = prop.Name;
                    return;
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    private static string? TryGetFeatureKey(string text)
    {
        foreach (var (key, aliases) in FeatureAliases)
        {
            foreach (var alias in aliases)
            {
                if (text.Contains(alias, StringComparison.OrdinalIgnoreCase))
                {
                    return key;
                }
            }
        }

        return null;
    }

    private static bool TryParseDesiredValue(string text, out bool value)
    {
        if (text.Contains("关闭", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("关掉", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("禁用", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("停用", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        if (text.Contains("打开", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("开启", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("启用", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("启动", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        value = false;
        return false;
    }

    private string SanitizeAssistantReply(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return reply;
        }

        var toolNames = BuildToolNameSet();
        var lines = reply.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var sanitized = new List<string>(lines.Length);
        var skipNextJsonArgs = false;

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                if (!skipNextJsonArgs)
                {
                    sanitized.Add(raw);
                }
                continue;
            }

            if (trimmed.Contains("MCP_RESULT", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("MCP 结果", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("MCP结果", StringComparison.OrdinalIgnoreCase))
            {
                skipNextJsonArgs = true;
                continue;
            }

            if ((trimmed.StartsWith("调用成功", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.StartsWith("调用失败", StringComparison.OrdinalIgnoreCase)) &&
                trimmed.Contains("bgi.", StringComparison.OrdinalIgnoreCase))
            {
                skipNextJsonArgs = true;
                continue;
            }

            if (skipNextJsonArgs && IsStandaloneJsonArguments(trimmed))
            {
                skipNextJsonArgs = false;
                continue;
            }

            skipNextJsonArgs = false;

            var cleaned = StripMcpResultMarkers(trimmed);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                continue;
            }

            if (TryParseInlineToolCall(cleaned, toolNames, out _) || IsToolName(cleaned, toolNames))
            {
                skipNextJsonArgs = true;
                continue;
            }

            if (TryParseToolCallJson(cleaned, out _))
            {
                continue;
            }

            sanitized.Add(cleaned == trimmed ? raw : cleaned);
        }

        return string.Join(Environment.NewLine, sanitized).Trim();
    }

    private static string StripMcpResultMarkers(string text)
    {
        var cleaned = text;
        cleaned = cleaned.Replace("MCP_RESULT", string.Empty, StringComparison.OrdinalIgnoreCase);
        cleaned = cleaned.Replace("MCP 结果", string.Empty, StringComparison.OrdinalIgnoreCase);
        cleaned = cleaned.Replace("MCP结果", string.Empty, StringComparison.OrdinalIgnoreCase);
        cleaned = cleaned.Trim();
        cleaned = cleaned.Trim('：', ':', '-', '·');
        return cleaned.Trim();
    }

    private static bool IsStandaloneJsonArguments(string text)
    {
        if (!text.StartsWith("{", StringComparison.Ordinal) || !text.EndsWith("}", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return !doc.RootElement.TryGetProperty("name", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void LogChat(string role, string content)
    {
        const int maxLength = 2000;
        var text = content ?? string.Empty;
        text = text.Replace("\r\n", "\n");
        if (text.Length > maxLength)
        {
            var suffix = $"... (truncated {text.Length - maxLength} chars)";
            ConsoleHelper.WriteLine($"[AI CHAT] {role}: {text.Substring(0, maxLength)}{suffix}");
            return;
        }

        ConsoleHelper.WriteLine($"[AI CHAT] {role}: {text}");
    }

    private static string TruncateForPayload(string content, int maxLength)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        if (maxLength <= 0)
        {
            return string.Empty;
        }

        if (content.Length <= maxLength)
        {
            return content;
        }

        var suffix = $"... (truncated {content.Length - maxLength} chars)";
        if (suffix.Length >= maxLength)
        {
            return content.Substring(0, maxLength);
        }

        var sliceLength = maxLength - suffix.Length;
        return content.Substring(0, sliceLength) + suffix;
    }

    private static string NormalizeJsonPrefix(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("json", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed[4..];
            if (rest.Length == 0)
            {
                return string.Empty;
            }

            rest = rest.TrimStart();
            if (rest.StartsWith(":"))
            {
                rest = rest[1..].TrimStart();
            }

            return rest;
        }

        return text;
    }

    private static IEnumerable<string> ExtractJsonObjects(string text)
    {
        var results = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return results;
        }

        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '{')
            {
                if (depth == 0)
                {
                    start = i;
                }

                depth++;
                continue;
            }

            if (c == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    results.Add(text.Substring(start, i - start + 1));
                    start = -1;
                }
            }
        }

        return results;
    }

    private HashSet<string> BuildToolNameSet()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in McpTools)
        {
            if (!string.IsNullOrWhiteSpace(tool.Name))
            {
                names.Add(tool.Name.Trim());
            }
        }

        return names;
    }

    private async Task<string> GetAiReplyAsync(IReadOnlyList<AiChatMessage> payloadMessages)
    {
        using var cts = new CancellationTokenSource(DefaultAiRequestTimeout);
        return await _chatService.GetChatCompletionAsync(Config, payloadMessages, cts.Token);
    }

    private void AddChatMessage(string role, string content, int maxChars = DefaultMaxChatMessageChars)
    {
        var safeContent = content ?? string.Empty;
        if (safeContent.Length > maxChars)
        {
            safeContent = TruncateForPayload(safeContent, maxChars);
        }

        Messages.Add(new AiChatMessage(role, safeContent));
    }

    private static IReadOnlyList<McpToolCall> LimitToolCalls(IReadOnlyList<McpToolCall> toolCalls, out string? notice)
    {
        notice = null;
        if (toolCalls.Count <= MaxAutoToolCallsPerRound)
        {
            return toolCalls;
        }

        notice = $"AI 返回了 {toolCalls.Count} 个 MCP 调用，为避免卡顿仅执行前 {MaxAutoToolCallsPerRound} 个。";
        var limited = new List<McpToolCall>(MaxAutoToolCallsPerRound);
        for (var i = 0; i < toolCalls.Count && i < MaxAutoToolCallsPerRound; i++)
        {
            limited.Add(toolCalls[i]);
        }

        return limited;
    }

    private static IReadOnlyList<McpToolCall> ExpandScriptRunCallsForSerial(IReadOnlyList<McpToolCall> toolCalls, out string? notice)
    {
        notice = null;
        if (toolCalls.Count == 0)
        {
            return toolCalls;
        }

        var expanded = new List<McpToolCall>(toolCalls.Count);
        var splitCount = 0;
        foreach (var call in toolCalls)
        {
            if (!string.Equals(call.Name, "bgi.script.run", StringComparison.OrdinalIgnoreCase))
            {
                expanded.Add(call);
                continue;
            }

            if (TrySplitScriptRunCall(call, out var splitCalls))
            {
                splitCount += splitCalls.Count;
                expanded.AddRange(splitCalls);
                continue;
            }

            expanded.Add(call);
        }

        if (splitCount == 0)
        {
            return toolCalls;
        }

        notice = $"检测到批量脚本执行，已改为串行逐个执行（共 {splitCount} 个）。";
        return expanded;
    }

    private static bool TrySplitScriptRunCall(McpToolCall call, out List<McpToolCall> splitCalls)
    {
        splitCalls = new List<McpToolCall>();
        if (string.IsNullOrWhiteSpace(call.ArgumentsJson))
        {
            return false;
        }

        try
        {
            var node = JsonNode.Parse(call.ArgumentsJson);
            if (node is not JsonObject obj)
            {
                return false;
            }

            if (!obj.TryGetPropertyValue("names", out var namesNode) || namesNode is not JsonArray namesArray || namesArray.Count <= 1)
            {
                return false;
            }

            string? type = null;
            if (obj.TryGetPropertyValue("type", out var typeNode) &&
                typeNode != null &&
                typeNode.GetValueKind() == JsonValueKind.String)
            {
                type = typeNode.GetValue<string>()?.Trim();
            }

            foreach (var item in namesArray)
            {
                if (item == null || item.GetValueKind() != JsonValueKind.String)
                {
                    continue;
                }

                var name = item.GetValue<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var singleArgs = new JsonObject
                {
                    ["name"] = name
                };
                if (!string.IsNullOrWhiteSpace(type))
                {
                    singleArgs["type"] = type;
                }

                splitCalls.Add(new McpToolCall(call.Name, singleArgs.ToJsonString()));
            }

            return splitCalls.Count > 1;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string EnsureCaptureScreenArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson.Trim() == "{}")
        {
            return "{\"includeData\":false}";
        }

        try
        {
            var node = JsonNode.Parse(argumentsJson);
            if (node is not JsonObject obj)
            {
                return "{\"includeData\":false}";
            }

            if (!obj.ContainsKey("includeData"))
            {
                obj["includeData"] = false;
            }

            return obj.ToJsonString();
        }
        catch
        {
            return "{\"includeData\":false}";
        }
    }

    private static string FormatMcpResultForDisplay(string content)
    {
        var normalized = DecodeUnicodeEscapes(content ?? string.Empty).Trim();
        if (TryFormatJson(normalized, true, out var formatted))
        {
            return formatted;
        }

        return normalized;
    }

    private static string FormatMcpResultForModel(string content)
    {
        var normalized = DecodeUnicodeEscapes(content ?? string.Empty).Trim();
        if (TryFormatJson(normalized, false, out var formatted))
        {
            return formatted;
        }

        return normalized;
    }

    private static string BuildMcpPayloadText(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var splitIndex = normalized.IndexOf('\n');
        if (splitIndex < 0)
        {
            return FormatMcpResultForModel(normalized);
        }

        var header = normalized[..splitIndex].Trim();
        var body = normalized[(splitIndex + 1)..].Trim();
        var compactBody = FormatMcpResultForModel(body);

        if (string.IsNullOrWhiteSpace(header))
        {
            return compactBody;
        }

        return string.IsNullOrWhiteSpace(compactBody)
            ? header
            : $"{header}\n{compactBody}";
    }

    private static bool TryFormatJson(string content, bool pretty, out string formatted)
    {
        formatted = content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var trimmed = content.Trim();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal) &&
            !trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var node = JsonNode.Parse(trimmed);
            if (node == null)
            {
                return false;
            }

            formatted = node.ToJsonString(pretty ? McpPrettyJsonOptions : McpCompactJsonOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string DecodeUnicodeEscapes(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || !content.Contains(@"\u", StringComparison.Ordinal))
        {
            return content;
        }

        return UnicodeEscapeRegex.Replace(content, static match =>
        {
            var hex = match.Groups["hex"].Value;
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint))
            {
                return match.Value;
            }

            if (codePoint is < 0 or > 0x10FFFF)
            {
                return match.Value;
            }

            return char.ConvertFromUtf32(codePoint);
        });
    }

    private IReadOnlyList<AiChatMessage> BuildPayloadMessages()
    {
        var maxContextChars = NormalizeMaxContextChars(Config.MaxContextChars);
        var systemPrompt = BuildSystemPrompt();
        systemPrompt = TruncateForPayload(systemPrompt, maxContextChars);

        var payload = new List<AiChatMessage>(Messages.Count + 2)
        {
            new("system", systemPrompt)
        };

        var remaining = maxContextChars - systemPrompt.Length;
        if (remaining <= 0)
        {
            return payload;
        }

        var mcpLimit = Math.Min(DefaultMaxMcpResultChars, maxContextChars);
        var tail = new List<AiChatMessage>(Messages.Count);

        for (var i = Messages.Count - 1; i >= 0 && remaining > 0; i--)
        {
            var message = Messages[i];
            string? role = null;
            string? content = null;

            if (message.IsMcp)
            {
                var compactMcpResult = BuildMcpPayloadText(message.Content);
                if (string.IsNullOrWhiteSpace(compactMcpResult))
                {
                    continue;
                }

                var trimmed = TruncateForPayload(compactMcpResult, Math.Min(mcpLimit, remaining));
                content = $"MCP_RESULT: {trimmed}";
                role = "system";
            }
            else if (message.IsUser || message.IsAssistant || message.IsSystem)
            {
                role = message.Role;
                content = message.Content;
            }

            if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            if (content.Length > remaining)
            {
                content = TruncateForPayload(content, remaining);
            }

            if (content.Length == 0)
            {
                continue;
            }

            tail.Add(new AiChatMessage(role, content));
            remaining -= content.Length;
        }

        tail.Reverse();
        payload.AddRange(tail);
        return payload;
    }

    private static int NormalizeMaxContextChars(int value)
    {
        return value > 0 ? value : DefaultMaxContextChars;
    }

    private string BuildSystemPrompt()
    {
        if (McpTools.Count == 0)
        {
            return DefaultSystemPrompt;
        }

        var builder = new StringBuilder(DefaultSystemPrompt);
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("可用 MCP 工具列表:");

        foreach (var tool in McpTools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name))
            {
                continue;
            }

            builder.Append("- ").Append(tool.Name);
            if (!string.IsNullOrWhiteSpace(tool.Description))
            {
                builder.Append(": ").Append(tool.Description.Trim());
            }

            builder.AppendLine();
            if (!string.IsNullOrWhiteSpace(tool.InputSchema))
            {
                builder.AppendLine($"  schema: {tool.InputSchema.Trim()}");
            }
        }

        return builder.ToString();
    }

    private sealed class McpToolCall
    {
        public McpToolCall(string name, string argumentsJson)
        {
            Name = name;
            ArgumentsJson = argumentsJson;
        }

        public string Name { get; }

        public string ArgumentsJson { get; }
    }
}
