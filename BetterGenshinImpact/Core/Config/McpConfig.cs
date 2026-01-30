using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// MCP 接口配置
/// </summary>
[Serializable]
public partial class McpConfig : ObservableObject
{
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
}
