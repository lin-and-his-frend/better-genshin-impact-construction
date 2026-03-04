using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Security.Cryptography;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// Web 远程控制配置
/// </summary>
[Serializable]
public partial class WebRemoteConfig : ObservableObject
{
    /// <summary>
    /// 启用 Web 远程控制
    /// </summary>
    [ObservableProperty]
    private bool _enabled = false;

    /// <summary>
    /// 允许第三方访问（优先监听 0.0.0.0 与 IPv6 [::]）
    /// </summary>
    [ObservableProperty]
    private bool _lanEnabled = false;

    /// <summary>
    /// 监听端口
    /// </summary>
    [ObservableProperty]
    private int _port = 50000;

    /// <summary>
    /// 历史字段（兼容旧配置）。Web 远程控制现为强制鉴权，此字段不再作为开关使用。
    /// </summary>
    [ObservableProperty]
    private bool _authEnabled = true;

    /// <summary>
    /// Web 登录用户名
    /// </summary>
    [ObservableProperty]
    private string _username = "admin";

    /// <summary>
    /// Web 登录密码
    /// </summary>
    [ObservableProperty]
    private string _password = string.Empty;

    /// <summary>
    /// 允许在 Web 端显示日志
    /// </summary>
    [ObservableProperty]
    private bool _logStreamEnabled = true;

    /// <summary>
    /// 允许传输屏幕画面
    /// </summary>
    [ObservableProperty]
    private bool _screenStreamEnabled = false;

    /// <summary>
    /// 允许高级配置接口（/api/config/get、/api/config/set）
    /// 默认关闭，避免通用反射路径被远程滥用
    /// </summary>
    [ObservableProperty]
    private bool _allowAdvancedConfigApi = false;

    /// <summary>
    /// 允许将遮罩日志转发给 AI 接口
    /// </summary>
    [ObservableProperty]
    private bool _aiLogRelayEnabled = false;

    /// <summary>
    /// 启用集群控制 API
    /// </summary>
    [ObservableProperty]
    private bool _clusterApiEnabled = false;

    /// <summary>
    /// 集群控制 Token
    /// </summary>
    [ObservableProperty]
    private string _clusterApiToken = string.Empty;

    /// <summary>
    /// 集群控制白名单 IP（逗号/换行分隔）
    /// </summary>
    [ObservableProperty]
    private string _clusterApiWhitelist = string.Empty;

    public static string CreateRandomToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    partial void OnClusterApiEnabledChanged(bool value)
    {
        if (value && string.IsNullOrWhiteSpace(ClusterApiToken))
        {
            ClusterApiToken = CreateRandomToken();
        }
    }
}
