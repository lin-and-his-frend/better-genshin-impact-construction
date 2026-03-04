using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// MCP 接口配置
/// </summary>
[Serializable]
public partial class McpConfig : ObservableObject
{
    private static readonly string[] AllowedWebSearchProviders =
    [
        "auto",
        "searxng",
        "fandom",
        "duckduckgo"
    ];

    /// <summary>
    /// 启用 MCP 监听
    /// </summary>
    [ObservableProperty]
    private bool _enabled = false;

    /// <summary>
    /// 监听端口
    /// </summary>
    [ObservableProperty]
    private int _port = 50001;

    /// <summary>
    /// 监听地址
    /// </summary>
    [ObservableProperty]
    private string _listenAddress = "127.0.0.1";

    /// <summary>
    /// 允许非回环地址连接 MCP（0.0.0.0/局域网地址）
    /// </summary>
    [ObservableProperty]
    private bool _allowNonLoopbackConnections = true;

    /// <summary>
    /// 已确认非回环地址监听的风险提示
    /// </summary>
    [ObservableProperty]
    private bool _nonLoopbackRiskAccepted = false;

    /// <summary>
    /// 允许通过 MCP 修改配置（bgi.config.set）
    /// </summary>
    [ObservableProperty]
    private bool _allowConfigSet = false;

    /// <summary>
    /// 允许通过 MCP 调用启动游戏动作（bgi.action.start_game）
    /// </summary>
    [ObservableProperty]
    private bool _allowStartGameAction = false;

    /// <summary>
    /// 允许 MCP 联网搜索（用于 AI 问答补充专业术语）
    /// </summary>
    [ObservableProperty]
    private bool _webSearchEnabled = true;

    /// <summary>
    /// 联网搜索 Provider：auto / searxng / fandom / duckduckgo
    /// </summary>
    [ObservableProperty]
    private string _webSearchProvider = "auto";

    /// <summary>
    /// 联网搜索基础地址（仅 Provider 为 searxng 或 auto 时使用）
    /// 例如：https://searxng.example.com 或 http://127.0.0.1:8080
    /// </summary>
    [ObservableProperty]
    private string _webSearchBaseUrl = string.Empty;

    /// <summary>
    /// 联网搜索默认返回条数（1-10）
    /// </summary>
    [ObservableProperty]
    private int _webSearchMaxResults = 5;

    /// <summary>
    /// 联网搜索语言（例如 zh-CN/en-US，部分 Provider 会忽略）
    /// </summary>
    [ObservableProperty]
    private string _webSearchLanguage = "zh-CN";

    partial void OnWebSearchProviderChanged(string value)
    {
        var normalized = NormalizeWebSearchProvider(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            WebSearchProvider = normalized;
        }
    }

    partial void OnWebSearchBaseUrlChanged(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            WebSearchBaseUrl = normalized;
        }
    }

    partial void OnWebSearchMaxResultsChanged(int value)
    {
        var clamped = Math.Clamp(value, 1, 10);
        if (value != clamped)
        {
            WebSearchMaxResults = clamped;
        }
    }

    partial void OnWebSearchLanguageChanged(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "zh-CN" : value.Trim();
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            WebSearchLanguage = normalized;
        }
    }

    private static string NormalizeWebSearchProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return "auto";
        }

        var normalized = provider.Trim().ToLowerInvariant();
        if (normalized == "searx")
        {
            return "searxng";
        }

        foreach (var allowed in AllowedWebSearchProviders)
        {
            if (string.Equals(normalized, allowed, StringComparison.Ordinal))
            {
                return normalized;
            }
        }

        return "auto";
    }
}
