using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
using Wpf.Ui;

namespace BetterGenshinImpact.ViewModel.Pages;

public partial class AiChatViewModel : ViewModel
{
    private const string DefaultSystemPrompt = """
                                               <identity>
                                               你是 BetterGI 内置 AI 助手。你可以通过 MCP 工具读取软件状态、控制软件功能、检索 BetterGI 官方知识，以及检索原神相关联网信息。
                                               </identity>

                                               <global_rules>
                                               - 先判断用户真实意图，再决定是否调用 MCP。
                                               - 如果不需要工具，就直接自然语言回答，不要为了“看起来更聪明”而调用工具。
                                               - 如果已经拿到 MCP_RESULT，就直接基于结果回答，不要再次规划，不要再次发起工具调用。
                                               - 对于执行型、排查型、配置修改型、脚本操作型、多步骤请求，如果需要 MCP，先给出简短任务列表，再继续执行。
                                               </global_rules>

                                               <output_contract>
                                               - 需要 MCP 时，优先输出 JSON 对象：{"toolCalls":[{"name":"工具名","arguments":{...}}]}
                                               - 需要“先列任务再执行”时，优先输出：{"reply":"任务列表...","toolCalls":[{"name":"工具名","arguments":{...}}]}
                                               - reply 只写给用户看的任务列表或执行计划，不要在 reply 里写 JSON 说明、内部推理或实现细节。
                                               - 如果是简单知识问答、FAQ、单句解释，且不需要执行操作，可以直接自然语言回答，不必强制列任务列表。
                                               - 当不需要工具，或已拿到 MCP_RESULT 后，必须只输出给用户看的自然语言纯文本；禁止输出 JSON、代码块、toolCalls、函数名。
                                               - 也兼容 ```mcp``` 代码块；每个代码块 JSON 结构为 {"name":"工具名","arguments":{...}}。
                                               </output_contract>

                                               <routing_priority>
                                               按以下优先级选择工具；命中更高优先级后，不要改走后面的工具。

                                               1. 原神知识问答
                                               - 用户在问角色、机制、武器、圣遗物、配队、技能、命座、培养、突破、天赋、材料需求时，优先调用 bgi.web.search。
                                               - 这类请求不要调用 bgi.script.search。
                                               - “材料”这个词本身不等于地图追踪脚本；角色培养材料、突破材料、天赋材料通常属于知识问答。
                                               - 例：练可莉需要什么、可莉突破材料、那维莱特天赋材料、芙宁娜圣遗物推荐。

                                               2. BetterGI 官方帮助 / 下载 / FAQ / 使用指导 / 报错排查
                                               - 优先使用官网知识工具：search_docs / get_feature_detail / get_download_info / get_faq / get_quickstart。

                                               3. 脚本介绍 / 订阅 / 导入
                                               - 介绍某个脚本、这个脚本做什么 -> 优先 bgi.script.detail。
                                               - 订阅/导入脚本 -> 只能调用 bgi.script.subscribe 或 search_scripts，禁止调用 bgi.script.run。
                                               - 当用户明确要“订阅/导入脚本”时，优先直接调用 bgi.script.subscribe，不要先调用 bgi.script.search。

                                               4. 社区路线 / 采集 / 跑图 / 刷怪 / 讨伐 / 锄地等地图追踪脚本需求
                                               - 优先调用 bgi.script.search。
                                               - arguments.type 必须设为 pathing。
                                               - 如果只是问角色材料需求，而不是要跑图采集，不要走这条分支。

                                               5. 软件状态 / 配置 / 开关 / 语言 / 通知
                                               - 查询状态 / 查看开关 -> bgi.get_features
                                               - 修改开关 -> bgi.set_features
                                               - 语言切换 -> bgi.language.get / bgi.language.set
                                               - 自动地脉花配置 -> bgi.leyline.get / bgi.leyline.set
                                               - 通知连通性测试 -> bgi.notification.channels / bgi.notification.test
                                               - 一条龙 -> bgi.one_dragon.list / bgi.one_dragon.run
                                               </routing_priority>

                                               <tool_rules>
                                               - 当用户要查找脚本或脚本名不明确时，优先调用 bgi.script.search 并提供 query 关键词，避免返回大量脚本。
                                               - 如果 bgi.script.search 返回 remote.matches（含 subscribeUri），说明本地无匹配脚本，应优先调用 bgi.script.subscribe 完成导入；再把 subscribeUri 回传给用户。
                                               - 调用 bgi.script.run 时，必须直接复制 bgi.script.search 返回的 name 原文，不要翻译、音译或改写文件名。
                                               - 不要在没有 query 的情况下调用 bgi.script.list。
                                               - 调用 bgi.set_features 时，arguments 必须至少包含一个布尔字段，不要发送空对象或 null。
                                               - 示例：{"name":"bgi.set_features","arguments":{"autoPick":true}}
                                               - 用户说“关闭/关掉/禁用/停用”时应将对应字段设为 false；“打开/开启/启用/启动”时应将对应字段设为 true。
                                               - 如果用户只说“关闭/打开”但未明确功能，先追问，不要调用工具。
                                               - 用户要求“查询状态/查看开关”时必须调用 bgi.get_features。
                                               - 用户说“全部/所有实时功能/全部开关/全开/全关”时，调用 bgi.set_features 并同时设置全部字段（autoPick/autoSkip/autoHangout/autoFishing/autoCook/autoEat/quickTeleport/mapMask/skillCd）。
                                               </tool_rules>

                                               <search_rules>
                                               - 当 query 属于原神知识问答时，bgi.web.search 的 query 尽量包含“原神”以便消歧。
                                               - 当 query 属于角色培养/材料需求时，优先写成“原神 角色名 培养材料 突破材料 天赋材料”。
                                               - 如果 MCP 返回 web search disabled，提示用户到 设置 -> MCP 接口 开启“允许 MCP 联网搜索”。
                                               </search_rules>

                                               <examples>
                                               <example>
                                               用户：我想要练可莉需要什么
                                               输出：{"toolCalls":[{"name":"bgi.web.search","arguments":{"query":"原神 可莉 培养材料 突破材料 天赋材料","maxResults":3,"provider":"auto"}}]}
                                               </example>
                                               <example>
                                               用户：帮我找枫丹泡泡桔采集路线
                                               输出：{"toolCalls":[{"name":"bgi.script.search","arguments":{"query":"枫丹 泡泡桔 采集 路线","type":"pathing","limit":5}}]}
                                               </example>
                                               <example>
                                               用户：导入甜甜花采集脚本
                                               输出：{"toolCalls":[{"name":"bgi.script.subscribe","arguments":{"query":"甜甜花","importNow":true,"limit":10}}]}
                                               </example>
                                               <example>
                                               用户：现在自动拾取开了吗
                                               输出：{"toolCalls":[{"name":"bgi.get_features","arguments":{}}]}
                                               </example>
                                               </examples>

                                               <final_note>
                                               MCP 工具返回会以 "MCP_RESULT:" 开头的系统消息提供给你。收到 MCP_RESULT 后再给出自然语言总结。
                                               </final_note>
                                               """;

    private const int MaxAutoToolCallsPerRound = 5;
    private const int MaxWebSearchToolCallsPerTurn = 2;
    private const int DefaultMaxContextChars = 80000;
    private const int DefaultMaxMcpResultChars = 8000;
    private const int DefaultMaxChatMessageChars = 20000;
    private static readonly TimeSpan DefaultAiRequestTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DefaultIntentRequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan DefaultMcpRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StreamingUiFlushInterval = TimeSpan.FromMilliseconds(80);
    private const int StreamingUiFlushChars = 8;
    private const string FinalAnswerStagePrompt =
        "你现在处于最终答复阶段：\n" +
        "1) 严禁再发起任何 MCP 工具调用；\n" +
        "2) 严禁输出 JSON、代码块、toolCalls；\n" +
        "3) 仅基于已有 MCP_RESULT，用自然语言直接回答用户问题；\n" +
        "4) 若前面已经给出任务列表，不要重新规划，直接按该列表汇报已完成/未完成项与结果；\n" +
        "5) 需要给出数量时尽量结构化列点，证据不足要明确说明。";
    private const string NoToolFallbackPrompt =
        "你现在只能输出最终用户答复：\n" +
        "1) 严禁调用 MCP 工具；\n" +
        "2) 严禁输出 JSON、代码块、toolCalls、函数名；\n" +
        "3) 仅根据已有 MCP_RESULT 作答，禁止编造；\n" +
        "4) 若证据不足，必须明确说明“不足以确定”，并给出下一步可执行建议；\n" +
        "5) 若前面已经给出任务列表，不要重新规划，直接基于已有结果汇报进展与结论；\n" +
        "6) 只输出给用户看的自然语言正文。";
    private const string ContextCompressionPrompt =
        "Based on our full conversation history, produce a concise summary of key takeaways and/or project progress.\n" +
        "1. Systematically cover all core topics discussed and the final conclusion/outcome for each; clearly highlight the latest primary focus.\n" +
        "2. If any tools were used, summarize tool usage (total call count) and extract the most valuable insights from tool outputs.\n" +
        "3. If there was an initial user goal, state it first and describe the current progress/status.\n" +
        "4. Write the summary in the user's language.";
    private const string McpCompressionPrompt =
        "你是 MCP 输出压缩器。请将下面的 MCP 工具输出压缩为“可直接用于回答用户问题”的摘要。\n" +
        "要求：\n" +
        "1) 仅保留关键结论、关键字段、关键数量、状态与错误信息；\n" +
        "2) 删除重复日志、模板字段、无关噪声；\n" +
        "3) 若包含列表，仅保留最有价值的前几项并给出总量；\n" +
        "4) 严禁输出 JSON、代码块、toolCalls；\n" +
        "5) 输出自然语言纯文本。";
    private const string McpChunkCompressionPrompt =
        "你将收到 MCP 输出的一个分段。请提取该分段中对最终答复有价值的信息，输出简短要点摘要。\n" +
        "要求：保留关键字段和值、状态和错误，删除噪声；仅输出纯文本。";
    private const string McpChunkMergePrompt =
        "你将收到多个 MCP 分段摘要。请合并为最终摘要，用于后续回答用户。\n" +
        "要求：去重、保持事实一致，优先保留数量/状态/错误/结论，不要 JSON，不要代码块。";
    private const string ContextCompressionNotice = "上下文超限正在压缩";
    private const string McpCompressionNotice = "MCP 内容超限正在压缩";
    private const string ContextCompressionSystemPrefix = "历史对话压缩摘要（上下文超限自动生成）:";
    private const string McpCompressionSystemPrefix = "[MCP压缩摘要]";
    private const string IntentClassifierPrompt = """
                                                 <identity>
                                                 你是 BetterGI 的意图分类器。
                                                 </identity>

                                                 <output_contract>
                                                 只返回一个 JSON 对象，不要输出解释、代码块或 Markdown。
                                                 输出字段固定为：
                                                 {"pathingIntent":bool,"scriptSubscribeIntent":bool,"scriptDetailIntent":bool,"docHelpIntent":bool,"downloadIntent":bool,"statusQueryIntent":bool,"realtimeFeatureQuery":bool,"desiredFeatureValue":true|false|null,"featureKey":"autoPick|autoSkip|autoHangout|autoFishing|autoCook|autoEat|quickTeleport|mapMask|skillCd|null","allFeaturesRequest":bool,"allRequest":bool,"reason":"<=12字"}
                                                 </output_contract>

                                                 <decision_principles>
                                                 - 输入里可能包含“最近对话上下文”和“当前用户输入”，请优先依据“当前用户输入”判定。
                                                 - 只有当前句存在代词（如“她/这个/那个”）时，才结合最近对话上下文消歧。
                                                 - 宁可少判 true，也不要把知识问答误判成执行型脚本请求。
                                                 - “材料”这个词本身不能直接推出 pathingIntent=true；角色培养材料、突破材料、天赋材料通常是知识问答。
                                                 </decision_principles>

                                                 <rules>
                                                 1. pathingIntent=true 仅用于明确要执行地图追踪脚本的请求，如采集路线、跑图、刷怪、讨伐、锄地。
                                                 2. 角色知识 / 培养 / 突破 / 天赋 / 武器 / 圣遗物 / 配队 / 机制问答 -> pathingIntent=false。
                                                 3. 用户表达“订阅/导入/安装脚本”时，scriptSubscribeIntent=true。
                                                 4. 用户表达“介绍脚本/用途/做什么”时，scriptDetailIntent=true。
                                                 5. 用户表达报错排查、不会用、使用指导、FAQ、下载安装等时，docHelpIntent=true；下载或版本信息时，downloadIntent=true。
                                                 6. 用户表达查看功能状态时，statusQueryIntent=true；表达实时触发相关状态时，realtimeFeatureQuery=true。
                                                 7. 用户表达开关意图时，desiredFeatureValue 输出 true/false，否则 null；如果能识别具体功能，featureKey 输出标准键名，否则 null。
                                                 8. 用户表达“全部/所有/一键/全开/全关”功能开关时，allFeaturesRequest=true；表达“全部订阅/一次性全部执行/所有脚本”等批量请求时，allRequest=true。
                                                 </rules>

                                                 <examples>
                                                 <example>
                                                 用户：我想要练可莉需要什么
                                                 输出：{"pathingIntent":false,"scriptSubscribeIntent":false,"scriptDetailIntent":false,"docHelpIntent":false,"downloadIntent":false,"statusQueryIntent":false,"realtimeFeatureQuery":false,"desiredFeatureValue":null,"featureKey":null,"allFeaturesRequest":false,"allRequest":false,"reason":"角色培养"}
                                                 </example>
                                                 <example>
                                                 用户：帮我找枫丹泡泡桔采集路线
                                                 输出：{"pathingIntent":true,"scriptSubscribeIntent":false,"scriptDetailIntent":false,"docHelpIntent":false,"downloadIntent":false,"statusQueryIntent":false,"realtimeFeatureQuery":false,"desiredFeatureValue":null,"featureKey":null,"allFeaturesRequest":false,"allRequest":false,"reason":"采集路线"}
                                                 </example>
                                                 <example>
                                                 用户：导入甜甜花采集脚本
                                                 输出：{"pathingIntent":false,"scriptSubscribeIntent":true,"scriptDetailIntent":false,"docHelpIntent":false,"downloadIntent":false,"statusQueryIntent":false,"realtimeFeatureQuery":false,"desiredFeatureValue":null,"featureKey":null,"allFeaturesRequest":false,"allRequest":false,"reason":"订阅脚本"}
                                                 </example>
                                                 <example>
                                                 用户：这个脚本做什么
                                                 输出：{"pathingIntent":false,"scriptSubscribeIntent":false,"scriptDetailIntent":true,"docHelpIntent":false,"downloadIntent":false,"statusQueryIntent":false,"realtimeFeatureQuery":false,"desiredFeatureValue":null,"featureKey":null,"allFeaturesRequest":false,"allRequest":false,"reason":"脚本介绍"}
                                                 </example>
                                                 </examples>
                                                 """;
    private static readonly HashSet<string> SubscribeArgumentAllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "names",
        "query",
        "limit",
        "importNow",
        "previewOnly",
        "dryRun"
    };
    private static readonly Regex McpBlockRegex = new(@"```mcp\s*(?<json>[\s\S]*?)```", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LooseReplyFieldRegex = new(
        "\"(?:reply|message|text|content|finalReply|final_reply|answer)\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"\\\\])*)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LooseDescriptionFieldRegex = new(
        "\"(?:description|summary|snippet)\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"\\\\])*)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ScriptNameHintRegex = new(@"[A-Za-z]+\d+|\d{2,}|[\u4e00-\u9fff]{2,}", RegexOptions.Compiled);
    private static readonly Regex MarkdownImageRegex = new(
        @"!\[(?<alt>[^\]]*)\]\((?<url>[^)\s]+)(?:\s+[""'][^""']*[""'])?\)",
        RegexOptions.Compiled);
    private static readonly Regex MarkdownLinkRegex = new(
        @"\[(?<text>[^\]]+)\]\((?<url>[^)\s]+)(?:\s+[""'][^""']*[""'])?\)",
        RegexOptions.Compiled);
    private static readonly Regex AbsoluteUrlRegex = new(
        @"\bhttps?://[^\s<>()]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
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
    private static readonly HashSet<string> KnownNonPrefixedToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "search_docs",
        "get_feature_detail",
        "get_download_info",
        "search_scripts",
        "get_faq",
        "get_quickstart",
        "get_logs",
        "get_features",
        "set_features",
        "language.get",
        "language.set",
        "script.search",
        "script.detail",
        "script.list",
        "script.run",
        "script.subscribe"
    };
    private static readonly HashSet<string> InformationalExecutionPlanSkipTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "bgi.web.search",
        "search_docs",
        "get_feature_detail",
        "get_download_info",
        "get_faq",
        "get_quickstart",
        "bgi.script.detail"
    };

    private readonly AiChatService _chatService;
    private readonly McpLocalClient _mcpClient;
    private string? _lastFeatureFocus;
    private List<string> _recentScriptCandidates = [];
    private DateTimeOffset _recentScriptCandidatesUpdatedUtc = DateTimeOffset.MinValue;
    private string? _cachedCompressedContextSummary;
    private int _cachedCompressedContextSignature = int.MinValue;
    private readonly Dictionary<int, string> _cachedCompressedMcpSummaryBySignature = new();

    private static readonly (string Key, string[] Aliases)[] FeatureAliases =
    [
        ("autoPick", new[] { "自动拾取", "拾取" }),
        ("autoSkip", new[] { "自动剧情", "剧情跳过", "跳过剧情", "自动跳过" }),
        ("autoHangout", new[] { "自动邀约", "邀约" }),
        ("autoFishing", new[] { "自动钓鱼", "钓鱼" }),
        ("autoCook", new[] { "自动烹饪", "烹饪" }),
        ("autoEat", new[] { "自动吃药", "吃药" }),
        ("quickTeleport", new[] { "快捷传送", "快速传送", "快传" }),
        ("mapMask", new[] { "地图遮罩", "遮罩" }),
        ("skillCd", new[] { "冷却提示", "技能冷却", "冷却" })
    ];

    private static readonly string[] AllFeatureKeywords = ["全部", "所有", "全关", "全开", "全都", "一键"];
    private static readonly string[] FeatureScopeKeywords = ["功能", "开关", "实时", "触发", "配置", "自动"];
    private static readonly string[] StatusKeywords = ["状态", "配置", "开关", "是否"];
    private static readonly string[] StatusVerbs = ["查询", "查看", "检查", "确认", "了解"];
    private static readonly string[] PathingPriorityKeywords =
    [
        "采集", "收集", "拾取", "捡取", "跑图", "路线", "点位", "材料", "特产", "挖矿", "矿", "薄荷",
        "打怪", "刷怪", "清怪", "击杀", "讨伐", "怪物", "精英怪", "boss", "BOSS", "锄地"
    ];
    private static readonly string[] PathingQueryNoisePhrases =
    [
        "我想要", "我想", "帮我", "请帮我", "麻烦", "请", "可以", "能不能", "一下", "帮忙",
        "自动", "脚本", "用脚本", "用地图追踪", "地图追踪", "运行", "执行", "安排"
    ];
    private static readonly string[] DocHelpKeywords =
    [
        "报错", "错误", "异常", "崩溃", "闪退", "失败", "无法", "打不开", "重定向", "预热",
        "怎么用", "不会用", "使用指导", "教程", "说明", "下载", "安装", "FAQ", "常见问题"
    ];
    private static readonly string[] ScriptSubscribeKeywords =
    [
        "订阅", "导入", "import", "安装脚本", "添加脚本"
    ];
    private static readonly string[] ScriptDetailKeywords =
    [
        "介绍", "用途", "说明", "做什么", "作用", "详情", "详细", "干嘛"
    ];
    private static readonly string[] GeneralKnowledgeKeywords =
    [
        "原神", "角色", "材料", "突破", "天赋", "命座", "武器", "圣遗物", "配队", "技能",
        "介绍", "是谁", "背景", "生日", "cv", "培养"
    ];
    private static readonly string[] CharacterKnowledgeKeywords =
    [
        "角色", "材料", "突破", "天赋", "命座", "武器", "圣遗物", "配队", "技能", "培养",
        "养成", "毕业", "练", "练度", "需要什么", "要什么"
    ];
    private static readonly string[] BetterGiDomainKeywords =
    [
        "bettergi", "bgi", "脚本", "订阅", "导入", "运行", "执行", "地图追踪", "pathing",
        "调度器", "全自动", "实时触发", "设置", "mcp", "下载", "安装", "更新", "官网", "faq"
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
    [NotifyCanExecuteChangedFor(nameof(ClearChatHistoryCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CallMcpToolCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearChatHistoryCommand))]
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
        Config.PropertyChanged += OnConfigPropertyChanged;
        Messages.CollectionChanged += (_, _) => ClearChatHistoryCommand.NotifyCanExecuteChanged();
    }

    private void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AiConfig.AutoExecuteMcpToolCalls) && Config.AutoExecuteMcpToolCalls)
        {
            UIDispatcherHelper.Invoke(() =>
            {
                ThemedMessageBox.Warning("你已开启“AI 自动执行 MCP 工具调用”。\n这会允许 AI 直接触发本地控制操作（如配置修改、任务控制、脚本订阅等），请仅在可信输入场景下启用。");
            });
        }
    }

    public override async Task OnNavigatedToAsync()
    {
        await RefreshMcpToolsAsync();
        await base.OnNavigatedToAsync();
    }

    public Task RefreshMcpToolsForBridgeAsync(CancellationToken cancellationToken = default)
    {
        return RunOnUiThreadAsync(RefreshMcpToolsAsync, cancellationToken);
    }

    public void ResetConversationForBridge(IReadOnlyList<AiChatMessage>? history)
    {
        RunOnUiThread(() => ResetConversationForBridgeCore(history));
    }

    private void ResetConversationForBridgeCore(IReadOnlyList<AiChatMessage>? history)
    {
        Messages.Clear();
        InputText = string.Empty;
        StatusText = "就绪";
        McpStatus = "内置 MCP 未连接";
        IsBusy = false;
        McpBusy = false;
        McpArguments = "{}";

        _recentScriptCandidates = [];
        _recentScriptCandidatesUpdatedUtc = DateTimeOffset.MinValue;
        _lastFeatureFocus = null;
        InvalidateCompressedContextCache();

        if (history == null || history.Count == 0)
        {
            return;
        }

        foreach (var item in history)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Content))
            {
                continue;
            }

            Messages.Add(new AiChatMessage(item.Role, item.Content.Trim()));
        }
    }

    public Task SendBridgeMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        return RunOnUiThreadAsync(() =>
        {
            InputText = message ?? string.Empty;
            return SendMessageAsync();
        }, cancellationToken);
    }

    public (string StatusText, IReadOnlyList<AiChatMessage> Messages) GetBridgeSnapshot()
    {
        return RunOnUiThread(() =>
        {
            var snapshot = Messages
                .Select(x => new AiChatMessage(x.Role, x.Content ?? string.Empty))
                .ToList();
            return (StatusText ?? string.Empty, (IReadOnlyList<AiChatMessage>)snapshot);
        });
    }

    public int GetBridgeMcpToolCount()
    {
        return RunOnUiThread(() => McpTools.Count);
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
            var intent = await ResolveIntentClassificationAsync(content);
            if (!string.IsNullOrWhiteSpace(intent.FeatureKey))
            {
                _lastFeatureFocus = intent.FeatureKey;
            }

            LogIntentClassification(content, intent);

            var payloadMessages = await BuildPayloadMessagesAsync();
            var aiReply = await GetAiReplyAsync(payloadMessages, BuildToolPlanningConfig());
            var reply = aiReply.RawReply;
            var replyMessageIndex = aiReply.StreamMessageIndex;
            LogChat("assistant_raw", reply);
            var toolExecutionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var executedWebSearchQueryKeys = new HashSet<string>(StringComparer.Ordinal);
            var blockedByToolGuard = false;
            var blockedByAutoExecute = false;
            var executedMcpRound = false;
            var planningMessageIndex = -1;
            var toolCalls = ParseMcpToolCalls(reply);
            toolCalls = CoerceRealtimeFeatureToolCalls(toolCalls, intent.RealtimeFeatureQueryIntent, out var coerceNotice);
            if (!string.IsNullOrWhiteSpace(coerceNotice))
            {
                AddChatMessage("system", coerceNotice);
                LogChat("system", coerceNotice);
            }

            toolCalls = CoercePathingPriorityToolCalls(content, toolCalls, intent.PathingPriorityIntent, out var pathingNotice);
            if (!string.IsNullOrWhiteSpace(pathingNotice))
            {
                AddChatMessage("system", pathingNotice);
                LogChat("system", pathingNotice);
            }

            toolCalls = CoerceScriptSubscribeToolCalls(content, toolCalls, intent, out var subscribeNotice);
            if (!string.IsNullOrWhiteSpace(subscribeNotice))
            {
                AddChatMessage("system", subscribeNotice);
                LogChat("system", subscribeNotice);
            }

            toolCalls = CoerceGeneralKnowledgeToolCalls(content, toolCalls, intent, out var knowledgeNotice);
            if (!string.IsNullOrWhiteSpace(knowledgeNotice))
            {
                AddChatMessage("system", knowledgeNotice);
                LogChat("system", knowledgeNotice);
            }

            toolCalls = CoerceCharacterKnowledgeFollowUpToolCalls(content, toolCalls, intent, out var knowledgeFollowUpNotice);
            if (!string.IsNullOrWhiteSpace(knowledgeFollowUpNotice))
            {
                AddChatMessage("system", knowledgeFollowUpNotice);
                LogChat("system", knowledgeFollowUpNotice);
            }

            toolCalls = NormalizeAndFilterToolCalls(toolCalls, out var normalizeNotice);
            if (!string.IsNullOrWhiteSpace(normalizeNotice))
            {
                AddChatMessage("system", normalizeNotice);
                LogChat("system", normalizeNotice);
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

            if (toolCalls.Count == 0 && TryBuildFallbackToolCalls(content, intent, out var fallbackCalls))
            {
                toolCalls = fallbackCalls;
                LogChat("system", $"检测到明确操作意图，已自动调用 {fallbackCalls[0].Name}。");
            }

            if (toolCalls.Count == 0 && TryBuildMandatoryWorkflowToolCalls(content, intent, out var workflowCalls))
            {
                toolCalls = workflowCalls;
                LogChat("system", $"未检测到可执行工具调用，已按工作流补充 MCP 调用：{workflowCalls[0].Name}。");
            }

            toolCalls = ApplyToolExecutionGuards(toolCalls, toolExecutionCounts, executedWebSearchQueryKeys, out var guardNotice);
            if (!string.IsNullOrWhiteSpace(guardNotice))
            {
                blockedByToolGuard = true;
                AddChatMessage("system", guardNotice);
                LogChat("system", guardNotice);
            }

            if (toolCalls.Count > 0 && ShouldShowExecutionPlanForToolCalls(intent, toolCalls, reply))
            {
                var planningReply = BuildVisibleExecutionPlanReply(reply, toolCalls);
                if (!string.IsNullOrWhiteSpace(planningReply))
                {
                    planningMessageIndex = UpsertAssistantPlanningMessage(replyMessageIndex, planningReply);
                    if (planningMessageIndex >= 0)
                    {
                        LogChat("assistant_plan", planningReply);
                        replyMessageIndex = -1;
                    }
                }
                else if (replyMessageIndex >= 0)
                {
                    RemoveChatMessageAt(replyMessageIndex);
                    replyMessageIndex = -1;
                }
            }
            else if (toolCalls.Count > 0 && replyMessageIndex >= 0)
            {
                RemoveChatMessageAt(replyMessageIndex);
                replyMessageIndex = -1;
            }

            if (toolCalls.Count > 0 && !Config.AutoExecuteMcpToolCalls)
            {
                var blockedToolNames = toolCalls.Select(call => call.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(4)
                    .ToArray();
                var blockedNames = string.Join(", ", blockedToolNames);
                var notice = string.IsNullOrWhiteSpace(blockedNames)
                    ? "已拦截 AI 生成的 MCP 自动调用。若需自动执行，请在 AI 设置中开启“自动执行 MCP 工具调用”。"
                    : $"已拦截 AI 生成的 MCP 自动调用：{blockedNames}。若需自动执行，请在 AI 设置中开启“自动执行 MCP 工具调用”。";
                AddChatMessage("system", notice);
                LogChat("system", notice);
                if (planningMessageIndex >= 0)
                {
                    StatusText = "已拦截自动执行";
                    return;
                }

                blockedByAutoExecute = true;
                reply = BuildAutoExecuteBlockedReply(blockedToolNames);
                toolCalls = [];
            }

            if (toolCalls.Count > 0)
            {
                RecordPlannedToolCalls(toolCalls, toolExecutionCounts);
                RecordExecutedWebSearchQueries(toolCalls, executedWebSearchQueryKeys);
                var executedCalls = await ExecuteMcpToolCallsAsync(toolCalls);
                executedMcpRound = executedCalls.Count > 0;
                toolCalls = [];

                var characterAutomationSearchCalls = BuildCharacterAutomationSearchCalls(content, intent, executedCalls);
                if (characterAutomationSearchCalls.Count > 0)
                {
                    RecordPlannedToolCalls(characterAutomationSearchCalls, toolExecutionCounts);
                    RecordExecutedWebSearchQueries(characterAutomationSearchCalls, executedWebSearchQueryKeys);
                    var searchNotice = $"已根据角色材料结果规划可自动执行部分：{string.Join("、", characterAutomationSearchCalls.Select(call => call.Name).Distinct(StringComparer.OrdinalIgnoreCase))}";
                    AddChatMessage("system", searchNotice);
                    LogChat("system", searchNotice);

                    var automationSearchResults = await ExecuteMcpToolCallsAsync(characterAutomationSearchCalls);
                    executedMcpRound = executedMcpRound || automationSearchResults.Count > 0;

                    var characterAutomationRunCalls = BuildCharacterAutomationRunCalls(automationSearchResults);
                    if (characterAutomationRunCalls.Count > 0)
                    {
                        RecordPlannedToolCalls(characterAutomationRunCalls, toolExecutionCounts);
                        RecordExecutedWebSearchQueries(characterAutomationRunCalls, executedWebSearchQueryKeys);
                        var runNotice = $"已为可自动化材料生成执行步骤：{string.Join("、", characterAutomationRunCalls.Select(call => call.Name).Distinct(StringComparer.OrdinalIgnoreCase))}";
                        AddChatMessage("system", runNotice);
                        LogChat("system", runNotice);

                        var automationRunResults = await ExecuteMcpToolCallsAsync(characterAutomationRunCalls);
                        executedMcpRound = executedMcpRound || automationRunResults.Count > 0;
                    }
                }

                StatusText = "AI 正在整理答案...";
                LogChat("system", "进入最终答复阶段：禁用 MCP 调用，禁用 JSON 响应格式。");
                var finalPayloadMessages = (await BuildPayloadMessagesAsync()).ToList();
                finalPayloadMessages.Add(new AiChatMessage("system", FinalAnswerStagePrompt));
                aiReply = await GetAiReplyAsync(finalPayloadMessages, BuildFinalAnswerConfig());
                reply = aiReply.RawReply;
                replyMessageIndex = aiReply.StreamMessageIndex;
                LogChat("assistant_raw", reply);
                if (IsInvalidFinalAnswerReply(reply))
                {
                    LogChat("system", "最终答复阶段检测到结构化/工具调用输出，改用无工具回退答复。");
                    reply = string.Empty;
                }
            }

            reply = SanitizeAssistantReply(reply);
            reply = EnsureMcpFailureConsistency(reply);
            var attemptedNoToolFallback = false;
            if (IsInvalidFinalAnswerReply(reply))
            {
                attemptedNoToolFallback = true;
                var noToolReply = await TryGenerateNoToolFallbackReplyAsync();
                if (!string.IsNullOrWhiteSpace(noToolReply))
                {
                    reply = noToolReply;
                }
            }

            if (IsInvalidFinalAnswerReply(reply))
            {
                reply = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(reply) && (blockedByToolGuard || executedMcpRound) && !attemptedNoToolFallback)
            {
                var noToolReply = await TryGenerateNoToolFallbackReplyAsync();
                if (!string.IsNullOrWhiteSpace(noToolReply))
                {
                    reply = noToolReply;
                }
            }

            if (string.IsNullOrWhiteSpace(reply))
            {
                reply = blockedByAutoExecute
                    ? BuildAutoExecuteBlockedReply(Array.Empty<string>())
                    : blockedByToolGuard
                    ? "联网检索调用已停止（防止重复调用）。请提供更具体关键词（例如“可莉90级突破+天赋材料清单”），我会基于现有结果直接给结论。"
                    : executedMcpRound
                        ? "我已完成 MCP 调用，但整理答案失败。请重试一次，我会直接给出自然语言结果。"
                        : "我拿到了结构化结果，但回复格式异常。请重试一次，我会直接用自然语言回答。";
            }

            if (replyMessageIndex >= 0)
            {
                UpdateChatMessageAt(replyMessageIndex, "assistant", reply);
            }
            else
            {
                AddChatMessage("assistant", reply);
            }

            LogChat("assistant", reply);
            StatusText = blockedByAutoExecute ? "已拦截自动执行" : "完成";
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
            var notice = FormatAiFailureNotice(ex.Message);
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

    private bool CanClearChatHistory()
    {
        return !IsBusy && !McpBusy && Messages.Count > 0;
    }

    [RelayCommand(CanExecute = nameof(CanClearChatHistory))]
    private void ClearChatHistory()
    {
        if (Messages.Count == 0)
        {
            StatusText = "聊天记录为空";
            return;
        }

        var result = ThemedMessageBox.Question(
            "是否清空当前 AI 聊天记录？",
            "清空聊天",
            MessageBoxButton.YesNo,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        Messages.Clear();
        _recentScriptCandidates = [];
        _recentScriptCandidatesUpdatedUtc = DateTimeOffset.MinValue;
        _lastFeatureFocus = null;
        InvalidateCompressedContextCache();
        StatusText = "聊天记录已清空";
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
            var timeout = GetMcpToolTimeout(SelectedMcpTool.Name);
            var argumentsJson = SelectedMcpTool.Name.Equals("bgi.capture_screen", StringComparison.OrdinalIgnoreCase)
                ? EnsureCaptureScreenArguments(McpArguments)
                : McpArguments;
            var result = await CallMcpToolOnWorkerAsync(SelectedMcpTool.Name, argumentsJson, timeout);
            var prefix = result.IsError ? "调用失败" : "调用成功";
            var formattedResult = await Task.Run(() => FormatMcpResultForDisplay(result.Content));
            AddChatMessage("mcp", $"{prefix} · {SelectedMcpTool.Name}\n{formattedResult}", DefaultMaxChatMessageChars);
            StatusText = result.IsError ? "MCP 调用失败" : "MCP 调用完成";
        }
        catch (OperationCanceledException)
        {
            var timeout = GetMcpToolTimeout(SelectedMcpTool.Name);
            AddChatMessage("system", $"MCP 调用超时({timeout.TotalSeconds:0}s): {SelectedMcpTool.Name}");
            StatusText = "MCP 调用超时";
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

    private async Task<IReadOnlyList<ExecutedMcpToolCall>> ExecuteMcpToolCallsAsync(IReadOnlyList<McpToolCall> toolCalls)
    {
        if (toolCalls.Count == 0)
        {
            return [];
        }

        McpBusy = true;
        var availableToolNames = BuildToolNameSet();
        var executed = new List<ExecutedMcpToolCall>(toolCalls.Count);
        try
        {
            foreach (var call in toolCalls)
            {
                if (availableToolNames.Count > 0 && !availableToolNames.Contains(call.Name))
                {
                    var notice = $"忽略未知 MCP 工具：{call.Name}";
                    AddChatMessage("system", notice);
                    LogChat("system", notice);
                    continue;
                }

                StatusText = $"调用 MCP: {call.Name}";
                try
                {
                    if (!Config.ShowMcpVisualizationOutput)
                    {
                        var progress = $"AI 正在调用 MCP：{call.Name}";
                        AddChatMessage("system", progress);
                        LogChat("system", progress);
                    }

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

                    var timeout = GetMcpToolTimeout(call.Name);
                    var result = await CallMcpToolOnWorkerAsync(call.Name, argumentsJson, timeout);
                    var prefix = result.IsError ? "调用失败" : "调用成功";
                    var formattedResult = Config.ShowMcpVisualizationOutput
                        ? await Task.Run(() => FormatMcpResultForDisplay(result.Content))
                        : string.Empty;
                    var message = $"{prefix} · {call.Name}\n{formattedResult}";
                    AddChatMessage("mcp", message, DefaultMaxChatMessageChars);
                    LogChat($"mcp:{call.Name}", message);
                    executed.Add(new ExecutedMcpToolCall(call.Name, argumentsJson, result));
                    if (result.IsError && !Config.ShowMcpVisualizationOutput)
                    {
                        var notice = $"MCP 调用失败：{call.Name}";
                        AddChatMessage("system", notice);
                        LogChat("system", notice);
                    }
                    UpdateScriptCandidateCache(call.Name, result.Content);
                    UpdateFeatureFocusFromToolCall(call.Name, argumentsJson);
                }
                catch (OperationCanceledException)
                {
                    var timeout = GetMcpToolTimeout(call.Name);
                    var notice = $"MCP 调用超时({timeout.TotalSeconds:0}s): {call.Name}";
                    AddChatMessage("system", notice);
                    LogChat("system", notice);
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

        return executed;
    }

    private IReadOnlyList<McpToolCall> ParseMcpToolCalls(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return Array.Empty<McpToolCall>();
        }

        if (TryParseJsonModeEnvelope(reply, out var jsonModeCalls, out _))
        {
            return jsonModeCalls;
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

    private static string BuildVisibleExecutionPlanReply(string rawReply, IReadOnlyList<McpToolCall> toolCalls)
    {
        if (TryParseJsonModeEnvelope(rawReply, out var plannedToolCalls, out var assistantReply) &&
            ShouldKeepAssistantReplyAsExecutionPlan(assistantReply) &&
            DoExecutionPlanToolCallsMatch(plannedToolCalls, toolCalls))
        {
            return SanitizeExecutionPlanReplyForDisplay(NormalizeExecutionPlanReply(assistantReply!));
        }

        var toolNames = toolCalls
            .Select(call => call.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
        return SanitizeExecutionPlanReplyForDisplay(BuildFallbackExecutionPlanReply(toolNames));
    }

    private static bool ShouldShowExecutionPlanForToolCalls(IntentClassification intent, IReadOnlyList<McpToolCall> toolCalls, string? rawReply = null)
    {
        if (toolCalls.Count == 0)
        {
            return false;
        }

        if (ShouldKeepAssistantReplyAsExecutionPlan(TryExtractAssistantReplyFromEnvelope(rawReply ?? string.Empty)))
        {
            return true;
        }

        var allInformational = toolCalls.All(call => InformationalExecutionPlanSkipTools.Contains(call.Name));
        if (!allInformational)
        {
            return true;
        }

        return intent.PathingPriorityIntent ||
               intent.ScriptSubscribeIntent ||
               intent.RealtimeFeatureQueryIntent ||
               intent.StatusQueryIntent ||
               intent.AllFeaturesRequest ||
               intent.IsAllRequest ||
               !string.IsNullOrWhiteSpace(intent.FeatureKey);
    }

    private static bool DoExecutionPlanToolCallsMatch(IReadOnlyList<McpToolCall> plannedToolCalls, IReadOnlyList<McpToolCall> actualToolCalls)
    {
        if (plannedToolCalls.Count != actualToolCalls.Count)
        {
            return false;
        }

        for (var i = 0; i < plannedToolCalls.Count; i++)
        {
            var plannedName = plannedToolCalls[i].Name?.Trim();
            var actualName = actualToolCalls[i].Name?.Trim();
            if (!string.Equals(plannedName, actualName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string? TryExtractAssistantReplyFromEnvelope(string rawReply)
    {
        if (!TryParseJsonModeEnvelope(rawReply, out _, out var assistantReply) ||
            string.IsNullOrWhiteSpace(assistantReply))
        {
            return null;
        }

        return assistantReply.Trim();
    }

    private static bool ShouldKeepAssistantReplyAsExecutionPlan(string? assistantReply)
    {
        if (string.IsNullOrWhiteSpace(assistantReply))
        {
            return false;
        }

        var text = assistantReply.Trim();
        if (text.Contains("任务", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("步骤", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("计划", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("清单", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var lines = text
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return false;
        }

        var numberedCount = lines.Count(line => Regex.IsMatch(line, @"^\d+\.\s"));
        if (numberedCount >= 2)
        {
            return true;
        }

        var bulletCount = lines.Count(line => line.StartsWith("- ", StringComparison.Ordinal) ||
                                              line.StartsWith("* ", StringComparison.Ordinal) ||
                                              line.StartsWith("+ ", StringComparison.Ordinal));
        if (bulletCount >= 2)
        {
            return true;
        }

        return text.Contains("先", StringComparison.OrdinalIgnoreCase) &&
               (text.Contains("再", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("然后", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("接着", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeExecutionPlanReply(string assistantReply)
    {
        var text = assistantReply.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (text.StartsWith("任务列表", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("执行计划", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("计划", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        return $"任务列表：\n{text}";
    }

    private static string SanitizeExecutionPlanReplyForDisplay(string planningReply)
    {
        if (string.IsNullOrWhiteSpace(planningReply))
        {
            return string.Empty;
        }

        var sanitized = MarkdownImageRegex.Replace(planningReply, match =>
        {
            var alt = match.Groups["alt"].Value.Trim();
            var url = match.Groups["url"].Value.Trim();
            if (IsAllowedExecutionPlanUrl(url))
            {
                return match.Value;
            }

            return string.IsNullOrWhiteSpace(alt) ? "[图片已省略]" : $"[图片已省略: {alt}]";
        });

        sanitized = MarkdownLinkRegex.Replace(sanitized, match =>
        {
            var text = match.Groups["text"].Value.Trim();
            var url = match.Groups["url"].Value.Trim();
            if (IsAllowedExecutionPlanUrl(url))
            {
                return match.Value;
            }

            return string.IsNullOrWhiteSpace(text) ? "外链已省略" : $"{text}（外链已省略）";
        });

        sanitized = AbsoluteUrlRegex.Replace(sanitized, match =>
        {
            var (url, suffix) = SplitUrlAndTrailingPunctuation(match.Value);
            if (IsAllowedExecutionPlanUrl(url))
            {
                return match.Value;
            }

            return ObfuscateExternalUrl(url) + suffix;
        });

        return sanitized;
    }

    private static bool IsAllowedExecutionPlanUrl(string? urlText)
    {
        if (string.IsNullOrWhiteSpace(urlText) ||
            !Uri.TryCreate(urlText.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host.Trim();
        return string.Equals(host, "bettergi.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".bettergi.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string ObfuscateExternalUrl(string url)
    {
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "hxxps://" + url["https://".Length..];
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return "hxxp://" + url["http://".Length..];
        }

        return url;
    }

    private static (string Url, string Suffix) SplitUrlAndTrailingPunctuation(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return (string.Empty, string.Empty);
        }

        var end = value.Length;
        while (end > 0 && IsTrailingUrlPunctuation(value[end - 1]))
        {
            end--;
        }

        return (value[..end], value[end..]);
    }

    private static bool IsTrailingUrlPunctuation(char c)
    {
        return c is '.' or ',' or '!' or '?' or ';' or ':' or ')' or ']' or '}' or '>' or '。' or '，' or '！' or '？' or '；' or '：' or '）' or '】' or '》';
    }

    private static string BuildFallbackExecutionPlanReply(IReadOnlyList<string> toolNames)
    {
        var safeToolNames = toolNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Take(4)
            .ToArray();
        var omittedCount = Math.Max(0, toolNames.Count - safeToolNames.Length);
        var builder = new StringBuilder();
        builder.AppendLine("任务列表：");

        var step = 1;
        if (safeToolNames.Length == 0)
        {
            builder.AppendLine($"{step}. 梳理当前请求并准备执行。");
            step++;
        }
        else
        {
            foreach (var toolName in safeToolNames)
            {
                builder.AppendLine($"{step}. {DescribeToolForExecutionPlan(toolName)}。");
                step++;
            }
        }

        if (omittedCount > 0)
        {
            builder.AppendLine($"{step}. 继续完成剩余 {omittedCount} 个 MCP 动作。");
            step++;
        }

        builder.AppendLine($"{step}. 根据执行结果整理结论并回复给你。");
        builder.Append("我会按这个顺序继续执行。");
        return builder.ToString();
    }

    private static string BuildAutoExecuteBlockedReply(IReadOnlyList<string> toolNames)
    {
        var safeToolNames = toolNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();

        if (safeToolNames.Length == 0)
        {
            return "这次请求需要调用 MCP 工具才能继续，但当前已关闭“自动执行 MCP 工具调用”，所以我没有继续执行。若需自动完成，请在 AI 设置中开启该选项后重试。";
        }

        return $"这次请求需要调用 MCP 工具才能继续（{string.Join("、", safeToolNames)}），但当前已关闭“自动执行 MCP 工具调用”，所以我没有继续执行。若需自动完成，请在 AI 设置中开启该选项后重试。";
    }

    private static string DescribeToolForExecutionPlan(string toolName)
    {
        return toolName.Trim().ToLowerInvariant() switch
        {
            "bgi.get_features" => "调用 bgi.get_features 读取当前实时功能状态",
            "bgi.set_features" => "调用 bgi.set_features 调整相关功能开关",
            "bgi.config.get" => "调用 bgi.config.get 读取当前配置",
            "bgi.config.set" => "调用 bgi.config.set 更新目标配置",
            "bgi.config.reload" => "调用 bgi.config.reload 重新加载配置",
            "bgi.leyline.get" => "调用 bgi.leyline.get 读取自动地脉花配置",
            "bgi.leyline.set" => "调用 bgi.leyline.set 更新自动地脉花配置",
            "bgi.notification.channels" => "调用 bgi.notification.channels 获取可用通知通道",
            "bgi.notification.test" => "调用 bgi.notification.test 测试通知通道",
            "bgi.script.search" => "调用 bgi.script.search 检索匹配脚本",
            "bgi.script.detail" => "调用 bgi.script.detail 查看脚本详情",
            "bgi.script.subscribe" => "调用 bgi.script.subscribe 导入目标脚本",
            "bgi.script.run" => "调用 bgi.script.run 执行目标脚本",
            "bgi.script.list" => "调用 bgi.script.list 列出本地脚本",
            "bgi.one_dragon.list" => "调用 bgi.one_dragon.list 读取一条龙配置",
            "bgi.one_dragon.run" => "调用 bgi.one_dragon.run 执行一条龙任务",
            "bgi.language.get" => "调用 bgi.language.get 查看当前语言设置",
            "bgi.language.set" => "调用 bgi.language.set 修改语言设置",
            "bgi.web.search" => "调用 bgi.web.search 获取联网信息",
            "search_docs" => "调用 search_docs 检索官网文档",
            "get_feature_detail" => "调用 get_feature_detail 查询功能说明",
            "get_download_info" => "调用 get_download_info 查询下载信息",
            "get_faq" => "调用 get_faq 查询常见问题",
            "get_quickstart" => "调用 get_quickstart 查询快速上手指引",
            "search_scripts" => "调用 search_scripts 搜索社区脚本",
            _ => $"调用 {toolName} 获取所需信息或执行操作"
        };
    }

    private void UpdateScriptCandidateCache(string toolName, string content)
    {
        if (!string.Equals(toolName, "bgi.script.search", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(toolName, "bgi.script.list", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(toolName, "search_scripts", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(toolName, "bgi.script.subscribe", StringComparison.OrdinalIgnoreCase))
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

    private async Task<McpToolCallResult> CallMcpToolOnWorkerAsync(string toolName, string argumentsJson, TimeSpan timeout)
    {
        return await Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(timeout);
            return await _mcpClient.CallToolAsync(toolName, argumentsJson, cts.Token).ConfigureAwait(false);
        }).ConfigureAwait(false);
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

            var hasArguments = doc.RootElement.TryGetProperty("arguments", out var argsElement);
            if (!hasArguments)
            {
                var toolNames = BuildToolNameSet();
                var normalizedName = ResolveToolNameAlias(name!, toolNames, out _);
                if (string.IsNullOrWhiteSpace(normalizedName) || !toolNames.Contains(normalizedName))
                {
                    return false;
                }
            }

            var argumentsJson = "{}";
            if (hasArguments)
            {
                argumentsJson = argsElement.GetRawText();
            }
            else if (TryBuildImplicitArgumentsFromToolObject(doc.RootElement, out var implicitArgumentsJson))
            {
                argumentsJson = implicitArgumentsJson;
            }

            call = new McpToolCall(name!, argumentsJson);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseJsonModeEnvelope(string reply, out IReadOnlyList<McpToolCall> toolCalls, out string? assistantReply)
    {
        toolCalls = Array.Empty<McpToolCall>();
        assistantReply = null;
        if (string.IsNullOrWhiteSpace(reply))
        {
            return false;
        }

        var normalized = NormalizeJsonPrefix(reply).Trim();
        if (!normalized.StartsWith("{", StringComparison.Ordinal) || !normalized.EndsWith("}", StringComparison.Ordinal))
        {
            var firstObject = ExtractJsonObjects(normalized).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(firstObject))
            {
                return false;
            }

            normalized = firstObject.Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(normalized);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var calls = new List<McpToolCall>(3);
            if (doc.RootElement.TryGetProperty("toolCalls", out var toolCallsElement) && toolCallsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in toolCallsElement.EnumerateArray())
                {
                    if (TryParseToolCallElement(item, out var parsed))
                    {
                        calls.Add(parsed!);
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("tool_calls", out var snakeToolCallsElement) && snakeToolCallsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in snakeToolCallsElement.EnumerateArray())
                {
                    if (TryParseToolCallElement(item, out var parsed))
                    {
                        calls.Add(parsed!);
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("tools", out var toolsElement) && toolsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in toolsElement.EnumerateArray())
                {
                    if (TryParseToolCallElement(item, out var parsed))
                    {
                        calls.Add(parsed!);
                    }
                }
            }

            if (calls.Count == 0 && TryParseToolCallElement(doc.RootElement, out var single))
            {
                calls.Add(single!);
            }

            assistantReply = TryGetFirstStringProperty(doc.RootElement, "reply", "message", "text", "content", "finalReply", "final_reply");
            toolCalls = calls;
            return calls.Count > 0 || !string.IsNullOrWhiteSpace(assistantReply);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseToolCallElement(JsonElement element, out McpToolCall? call)
    {
        call = null;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var name = nameElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var argumentsJson = "{}";
        var hasArguments = false;
        if (element.TryGetProperty("arguments", out var argsElement))
        {
            argumentsJson = argsElement.GetRawText();
            hasArguments = true;
        }
        else if (TryBuildImplicitArgumentsFromToolObject(element, out var implicitArgumentsJson))
        {
            argumentsJson = implicitArgumentsJson;
        }

        if (!hasArguments &&
            argumentsJson == "{}" &&
            !name.StartsWith("bgi.", StringComparison.OrdinalIgnoreCase) &&
            !KnownNonPrefixedToolNames.Contains(name))
        {
            return false;
        }

        call = new McpToolCall(name, argumentsJson);
        return true;
    }

    private static bool TryBuildImplicitArgumentsFromToolObject(JsonElement element, out string argumentsJson)
    {
        argumentsJson = "{}";
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var args = new JsonObject();
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, "name", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(property.Name, "arguments", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                args[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
            catch (JsonException)
            {
                continue;
            }
        }

        if (args.Count == 0)
        {
            return false;
        }

        argumentsJson = args.ToJsonString();
        return true;
    }

    private static string? TryGetFirstStringProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var value = prop.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
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
                error = "bgi.set_features 仅支持布尔字段（autoPick/autoSkip/autoHangout/autoFishing/autoCook/autoEat/quickTeleport/mapMask/skillCd）";
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

    private bool TryBuildFallbackToolCalls(string userText, IntentClassification intent, out IReadOnlyList<McpToolCall> calls)
    {
        calls = Array.Empty<McpToolCall>();
        if (intent.PathingPriorityIntent)
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

        if (intent.ScriptSubscribeIntent)
        {
            var candidates = GetRecentScriptCandidates();
            var subscribeAll = intent.IsAllRequest;
            if (candidates.Count > 0)
            {
                var names = subscribeAll ? candidates : new[] { candidates[0] };
                var args = new JsonObject
                {
                    ["names"] = new JsonArray(names.Select(n => JsonValue.Create(n)).ToArray())
                };
                calls = new[] { new McpToolCall("bgi.script.subscribe", args.ToJsonString()) };
                return true;
            }

            var query = BuildPathingSearchQuery(userText);
            if (!string.IsNullOrWhiteSpace(query) && !IsGenericSubscribeQuery(query))
            {
                var args = new JsonObject
                {
                    ["query"] = query,
                    ["limit"] = subscribeAll ? 30 : 10,
                    ["importNow"] = true
                };
                calls = new[] { new McpToolCall("bgi.script.subscribe", args.ToJsonString()) };
                return true;
            }
        }

        if (intent.ScriptDetailIntent)
        {
            var candidates = GetRecentScriptCandidates();
            var all = intent.IsAllRequest;
            var args = new JsonObject();
            if (candidates.Count > 0)
            {
                var names = all ? candidates.Take(5).ToList() : new List<string> { candidates[0] };
                args["names"] = new JsonArray(names.Select(name => JsonValue.Create(name)).ToArray());
            }
            else
            {
                var query = BuildPathingSearchQuery(userText);
                if (string.IsNullOrWhiteSpace(query) || IsGenericSubscribeQuery(query))
                {
                    query = BuildDocSearchQuery(userText);
                }

                if (!string.IsNullOrWhiteSpace(query))
                {
                    args["query"] = query;
                    args["limit"] = 5;
                }
            }

            if (args.Count > 0)
            {
                calls = new[] { new McpToolCall("bgi.script.detail", args.ToJsonString()) };
                return true;
            }
        }

        if (intent.DocHelpIntent)
        {
            if (intent.DownloadIntent)
            {
                calls = new[] { new McpToolCall("get_download_info", "{\"limit\":8}") };
                return true;
            }

            var query = BuildDocSearchQuery(userText);
            if (!string.IsNullOrWhiteSpace(query))
            {
                var searchArgs = new JsonObject
                {
                    ["query"] = query,
                    ["limit"] = 5
                };
                calls = new[] { new McpToolCall("search_docs", searchArgs.ToJsonString()) };
                return true;
            }
        }

        if (intent.StatusQueryIntent)
        {
            calls = new[] { new McpToolCall("bgi.get_features", "{}") };
            return true;
        }

        if (!intent.DesiredFeatureValue.HasValue)
        {
            return false;
        }

        var value = intent.DesiredFeatureValue.Value;
        if (intent.AllFeaturesRequest)
        {
            var allArgs = BuildAllFeaturesArgs(value);
            calls = new[] { new McpToolCall("bgi.set_features", allArgs.ToJsonString()) };
            return true;
        }

        var featureKey = intent.FeatureKey;
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

    private bool TryBuildMandatoryWorkflowToolCalls(string userText, IntentClassification intent, out IReadOnlyList<McpToolCall> calls)
    {
        calls = Array.Empty<McpToolCall>();
        if (string.IsNullOrWhiteSpace(userText))
        {
            return false;
        }

        if (intent.StatusQueryIntent || intent.RealtimeFeatureQueryIntent)
        {
            calls = new[] { new McpToolCall("bgi.get_features", "{}") };
            return true;
        }

        if (intent.PathingPriorityIntent || IsPathingPriorityIntentByKeyword(userText))
        {
            var pathingQuery = BuildPathingSearchQuery(userText);
            if (!string.IsNullOrWhiteSpace(pathingQuery))
            {
                var args = new JsonObject
                {
                    ["query"] = pathingQuery,
                    ["type"] = "pathing",
                    ["limit"] = 5
                };
                calls = new[] { new McpToolCall("bgi.script.search", args.ToJsonString()) };
                return true;
            }
        }

        if (intent.DocHelpIntent)
        {
            var docsQuery = BuildDocSearchQuery(userText);
            if (!string.IsNullOrWhiteSpace(docsQuery))
            {
                var args = new JsonObject
                {
                    ["query"] = docsQuery,
                    ["limit"] = 5
                };
                calls = new[] { new McpToolCall("search_docs", args.ToJsonString()) };
                return true;
            }
        }

        if (!IsGeneralKnowledgeQuery(userText))
        {
            return false;
        }

        var knowledgeQuery = BuildDocSearchQuery(userText);
        if (string.IsNullOrWhiteSpace(knowledgeQuery))
        {
            return false;
        }

        if (!knowledgeQuery.Contains("原神", StringComparison.OrdinalIgnoreCase))
        {
            knowledgeQuery = $"原神 {knowledgeQuery}";
        }

        var webArgs = new JsonObject
        {
            ["query"] = knowledgeQuery,
            ["maxResults"] = 3,
            ["provider"] = "auto"
        };
        calls = new[] { new McpToolCall("bgi.web.search", webArgs.ToJsonString()) };
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

    private static bool ContainsAny(string text, params string[] keywords)
    {
        if (string.IsNullOrWhiteSpace(text) || keywords.Length == 0)
        {
            return false;
        }

        foreach (var keyword in keywords)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                continue;
            }

            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

    private static string FormatAiFailureNotice(string? message)
    {
        var reason = FormatAiFailureReason(message);
        return reason.StartsWith("请求失败", StringComparison.OrdinalIgnoreCase)
            ? reason
            : $"请求失败: {reason}";
    }

    private static string FormatAiFailureReason(string? message)
    {
        var text = message?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "AI 接口返回了空错误信息。";
        }

        return text.StartsWith("请求失败:", StringComparison.OrdinalIgnoreCase)
            ? text["请求失败:".Length..].Trim()
            : text;
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

    private static bool IsLikelyFeatureControlText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (TryParseDesiredValue(text, out _))
        {
            return true;
        }

        if (ContainsAny(text,
                "自动拾取",
                "自动剧情",
                "自动邀约",
                "自动钓鱼",
                "自动烹饪",
                "自动吃药",
                "快捷传送",
                "地图遮罩",
                "冷却提示"))
        {
            return true;
        }

        foreach (var keyword in StatusKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
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

    private static bool IsPathingPriorityIntentByKeyword(string text)
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

        if (ContainsAny(text, DocHelpKeywords))
        {
            return false;
        }

        foreach (var keyword in PathingPriorityKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                if (IsLikelyFeatureControlText(text) &&
                    !ContainsAny(text, "采集", "收集", "跑图", "路线", "打怪", "刷怪", "讨伐", "挖矿", "锄地"))
                {
                    return false;
                }

                return true;
            }
        }

        return false;
    }

    private async Task<IntentClassification> ResolveIntentClassificationAsync(string userText)
    {
        var keyword = BuildKeywordIntentClassification(userText);
        if (string.IsNullOrWhiteSpace(userText))
        {
            return keyword;
        }

        var llmHints = await TryClassifyIntentWithLlmAsync(userText);
        if (!llmHints.HasValue)
        {
            return NormalizeIntentClassification(userText, keyword with { ClassifierSource = "keyword_fallback" });
        }

        return NormalizeIntentClassification(userText, MergeIntentClassification(keyword, llmHints.Value));
    }

    private IntentClassification BuildKeywordIntentClassification(string userText)
    {
        var hasDesiredValue = TryParseDesiredValue(userText, out var desiredValue);
        return new IntentClassification(
            PathingPriorityIntent: IsPathingPriorityIntentByKeyword(userText),
            ScriptSubscribeIntent: IsScriptSubscribeIntent(userText),
            ScriptDetailIntent: IsScriptDetailIntent(userText),
            DocHelpIntent: IsDocHelpIntent(userText),
            DownloadIntent: IsDownloadIntent(userText),
            StatusQueryIntent: IsStatusQuery(userText),
            RealtimeFeatureQueryIntent: IsRealtimeFeatureQuery(userText),
            DesiredFeatureValue: hasDesiredValue ? desiredValue : null,
            AllFeaturesRequest: IsAllFeaturesRequest(userText),
            IsAllRequest: IsAllRequest(userText),
            FeatureKey: TryGetFeatureKey(userText),
            ClassifierSource: "keyword",
            ClassifierReason: null);
    }

    private async Task<IntentClassificationHints?> TryClassifyIntentWithLlmAsync(string userText)
    {
        if (string.IsNullOrWhiteSpace(Config.ApiKey) || string.IsNullOrWhiteSpace(Config.Model))
        {
            return null;
        }

        var classifierInput = BuildIntentClassifierInput(userText);
        var classifierMessages = new List<AiChatMessage>(2)
        {
            new("system", IntentClassifierPrompt),
            new("user", classifierInput)
        };

        try
        {
            using var cts = new CancellationTokenSource(DefaultIntentRequestTimeout);
            var rawReply = await _chatService.GetChatCompletionAsync(
                    BuildIntentClassifierConfig(),
                    classifierMessages,
                    cts.Token)
                .ConfigureAwait(false);

            if (TryParseIntentClassifierReply(rawReply, out var hints))
            {
                return hints;
            }
        }
        catch (OperationCanceledException)
        {
            LogChat("system", "意图分类超时，已回退关键词规则。");
        }
        catch (Exception ex)
        {
            LogChat("system", $"意图分类已回退关键词规则：{FormatAiFailureReason(ex.Message)}");
        }

        return null;
    }

    private AiConfig BuildIntentClassifierConfig()
    {
        return new AiConfig
        {
            BaseUrl = Config.BaseUrl,
            ApiKey = Config.ApiKey,
            Model = Config.Model,
            UseJsonMode = true,
            UseStreamingResponse = false,
            AutoExecuteMcpToolCalls = false,
            MaxContextChars = 2048
        };
    }

    private string BuildIntentClassifierInput(string userText)
    {
        var current = userText?.Trim() ?? string.Empty;
        if (current.Length == 0)
        {
            return string.Empty;
        }

        const int maxContextItems = 4;
        const int maxContextChars = 640;
        var context = new List<(string role, string content)>(maxContextItems);
        var usedChars = 0;
        for (var i = Messages.Count - 1; i >= 0 && context.Count < maxContextItems; i--)
        {
            var message = Messages[i];
            if (message.IsMcp || message.IsSystem)
            {
                continue;
            }

            if (!(message.IsUser || message.IsAssistant))
            {
                continue;
            }

            var content = message.Content?.Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            if (i == Messages.Count - 1 && message.IsUser &&
                string.Equals(content, current, StringComparison.Ordinal))
            {
                continue;
            }

            content = TruncateForPayload(content, 220);
            if (content.Length == 0 || usedChars + content.Length > maxContextChars)
            {
                continue;
            }

            context.Add((message.IsUser ? "用户" : "助手", content));
            usedChars += content.Length;
        }

        if (context.Count == 0)
        {
            return current;
        }

        context.Reverse();
        var builder = new StringBuilder();
        builder.AppendLine("最近对话上下文：");
        foreach (var (role, content) in context)
        {
            builder.Append(role).Append('：').AppendLine(content);
        }

        builder.Append("当前用户输入：").Append(current);
        return builder.ToString();
    }

    private static IntentClassification MergeIntentClassification(IntentClassification keyword, IntentClassificationHints hints)
    {
        var normalizedFeatureKey = NormalizeFeatureKey(hints.FeatureKey);
        return new IntentClassification(
            PathingPriorityIntent: hints.PathingPriorityIntent ?? keyword.PathingPriorityIntent,
            ScriptSubscribeIntent: hints.ScriptSubscribeIntent ?? keyword.ScriptSubscribeIntent,
            ScriptDetailIntent: hints.ScriptDetailIntent ?? keyword.ScriptDetailIntent,
            DocHelpIntent: hints.DocHelpIntent ?? keyword.DocHelpIntent,
            DownloadIntent: hints.DownloadIntent ?? keyword.DownloadIntent,
            StatusQueryIntent: hints.StatusQueryIntent ?? keyword.StatusQueryIntent,
            RealtimeFeatureQueryIntent: hints.RealtimeFeatureQueryIntent ?? keyword.RealtimeFeatureQueryIntent,
            DesiredFeatureValue: hints.DesiredFeatureValue ?? keyword.DesiredFeatureValue,
            AllFeaturesRequest: hints.AllFeaturesRequest ?? keyword.AllFeaturesRequest,
            IsAllRequest: hints.IsAllRequest ?? keyword.IsAllRequest,
            FeatureKey: normalizedFeatureKey ?? keyword.FeatureKey,
            ClassifierSource: "llm",
            ClassifierReason: string.IsNullOrWhiteSpace(hints.ClassifierReason)
                ? keyword.ClassifierReason
                : hints.ClassifierReason.Trim());
    }

    private static IntentClassification NormalizeIntentClassification(string userText, IntentClassification intent)
    {
        if (string.IsNullOrWhiteSpace(userText))
        {
            return intent;
        }

        var text = userText.Trim();
        var hasScriptKeyword = ContainsAny(text, ScriptSubscribeKeywords) ||
                               ContainsAny(text, "脚本", "地图追踪", "pathing", "路线", "跑图", "订阅", "导入", "执行", "运行");
        var isGeneralKnowledge = IsGeneralKnowledgeQuery(text);
        var hasFeatureControlContext = IsLikelyFeatureControlText(text);
        var hasPathingAction = ContainsAny(text, "采集", "收集", "拾取", "捡", "跑图", "路线", "打怪", "刷怪", "讨伐", "锄地", "挖矿");
        var hasDocHelpKeyword = ContainsAny(text, DocHelpKeywords);

        var normalized = intent;
        var adjusted = false;

        if (intent.ScriptDetailIntent && !hasScriptKeyword)
        {
            normalized = normalized with { ScriptDetailIntent = false };
            adjusted = true;
        }

        if (isGeneralKnowledge && !hasScriptKeyword)
        {
            normalized = normalized with
            {
                PathingPriorityIntent = false,
                ScriptSubscribeIntent = false,
                ScriptDetailIntent = false,
                DownloadIntent = false,
                StatusQueryIntent = false,
                RealtimeFeatureQueryIntent = false,
                DesiredFeatureValue = null,
                AllFeaturesRequest = false,
                IsAllRequest = false,
                FeatureKey = null
            };

            if (!ContainsAny(text, DocHelpKeywords))
            {
                normalized = normalized with { DocHelpIntent = false };
            }

            adjusted = true;
        }

        if (normalized.RealtimeFeatureQueryIntent && !hasFeatureControlContext)
        {
            normalized = normalized with
            {
                RealtimeFeatureQueryIntent = false,
                StatusQueryIntent = false
            };
            adjusted = true;
        }

        if (hasPathingAction &&
            !hasDocHelpKeyword &&
            !hasFeatureControlContext &&
            !isGeneralKnowledge)
        {
            var featureKey = normalized.FeatureKey;
            if (string.Equals(featureKey, "autoPick", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("自动拾取", StringComparison.OrdinalIgnoreCase))
            {
                featureKey = null;
            }

            normalized = normalized with
            {
                PathingPriorityIntent = true,
                RealtimeFeatureQueryIntent = false,
                StatusQueryIntent = false,
                DesiredFeatureValue = null,
                AllFeaturesRequest = false,
                FeatureKey = featureKey
            };
            adjusted = true;
        }

        if (!adjusted)
        {
            return normalized;
        }

        var source = normalized.ClassifierSource;
        if (!string.Equals(source, "keyword", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(source, "keyword_fallback", StringComparison.OrdinalIgnoreCase))
        {
            source = "llm_guarded";
        }

        return normalized with { ClassifierSource = source };
    }

    private static bool IsGeneralKnowledgeQuery(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (ContainsAny(text, BetterGiDomainKeywords))
        {
            return false;
        }

        return ContainsAny(text, GeneralKnowledgeKeywords);
    }

    private static bool TryParseIntentClassifierReply(string reply, out IntentClassificationHints hints)
    {
        hints = default;
        if (string.IsNullOrWhiteSpace(reply))
        {
            return false;
        }

        var normalized = reply.Trim();
        if (TryParseJsonModeEnvelope(normalized, out _, out var jsonReply) &&
            !string.IsNullOrWhiteSpace(jsonReply))
        {
            normalized = jsonReply.Trim();
        }

        normalized = NormalizeJsonPrefix(normalized).Trim();
        if (!normalized.StartsWith("{", StringComparison.Ordinal))
        {
            var firstObject = ExtractJsonObjects(normalized).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(firstObject))
            {
                return false;
            }

            normalized = firstObject.Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(normalized);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var pathingIntent = ReadNullableBool(doc.RootElement, "pathingIntent", "pathing", "pathingPriority");
            var scriptSubscribeIntent = ReadNullableBool(doc.RootElement, "scriptSubscribeIntent", "subscribeIntent", "scriptSubscribe");
            var scriptDetailIntent = ReadNullableBool(doc.RootElement, "scriptDetailIntent", "detailIntent", "scriptDetail");
            var docHelpIntent = ReadNullableBool(doc.RootElement, "docHelpIntent", "docsIntent", "helpIntent");
            var downloadIntent = ReadNullableBool(doc.RootElement, "downloadIntent", "download");
            var statusQueryIntent = ReadNullableBool(doc.RootElement, "statusQueryIntent", "statusIntent", "statusQuery");
            var realtimeFeatureQuery = ReadNullableBool(doc.RootElement, "realtimeFeatureQuery", "realtimeIntent", "realtimeQuery");
            var allFeaturesRequest = ReadNullableBool(doc.RootElement, "allFeaturesRequest", "allFeaturesIntent");
            var allRequest = ReadNullableBool(doc.RootElement, "allRequest", "subscribeAll", "batchAll");
            var desiredFeatureValue = ReadNullableBool(doc.RootElement, "desiredFeatureValue", "featureToggleValue", "featureValue");
            var featureKey = TryGetFirstStringProperty(doc.RootElement, "featureKey", "feature", "featureName");
            var reason = TryGetFirstStringProperty(doc.RootElement, "reason", "why", "desc");

            var hasAnySignal = pathingIntent.HasValue ||
                               scriptSubscribeIntent.HasValue ||
                               scriptDetailIntent.HasValue ||
                               docHelpIntent.HasValue ||
                               downloadIntent.HasValue ||
                               statusQueryIntent.HasValue ||
                               realtimeFeatureQuery.HasValue ||
                               allFeaturesRequest.HasValue ||
                               allRequest.HasValue ||
                               desiredFeatureValue.HasValue ||
                               !string.IsNullOrWhiteSpace(featureKey);
            if (!hasAnySignal)
            {
                return false;
            }

            hints = new IntentClassificationHints(
                pathingIntent,
                scriptSubscribeIntent,
                scriptDetailIntent,
                docHelpIntent,
                downloadIntent,
                statusQueryIntent,
                realtimeFeatureQuery,
                desiredFeatureValue,
                allFeaturesRequest,
                allRequest,
                featureKey,
                reason);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool? ReadNullableBool(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return property.GetBoolean();
            }

            if (property.ValueKind == JsonValueKind.Number &&
                property.TryGetInt32(out var numeric) &&
                (numeric == 0 || numeric == 1))
            {
                return numeric == 1;
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = property.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var lowered = text.ToLowerInvariant();
            if (lowered is "true" or "1" or "yes" or "y" or "on" or "enable" or "enabled" or "open" or "开启" or "打开" or "启用" or "是")
            {
                return true;
            }

            if (lowered is "false" or "0" or "no" or "n" or "off" or "disable" or "disabled" or "close" or "关闭" or "禁用" or "否")
            {
                return false;
            }
        }

        return null;
    }

    private static string? NormalizeFeatureKey(string? rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return null;
        }

        var trimmed = rawKey.Trim();
        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var (key, aliases) in FeatureAliases)
        {
            if (string.Equals(trimmed, key, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }

            foreach (var alias in aliases)
            {
                if (string.Equals(trimmed, alias, StringComparison.OrdinalIgnoreCase))
                {
                    return key;
                }
            }
        }

        return trimmed.ToLowerInvariant() switch
        {
            "autopick" or "auto_pick" or "pick" => "autoPick",
            "autoskip" or "auto_skip" or "skip" => "autoSkip",
            "autohangout" or "auto_hangout" or "hangout" => "autoHangout",
            "autofishing" or "auto_fishing" or "fishing" => "autoFishing",
            "autocook" or "auto_cook" or "cook" => "autoCook",
            "autoeat" or "auto_eat" or "eat" => "autoEat",
            "quickteleport" or "quick_teleport" or "teleport" => "quickTeleport",
            "mapmask" or "map_mask" or "mask" => "mapMask",
            "skillcd" or "skill_cd" or "cd" => "skillCd",
            _ => null
        };
    }

    private void LogIntentClassification(string userText, IntentClassification intent)
    {
        var payload = new JsonObject
        {
            ["source"] = intent.ClassifierSource,
            ["pathingIntent"] = intent.PathingPriorityIntent,
            ["scriptSubscribeIntent"] = intent.ScriptSubscribeIntent,
            ["scriptDetailIntent"] = intent.ScriptDetailIntent,
            ["docHelpIntent"] = intent.DocHelpIntent,
            ["downloadIntent"] = intent.DownloadIntent,
            ["statusQueryIntent"] = intent.StatusQueryIntent,
            ["realtimeFeatureQuery"] = intent.RealtimeFeatureQueryIntent,
            ["desiredFeatureValue"] = intent.DesiredFeatureValue.HasValue ? JsonValue.Create(intent.DesiredFeatureValue.Value) : null,
            ["allFeaturesRequest"] = intent.AllFeaturesRequest,
            ["allRequest"] = intent.IsAllRequest,
            ["featureKey"] = intent.FeatureKey,
            ["query"] = BuildPathingSearchQuery(userText)
        };
        if (!string.IsNullOrWhiteSpace(intent.ClassifierReason))
        {
            payload["reason"] = intent.ClassifierReason;
        }

        LogChat("intent", payload.ToJsonString(McpCompactJsonOptions));
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

    private static bool IsDocHelpIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (IsPathingPriorityIntentByKeyword(text))
        {
            return false;
        }

        if (TryParseDesiredValue(text, out _) || TryGetFeatureKey(text) != null)
        {
            return false;
        }

        foreach (var keyword in DocHelpKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsScriptSubscribeIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var keyword in ScriptSubscribeKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsScriptDetailIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (IsScriptSubscribeIntent(text))
        {
            return false;
        }

        var hasScriptKeyword = text.Contains("脚本", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("路径", StringComparison.OrdinalIgnoreCase);
        if (!hasScriptKeyword)
        {
            return false;
        }

        foreach (var keyword in ScriptDetailKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAllRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("全部", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("所有", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("全都", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("一次性", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGenericSubscribeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var normalized = query.Trim();
        normalized = normalized.Replace("脚本", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("订阅", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("导入", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("全部", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("所有", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("全都", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
        return normalized.Length == 0;
    }

    private static string BuildDocSearchQuery(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var query = text.Trim();
        query = Regex.Replace(query, @"[，。！？,.!?:：；;（）()\[\]{}""'`]+", " ");
        query = Regex.Replace(query, @"\s+", " ").Trim();
        if (query.Length > 120)
        {
            query = query[..120].Trim();
        }

        return query;
    }

    private static bool IsDownloadIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("下载", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("安装", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("版本", StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<McpToolCall> CoerceRealtimeFeatureToolCalls(IReadOnlyList<McpToolCall> toolCalls, bool isRealtimeFeatureQuery, out string? notice)
    {
        notice = null;
        if (toolCalls.Count == 0 || !isRealtimeFeatureQuery)
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

    private IReadOnlyList<McpToolCall> CoercePathingPriorityToolCalls(string userText, IReadOnlyList<McpToolCall> toolCalls, bool shouldPrioritizePathing, out string? notice)
    {
        notice = null;
        if (toolCalls.Count == 0 || !shouldPrioritizePathing)
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

    private IReadOnlyList<McpToolCall> CoerceScriptSubscribeToolCalls(string userText, IReadOnlyList<McpToolCall> toolCalls, IntentClassification intent, out string? notice)
    {
        notice = null;
        if (toolCalls.Count == 0 || !intent.ScriptSubscribeIntent)
        {
            return toolCalls;
        }

        var subscribeAll = intent.IsAllRequest;
        var cachedCandidates = GetRecentScriptCandidates();
        var rewritten = new List<McpToolCall>(toolCalls.Count);
        var changed = false;

        foreach (var call in toolCalls)
        {
            if (string.Equals(call.Name, "bgi.script.search", StringComparison.OrdinalIgnoreCase))
            {
                var subscribeArgs = new JsonObject();
                var searchLimit = 20;
                string? searchQuery = null;

                if (!string.IsNullOrWhiteSpace(call.ArgumentsJson))
                {
                    try
                    {
                        var parsed = JsonNode.Parse(call.ArgumentsJson) as JsonObject;
                        if (parsed != null)
                        {
                            if (parsed.TryGetPropertyValue("query", out var queryNode) &&
                                queryNode != null &&
                                queryNode.GetValueKind() == JsonValueKind.String)
                            {
                                searchQuery = queryNode.GetValue<string>()?.Trim();
                            }

                            if (parsed.TryGetPropertyValue("limit", out var limitNode) &&
                                limitNode != null &&
                                limitNode.GetValueKind() == JsonValueKind.Number &&
                                limitNode is JsonValue limitValue &&
                                limitValue.TryGetValue<int>(out var parsedLimit))
                            {
                                searchLimit = Math.Clamp(parsedLimit, 1, 100);
                            }
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }

                if (subscribeAll && cachedCandidates.Count > 0)
                {
                    var allNames = cachedCandidates
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(name => JsonValue.Create(name))
                        .Where(node => node != null)
                        .ToArray();
                    if (allNames.Length > 0)
                    {
                        subscribeArgs["names"] = new JsonArray(allNames!);
                    }
                }
                else
                {
                    var effectiveQuery = !string.IsNullOrWhiteSpace(searchQuery)
                        ? searchQuery
                        : BuildPathingSearchQuery(userText);
                    if (!string.IsNullOrWhiteSpace(effectiveQuery) && !IsGenericSubscribeQuery(effectiveQuery))
                    {
                        subscribeArgs["query"] = effectiveQuery;
                        subscribeArgs["limit"] = subscribeAll ? 50 : searchLimit;
                    }
                    else if (cachedCandidates.Count > 0)
                    {
                        subscribeArgs["names"] = new JsonArray(JsonValue.Create(cachedCandidates[0]));
                    }
                }

                subscribeArgs["importNow"] = true;
                rewritten.Add(new McpToolCall("bgi.script.subscribe", subscribeArgs.ToJsonString()));
                changed = true;
                continue;
            }

            if (string.Equals(call.Name, "bgi.script.run", StringComparison.OrdinalIgnoreCase))
            {
                var names = TryExtractScriptRunNames(call.ArgumentsJson);
                if (subscribeAll && cachedCandidates.Count > 0)
                {
                    names = cachedCandidates.ToList();
                }
                else if (names.Count == 0 && cachedCandidates.Count > 0)
                {
                    names = new List<string> { cachedCandidates[0] };
                }

                var args = new JsonObject();
                if (names.Count > 0)
                {
                    args["names"] = new JsonArray(names
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(name => JsonValue.Create(name))
                        .Where(node => node != null)
                        .ToArray()!);
                }
                else
                {
                    var query = BuildPathingSearchQuery(userText);
                    if (!string.IsNullOrWhiteSpace(query) && !IsGenericSubscribeQuery(query))
                    {
                        args["query"] = query;
                    }
                }

                if (subscribeAll)
                {
                    args["limit"] = 50;
                }

                args["importNow"] = true;

                rewritten.Add(new McpToolCall("bgi.script.subscribe", args.ToJsonString()));
                changed = true;
                continue;
            }

            if (string.Equals(call.Name, "bgi.script.subscribe", StringComparison.OrdinalIgnoreCase))
            {
                if (TryNormalizeSubscribeArguments(call.ArgumentsJson, userText, subscribeAll, cachedCandidates, out var normalizedArgs, out var argsChanged))
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

        notice = "检测到“订阅/导入脚本”意图，已将执行操作改写为订阅导入工具（bgi.script.subscribe）。";
        return rewritten;
    }

    private static IReadOnlyList<McpToolCall> CoerceGeneralKnowledgeToolCalls(
        string userText,
        IReadOnlyList<McpToolCall> toolCalls,
        IntentClassification intent,
        out string? notice)
    {
        notice = null;
        if (toolCalls.Count == 0 || !ShouldForceKnowledgeWebSearch(userText, intent, toolCalls))
        {
            return toolCalls;
        }

        var knowledgeCall = BuildKnowledgeWebSearchCall(userText);
        if (knowledgeCall == null)
        {
            return toolCalls;
        }

        notice = "检测到原神角色/培养知识问答，已改为优先联网检索资料，不再搜索脚本。";
        return new[] { knowledgeCall };
    }

    private IReadOnlyList<McpToolCall> CoerceCharacterKnowledgeFollowUpToolCalls(
        string userText,
        IReadOnlyList<McpToolCall> toolCalls,
        IntentClassification intent,
        out string? notice)
    {
        notice = null;
        if (toolCalls.Count == 0 ||
            !IsCharacterKnowledgeQuery(userText) ||
            ShouldAutoPrepareCharacterMaterials(userText, intent) ||
            !HasRecentCharacterKnowledgeResult())
        {
            return toolCalls;
        }

        var hasIrrelevantToolCall = toolCalls.Any(call =>
            !string.Equals(call.Name, "bgi.web.search", StringComparison.OrdinalIgnoreCase));
        if (!hasIrrelevantToolCall)
        {
            return toolCalls;
        }

        notice = "检测到当前问题可以直接基于上一轮角色材料结果整理，无需再次调用无关工具。";
        return [];
    }

    private static bool ShouldForceKnowledgeWebSearch(string userText, IntentClassification intent, IReadOnlyList<McpToolCall> toolCalls)
    {
        if (string.IsNullOrWhiteSpace(userText) || toolCalls.Count == 0)
        {
            return false;
        }

        if (intent.PathingPriorityIntent ||
            intent.ScriptSubscribeIntent ||
            intent.ScriptDetailIntent ||
            intent.DocHelpIntent ||
            intent.DownloadIntent ||
            intent.StatusQueryIntent ||
            intent.RealtimeFeatureQueryIntent ||
            intent.AllFeaturesRequest ||
            !string.IsNullOrWhiteSpace(intent.FeatureKey))
        {
            return false;
        }

        if (toolCalls.Any(call => string.Equals(call.Name, "bgi.web.search", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (IsCharacterKnowledgeQuery(userText))
        {
            return true;
        }

        return IsGeneralKnowledgeQuery(userText) &&
               toolCalls.Any(call => IsScriptToolCallName(call.Name));
    }

    private static bool IsScriptToolCallName(string? toolName)
    {
        return string.Equals(toolName, "bgi.script.search", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(toolName, "bgi.script.list", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(toolName, "bgi.script.detail", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(toolName, "bgi.script.run", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCharacterKnowledgeQuery(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (ContainsAny(text, BetterGiDomainKeywords))
        {
            return false;
        }

        return ContainsAny(text, CharacterKnowledgeKeywords);
    }

    private static McpToolCall? BuildKnowledgeWebSearchCall(string userText)
    {
        var knowledgeQuery = BuildKnowledgeWebSearchQuery(userText);
        if (string.IsNullOrWhiteSpace(knowledgeQuery))
        {
            return null;
        }

        var args = new JsonObject
        {
            ["query"] = knowledgeQuery,
            ["maxResults"] = 3,
            ["provider"] = "auto"
        };
        return new McpToolCall("bgi.web.search", args.ToJsonString());
    }

    private static IReadOnlyList<McpToolCall> BuildCharacterAutomationSearchCalls(
        string userText,
        IntentClassification intent,
        IReadOnlyList<ExecutedMcpToolCall> executedCalls)
    {
        if (!ShouldAutoPrepareCharacterMaterials(userText, intent) || executedCalls.Count == 0)
        {
            return [];
        }

        var queries = new List<string>();
        foreach (var executed in executedCalls)
        {
            if (!string.Equals(executed.Name, "bgi.web.search", StringComparison.OrdinalIgnoreCase) ||
                executed.Result.IsError)
            {
                continue;
            }

            queries.AddRange(ExtractAutomatableMaterialQueries(userText, executed.Result.Content));
        }

        if (queries.Count == 0)
        {
            return [];
        }

        var distinctQueries = queries
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();

        return distinctQueries
            .Select(query => new McpToolCall(
                "bgi.script.search",
                new JsonObject
                {
                    ["query"] = query,
                    ["type"] = "pathing",
                    ["limit"] = 1
                }.ToJsonString()))
            .ToArray();
    }

    private static IReadOnlyList<McpToolCall> BuildCharacterAutomationRunCalls(IReadOnlyList<ExecutedMcpToolCall> executedCalls)
    {
        if (executedCalls.Count == 0)
        {
            return [];
        }

        var calls = new List<McpToolCall>(4);
        foreach (var executed in executedCalls)
        {
            if (!string.Equals(executed.Name, "bgi.script.search", StringComparison.OrdinalIgnoreCase) ||
                executed.Result.IsError)
            {
                continue;
            }

            if (TryBuildScriptExecutionCallsFromSearchResult(executed.Result.Content, out var followUps))
            {
                calls.AddRange(followUps);
            }
        }

        return calls
            .Take(4)
            .ToArray();
    }

    private static bool ShouldAutoPrepareCharacterMaterials(string userText, IntentClassification intent)
    {
        if (string.IsNullOrWhiteSpace(userText) ||
            !IsCharacterKnowledgeQuery(userText))
        {
            return false;
        }

        if (intent.PathingPriorityIntent ||
            intent.ScriptSubscribeIntent ||
            intent.ScriptDetailIntent ||
            intent.DocHelpIntent ||
            intent.DownloadIntent)
        {
            return false;
        }

        return ContainsAny(userText, "准备", "收集", "直接运行", "直接执行", "自动", "能做的部分", "帮我处理");
    }

    private bool HasRecentCharacterKnowledgeResult()
    {
        for (var i = Messages.Count - 1; i >= 0 && i >= Messages.Count - 8; i--)
        {
            var message = Messages[i];
            if (!message.IsMcp || string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            var content = message.Content;
            if (content.Contains("honeyhunter_character_data", StringComparison.OrdinalIgnoreCase) &&
                content.Contains("allRequired", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> ExtractAutomatableMaterialQueries(string userText, string webSearchContent)
    {
        if (string.IsNullOrWhiteSpace(webSearchContent))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(webSearchContent);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("results", out var resultsElement) ||
                resultsElement.ValueKind != JsonValueKind.Array ||
                resultsElement.GetArrayLength() == 0)
            {
                return [];
            }

            var materialSourceName = userText.Contains("天赋", StringComparison.OrdinalIgnoreCase)
                ? "skillAscension"
                : userText.Contains("突破", StringComparison.OrdinalIgnoreCase)
                    ? "characterAscension"
                    : "allRequired";

            if (resultsElement[0].ValueKind != JsonValueKind.Object ||
                !resultsElement[0].TryGetProperty("materials", out var materialsElement) ||
                materialsElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            if (!materialsElement.TryGetProperty(materialSourceName, out var sourceElement) ||
                sourceElement.ValueKind != JsonValueKind.Array)
            {
                if (!materialsElement.TryGetProperty("allRequired", out sourceElement) ||
                    sourceElement.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }
            }

            var queries = new List<string>();
            foreach (var item in sourceElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var materialName = nameElement.GetString()?.Trim();
                var query = NormalizeAutomatableMaterialQuery(materialName);
                if (!string.IsNullOrWhiteSpace(query))
                {
                    queries.Add(query);
                }
            }

            return queries
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? NormalizeAutomatableMaterialQuery(string? materialName)
    {
        if (string.IsNullOrWhiteSpace(materialName))
        {
            return null;
        }

        var name = materialName.Trim();
        if (ContainsAny(name, "摩拉", "智识之冕", "哲学", "指引", "教导", "玛瑙", "北风之环", "常燃火种"))
        {
            return null;
        }

        if (name.EndsWith("绘卷", StringComparison.OrdinalIgnoreCase))
        {
            return "绘卷";
        }

        if (ContainsAny(name, "蘑菇", "花", "果", "草", "矿", "鱼"))
        {
            return name;
        }

        return name.EndsWith("绘卷", StringComparison.OrdinalIgnoreCase) ? "绘卷" : null;
    }

    private static bool TryBuildScriptExecutionCallsFromSearchResult(string searchContent, out List<McpToolCall> calls)
    {
        calls = new List<McpToolCall>();
        if (string.IsNullOrWhiteSpace(searchContent))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(searchContent);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (TryGetFirstRunnableScriptName(doc.RootElement, "matches", out var localName))
            {
                calls.Add(new McpToolCall(
                    "bgi.script.run",
                    new JsonObject
                    {
                        ["name"] = localName,
                        ["type"] = "pathing"
                    }.ToJsonString()));
                return true;
            }

            if (doc.RootElement.TryGetProperty("remote", out var remoteElement) &&
                remoteElement.ValueKind == JsonValueKind.Object &&
                TryGetFirstRunnableScriptName(remoteElement, "matches", out var remoteName))
            {
                calls.Add(new McpToolCall(
                    "bgi.script.subscribe",
                    new JsonObject
                    {
                        ["names"] = new JsonArray(JsonValue.Create(remoteName)),
                        ["importNow"] = true
                    }.ToJsonString()));
                calls.Add(new McpToolCall(
                    "bgi.script.run",
                    new JsonObject
                    {
                        ["name"] = remoteName,
                        ["type"] = "pathing"
                    }.ToJsonString()));
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetFirstRunnableScriptName(JsonElement root, string propertyName, out string scriptName)
    {
        scriptName = string.Empty;
        if (!root.TryGetProperty(propertyName, out var matchesElement) ||
            matchesElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in matchesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var candidate = nameElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(candidate) ||
                candidate.Contains("不可用", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            scriptName = candidate;
            return true;
        }

        return false;
    }

    private static string BuildKnowledgeWebSearchQuery(string text)
    {
        var query = BuildDocSearchQuery(text);
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        if (!query.Contains("原神", StringComparison.OrdinalIgnoreCase))
        {
            query = $"原神 {query}";
        }

        if (IsCharacterKnowledgeQuery(text) &&
            ContainsAny(text, "练", "培养", "养成", "毕业", "需要什么", "要什么") &&
            !ContainsAny(query, "材料", "突破", "天赋", "武器", "圣遗物", "配队"))
        {
            query = $"{query} 培养材料 突破材料 天赋材料";
        }

        return query.Trim();
    }

    private static TimeSpan GetMcpToolTimeout(string toolName)
    {
        if (string.Equals(toolName, "bgi.script.subscribe", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromSeconds(180);
        }

        if (string.Equals(toolName, "bgi.script.search", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(toolName, "search_scripts", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromSeconds(90);
        }

        if (string.Equals(toolName, "search_docs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(toolName, "get_feature_detail", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(toolName, "get_download_info", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(toolName, "get_faq", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(toolName, "get_quickstart", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromSeconds(60);
        }

        return DefaultMcpRequestTimeout;
    }

    private static bool TryNormalizeSubscribeArguments(
        string argumentsJson,
        string userText,
        bool subscribeAll,
        IReadOnlyList<string> cachedCandidates,
        out string normalizedArgsJson,
        out bool changed)
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
                var parsed = JsonNode.Parse(argumentsJson);
                if (parsed is not JsonObject parsedObj)
                {
                    args = new JsonObject();
                    changed = true;
                }
                else
                {
                    args = parsedObj;
                }
            }
            catch (JsonException)
            {
                args = new JsonObject();
                changed = true;
            }
        }

        if (PruneUnsupportedSubscribeArguments(args))
        {
            changed = true;
        }

        var names = new List<string>();
        if (args.TryGetPropertyValue("name", out var singleNameNode) &&
            singleNameNode != null &&
            singleNameNode.GetValueKind() == JsonValueKind.String)
        {
            var singleName = singleNameNode.GetValue<string>()?.Trim();
            if (!string.IsNullOrWhiteSpace(singleName))
            {
                names.Add(singleName);
            }
        }

        if (args.TryGetPropertyValue("names", out var namesNode) &&
            namesNode is JsonArray namesArray)
        {
            foreach (var node in namesArray)
            {
                if (node == null || node.GetValueKind() != JsonValueKind.String)
                {
                    continue;
                }

                var name = node.GetValue<string>()?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }

        var query = args.TryGetPropertyValue("query", out var queryNode) &&
                    queryNode != null &&
                    queryNode.GetValueKind() == JsonValueKind.String
            ? queryNode.GetValue<string>()?.Trim()
            : null;

        if (names.Count == 0 && string.IsNullOrWhiteSpace(query))
        {
            if (cachedCandidates.Count > 0)
            {
                names = subscribeAll ? cachedCandidates.ToList() : new List<string> { cachedCandidates[0] };
                changed = true;
            }
            else
            {
                var builtQuery = BuildPathingSearchQuery(userText);
                if (!string.IsNullOrWhiteSpace(builtQuery) && !IsGenericSubscribeQuery(builtQuery))
                {
                    args["query"] = builtQuery;
                    changed = true;
                }
            }
        }

        if (names.Count > 0)
        {
            var distinctNames = names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => JsonValue.Create(name))
                .Where(node => node != null)
                .ToArray();

            args["names"] = new JsonArray(distinctNames!);
            if (args.ContainsKey("name"))
            {
                args.Remove("name");
            }

            if (args.ContainsKey("query"))
            {
                args.Remove("query");
            }

            changed = true;
        }

        if (subscribeAll &&
            (!args.TryGetPropertyValue("limit", out var limitNode) ||
             limitNode == null ||
             limitNode.GetValueKind() == JsonValueKind.Null))
        {
            args["limit"] = 50;
            changed = true;
        }

        if (!args.ContainsKey("importNow") &&
            !args.ContainsKey("previewOnly") &&
            !args.ContainsKey("dryRun"))
        {
            args["importNow"] = true;
            changed = true;
        }

        normalizedArgsJson = args.ToJsonString();
        return true;
    }

    private static bool PruneUnsupportedSubscribeArguments(JsonObject args)
    {
        var removeKeys = new List<string>();
        foreach (var item in args)
        {
            if (SubscribeArgumentAllowedKeys.Contains(item.Key))
            {
                continue;
            }

            removeKeys.Add(item.Key);
        }

        if (removeKeys.Count == 0)
        {
            return false;
        }

        foreach (var key in removeKeys)
        {
            args.Remove(key);
        }

        return true;
    }

    private static List<string> TryExtractScriptRunNames(string argumentsJson)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return names;
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return names;
            }

            if (doc.RootElement.TryGetProperty("name", out var nameElement) &&
                nameElement.ValueKind == JsonValueKind.String)
            {
                var name = nameElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }

            if (doc.RootElement.TryGetProperty("names", out var namesElement) &&
                namesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in namesElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var name = item.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name);
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return names;
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
        var featureControlContext = IsLikelyFeatureControlText(text);
        foreach (var (key, aliases) in FeatureAliases)
        {
            foreach (var alias in aliases)
            {
                if (string.Equals(alias, "拾取", StringComparison.OrdinalIgnoreCase) &&
                    !featureControlContext &&
                    !text.Contains("自动拾取", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

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

        if (TryParseJsonModeEnvelope(reply, out var jsonModeCalls, out var jsonReply))
        {
            if (!string.IsNullOrWhiteSpace(jsonReply))
            {
                return jsonReply.Trim();
            }

            if (jsonModeCalls.Count > 0)
            {
                return string.Empty;
            }
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

        var sanitizedReply = string.Join(Environment.NewLine, sanitized).Trim();
        return NormalizeStructuredReplyForUser(sanitizedReply);
    }

    private bool IsInvalidFinalAnswerReply(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return true;
        }

        if (ParseMcpToolCalls(reply).Count > 0)
        {
            return true;
        }

        var trimmed = reply.Trim();
        return LooksLikeStructuredAssistantOutput(trimmed);
    }

    private static string NormalizeStructuredReplyForUser(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return reply;
        }

        var trimmed = reply.Trim();
        if (!LooksLikeStructuredAssistantOutput(trimmed))
        {
            return trimmed;
        }

        if (TryExtractUserFacingReplyFromJson(trimmed, out var structuredReply))
        {
            return structuredReply;
        }

        if (TryExtractUserFacingReplyFromLooseJson(trimmed, out var looseReply))
        {
            return looseReply;
        }

        if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return trimmed;
    }

    private static bool LooksLikeStructuredAssistantOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.StartsWith("{", StringComparison.Ordinal) || text.StartsWith("[", StringComparison.Ordinal))
        {
            return true;
        }

        return text.Contains("\"toolCalls\"", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("\"reply\"", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("\"result\"", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("\"results\"", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("toolCalls", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractUserFacingReplyFromJson(string text, out string reply)
    {
        reply = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var candidates = new List<string>();
        var normalized = NormalizeJsonPrefix(text).Trim();
        if (normalized.StartsWith("{", StringComparison.Ordinal) || normalized.StartsWith("[", StringComparison.Ordinal))
        {
            candidates.Add(normalized);
        }

        foreach (var json in ExtractJsonObjects(normalized))
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                candidates.Add(json.Trim());
            }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (TryExtractReplyFromJsonElement(doc.RootElement, out reply))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
            }
        }

        return false;
    }

    private static bool TryExtractReplyFromJsonElement(JsonElement element, out string reply)
    {
        reply = string.Empty;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                reply = NormalizeLooseJsonText(element.GetString() ?? string.Empty);
                return reply.Length > 0;
            case JsonValueKind.Array:
                return TryFormatCollectionReply(element, out reply);
            case JsonValueKind.Object:
                var direct = TryGetFirstStringProperty(
                    element,
                    "reply",
                    "message",
                    "text",
                    "content",
                    "finalReply",
                    "final_reply",
                    "answer");
                if (!string.IsNullOrWhiteSpace(direct))
                {
                    reply = NormalizeLooseJsonText(direct);
                    if (!string.IsNullOrWhiteSpace(reply))
                    {
                        return true;
                    }
                }

                if (element.TryGetProperty("result", out var resultElement) &&
                    TryFormatCollectionReply(resultElement, out reply))
                {
                    return true;
                }

                if (element.TryGetProperty("results", out var resultsElement) &&
                    TryFormatCollectionReply(resultsElement, out reply))
                {
                    return true;
                }

                if (element.TryGetProperty("items", out var itemsElement) &&
                    TryFormatCollectionReply(itemsElement, out reply))
                {
                    return true;
                }

                if (element.TryGetProperty("matches", out var matchesElement) &&
                    TryFormatCollectionReply(matchesElement, out reply))
                {
                    return true;
                }

                if (element.TryGetProperty("data", out var dataElement) &&
                    TryFormatCollectionReply(dataElement, out reply))
                {
                    return true;
                }

                return TryExtractLineFromResultItem(element, out reply);
            default:
                return false;
        }
    }

    private static bool TryFormatCollectionReply(JsonElement element, out string reply)
    {
        reply = string.Empty;
        if (element.ValueKind == JsonValueKind.String)
        {
            reply = NormalizeLooseJsonText(element.GetString() ?? string.Empty);
            return reply.Length > 0;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            return TryExtractLineFromResultItem(element, out reply);
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in element.EnumerateArray())
        {
            if (lines.Count >= 8)
            {
                break;
            }

            if (!TryExtractLineFromResultItem(item, out var line))
            {
                continue;
            }

            if (seen.Add(line))
            {
                lines.Add(line);
            }
        }

        if (lines.Count == 0)
        {
            return false;
        }

        reply = string.Join(Environment.NewLine, lines.Select((line, index) => $"{index + 1}. {line}"));
        return true;
    }

    private static bool TryExtractLineFromResultItem(JsonElement element, out string line)
    {
        line = string.Empty;
        if (element.ValueKind == JsonValueKind.String)
        {
            line = NormalizeLooseJsonText(element.GetString() ?? string.Empty);
            return line.Length > 0;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            return TryFormatCollectionReply(element, out line);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var title = TryGetFirstStringProperty(element, "title", "name");
        var description = TryGetFirstStringProperty(element, "description", "summary", "snippet", "text", "content", "message", "reply");
        var primary = TryGetFirstStringProperty(element, "description", "summary", "snippet", "text", "content", "message", "reply", "title", "name");
        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(description))
        {
            line = NormalizeLooseJsonText($"{title}：{description}");
            return line.Length > 0;
        }

        if (!string.IsNullOrWhiteSpace(primary))
        {
            line = NormalizeLooseJsonText(primary);
            return line.Length > 0;
        }

        return false;
    }

    private static bool TryExtractUserFacingReplyFromLooseJson(string text, out string reply)
    {
        reply = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var replyMatch = LooseReplyFieldRegex.Match(text);
        if (replyMatch.Success)
        {
            var extracted = NormalizeLooseJsonText(replyMatch.Groups["value"].Value);
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                reply = extracted;
                return true;
            }
        }

        var descriptionMatches = LooseDescriptionFieldRegex.Matches(text);
        if (descriptionMatches.Count == 0)
        {
            return false;
        }

        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in descriptionMatches)
        {
            if (lines.Count >= 8)
            {
                break;
            }

            var extracted = NormalizeLooseJsonText(match.Groups["value"].Value);
            if (string.IsNullOrWhiteSpace(extracted) || !seen.Add(extracted))
            {
                continue;
            }

            lines.Add(extracted);
        }

        if (lines.Count == 0)
        {
            return false;
        }

        reply = lines.Count == 1
            ? lines[0]
            : string.Join(Environment.NewLine, lines.Select((line, index) => $"{index + 1}. {line}"));
        return true;
    }

    private static string NormalizeLooseJsonText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        normalized = normalized.Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal);
        normalized = DecodeUnicodeEscapes(normalized);
        normalized = normalized.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        normalized = string.Join(
            Environment.NewLine,
            normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Trim(' ', '"', '\'', '，', ',', '。', '.', '；', ';');
    }

    private string EnsureMcpFailureConsistency(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return reply;
        }

        if (!LooksLikeSuccessClaim(reply))
        {
            return reply;
        }

        var failureReason = TryGetCurrentTurnMcpFailureReason();
        if (string.IsNullOrWhiteSpace(failureReason))
        {
            return reply;
        }

        return $"本次操作未成功：{failureReason}。请稍后重试，或先使用 previewOnly=true 仅生成订阅链接。";
    }

    private string? TryGetCurrentTurnMcpFailureReason()
    {
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            var message = Messages[i];
            if (message.IsUser)
            {
                break;
            }

            if (!message.IsMcp || string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            if (!message.Content.Contains("调用失败", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var extracted = TryExtractMcpFailureReason(message.Content);
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                return extracted;
            }

            return "MCP 返回调用失败";
        }

        return null;
    }

    private static bool LooksLikeSuccessClaim(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return false;
        }

        var hasSuccess = reply.Contains("成功", StringComparison.OrdinalIgnoreCase) ||
                         reply.Contains("已订阅", StringComparison.OrdinalIgnoreCase) ||
                         reply.Contains("完成订阅", StringComparison.OrdinalIgnoreCase) ||
                         reply.Contains("success", StringComparison.OrdinalIgnoreCase);
        if (!hasSuccess)
        {
            return false;
        }

        return !reply.Contains("失败", StringComparison.OrdinalIgnoreCase) &&
               !reply.Contains("错误", StringComparison.OrdinalIgnoreCase) &&
               !reply.Contains("未成功", StringComparison.OrdinalIgnoreCase) &&
               !reply.Contains("超时", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryExtractMcpFailureReason(string mcpMessage)
    {
        if (string.IsNullOrWhiteSpace(mcpMessage))
        {
            return null;
        }

        var splitIndex = mcpMessage.IndexOf('\n');
        if (splitIndex < 0 || splitIndex + 1 >= mcpMessage.Length)
        {
            return null;
        }

        var body = DecodeUnicodeEscapes(mcpMessage[(splitIndex + 1)..]).Trim();
        if (body.Length == 0)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var importError = TryGetStringProperty(doc.RootElement, "importError");
            if (!string.IsNullOrWhiteSpace(importError))
            {
                return importError;
            }

            var error = TryGetStringProperty(doc.RootElement, "error");
            if (!string.IsNullOrWhiteSpace(error))
            {
                return error;
            }

            var message = TryGetStringProperty(doc.RootElement, "message");
            if (!string.IsNullOrWhiteSpace(message))
            {
                return message;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string? TryGetStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString()?.Trim();
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

    private IReadOnlyList<McpToolCall> NormalizeAndFilterToolCalls(IReadOnlyList<McpToolCall> toolCalls, out string? notice)
    {
        notice = null;
        if (toolCalls.Count == 0)
        {
            return toolCalls;
        }

        var toolNames = BuildToolNameSet();
        var normalized = new List<McpToolCall>(toolCalls.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rewrittenAliases = new List<string>(2);
        var ignoredUnknown = new List<string>(2);

        foreach (var call in toolCalls)
        {
            var rawName = call.Name?.Trim();
            if (string.IsNullOrWhiteSpace(rawName))
            {
                continue;
            }

            var resolvedName = ResolveToolNameAlias(rawName, toolNames, out var aliasChanged);
            if (string.IsNullOrWhiteSpace(resolvedName))
            {
                if (ignoredUnknown.Count < 3)
                {
                    ignoredUnknown.Add(rawName);
                }

                continue;
            }

            if (toolNames.Count > 0 && !toolNames.Contains(resolvedName))
            {
                if (ignoredUnknown.Count < 3)
                {
                    ignoredUnknown.Add(rawName);
                }

                continue;
            }

            var dedupeKey = $"{resolvedName}\n{call.ArgumentsJson}";
            if (!seen.Add(dedupeKey))
            {
                continue;
            }

            if (aliasChanged && rewrittenAliases.Count < 3)
            {
                rewrittenAliases.Add($"{rawName}→{resolvedName}");
            }

            normalized.Add(aliasChanged
                ? new McpToolCall(resolvedName, call.ArgumentsJson)
                : call);
        }

        if (rewrittenAliases.Count > 0 || ignoredUnknown.Count > 0)
        {
            var parts = new List<string>(2);
            if (rewrittenAliases.Count > 0)
            {
                parts.Add($"已自动修正工具名：{string.Join("，", rewrittenAliases)}");
            }

            if (ignoredUnknown.Count > 0)
            {
                parts.Add($"已忽略未知工具：{string.Join("，", ignoredUnknown)}");
            }

            notice = string.Join("。", parts) + "。";
        }

        return normalized;
    }

    private static string? ResolveToolNameAlias(string rawName, HashSet<string> toolNames, out bool changed)
    {
        changed = false;
        var name = rawName.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        if (toolNames.Contains(name))
        {
            return name;
        }

        var alias = name.ToLowerInvariant() switch
        {
            "get_logs" => "bgi.get_logs",
            "get_status" => "bgi.get_features",
            "bgi.get_status" => "bgi.get_features",
            "get_features" => "bgi.get_features",
            "set_features" => "bgi.set_features",
            "language.get" => "bgi.language.get",
            "language.set" => "bgi.language.set",
            "script.search" => "bgi.script.search",
            "script.detail" => "bgi.script.detail",
            "script.list" => "bgi.script.list",
            "script.run" => "bgi.script.run",
            "script.subscribe" => "bgi.script.subscribe",
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(alias))
        {
            changed = true;
            return alias;
        }

        if (!name.StartsWith("bgi.", StringComparison.OrdinalIgnoreCase) &&
            IsLikelyToolName(name))
        {
            var prefixed = $"bgi.{name}";
            if (toolNames.Count == 0 || toolNames.Contains(prefixed))
            {
                changed = true;
                return prefixed;
            }
        }

        if (name.StartsWith("bgi.", StringComparison.OrdinalIgnoreCase) &&
            IsLikelyToolName(name))
        {
            return name;
        }

        return null;
    }

    private static bool IsLikelyToolName(string name)
    {
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private async Task<AiReplyResult> GetAiReplyAsync(IReadOnlyList<AiChatMessage> payloadMessages, AiConfig? runtimeConfig = null)
    {
        var requestConfig = runtimeConfig ?? Config;
        using var cts = new CancellationTokenSource(DefaultAiRequestTimeout);
        if (!requestConfig.UseStreamingResponse)
        {
            var reply = await _chatService.GetChatCompletionAsync(requestConfig, payloadMessages, cts.Token);
            return new AiReplyResult(reply, -1);
        }

        var streamMessageIndex = AddChatMessageAndGetIndex("assistant", string.Empty);
        var streamedBuilder = new StringBuilder();
        var pendingBuffer = new StringBuilder();
        var lastFlushUtc = DateTime.UtcNow;

        try
        {
            var streamedReply = await _chatService.GetChatCompletionAsync(
                    requestConfig,
                    payloadMessages,
                    cts.Token,
                    onDelta: delta =>
                    {
                        if (string.IsNullOrEmpty(delta))
                        {
                            return Task.CompletedTask;
                        }

                        streamedBuilder.Append(delta);
                        pendingBuffer.Append(delta);
                        var now = DateTime.UtcNow;
                        if (pendingBuffer.Length >= StreamingUiFlushChars ||
                            now - lastFlushUtc >= StreamingUiFlushInterval)
                        {
                            FlushPendingBuffer();
                        }

                        return Task.CompletedTask;
                    })
                .ConfigureAwait(false);

            FlushPendingBuffer();
            var finalReply = string.IsNullOrWhiteSpace(streamedReply)
                ? streamedBuilder.ToString()
                : streamedReply;
            if (string.IsNullOrWhiteSpace(finalReply))
            {
                RemoveChatMessageAt(streamMessageIndex);
                return new AiReplyResult(string.Empty, -1);
            }

            UpdateChatMessageAt(streamMessageIndex, "assistant", finalReply);
            return new AiReplyResult(finalReply, streamMessageIndex);
        }
        catch
        {
            RemoveChatMessageAt(streamMessageIndex);
            throw;
        }

        void FlushPendingBuffer()
        {
            if (pendingBuffer.Length == 0)
            {
                return;
            }

            UpdateChatMessageAt(streamMessageIndex, "assistant", streamedBuilder.ToString());
            pendingBuffer.Clear();
            lastFlushUtc = DateTime.UtcNow;
        }
    }

    private async Task<string?> TryGenerateNoToolFallbackReplyAsync()
    {
        if (string.IsNullOrWhiteSpace(Config.ApiKey) || string.IsNullOrWhiteSpace(Config.Model))
        {
            return null;
        }

        try
        {
            var payload = (await BuildPayloadMessagesAsync()).ToList();
            payload.Add(new AiChatMessage("system", NoToolFallbackPrompt));

            const int maxAttempts = 2;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var reply = await _chatService.GetChatCompletionAsync(
                        BuildNoToolFallbackConfig(),
                        payload,
                        cts.Token)
                    .ConfigureAwait(false);
                reply = SanitizeAssistantReply(reply);
                reply = EnsureMcpFailureConsistency(reply);
                if (!IsInvalidFinalAnswerReply(reply))
                {
                    return reply.Trim();
                }

                if (attempt >= maxAttempts)
                {
                    break;
                }

                payload.Add(new AiChatMessage(
                    "system",
                    "上一条回复仍然是结构化输出或工具调用格式。现在只允许输出自然语言最终答案，禁止任何 JSON/toolCalls。"));
            }

            return null;
        }
        catch (Exception ex)
        {
            LogChat("system", $"生成无工具回退回复失败：{ex.Message}");
            return null;
        }
    }

    private AiConfig BuildToolPlanningConfig()
    {
        return BuildStageConfig(useJsonMode: true, useStreamingResponse: Config.UseStreamingResponse);
    }

    private AiConfig BuildFinalAnswerConfig()
    {
        return BuildStageConfig(useJsonMode: false, useStreamingResponse: Config.UseStreamingResponse);
    }

    private AiConfig BuildNoToolFallbackConfig()
    {
        return BuildStageConfig(useJsonMode: false, useStreamingResponse: false);
    }

    private static AiConfig BuildContextCompressionConfig(int maxContextChars, CompressionRuntimeOptions options)
    {
        return new AiConfig
        {
            BaseUrl = options.BaseUrl,
            ApiKey = options.ApiKey,
            Model = options.Model,
            UseJsonMode = false,
            UseStreamingResponse = false,
            AutoExecuteMcpToolCalls = false,
            MaxContextChars = Math.Max(4096, NormalizeMaxContextChars(maxContextChars))
        };
    }

    private static AiConfig BuildMcpCompressionConfig(int maxContextChars, CompressionRuntimeOptions options)
    {
        return new AiConfig
        {
            BaseUrl = options.BaseUrl,
            ApiKey = options.ApiKey,
            Model = options.Model,
            UseJsonMode = false,
            UseStreamingResponse = false,
            AutoExecuteMcpToolCalls = false,
            MaxContextChars = Math.Max(4096, NormalizeMaxContextChars(maxContextChars))
        };
    }

    private AiConfig BuildStageConfig(bool useJsonMode, bool useStreamingResponse)
    {
        return new AiConfig
        {
            BaseUrl = Config.BaseUrl,
            ApiKey = Config.ApiKey,
            Model = Config.Model,
            UseJsonMode = useJsonMode,
            UseStreamingResponse = useStreamingResponse,
            AutoExecuteMcpToolCalls = false,
            MaxContextChars = NormalizeMaxContextChars(Config.MaxContextChars)
        };
    }

    private static string NormalizeChatMessageContent(string content, int maxChars = DefaultMaxChatMessageChars)
    {
        var safeContent = content ?? string.Empty;
        if (safeContent.Length > maxChars)
        {
            safeContent = TruncateForPayload(safeContent, maxChars);
        }

        return safeContent;
    }

    private int AddChatMessageAndGetIndex(string role, string content, int maxChars = DefaultMaxChatMessageChars)
    {
        var safeContent = NormalizeChatMessageContent(content, maxChars);
        var index = Messages.Count;
        Messages.Add(new AiChatMessage(role, safeContent));
        InvalidateCompressedContextCache();
        return index;
    }

    private void AddChatMessage(string role, string content, int maxChars = DefaultMaxChatMessageChars)
    {
        _ = AddChatMessageAndGetIndex(role, content, maxChars);
    }

    private int UpsertAssistantPlanningMessage(int existingIndex, string planningReply, int maxChars = DefaultMaxChatMessageChars)
    {
        if (string.IsNullOrWhiteSpace(planningReply))
        {
            return -1;
        }

        if (existingIndex >= 0)
        {
            UpdateChatMessageAt(existingIndex, "assistant", planningReply, maxChars);
            return existingIndex;
        }

        return AddChatMessageAndGetIndex("assistant", planningReply, maxChars);
    }

    private void UpdateChatMessageAt(int index, string role, string content, int maxChars = DefaultMaxChatMessageChars)
    {
        if (index < 0)
        {
            return;
        }

        UIDispatcherHelper.Invoke(() =>
        {
            if (index < 0 || index >= Messages.Count)
            {
                return;
            }

            var safeContent = NormalizeChatMessageContent(content, maxChars);
            Messages[index] = new AiChatMessage(role, safeContent);
            InvalidateCompressedContextCache();
        });
    }

    private void RemoveChatMessageAt(int index)
    {
        if (index < 0)
        {
            return;
        }

        UIDispatcherHelper.Invoke(() =>
        {
            if (index < 0 || index >= Messages.Count)
            {
                return;
            }

            Messages.RemoveAt(index);
            InvalidateCompressedContextCache();
        });
    }

    private void InvalidateCompressedContextCache()
    {
        _cachedCompressedContextSummary = null;
        _cachedCompressedContextSignature = int.MinValue;
        _cachedCompressedMcpSummaryBySignature.Clear();
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    private static T RunOnUiThread<T>(Func<T> func)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            return func();
        }

        return dispatcher.Invoke(func);
    }

    private static Task RunOnUiThreadAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            return action();
        }

        return dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    private void SetStatusTextSafe(string text)
    {
        UIDispatcherHelper.Invoke(() => StatusText = text);
    }

    private string GetStatusTextSafe()
    {
        var text = string.Empty;
        UIDispatcherHelper.Invoke(() => text = StatusText);
        return text;
    }

    private IReadOnlyList<McpToolCall> ApplyToolExecutionGuards(
        IReadOnlyList<McpToolCall> toolCalls,
        IReadOnlyDictionary<string, int> executedCounts,
        IReadOnlySet<string> executedWebSearchQueryKeys,
        out string? notice)
    {
        notice = null;
        if (toolCalls.Count == 0)
        {
            return toolCalls;
        }

        var executedWebSearch = executedCounts.TryGetValue("bgi.web.search", out var count)
            ? count
            : 0;
        var remainingWebSearch = Math.Max(0, MaxWebSearchToolCallsPerTurn - executedWebSearch);
        var filtered = new List<McpToolCall>(toolCalls.Count);
        var blockedWebSearch = 0;
        var blockedDuplicateWebSearch = 0;
        var queryKeys = new HashSet<string>(executedWebSearchQueryKeys, StringComparer.Ordinal);

        foreach (var call in toolCalls)
        {
            if (!IsWebSearchToolCallName(call.Name))
            {
                filtered.Add(call);
                continue;
            }

            var query = TryExtractWebSearchQuery(call.ArgumentsJson);
            var queryKey = NormalizeWebSearchQueryKey(query);
            if (!string.IsNullOrWhiteSpace(queryKey) && queryKeys.Contains(queryKey))
            {
                blockedDuplicateWebSearch++;
                continue;
            }

            if (remainingWebSearch > 0)
            {
                filtered.Add(call);
                remainingWebSearch--;
                if (!string.IsNullOrWhiteSpace(queryKey))
                {
                    queryKeys.Add(queryKey);
                }
                continue;
            }

            blockedWebSearch++;
        }

        if (blockedWebSearch > 0 || blockedDuplicateWebSearch > 0)
        {
            var noticeParts = new List<string>(2);
            if (blockedWebSearch > 0)
            {
                noticeParts.Add($"已拦截额外 {blockedWebSearch} 次 bgi.web.search（本轮上限 {MaxWebSearchToolCallsPerTurn}）");
            }

            if (blockedDuplicateWebSearch > 0)
            {
                noticeParts.Add($"已拦截重复检索 {blockedDuplicateWebSearch} 次");
            }

            notice = string.Join("，", noticeParts) + "，请基于已有结果直接回答。";
        }

        return filtered;
    }

    private static bool IsWebSearchToolCallName(string? name)
    {
        return string.Equals(name, "bgi.web.search", StringComparison.OrdinalIgnoreCase);
    }

    private static void RecordPlannedToolCalls(IReadOnlyList<McpToolCall> toolCalls, IDictionary<string, int> counts)
    {
        if (toolCalls.Count == 0)
        {
            return;
        }

        foreach (var call in toolCalls)
        {
            if (string.IsNullOrWhiteSpace(call.Name))
            {
                continue;
            }

            if (counts.TryGetValue(call.Name, out var existing))
            {
                counts[call.Name] = existing + 1;
            }
            else
            {
                counts[call.Name] = 1;
            }
        }
    }

    private static void RecordExecutedWebSearchQueries(IReadOnlyList<McpToolCall> toolCalls, ISet<string> queryKeys)
    {
        if (toolCalls.Count == 0 || queryKeys.Count > 64)
        {
            return;
        }

        foreach (var call in toolCalls)
        {
            if (!IsWebSearchToolCallName(call.Name))
            {
                continue;
            }

            var query = TryExtractWebSearchQuery(call.ArgumentsJson);
            var queryKey = NormalizeWebSearchQueryKey(query);
            if (!string.IsNullOrWhiteSpace(queryKey))
            {
                queryKeys.Add(queryKey);
            }
        }
    }

    private static string? TryExtractWebSearchQuery(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("query", out var queryElement) ||
                queryElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return queryElement.GetString()?.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeWebSearchQueryKey(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        var normalized = query.Trim().ToLowerInvariant();
        normalized = normalized.Replace("原神", " ", StringComparison.Ordinal)
            .Replace("genshin", " ", StringComparison.Ordinal)
            .Replace("impact", " ", StringComparison.Ordinal)
            .Replace("材料", " ", StringComparison.Ordinal)
            .Replace("介绍", " ", StringComparison.Ordinal)
            .Replace("列表", " ", StringComparison.Ordinal);
        normalized = Regex.Replace(normalized, @"[\p{P}\p{S}\s]+", string.Empty);
        return normalized;
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

        var buffer = new StringBuilder(content.Length);
        for (var i = 0; i < content.Length; i++)
        {
            if (i + 5 >= content.Length ||
                content[i] != '\\' ||
                content[i + 1] != 'u')
            {
                buffer.Append(content[i]);
                continue;
            }

            var hex = content.Substring(i + 2, 4);
            if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codeUnit))
            {
                buffer.Append(content[i]);
                continue;
            }

            var decoded = (char)codeUnit;
            if (char.IsHighSurrogate(decoded))
            {
                if (i + 11 < content.Length &&
                    content[i + 6] == '\\' &&
                    content[i + 7] == 'u')
                {
                    var nextHex = content.Substring(i + 8, 4);
                    if (ushort.TryParse(nextHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lowCodeUnit))
                    {
                        var low = (char)lowCodeUnit;
                        if (char.IsLowSurrogate(low))
                        {
                            buffer.Append(decoded);
                            buffer.Append(low);
                            i += 11;
                            continue;
                        }
                    }
                }

                buffer.Append('\uFFFD');
                i += 5;
                continue;
            }

            if (char.IsLowSurrogate(decoded))
            {
                buffer.Append('\uFFFD');
                i += 5;
                continue;
            }

            buffer.Append(decoded);
            i += 5;
        }

        return buffer.ToString();
    }

    private async Task<IReadOnlyList<AiChatMessage>> BuildPayloadMessagesAsync()
    {
        var options = CaptureCompressionRuntimeOptions();
        var systemPrompt = BuildSystemPrompt();
        var sourceMessages = CapturePayloadSourceMessages();
        var maxContextChars = NormalizeMaxContextChars(Config.MaxContextChars);
        return await Task.Run(async () =>
        {
            var mcpLimit = Math.Min(DefaultMaxMcpResultChars, maxContextChars);
            var history = await BuildPayloadHistoryEntriesAsync(sourceMessages, mcpLimit, options).ConfigureAwait(false);
            var initial = BuildPayloadMessagesCore(history, maxContextChars, systemPrompt);
            if (!initial.ContextOverflow)
            {
                return initial.Payload;
            }

            var compressedSummary = await TryCompressContextAsync(initial.MaxContextChars, history, options).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(compressedSummary))
            {
                return initial.Payload;
            }

            return BuildPayloadMessagesCore(history, maxContextChars, systemPrompt, compressedSummary).Payload;
        }).ConfigureAwait(false);
    }

    private CompressionRuntimeOptions CaptureCompressionRuntimeOptions()
    {
        return new CompressionRuntimeOptions(
            Config.BaseUrl ?? string.Empty,
            Config.ApiKey ?? string.Empty,
            Config.Model ?? string.Empty);
    }

    private List<PayloadSourceMessage> CapturePayloadSourceMessages()
    {
        var snapshot = new List<PayloadSourceMessage>(Messages.Count);
        foreach (var message in Messages)
        {
            if (message.IsMcp)
            {
                snapshot.Add(new PayloadSourceMessage(message.Role, message.Content, true, false));
                continue;
            }

            if (message.IsUser || message.IsAssistant || message.IsSystem)
            {
                snapshot.Add(new PayloadSourceMessage(message.Role, message.Content, false, true));
            }
        }

        return snapshot;
    }

    private PayloadBuildResult BuildPayloadMessagesCore(
        IReadOnlyList<(string Role, string Content)> history,
        int maxContextChars,
        string systemPrompt,
        string? compressedContextSummary = null)
    {
        systemPrompt = TruncateForPayload(systemPrompt, maxContextChars);

        var payload = new List<AiChatMessage>(history.Count + 3)
        {
            new("system", systemPrompt)
        };

        var summaryMessage = BuildCompressedContextSummaryMessage(compressedContextSummary);
        var remaining = maxContextChars - systemPrompt.Length;
        if (!string.IsNullOrWhiteSpace(summaryMessage) && remaining > 0)
        {
            var trimmedSummary = TruncateForPayload(summaryMessage, remaining);
            if (!string.IsNullOrWhiteSpace(trimmedSummary))
            {
                payload.Add(new AiChatMessage("system", trimmedSummary));
                remaining -= trimmedSummary.Length;
            }
        }

        var historyChars = history.Sum(entry => entry.Content.Length);
        var summaryChars = summaryMessage?.Length ?? 0;
        var contextOverflow = systemPrompt.Length + summaryChars + historyChars > maxContextChars;

        if (remaining <= 0)
        {
            return new PayloadBuildResult(payload, contextOverflow || history.Count > 0, maxContextChars);
        }

        var tail = new List<AiChatMessage>(history.Count);
        var consumedAll = true;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (remaining <= 0)
            {
                consumedAll = false;
                break;
            }

            var entry = history[i];
            var content = entry.Content;
            if (content.Length > remaining)
            {
                content = TruncateForPayload(content, remaining);
            }

            if (content.Length == 0)
            {
                consumedAll = false;
                continue;
            }

            tail.Add(new AiChatMessage(entry.Role, content));
            remaining -= content.Length;

            if (content.Length < entry.Content.Length)
            {
                consumedAll = false;
                break;
            }
        }

        if (!consumedAll)
        {
            contextOverflow = true;
        }

        tail.Reverse();
        payload.AddRange(tail);
        return new PayloadBuildResult(payload, contextOverflow, maxContextChars);
    }

    private async Task<List<(string Role, string Content)>> BuildPayloadHistoryEntriesAsync(
        IReadOnlyList<PayloadSourceMessage> sourceMessages,
        int mcpLimit,
        CompressionRuntimeOptions options)
    {
        var history = new List<(string Role, string Content)>(sourceMessages.Count);
        foreach (var message in sourceMessages)
        {
            string? role = null;
            string? content = null;

            if (message.IsMcpResult)
            {
                var compactMcpResult = BuildMcpPayloadText(message.Content);
                if (string.IsNullOrWhiteSpace(compactMcpResult))
                {
                    continue;
                }

                var prepared = await PrepareMcpResultForPayloadAsync(compactMcpResult, mcpLimit, options).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(prepared))
                {
                    continue;
                }

                content = $"MCP_RESULT: {prepared}";
                role = "system";
            }
            else if (message.IsDialogRole)
            {
                role = message.Role;
                content = message.Content;
            }

            if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            history.Add((role, content));
        }

        return history;
    }

    private async Task<string> PrepareMcpResultForPayloadAsync(
        string compactMcpResult,
        int mcpLimit,
        CompressionRuntimeOptions options)
    {
        if (compactMcpResult.Length <= mcpLimit)
        {
            return compactMcpResult;
        }

        var compressed = await TryCompressMcpContentAsync(compactMcpResult, mcpLimit, options).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(compressed))
        {
            return compressed.Length <= mcpLimit
                ? compressed
                : TruncateForPayload(compressed, mcpLimit);
        }

        return TruncateForPayload(compactMcpResult, mcpLimit);
    }

    private async Task<string?> TryCompressMcpContentAsync(
        string compactMcpResult,
        int mcpLimit,
        CompressionRuntimeOptions options)
    {
        if (string.IsNullOrWhiteSpace(compactMcpResult) ||
            string.IsNullOrWhiteSpace(options.ApiKey) ||
            string.IsNullOrWhiteSpace(options.Model))
        {
            return null;
        }

        var signature = ComputeMcpCompressionSignature(compactMcpResult, mcpLimit);
        if (_cachedCompressedMcpSummaryBySignature.TryGetValue(signature, out var cachedSummary) &&
            !string.IsNullOrWhiteSpace(cachedSummary))
        {
            return cachedSummary;
        }

        var previousStatus = GetStatusTextSafe();
        SetStatusTextSafe(McpCompressionNotice);
        LogChat("system", McpCompressionNotice);

        try
        {
            var summary = await SummarizeMcpContentByChunksAsync(compactMcpResult, mcpLimit, options).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(summary))
            {
                return null;
            }

            if (_cachedCompressedMcpSummaryBySignature.Count > 96)
            {
                _cachedCompressedMcpSummaryBySignature.Clear();
            }

            _cachedCompressedMcpSummaryBySignature[signature] = summary;
            return summary;
        }
        catch (OperationCanceledException)
        {
            LogChat("system", "MCP 内容压缩超时，已回退原始截断结果。");
            return null;
        }
        catch (Exception ex)
        {
            LogChat("system", $"MCP 内容压缩失败，已回退原始截断结果：{ex.Message}");
            return null;
        }
        finally
        {
            if (string.Equals(GetStatusTextSafe(), McpCompressionNotice, StringComparison.Ordinal))
            {
                SetStatusTextSafe(previousStatus);
            }
        }
    }

    private async Task<string?> SummarizeMcpContentByChunksAsync(
        string compactMcpResult,
        int mcpLimit,
        CompressionRuntimeOptions options)
    {
        var chunkSize = Math.Max(3600, Math.Min(12000, mcpLimit * 2));
        var chunks = SplitTextIntoChunks(compactMcpResult, chunkSize, 8);
        if (chunks.Count == 0)
        {
            return null;
        }

        string? summary;
        if (chunks.Count == 1)
        {
            summary = await RequestMcpCompressionAsync(
                McpCompressionPrompt,
                chunks[0],
                Math.Max(4096, chunkSize + 1024),
                TimeSpan.FromSeconds(22),
                options).ConfigureAwait(false);
        }
        else
        {
            var chunkSummaries = new List<string>(chunks.Count);
            for (var i = 0; i < chunks.Count; i++)
            {
                var chunkPrompt = $"{McpChunkCompressionPrompt}\n当前分段：{i + 1}/{chunks.Count}";
                var chunkSummary = await RequestMcpCompressionAsync(
                    chunkPrompt,
                    chunks[i],
                    Math.Max(4096, chunkSize + 1024),
                    TimeSpan.FromSeconds(20),
                    options).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(chunkSummary))
                {
                    chunkSummary = TruncateForPayload(chunks[i], Math.Max(1200, chunkSize / 2));
                }

                chunkSummaries.Add($"分段{i + 1}摘要：{chunkSummary}");
            }

            var mergedInput = string.Join(Environment.NewLine + Environment.NewLine, chunkSummaries);
            summary = await RequestMcpCompressionAsync(
                McpChunkMergePrompt,
                mergedInput,
                Math.Max(4096, Math.Min(12000, mergedInput.Length + 1200)),
                TimeSpan.FromSeconds(25),
                options).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(summary))
            {
                summary = mergedInput;
            }
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var normalized = summary.Trim();
        var withPrefix = normalized.StartsWith(McpCompressionSystemPrefix, StringComparison.Ordinal)
            ? normalized
            : $"{McpCompressionSystemPrefix}\n{normalized}";
        return TruncateForPayload(withPrefix, mcpLimit);
    }

    private async Task<string?> RequestMcpCompressionAsync(
        string prompt,
        string sourceText,
        int maxContextChars,
        TimeSpan timeout,
        CompressionRuntimeOptions options)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return null;
        }

        var messages = new List<AiChatMessage>(2)
        {
            new("system", "你是 MCP 工具输出压缩助手，只允许输出自然语言纯文本。"),
            new("user", $"{prompt}\n\nMCP_OUTPUT:\n{sourceText}")
        };

        using var cts = new CancellationTokenSource(timeout);
        var reply = await _chatService.GetChatCompletionAsync(
            BuildMcpCompressionConfig(maxContextChars, options),
            messages,
            cts.Token).ConfigureAwait(false);

        reply = SanitizeAssistantReply(reply).Trim();
        if (IsInvalidFinalAnswerReply(reply))
        {
            reply = NormalizeStructuredReplyForUser(reply).Trim();
        }

        return string.IsNullOrWhiteSpace(reply) ? null : reply;
    }

    private static List<string> SplitTextIntoChunks(string text, int chunkSize, int maxChunks)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text) || chunkSize <= 0 || maxChunks <= 0)
        {
            return chunks;
        }

        var start = 0;
        while (start < text.Length && chunks.Count < maxChunks)
        {
            var length = Math.Min(chunkSize, text.Length - start);
            var end = start + length;
            if (end < text.Length)
            {
                var scanLength = end - start;
                var lineBreak = text.LastIndexOf('\n', end - 1, scanLength);
                if (lineBreak > start + (chunkSize / 2))
                {
                    end = lineBreak + 1;
                    length = end - start;
                }
            }

            if (length <= 0)
            {
                break;
            }

            chunks.Add(text.Substring(start, length));
            start = end;
        }

        if (start < text.Length && chunks.Count > 0)
        {
            var omittedChars = text.Length - start;
            chunks[^1] = $"{chunks[^1]}\n...（后续省略 {omittedChars} 字符）...";
        }

        return chunks;
    }

    private static int ComputeMcpCompressionSignature(string content, int mcpLimit)
    {
        return ComputeStableTextSignature("mcp", mcpLimit, content);
    }

    private async Task<string?> TryCompressContextAsync(
        int maxContextChars,
        IReadOnlyList<(string Role, string Content)> historyEntries,
        CompressionRuntimeOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(options.Model))
        {
            return null;
        }

        var historyForCompression = BuildHistoryForCompression(maxContextChars, historyEntries);
        if (string.IsNullOrWhiteSpace(historyForCompression))
        {
            return null;
        }

        var signature = ComputeCompressionSignature(maxContextChars, historyForCompression);
        if (_cachedCompressedContextSignature == signature &&
            !string.IsNullOrWhiteSpace(_cachedCompressedContextSummary))
        {
            return _cachedCompressedContextSummary;
        }

        var previousStatus = GetStatusTextSafe();
        SetStatusTextSafe(ContextCompressionNotice);
        LogChat("system", ContextCompressionNotice);

        try
        {
            var compressionMessages = new List<AiChatMessage>(2)
            {
                new("system", "You are a context compression assistant. Output plain text summary only."),
                new("user", $"{ContextCompressionPrompt}\n\nConversation History:\n{historyForCompression}")
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var summary = await _chatService.GetChatCompletionAsync(
                BuildContextCompressionConfig(maxContextChars, options),
                compressionMessages,
                cts.Token).ConfigureAwait(false);

            summary = SanitizeAssistantReply(summary);
            summary = summary.Trim();
            if (IsInvalidFinalAnswerReply(summary))
            {
                summary = NormalizeStructuredReplyForUser(summary).Trim();
            }

            if (string.IsNullOrWhiteSpace(summary))
            {
                return null;
            }

            summary = TruncateForPayload(summary, Math.Max(1200, Math.Min(6000, maxContextChars / 3)));
            _cachedCompressedContextSummary = summary;
            _cachedCompressedContextSignature = signature;
            return summary;
        }
        catch (OperationCanceledException)
        {
            LogChat("system", "上下文压缩超时，已回退到截断上下文。");
            return null;
        }
        catch (Exception ex)
        {
            LogChat("system", $"上下文压缩失败，已回退到截断上下文：{ex.Message}");
            return null;
        }
        finally
        {
            if (string.Equals(GetStatusTextSafe(), ContextCompressionNotice, StringComparison.Ordinal))
            {
                SetStatusTextSafe(previousStatus);
            }
        }
    }

    private string BuildHistoryForCompression(int maxContextChars, IReadOnlyList<(string Role, string Content)> historyEntries)
    {
        if (historyEntries.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var entry in historyEntries)
        {
            var role = entry.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                ? "用户"
                : entry.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                    ? "助手"
                    : "系统";
            builder.Append(role).Append('：').AppendLine(entry.Content.Trim());
        }

        var transcript = builder.ToString().Trim();
        if (transcript.Length == 0)
        {
            return transcript;
        }

        var maxChars = Math.Max(10000, Math.Min(50000, maxContextChars + (maxContextChars / 4)));
        if (transcript.Length <= maxChars)
        {
            return transcript;
        }

        var headChars = Math.Max(3000, maxChars * 4 / 10);
        var tailChars = Math.Max(3000, maxChars - headChars - 64);
        if (headChars + tailChars >= transcript.Length)
        {
            return transcript;
        }

        var omitted = transcript.Length - headChars - tailChars;
        return $"{transcript.Substring(0, headChars)}\n...（中间省略 {omitted} 字符）...\n{transcript.Substring(transcript.Length - tailChars)}";
    }

    private static string BuildCompressedContextSummaryMessage(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return string.Empty;
        }

        return $"{ContextCompressionSystemPrefix}\n{summary.Trim()}";
    }

    private int ComputeCompressionSignature(int maxContextChars, string historyForCompression)
    {
        return ComputeStableTextSignature("context", maxContextChars, historyForCompression);
    }

    private static int ComputeStableTextSignature(string scope, int limit, string content)
    {
        using var sha256 = SHA256.Create();
        var scopeBytes = Encoding.UTF8.GetBytes(scope);
        var limitBytes = BitConverter.GetBytes(limit);
        var contentBytes = Encoding.UTF8.GetBytes(content ?? string.Empty);

        sha256.TransformBlock(scopeBytes, 0, scopeBytes.Length, null, 0);
        sha256.TransformBlock(limitBytes, 0, limitBytes.Length, null, 0);
        sha256.TransformFinalBlock(contentBytes, 0, contentBytes.Length);

        var hash = sha256.Hash;
        if (hash == null || hash.Length < sizeof(int))
        {
            return HashCode.Combine(scope, limit, content?.Length ?? 0);
        }

        return BitConverter.ToInt32(hash, 0);
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

    private readonly record struct PayloadBuildResult(
        IReadOnlyList<AiChatMessage> Payload,
        bool ContextOverflow,
        int MaxContextChars);

    private readonly record struct CompressionRuntimeOptions(
        string BaseUrl,
        string ApiKey,
        string Model);

    private readonly record struct PayloadSourceMessage(
        string Role,
        string Content,
        bool IsMcpResult,
        bool IsDialogRole);

    private readonly record struct IntentClassification(
        bool PathingPriorityIntent,
        bool ScriptSubscribeIntent,
        bool ScriptDetailIntent,
        bool DocHelpIntent,
        bool DownloadIntent,
        bool StatusQueryIntent,
        bool RealtimeFeatureQueryIntent,
        bool? DesiredFeatureValue,
        bool AllFeaturesRequest,
        bool IsAllRequest,
        string? FeatureKey,
        string ClassifierSource,
        string? ClassifierReason);

    private readonly record struct IntentClassificationHints(
        bool? PathingPriorityIntent,
        bool? ScriptSubscribeIntent,
        bool? ScriptDetailIntent,
        bool? DocHelpIntent,
        bool? DownloadIntent,
        bool? StatusQueryIntent,
        bool? RealtimeFeatureQueryIntent,
        bool? DesiredFeatureValue,
        bool? AllFeaturesRequest,
        bool? IsAllRequest,
        string? FeatureKey,
        string? ClassifierReason);

    private readonly record struct AiReplyResult(string RawReply, int StreamMessageIndex);
    private readonly record struct ExecutedMcpToolCall(string Name, string ArgumentsJson, McpToolCallResult Result);

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
