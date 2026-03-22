using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Windows;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.Core.Script.Utils;
using BetterGenshinImpact.Core.Recorder;
using BetterGenshinImpact.GameTask.AutoArtifactSalvage;
using BetterGenshinImpact.GameTask.AutoDomain;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFishing;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation;
using BetterGenshinImpact.GameTask.AutoLeyLineOutcrop;
using BetterGenshinImpact.GameTask.AutoMusicGame;
using BetterGenshinImpact.GameTask.AutoStygianOnslaught;
using BetterGenshinImpact.GameTask.AutoWood;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.GetGridIcons;
using BetterGenshinImpact.GameTask.UseRedeemCode;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.Service.Notification;
using BetterGenshinImpact.Service.Notifier;
using BetterGenshinImpact.ViewModel.Pages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace BetterGenshinImpact.Service.Remote;

internal sealed class WebRemoteService : IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly string[] ConfigReadBlockedPrefixes =
    [
        "AiConfig.ApiKey",
        "AiConfig.VectorApiKey",
        "CommonConfig.OssAccessKeyId",
        "CommonConfig.OssAccessKeySecret",
        "CommonConfig.WebDavUsername",
        "CommonConfig.WebDavPassword",
        "WebRemoteConfig.AuthEnabled",
        "WebRemoteConfig.Password",
        "WebRemoteConfig.ClusterApiToken"
    ];
    private const string SessionCookieName = "bgi_web_session";
    private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(8);
    private static readonly TimeSpan SessionTtlRemember = TimeSpan.FromDays(14);
    private static readonly TimeSpan UiSchemaCacheLifetime = TimeSpan.FromSeconds(4);
    private static readonly string[] OneDragonDayNames = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
    private static readonly string[] OneDragonDefaultTaskNames =
    [
        "领取邮件",
        "合成树脂",
        "自动秘境",
        "自动幽境危战",
        "自动地脉花",
        "领取每日奖励",
        "领取尘歌壶奖励"
    ];
    private static readonly string[] OneDragonPreferredFieldOrder =
    [
        "craftingBenchCountry",
        "minResinToKeep",
        "weeklyDomainEnabled",
        "partyName",
        "domainName",
        "sundayEverySelectedValue",
        "sundaySelectedValue",
        "adventurersGuildCountry",
        "dailyRewardPartyName",
        "sereniteaPotTpType",
        "completionAction",
        "leyLineOneDragonMode",
        "leyLineResinExhaustionMode",
        "leyLineOpenModeCountMin",
        "leyLineRunCount"
    ];
    private static readonly string[] LegacyGetRoutePaths =
    [
        "/",
        "/index",
        "/index.html",
        "/web",
        "/web/",
        "/web/index.html",
        "/scheduler",
        "/scheduler/",
        "/scheduler.html",
        "/api-center",
        "/api-center/",
        "/api-center.html",
        "/one-dragon",
        "/one-dragon/",
        "/one-dragon.html",
        "/web/scheduler",
        "/web/scheduler/",
        "/web/scheduler.html",
        "/web/api-center",
        "/web/api-center/",
        "/web/api-center.html",
        "/web/one-dragon",
        "/web/one-dragon/",
        "/web/one-dragon.html",
        "/api/status",
        "/api/network",
        "/api/strategies/auto-fight",
        "/api/strategies/tcg",
        "/api/options/domain-names",
        "/api/options/grid-names",
        "/api/options/fishing-time-policy",
        "/api/options/recognition-failure-policy",
        "/api/options/leyline-types",
        "/api/options/leyline-countries",
        "/api/options/elite-drop-mode",
        "/api/options/sunday-values",
        "/api/options/resin-priority",
        "/api/options/script-project-statuses",
        "/api/options/script-project-schedules",
        "/api/config/basic",
        "/api/settings/auto-gi",
        "/api/settings/auto-wood",
        "/api/settings/auto-fight",
        "/api/settings/auto-domain",
        "/api/settings/auto-stygian",
        "/api/settings/auto-fishing",
        "/api/settings/auto-music",
        "/api/settings/auto-artifact",
        "/api/settings/grid-icons",
        "/api/settings/notification",
        "/api/one-dragon/configs",
        "/api/one-dragon/config",
        "/api/one-dragon/options",
        "/api/options/notification-channels",
        "/api/logs",
        "/api/logs/stream",
        "/api/screen",
        "/api/scripts/groups",
        "/api/scripts/group/detail",
        "/api/scripts/library"
    ];
    private static readonly string[] LegacyPostRoutePaths =
    [
        "/api/config/basic",
        "/api/settings/auto-gi",
        "/api/settings/auto-wood",
        "/api/settings/auto-fight",
        "/api/settings/auto-domain",
        "/api/settings/auto-stygian",
        "/api/settings/auto-fishing",
        "/api/settings/auto-music",
        "/api/settings/auto-artifact",
        "/api/settings/grid-icons",
        "/api/settings/notification",
        "/api/notification/test",
        "/api/tasks/run",
        "/api/one-dragon/config",
        "/api/one-dragon/config/clone",
        "/api/one-dragon/config/rename",
        "/api/one-dragon/config/delete",
        "/api/one-dragon/select",
        "/api/one-dragon/run",
        "/api/game/start",
        "/api/game/stop",
        "/api/scripts/run",
        "/api/scripts/group/create",
        "/api/scripts/group/delete",
        "/api/scripts/group/rename",
        "/api/scripts/group/add-items",
        "/api/scripts/group/remove-item",
        "/api/scripts/group/update-item",
        "/api/scripts/group/batch-update",
        "/api/scripts/group/reorder",
        "/api/scripts/group/copy",
        "/api/scripts/group/reverse",
        "/api/scripts/group/set-next",
        "/api/scripts/group/set-next-group",
        "/api/scripts/group/run-from",
        "/api/scripts/group/export-merged",
        "/api/tasks/cancel",
        "/api/tasks/pause",
        "/api/tasks/resume"
    ];
    private static readonly HashSet<string> NonTaskSettingsEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/settings/notification"
    };
    private enum OpenApiSpecScope
    {
        Api,
        Cluster
    }

    private readonly IConfigService _configService;
    private readonly IScriptService _scriptService;
    private readonly WebAiBridgeService _webAiBridgeService;
    private readonly ILogger<WebRemoteService> _logger;
    private readonly Dictionary<string, Func<HttpListenerRequest, HttpListenerResponse, CancellationToken, Task>> _getRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<HttpListenerRequest, HttpListenerResponse, CancellationToken, Task>> _postRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly object _uiSchemaCacheSync = new();
    private readonly Dictionary<string, SessionTicket> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedUiSchema> _uiSchemaCache = new(StringComparer.OrdinalIgnoreCase);
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private WebRemoteConfig? _config;
    private int _currentPort;
    private bool _currentLanEnabled;
    private bool _currentLanActive;
    private string _currentPrefix = string.Empty;
    private string? _lastWarning;
    private bool _authMissingPromptShown;

    public WebRemoteService(
        IConfigService configService,
        IScriptService scriptService,
        WebAiBridgeService webAiBridgeService,
        ILogger<WebRemoteService> logger)
    {
        _configService = configService;
        _scriptService = scriptService;
        _webAiBridgeService = webAiBridgeService;
        _logger = logger;
        RegisterRoutes();
    }

    private void RegisterRoutes()
    {
        // Dynamic route table. New features can register here with minimal changes.
        MapGet("/login", HandleLoginPageAsync);
        MapGet("/api/auth/me", HandleAuthMeAsync);
        MapPost("/api/auth/login", HandleAuthLoginAsync);
        MapPost("/api/auth/logout", HandleAuthLogoutAsync);
        MapGet("/api/config/get", HandleConfigGetAsync);
        MapPost("/api/config/set", HandleConfigSetAsync);
        MapGet("/api/ui/schema", HandleUiSchemaAsync);
        MapGet("/api/ui/routes", HandleUiRoutesAsync);
        MapGet("/api/ui/i18n", HandleUiI18nAsync);
        MapGet("/api/routes", HandleUiRoutesAsync);
        MapGet("/api/meta", HandleApiMetaAsync);
        MapGet("/api/health", HandleApiHealthAsync);
        MapGet("/openapi.json", HandleOpenApiDocumentAsync);
        MapGet("/docs", HandleOpenApiSwaggerAsync);
        MapGet("/redoc", HandleOpenApiRedocAsync);
        MapGet("/api/openapi.json", HandleOpenApiDocumentAsync);
        MapGet("/api/docs", HandleOpenApiSwaggerAsync);
        MapGet("/api/redoc", HandleOpenApiRedocAsync);
        MapGet("/api/options/leyline-types", async (_, response, ct) =>
        {
            await WriteJsonAsync(response, LoadLeyLineOutcropTypes(), ct);
        });
        MapGet("/api/options/leyline-countries", async (_, response, ct) =>
        {
            await WriteJsonAsync(response, LoadLeyLineOutcropCountries(), ct);
        });
        MapGet("/api/options/elite-drop-mode", async (_, response, ct) =>
        {
            await WriteJsonAsync(response, LoadAutoFightEliteDropModes(), ct);
        });
        MapGet("/api/options/sunday-values", async (_, response, ct) =>
        {
            await WriteJsonAsync(response, LoadOneDragonSundayValues(), ct);
        });
        MapGet("/api/options/resin-priority", async (_, response, ct) =>
        {
            await WriteJsonAsync(response, LoadResinPriorityOptions(), ct);
        });
        MapGet("/api/options/script-project-statuses", async (_, response, ct) =>
        {
            await WriteJsonAsync(response, LoadScriptProjectStatuses(), ct);
        });
        MapGet("/api/options/script-project-schedules", async (_, response, ct) =>
        {
            await WriteJsonAsync(response, LoadScriptProjectSchedules(), ct);
        });
        MapGet("/api/options/notification-channels", async (_, response, ct) =>
        {
            await WriteJsonAsync(response, LoadNotificationChannels(), ct);
        });
        MapGet("/api/scripts/groups", async (_, response, ct) =>
        {
            await HandleScriptGroupsAsync(response, ct);
        });
        MapGet("/api/scripts/group", HandleScriptGroupGetAsync);
        MapGet("/api/scripts/group/detail", HandleScriptGroupDetailAsync);
        MapGet("/api/scripts/library", async (_, response, ct) =>
        {
            await HandleScriptLibraryAsync(response, ct);
        });
        MapPost("/api/scripts/group/save", HandleScriptGroupSaveAsync);
        MapPost("/api/scripts/group/create", HandleScriptGroupCreateAsync);
        MapPost("/api/scripts/group/delete", HandleScriptGroupDeleteAsync);
        MapPost("/api/scripts/group/rename", HandleScriptGroupRenameAsync);
        MapPost("/api/scripts/group/add-items", HandleScriptGroupAddItemsAsync);
        MapPost("/api/scripts/group/remove-item", HandleScriptGroupRemoveItemAsync);
        MapPost("/api/scripts/group/update-item", HandleScriptGroupUpdateItemAsync);
        MapPost("/api/scripts/group/batch-update", HandleScriptGroupBatchUpdateAsync);
        MapPost("/api/scripts/group/reorder", HandleScriptGroupReorderAsync);
        MapPost("/api/scripts/group/copy", HandleScriptGroupCopyAsync);
        MapPost("/api/scripts/group/reverse", HandleScriptGroupReverseAsync);
        MapPost("/api/scripts/group/set-next", HandleScriptGroupSetNextAsync);
        MapPost("/api/scripts/group/set-next-group", HandleScriptGroupSetNextGroupAsync);
        MapPost("/api/scripts/group/run-from", HandleScriptGroupRunFromAsync);
        MapPost("/api/scripts/group/export-merged", HandleScriptGroupExportMergedAsync);
        MapGet("/api/settings/auto-leyline", async (_, response, ct) =>
        {
            await WriteJsonAsync(response, BuildAutoLeyLineSettings(), ct);
        });
        MapPost("/api/settings/auto-leyline", HandleAutoLeyLineSettingsAsync);
        MapGet("/api/settings/notification", async (_, response, ct) =>
        {
            await WriteJsonAsync(response, BuildNotificationSettings(), ct);
        });
        MapPost("/api/settings/notification", HandleNotificationSettingsAsync);
        MapPost("/api/notification/test", HandleNotificationTestAsync);
        MapGet("/api/library/js", HandleJsLibraryAsync);
        MapPost("/api/library/js/run", HandleJsRunAsync);
        MapPost("/api/library/js/delete", HandleJsDeleteAsync);
        MapGet("/api/library/pathing", HandlePathingLibraryAsync);
        MapPost("/api/library/pathing/run", HandlePathingRunAsync);
        MapPost("/api/library/pathing/delete", HandlePathingDeleteAsync);
        MapGet("/api/library/keymouse", HandleKeyMouseLibraryAsync);
        MapPost("/api/library/keymouse/play", HandleKeyMousePlayAsync);
        MapPost("/api/library/keymouse/delete", HandleKeyMouseDeleteAsync);
        MapPost("/api/library/keymouse/rename", HandleKeyMouseRenameAsync);
        MapPost("/api/library/keymouse/record/start", HandleKeyMouseRecordStartAsync);
        MapPost("/api/library/keymouse/record/stop", HandleKeyMouseRecordStopAsync);
        MapPost("/api/ai/chat", HandleAiChatAsync);
        MapGet("/api/one-dragon/options", HandleOneDragonOptionsAsync);
        MapPost("/api/one-dragon/config/clone", HandleOneDragonCloneAsync);
        MapPost("/api/one-dragon/config/rename", HandleOneDragonRenameAsync);
        MapPost("/api/one-dragon/config/delete", HandleOneDragonDeleteAsync);
    }

    private void MapGet(string path, Func<HttpListenerRequest, HttpListenerResponse, CancellationToken, Task> handler)
    {
        _getRoutes[path] = handler;
    }

    private void MapPost(string path, Func<HttpListenerRequest, HttpListenerResponse, CancellationToken, Task> handler)
    {
        _postRoutes[path] = handler;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _config = _configService.Get().WebRemoteConfig;
        _config.PropertyChanged += OnConfigChanged;
        StartOrStopListener();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopListener();
        if (_config != null)
        {
            _config.PropertyChanged -= OnConfigChanged;
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        StopListener();
        if (_config != null)
        {
            _config.PropertyChanged -= OnConfigChanged;
            _config = null;
        }
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        InvalidateUiSchemaCache();
        if (e.PropertyName is nameof(WebRemoteConfig.Enabled) or nameof(WebRemoteConfig.Port) or nameof(WebRemoteConfig.LanEnabled) or nameof(WebRemoteConfig.Username) or nameof(WebRemoteConfig.Password))
        {
            StartOrStopListener(e.PropertyName is nameof(WebRemoteConfig.Port) or nameof(WebRemoteConfig.LanEnabled));
        }
    }

    private void StartOrStopListener(bool forceRestart = false)
    {
        if (_config == null)
        {
            return;
        }

        if (!_config.Enabled)
        {
            StopListener();
            return;
        }

        if (!HasWebAuthCredentials(_config))
        {
            StopListener();
            _lastWarning = "Web 远程控制未启动：请先设置鉴权账号和密码。";
            _logger.LogWarning(_lastWarning);
            if (!_authMissingPromptShown)
            {
                _authMissingPromptShown = true;
                RunOnUiThread(() =>
                {
                    MessageBox.Show(
                        "Web 远程控制已启用，但未设置鉴权账号/密码。\n请先配置账号和密码后再启动 Web 远程控制。",
                        "Web 远程控制",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                });
            }
            return;
        }

        if (_listener != null && !forceRestart && _currentPort == _config.Port && _currentLanEnabled == _config.LanEnabled)
        {
            return;
        }

        if (_listener != null)
        {
            StopListener();
        }

        if (_config.Port is < 1 or > 65535)
        {
            _logger.LogWarning("Web 远程控制端口无效: {Port}", _config.Port);
            return;
        }

        var started = TryStartListener(_config.Port, _config.LanEnabled, out var listener, out var warning, out var lanActive, out var usedPrefix);
        if (!started || listener == null)
        {
            if (!string.IsNullOrWhiteSpace(warning))
            {
                _logger.LogWarning(warning);
            }
            return;
        }

        _listener = listener;
        _cts = new CancellationTokenSource();
        _listenTask = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        _currentPort = _config.Port;
        _currentLanEnabled = _config.LanEnabled;
        _currentLanActive = lanActive;
        _currentPrefix = usedPrefix ?? string.Empty;
        _lastWarning = warning;
        _authMissingPromptShown = false;
        if (!string.IsNullOrWhiteSpace(warning))
        {
            _logger.LogWarning(warning);
        }

        if (_config.LanEnabled)
        {
            _ = TryEnsureFirewallRuleAsync(_config.Port);
        }

        _logger.LogInformation("Web 远程控制已启动，端口 {Port}，第三方访问 {Enabled}", _config.Port, _currentLanActive);
    }

    private void StopListener()
    {
        lock (_sync)
        {
            try
            {
                _cts?.Cancel();
            }
            catch
            {
            }

            try
            {
                _listener?.Stop();
            }
            catch
            {
            }

            try
            {
                _listener?.Close();
            }
            catch
            {
            }

            _listener = null;
            _cts?.Dispose();
            _cts = null;
            _listenTask = null;
            _currentPort = 0;
            _currentLanEnabled = false;
            _currentLanActive = false;
            _currentPrefix = string.Empty;
            _lastWarning = null;
            _authMissingPromptShown = false;
            _sessions.Clear();
        }
    }

    private static bool TryStartListener(int port, bool lanEnabled, out HttpListener? listener, out string? warning, out bool lanActive, out string? usedPrefix)
    {
        listener = null;
        warning = null;
        lanActive = false;
        usedPrefix = null;

        var prefixes = new List<string>();
        if (lanEnabled)
        {
            prefixes.Add($"http://0.0.0.0:{port}/");
            prefixes.Add($"http://[::]:{port}/");
            prefixes.Add($"http://+:{port}/");
            prefixes.Add($"http://*:{port}/");
            prefixes.Add($"http://localhost:{port}/");
            prefixes.Add($"http://127.0.0.1:{port}/");
            prefixes.Add($"http://[::1]:{port}/");
        }
        else
        {
            prefixes.Add($"http://localhost:{port}/");
            prefixes.Add($"http://127.0.0.1:{port}/");
            prefixes.Add($"http://[::1]:{port}/");
        }

        foreach (var prefix in prefixes)
        {
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add(prefix);
                l.Start();
                listener = l;
                usedPrefix = prefix;
                lanActive = lanEnabled && IsThirdPartyPrefix(prefix);
                if (lanEnabled && !IsThirdPartyPrefix(prefix))
                {
                    warning = "第三方访问监听失败，已降级为仅本地可访问。如需第三方访问，请以管理员运行或执行 netsh http add urlacl url=http://+:" + port + "/ user=Everyone";
                }
                return true;
            }
            catch (HttpListenerException)
            {
                listener?.Close();
                listener = null;
                continue;
            }
            catch
            {
                listener?.Close();
                listener = null;
                continue;
            }
        }

        warning = lanEnabled
            ? "Web 远程控制启动失败：第三方访问可能缺少 URL ACL 或端口被占用。请尝试以管理员运行或执行 netsh http add urlacl url=http://+:" + port + "/ user=Everyone"
            : "Web 远程控制启动失败：端口被占用或系统限制。请更换端口。";
        return false;
    }

    private static bool IsThirdPartyPrefix(string prefix)
    {
        return prefix.StartsWith("http://0.0.0.0:", StringComparison.OrdinalIgnoreCase) ||
               prefix.StartsWith("http://[::]:", StringComparison.OrdinalIgnoreCase) ||
               prefix.StartsWith("http://+:", StringComparison.OrdinalIgnoreCase) ||
               prefix.StartsWith("http://*:", StringComparison.OrdinalIgnoreCase);
    }

    private async Task TryEnsureFirewallRuleAsync(int port)
    {
        await Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"advfirewall firewall add rule name=\"BetterGI Web Remote {port}\" dir=in action=allow protocol=TCP localport={port}",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null)
                {
                    return;
                }
                proc.WaitForExit(3000);
                if (proc.ExitCode != 0)
                {
                    var err = proc.StandardError.ReadToEnd().Trim();
                    if (string.IsNullOrWhiteSpace(err))
                    {
                        err = proc.StandardOutput.ReadToEnd().Trim();
                    }
                    _logger.LogWarning("添加防火墙规则失败，请手动放行端口 {Port}: {Message}", port, err);
                    SetWarningIfEmpty($"添加防火墙规则失败，请手动放行端口 {port}: {err}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "自动添加防火墙规则失败，请手动放行端口 {Port}", port);
                SetWarningIfEmpty($"自动添加防火墙规则失败，请手动放行端口 {port}");
            }
        });
    }

    private void SetWarningIfEmpty(string warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
        {
            return;
        }

        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_lastWarning))
            {
                _lastWarning = warning;
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        if (_listener == null)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Web 远程控制监听异常");
                continue;
            }

            _ = Task.Run(() => HandleContextAsync(context, ct), ct);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            if (_config == null || !_config.Enabled)
            {
                response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                response.Close();
                return;
            }

            var path = request.Url?.AbsolutePath ?? "/";
            if (path.Length > 1 && path.EndsWith('/'))
            {
                path = path.TrimEnd('/');
            }

            var isClusterRequest = IsClusterRequest(path);
            if (isClusterRequest)
            {
                ApplyClusterCorsHeaders(request, response);
                if (string.Equals(request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    response.StatusCode = (int)HttpStatusCode.NoContent;
                    response.ContentLength64 = 0;
                    response.OutputStream.Close();
                    return;
                }

                if (!AuthorizeCluster(request, response, _config))
                {
                    response.Close();
                    return;
                }

                path = "/api" + path.Substring("/api/cluster".Length);
                if (string.Equals(path, "/api", StringComparison.OrdinalIgnoreCase))
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    response.Close();
                    return;
                }
            }
            else
            {
                if (!IsAnonymousPath(path) && !Authorize(request, response, _config, path))
                {
                    response.Close();
                    return;
                }
            }

            switch (request.HttpMethod.ToUpperInvariant())
            {
                case "GET":
                    await HandleGetAsync(path, request, response, ct);
                    break;
                case "POST":
                    await HandlePostAsync(path, request, response, ct);
                    break;
                default:
                    response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    response.Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "处理 Web 请求失败");
            if (response.OutputStream.CanWrite)
            {
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                response.Close();
            }
        }
    }

    private async Task HandleGetAsync(string path, HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        if (_getRoutes.TryGetValue(path, out var dynamicHandler))
        {
            await dynamicHandler(request, response, ct);
            return;
        }

        switch (path)
        {
            case "":
            case "/":
            case "/index":
            case "/index.html":
            case "/web":
            case "/web/":
            case "/web/index.html":
                await WriteStringAsync(response, LoadWebIndexHtmlV2(), "text/html; charset=utf-8", ct);
                break;
            case "/automation":
            case "/automation/":
            case "/automation.html":
            case "/web/automation":
            case "/web/automation/":
            case "/web/automation.html":
                await WriteStringAsync(response, WebAutomationHtmlV2.Value, "text/html; charset=utf-8", ct);
                break;
            case "/scheduler":
            case "/scheduler/":
            case "/scheduler.html":
            case "/web/scheduler":
            case "/web/scheduler/":
            case "/web/scheduler.html":
            case "/api-center":
            case "/api-center/":
            case "/api-center.html":
            case "/web/api-center":
            case "/web/api-center/":
            case "/web/api-center.html":
            case "/one-dragon":
            case "/one-dragon/":
            case "/one-dragon.html":
            case "/web/one-dragon":
            case "/web/one-dragon/":
            case "/web/one-dragon.html":
                await WriteStringAsync(response, LoadWebIndexHtmlV2(), "text/html; charset=utf-8", ct);
                break;
            case "/api/status":
                await WriteJsonAsync(response, BuildStatus(), ct);
                break;
            case "/api/network":
                await WriteJsonAsync(response, new { addresses = GetLocalIpAddresses() }, ct);
                break;
            case "/api/strategies/auto-fight":
                await WriteJsonAsync(response, LoadAutoFightStrategies(), ct);
                break;
            case "/api/strategies/tcg":
                await WriteJsonAsync(response, LoadTcgStrategies(), ct);
                break;
            case "/api/options/domain-names":
                await WriteJsonAsync(response, LoadDomainNames(), ct);
                break;
            case "/api/options/grid-names":
                await WriteJsonAsync(response, LoadGridNames(), ct);
                break;
            case "/api/options/fishing-time-policy":
                await WriteJsonAsync(response, LoadFishingTimePolicies(), ct);
                break;
            case "/api/options/recognition-failure-policy":
                await WriteJsonAsync(response, LoadRecognitionFailurePolicies(), ct);
                break;
            case "/api/config/basic":
                await WriteJsonAsync(response, BuildBasicFeatureState(), ct);
                break;
            case "/api/settings/auto-gi":
                await WriteJsonAsync(response, BuildAutoGeniusInvokationSettings(), ct);
                break;
            case "/api/settings/auto-wood":
                await WriteJsonAsync(response, BuildAutoWoodSettings(), ct);
                break;
            case "/api/settings/auto-fight":
                await WriteJsonAsync(response, BuildAutoFightSettings(), ct);
                break;
            case "/api/settings/auto-domain":
                await WriteJsonAsync(response, BuildAutoDomainSettings(), ct);
                break;
            case "/api/settings/auto-stygian":
                await WriteJsonAsync(response, BuildAutoStygianSettings(), ct);
                break;
            case "/api/settings/auto-fishing":
                await WriteJsonAsync(response, BuildAutoFishingSettings(), ct);
                break;
            case "/api/settings/auto-music":
                await WriteJsonAsync(response, BuildAutoMusicSettings(), ct);
                break;
            case "/api/settings/auto-artifact":
                await WriteJsonAsync(response, BuildAutoArtifactSettings(), ct);
                break;
            case "/api/settings/grid-icons":
                await WriteJsonAsync(response, BuildGridIconsSettings(), ct);
                break;
            case "/api/settings/notification":
                await WriteJsonAsync(response, BuildNotificationSettings(), ct);
                break;
            case "/api/one-dragon/configs":
                await HandleOneDragonConfigsAsync(response, ct);
                break;
            case "/api/one-dragon/config":
                await HandleOneDragonConfigGetAsync(request, response, ct);
                break;
            case "/api/logs":
                await HandleLogSnapshotAsync(response, ct);
                break;
            case "/api/logs/stream":
                await HandleLogStreamAsync(response, ct);
                break;
            case "/api/screen":
                await HandleScreenAsync(response, ct);
                break;
            case "/api/scripts/groups":
                await HandleScriptGroupsAsync(response, ct);
                break;
            case "/api/scripts/group/detail":
                await HandleScriptGroupDetailAsync(request, response, ct);
                break;
            case "/api/scripts/library":
                await HandleScriptLibraryAsync(response, ct);
                break;
            default:
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
                break;
        }
    }

    private async Task HandlePostAsync(string path, HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        if (_postRoutes.TryGetValue(path, out var dynamicHandler))
        {
            await dynamicHandler(request, response, ct);
            return;
        }

        switch (path)
        {
            case "/api/config/basic":
                await HandleBasicConfigUpdateAsync(request, response, ct);
                break;
            case "/api/settings/auto-gi":
                await HandleAutoGeniusInvokationSettingsAsync(request, response, ct);
                break;
            case "/api/settings/auto-wood":
                await HandleAutoWoodSettingsAsync(request, response, ct);
                break;
            case "/api/settings/auto-fight":
                await HandleAutoFightSettingsAsync(request, response, ct);
                break;
            case "/api/settings/auto-domain":
                await HandleAutoDomainSettingsAsync(request, response, ct);
                break;
            case "/api/settings/auto-stygian":
                await HandleAutoStygianSettingsAsync(request, response, ct);
                break;
            case "/api/settings/auto-fishing":
                await HandleAutoFishingSettingsAsync(request, response, ct);
                break;
            case "/api/settings/auto-music":
                await HandleAutoMusicSettingsAsync(request, response, ct);
                break;
            case "/api/settings/auto-artifact":
                await HandleAutoArtifactSettingsAsync(request, response, ct);
                break;
            case "/api/settings/grid-icons":
                await HandleGridIconsSettingsAsync(request, response, ct);
                break;
            case "/api/settings/notification":
                await HandleNotificationSettingsAsync(request, response, ct);
                break;
            case "/api/notification/test":
                await HandleNotificationTestAsync(request, response, ct);
                break;
            case "/api/tasks/run":
                await HandleTaskRunAsync(request, response, ct);
                break;
            case "/api/one-dragon/config":
                await HandleOneDragonConfigSetAsync(request, response, ct);
                break;
            case "/api/one-dragon/select":
                await HandleOneDragonSelectAsync(request, response, ct);
                break;
            case "/api/one-dragon/run":
                await HandleOneDragonRunAsync(request, response, ct);
                break;
            case "/api/game/start":
                await HandleGameStartAsync(response, ct);
                break;
            case "/api/game/stop":
                await HandleGameStopAsync(response, ct);
                break;
            case "/api/scripts/run":
                await HandleScriptRunAsync(request, response, ct);
                break;
            case "/api/scripts/group/create":
                await HandleScriptGroupCreateAsync(request, response, ct);
                break;
            case "/api/scripts/group/delete":
                await HandleScriptGroupDeleteAsync(request, response, ct);
                break;
            case "/api/scripts/group/rename":
                await HandleScriptGroupRenameAsync(request, response, ct);
                break;
            case "/api/scripts/group/add-items":
                await HandleScriptGroupAddItemsAsync(request, response, ct);
                break;
            case "/api/scripts/group/remove-item":
                await HandleScriptGroupRemoveItemAsync(request, response, ct);
                break;
            case "/api/scripts/group/update-item":
                await HandleScriptGroupUpdateItemAsync(request, response, ct);
                break;
            case "/api/scripts/group/batch-update":
                await HandleScriptGroupBatchUpdateAsync(request, response, ct);
                break;
            case "/api/scripts/group/reorder":
                await HandleScriptGroupReorderAsync(request, response, ct);
                break;
            case "/api/tasks/cancel":
                await HandleTaskCancelAsync(response, ct);
                break;
            case "/api/tasks/pause":
                await HandleTaskPauseAsync(response, ct);
                break;
            case "/api/tasks/resume":
                await HandleTaskResumeAsync(response, ct);
                break;
            default:
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
                break;
        }
    }

    private Task HandleLoginPageAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        if (_config != null && TryAuthorizeBySession(request, out _))
        {
            var redirect = NormalizeRedirectPath(request.QueryString["redirect"]);
            response.StatusCode = (int)HttpStatusCode.Redirect;
            response.RedirectLocation = redirect;
            response.Close();
            return Task.CompletedTask;
        }

        return WriteStringAsync(response, WebLoginHtmlV2.Value, "text/html; charset=utf-8", ct);
    }

    private async Task HandleAuthMeAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        if (_config == null || !_config.Enabled)
        {
            response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            await WriteJsonAsync(response, new { authenticated = false, error = "service disabled" }, ct);
            return;
        }

        if (TryAuthorizeBySession(request, out var sessionUser))
        {
            await WriteJsonAsync(response, new { authenticated = true, username = sessionUser }, ct);
            return;
        }

        if (TryAuthorizeByBasic(request, _config, out var basicUser))
        {
            await WriteJsonAsync(response, new { authenticated = true, username = basicUser }, ct);
            return;
        }

        response.StatusCode = (int)HttpStatusCode.Unauthorized;
        await WriteJsonAsync(response, new { authenticated = false }, ct);
    }

    private async Task HandleAuthLoginAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        if (_config == null || !_config.Enabled)
        {
            response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            await WriteJsonAsync(response, new { ok = false, error = "Web remote is disabled" }, ct);
            return;
        }

        if (!HasWebAuthCredentials(_config))
        {
            response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            await WriteJsonAsync(response, new { ok = false, error = "请先在桌面端配置 Web 鉴权账号和密码" }, ct);
            return;
        }

        var payload = await ReadJsonAsync<WebAuthLoginRequest>(request, ct);
        var username = payload?.Username?.Trim() ?? string.Empty;
        var password = payload?.Password ?? string.Empty;
        if (string.IsNullOrWhiteSpace(username) ||
            !SecureEquals(username, _config.Username) ||
            !SecureEquals(password, _config.Password))
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            await WriteJsonAsync(response, new { ok = false, error = "用户名或密码错误" }, ct);
            return;
        }

        var persistent = payload?.Remember == true;
        var token = CreateSession(username, persistent);
        SetSessionCookie(response, token, persistent);
        await WriteJsonAsync(response, new { ok = true, username }, ct);
    }

    private async Task HandleAuthLogoutAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var token = request.Cookies[SessionCookieName]?.Value;
        RemoveSession(token);
        ClearSessionCookie(response);
        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private static string NormalizeRedirectPath(string? redirect)
    {
        if (string.IsNullOrWhiteSpace(redirect))
        {
            return "/";
        }

        var value = redirect.Trim();
        if (!value.StartsWith("/", StringComparison.Ordinal))
        {
            return "/";
        }

        if (value.StartsWith("//", StringComparison.Ordinal) ||
            value.StartsWith("/login", StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        return value;
    }

    private async Task HandleBasicConfigUpdateAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        BasicFeaturePatch? patch;
        var encoding = request.ContentEncoding ?? Encoding.UTF8;
        using (var reader = new StreamReader(request.InputStream, encoding))
        {
            var json = await reader.ReadToEndAsync(ct);
            patch = JsonSerializer.Deserialize<BasicFeaturePatch>(json, JsonOptions);
        }

        if (patch == null)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        ApplyBasicFeaturePatch(patch);
        await WriteJsonAsync(response, BuildBasicFeatureState(), ct);
    }

    private async Task HandleLogSnapshotAsync(HttpListenerResponse response, CancellationToken ct)
    {
        if (_config?.LogStreamEnabled != true)
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            response.Close();
            return;
        }

        var lines = LogRelayHub.GetSnapshot(200);
        await WriteJsonAsync(response, lines, ct);
    }

    private async Task HandleConfigGetAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        if (_config?.AllowAdvancedConfigApi != true)
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            await WriteJsonAsync(response, new
            {
                error = "advanced config api disabled. Enable \"允许 Web 高级配置接口\" in settings first."
            }, ct);
            return;
        }

        var path = request.QueryString["path"]?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Missing path" }, ct);
            return;
        }

        if (!TryNormalizeConfigPath(path, out var normalizedPath, out var normalizeError))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = normalizeError ?? "Invalid path" }, ct);
            return;
        }

        if (!IsConfigPathAllowedForRead(normalizedPath, out var policyError))
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            await WriteJsonAsync(response, new { error = policyError ?? "Config path not allowed" }, ct);
            return;
        }

        var config = _configService.Get();
        if (!ConfigPathAccessor.TryGetValue(config, normalizedPath, out var value, out var error))
        {
            await WriteJsonAsync(response, new { error }, ct);
            return;
        }

        await WriteJsonAsync(response, value ?? new { }, ct);
    }

    private async Task HandleConfigSetAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        if (_config?.AllowAdvancedConfigApi != true)
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            await WriteJsonAsync(response, new
            {
                error = "advanced config api disabled. Enable \"允许 Web 高级配置接口\" in settings first."
            }, ct);
            return;
        }

        ConfigSetRequest? payload;
        var encoding = request.ContentEncoding ?? Encoding.UTF8;
        using (var reader = new StreamReader(request.InputStream, encoding))
        {
            var json = await reader.ReadToEndAsync(ct);
            payload = JsonSerializer.Deserialize<ConfigSetRequest>(json, JsonOptions);
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.Path))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Missing path" }, ct);
            return;
        }

        if (!TryNormalizeConfigPath(payload.Path, out var normalizedPath, out var normalizeError))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = normalizeError ?? "Invalid path" }, ct);
            return;
        }

        if (!IsConfigPathAllowedForWrite(normalizedPath, out var policyError))
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            await WriteJsonAsync(response, new { error = policyError ?? "Config path is not writable via WebRemote" }, ct);
            return;
        }

        var success = false;
        string? error = null;
        RunOnUiThread(() =>
        {
            var config = _configService.Get();
            success = ConfigPathAccessor.TrySetValue(config, normalizedPath, payload.Value, out error);
        });

        if (!success)
        {
            await WriteJsonAsync(response, new { error }, ct);
            return;
        }

        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private static bool TryNormalizeConfigPath(string rawPath, out string normalizedPath, out string? error)
    {
        normalizedPath = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            error = "Path is empty";
            return false;
        }

        var segments = rawPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            error = "Path is empty";
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

            var bracketStart = segment.IndexOf('[');
            if (bracketStart < 0)
            {
                if (!IsSafeConfigIdentifier(segment))
                {
                    error = $"Invalid segment '{segment}'";
                    return false;
                }

                normalizedSegments.Add(segment);
                continue;
            }

            if (!segment.EndsWith("]", StringComparison.Ordinal))
            {
                error = $"Invalid index expression in '{segment}'";
                return false;
            }

            var propertyName = segment[..bracketStart];
            if (!IsSafeConfigIdentifier(propertyName))
            {
                error = $"Invalid segment '{segment}'";
                return false;
            }

            var indexText = segment[(bracketStart + 1)..^1];
            if (!int.TryParse(indexText, out var parsedIndex) || parsedIndex < 0)
            {
                error = $"Invalid index in '{segment}'";
                return false;
            }

            normalizedSegments.Add($"{propertyName}[{parsedIndex}]");
        }

        normalizedPath = string.Join('.', normalizedSegments);
        return true;
    }

    private static bool IsSafeConfigIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        foreach (var ch in identifier)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_')
            {
                return false;
            }
        }

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

        // Guarded by AllowAdvancedConfigApi and sensitive-prefix blacklist above.
        // This enables full Web parity with WPF config editing while keeping secrets protected.
        return true;
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

    private async Task HandleGameStartAsync(HttpListenerResponse response, CancellationToken ct)
    {
        await RunGameStartAsync();
        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandleGameStopAsync(HttpListenerResponse response, CancellationToken ct)
    {
        RunOnUiThread(() =>
        {
            var homeVm = App.GetService<BetterGenshinImpact.ViewModel.Pages.HomePageViewModel>();
            homeVm?.StopTriggerCommand.Execute(null);
        });

        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandleScriptGroupsAsync(HttpListenerResponse response, CancellationToken ct)
    {
        var groups = LoadScriptGroupInfos();
        await WriteJsonAsync(response, groups, ct);
    }

    private async Task HandleScriptGroupGetAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var name = request.QueryString["name"];
        if (string.IsNullOrWhiteSpace(name))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        if (!TryGetScriptGroupFilePath(name, out var path, out _))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        if (!File.Exists(path))
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            response.Close();
            return;
        }

        var json = await File.ReadAllTextAsync(path, ct);
        await WriteStringAsync(response, json, "application/json; charset=utf-8", ct);
    }

    private async Task HandleScriptGroupCreateAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        ScriptGroupNameRequest? payload = await ReadJsonAsync<ScriptGroupNameRequest>(request, ct);
        if (payload == null || string.IsNullOrWhiteSpace(payload.Name))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        if (!TryGetScriptGroupFilePath(payload.Name, out var file, out var fileError))
        {
            await WriteJsonAsync(response, new { error = fileError ?? "配置组名称非法" }, ct);
            return;
        }

        if (File.Exists(file))
        {
            await WriteJsonAsync(response, new { error = "配置组已存在" }, ct);
            return;
        }

        var group = new ScriptGroup { Name = payload.Name };
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, group.ToJson(), ct);
        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandleScriptGroupDeleteAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        ScriptGroupNameRequest? payload = await ReadJsonAsync<ScriptGroupNameRequest>(request, ct);
        if (payload == null || string.IsNullOrWhiteSpace(payload.Name))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        if (!TryGetScriptGroupFilePath(payload.Name, out var file, out var fileError))
        {
            await WriteJsonAsync(response, new { error = fileError ?? "配置组名称非法" }, ct);
            return;
        }

        if (File.Exists(file))
        {
            File.Delete(file);
        }

        var contextConfig = TaskContext.Instance().Config;
        contextConfig.NextScheduledTask.RemoveAll(item => string.Equals(item.Item1, payload.Name, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(contextConfig.NextScriptGroupName, payload.Name, StringComparison.OrdinalIgnoreCase))
        {
            contextConfig.NextScriptGroupName = string.Empty;
        }

        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandleScriptGroupRenameAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        ScriptGroupRenameRequest? payload = await ReadJsonAsync<ScriptGroupRenameRequest>(request, ct);
        if (payload == null || string.IsNullOrWhiteSpace(payload.OldName) || string.IsNullOrWhiteSpace(payload.NewName))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        if (!TryGetScriptGroupFilePath(payload.OldName, out var oldFile, out var oldError))
        {
            await WriteJsonAsync(response, new { error = oldError ?? "旧配置组名称非法" }, ct);
            return;
        }

        if (!TryGetScriptGroupFilePath(payload.NewName, out var newFile, out var newError))
        {
            await WriteJsonAsync(response, new { error = newError ?? "新配置组名称非法" }, ct);
            return;
        }

        if (!File.Exists(oldFile))
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            response.Close();
            return;
        }

        if (File.Exists(newFile))
        {
            await WriteJsonAsync(response, new { error = "新名称已存在" }, ct);
            return;
        }

        File.Move(oldFile, newFile);

        var contextConfig = TaskContext.Instance().Config;
        for (var i = 0; i < contextConfig.NextScheduledTask.Count; i++)
        {
            var item = contextConfig.NextScheduledTask[i];
            if (string.Equals(item.Item1, payload.OldName, StringComparison.OrdinalIgnoreCase))
            {
                contextConfig.NextScheduledTask[i] = (payload.NewName, item.Item2, item.Item3, item.Item4);
            }
        }

        if (string.Equals(contextConfig.NextScriptGroupName, payload.OldName, StringComparison.OrdinalIgnoreCase))
        {
            contextConfig.NextScriptGroupName = payload.NewName;
        }

        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandleScriptGroupSaveAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        ScriptGroupSaveRequest? payload = await ReadJsonAsync<ScriptGroupSaveRequest>(request, ct);
        if (payload == null || string.IsNullOrWhiteSpace(payload.Json))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        ScriptGroup? group;
        try
        {
            group = ScriptGroup.FromJson(payload.Json);
        }
        catch
        {
            await WriteJsonAsync(response, new { error = "JSON 解析失败" }, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(group.Name))
        {
            await WriteJsonAsync(response, new { error = "配置组名称为空" }, ct);
            return;
        }

        if (!TryGetScriptGroupFilePath(group.Name, out var file, out var fileError))
        {
            await WriteJsonAsync(response, new { error = fileError ?? "配置组名称非法" }, ct);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        AlignScriptGroupNextTask(group);
        await File.WriteAllTextAsync(file, group.ToJson(), ct);
        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandleScriptGroupDetailAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var name = request.QueryString["name"];
        if (string.IsNullOrWhiteSpace(name))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        var group = LoadScriptGroupByName(name, out _);
        if (group == null)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            response.Close();
            return;
        }

        var nextTask = TaskContext.Instance().Config.NextScheduledTask
            .Find(item => string.Equals(item.Item1, group.Name, StringComparison.OrdinalIgnoreCase));

        var detail = new ScriptGroupDetail
        {
            Name = group.Name,
            Projects = group.Projects.Select(p => new ScriptGroupProjectInfo
            {
                Index = p.Index,
                Name = p.Name,
                FolderName = p.FolderName,
                Type = p.Type,
                Status = p.Status,
                Schedule = p.Schedule,
                RunNum = p.RunNum,
                NextFlag = nextTask != default &&
                           nextTask.Item2 == p.Index &&
                           string.Equals(nextTask.Item3, p.FolderName, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(nextTask.Item4, p.Name, StringComparison.OrdinalIgnoreCase)
            }).ToList()
        };
        await WriteJsonAsync(response, detail, ct);
    }

    private async Task HandleScriptLibraryAsync(HttpListenerResponse response, CancellationToken ct)
    {
        var library = BuildScriptLibrary();
        await WriteJsonAsync(response, library, ct);
    }

    private async Task HandleScriptGroupAddItemsAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        ScriptGroupAddItemsRequest? payload = await ReadJsonAsync<ScriptGroupAddItemsRequest>(request, ct);
        if (payload == null || string.IsNullOrWhiteSpace(payload.Name))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        var group = LoadScriptGroupByName(payload.Name, out var error);
        if (group == null)
        {
            await WriteJsonAsync(response, new { error }, ct);
            return;
        }

        if (payload.JsFolders != null)
        {
            foreach (var folder in payload.JsFolders.Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                try
                {
                    var project = new BetterGenshinImpact.Core.Script.Project.ScriptProject(folder);
                    group.AddProject(new ScriptGroupProject(project));
                }
                catch
                {
                }
            }
        }

        if (payload.KeyMouseNames != null)
        {
            var keyMouseRoot = Path.GetFullPath(Global.Absolute(@"User\KeyMouseScript"));
            foreach (var name in payload.KeyMouseNames.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                if (!TryNormalizeRelativePathUnderRoot(keyMouseRoot, name, out var normalizedRelative))
                {
                    _logger.LogDebug("忽略非法键鼠脚本路径: {Name}", name);
                    continue;
                }

                var fullPath = Path.GetFullPath(Path.Combine(keyMouseRoot, normalizedRelative.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(fullPath))
                {
                    _logger.LogDebug("忽略不存在的键鼠脚本: {Path}", fullPath);
                    continue;
                }

                group.AddProject(ScriptGroupProject.BuildKeyMouseProject(normalizedRelative));
            }
        }

        if (payload.Pathing != null)
        {
            var pathingRoot = Path.GetFullPath(MapPathingViewModel.PathJsonPath);
            foreach (var item in payload.Pathing)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Name))
                {
                    continue;
                }

                var rawRelative = string.IsNullOrWhiteSpace(item.Folder)
                    ? item.Name
                    : $"{item.Folder}/{item.Name}";
                if (!TryNormalizeRelativePathUnderRoot(pathingRoot, rawRelative, out var normalizedRelative))
                {
                    _logger.LogDebug("忽略非法路径追踪脚本路径: {Path}", rawRelative);
                    continue;
                }

                var fullPath = Path.GetFullPath(Path.Combine(pathingRoot, normalizedRelative.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(fullPath))
                {
                    _logger.LogDebug("忽略不存在的路径追踪脚本: {Path}", fullPath);
                    continue;
                }

                var fileName = Path.GetFileName(normalizedRelative);
                var folder = Path.GetDirectoryName(normalizedRelative)?.Replace('\\', '/') ?? string.Empty;
                if (folder == ".")
                {
                    folder = string.Empty;
                }

                group.AddProject(ScriptGroupProject.BuildPathingProject(fileName, folder));
            }
        }

        if (payload.ShellCommands != null)
        {
            foreach (var command in payload.ShellCommands.Where(c => !string.IsNullOrWhiteSpace(c)))
            {
                group.AddProject(ScriptGroupProject.BuildShellProject(command.Trim()));
            }
        }

        ReindexProjects(group);
        AlignScriptGroupNextTask(group);
        SaveScriptGroup(group);
        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandleScriptGroupRemoveItemAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        ScriptGroupItemRequest? payload = await ReadJsonAsync<ScriptGroupItemRequest>(request, ct);
        if (payload == null || string.IsNullOrWhiteSpace(payload.Name))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        var group = LoadScriptGroupByName(payload.Name, out var error);
        if (group == null)
        {
            await WriteJsonAsync(response, new { error }, ct);
            return;
        }

        if (payload.Index < 0 || payload.Index >= group.Projects.Count)
        {
            await WriteJsonAsync(response, new { error = "索引超出范围" }, ct);
            return;
        }

        group.Projects.RemoveAt(payload.Index);
        ReindexProjects(group);
        AlignScriptGroupNextTask(group);
        SaveScriptGroup(group);
        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandleScriptGroupUpdateItemAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        ScriptGroupItemRequest? payload = await ReadJsonAsync<ScriptGroupItemRequest>(request, ct);
        if (payload == null || string.IsNullOrWhiteSpace(payload.Name))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        var group = LoadScriptGroupByName(payload.Name, out var error);
        if (group == null)
        {
            await WriteJsonAsync(response, new { error }, ct);
            return;
        }

        if (payload.Index < 0 || payload.Index >= group.Projects.Count)
        {
            await WriteJsonAsync(response, new { error = "索引超出范围" }, ct);
            return;
        }

        var project = group.Projects[payload.Index];
        ApplyScriptGroupItemPatch(project, payload.Status, payload.Schedule, payload.RunNum);

        SaveScriptGroup(group);
        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandleScriptGroupBatchUpdateAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        ScriptGroupBatchUpdateRequest? payload = await ReadJsonAsync<ScriptGroupBatchUpdateRequest>(request, ct);
        if (payload == null || string.IsNullOrWhiteSpace(payload.Name))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        if (string.IsNullOrWhiteSpace(payload.Status) &&
            string.IsNullOrWhiteSpace(payload.Schedule) &&
            (!payload.RunNum.HasValue || payload.RunNum.Value <= 0))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "缺少可更新字段" }, ct);
            return;
        }

        var group = LoadScriptGroupByName(payload.Name, out var error);
        if (group == null)
        {
            await WriteJsonAsync(response, new { error }, ct);
            return;
        }

        var indices = (payload.Indices == null || payload.Indices.Length == 0)
            ? Enumerable.Range(0, group.Projects.Count).ToArray()
            : payload.Indices
                .Where(i => i >= 0 && i < group.Projects.Count)
                .Distinct()
                .ToArray();
        if (indices.Length == 0)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "没有可更新的索引" }, ct);
            return;
        }

        var updated = 0;
        foreach (var index in indices)
        {
            var project = group.Projects[index];
            if (ApplyScriptGroupItemPatch(project, payload.Status, payload.Schedule, payload.RunNum))
            {
                updated++;
            }
        }

        SaveScriptGroup(group);
        await WriteJsonAsync(response, new
        {
            ok = true,
            updated,
            total = indices.Length
        }, ct);
    }

    private async Task HandleScriptGroupReorderAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        ScriptGroupReorderRequest? payload = await ReadJsonAsync<ScriptGroupReorderRequest>(request, ct);
        if (payload == null || string.IsNullOrWhiteSpace(payload.Name))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        var group = LoadScriptGroupByName(payload.Name, out var error);
        if (group == null)
        {
            await WriteJsonAsync(response, new { error }, ct);
            return;
        }

        if (payload.FromIndex < 0 || payload.FromIndex >= group.Projects.Count ||
            payload.ToIndex < 0 || payload.ToIndex >= group.Projects.Count)
        {
            await WriteJsonAsync(response, new { error = "索引超出范围" }, ct);
            return;
        }

        var item = group.Projects[payload.FromIndex];
        group.Projects.RemoveAt(payload.FromIndex);
        group.Projects.Insert(payload.ToIndex, item);
        ReindexProjects(group);
        AlignScriptGroupNextTask(group);
        SaveScriptGroup(group);
        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandleScriptGroupCopyAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        ScriptGroupCopyRequest? payload = await ReadJsonAsync<ScriptGroupCopyRequest>(request, ct);
        if (payload == null || string.IsNullOrWhiteSpace(payload.Name))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        var source = LoadScriptGroupByName(payload.Name, out var error);
        if (source == null)
        {
            await WriteJsonAsync(response, new { error }, ct);
            return;
        }

        var existingNames = LoadScriptGroupInfos().Select(x => x.Name).ToArray();
        var cloneName = string.IsNullOrWhiteSpace(payload.NewName)
            ? BuildScriptGroupCloneName(source.Name, existingNames)
            : payload.NewName.Trim();
        if (!TryGetScriptGroupFilePath(cloneName, out var file, out var fileError))
        {
            await WriteJsonAsync(response, new { error = fileError ?? "新配置组名称非法" }, ct);
            return;
        }

        if (File.Exists(file))
        {
            await WriteJsonAsync(response, new { error = "新名称已存在" }, ct);
            return;
        }

        ScriptGroup cloned;
        try
        {
            cloned = ScriptGroup.FromJson(source.ToJson());
            cloned.Name = cloneName;
            ReindexProjects(cloned);
            SaveScriptGroup(cloned);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(response, new { error = ex.Message }, ct);
            return;
        }

        await WriteJsonAsync(response, new { ok = true, name = cloneName }, ct);
    }

    private async Task HandleScriptGroupReverseAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        ScriptGroupNameRequest? payload = await ReadJsonAsync<ScriptGroupNameRequest>(request, ct);
        if (payload == null || string.IsNullOrWhiteSpace(payload.Name))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        var group = LoadScriptGroupByName(payload.Name, out var error);
        if (group == null)
        {
            await WriteJsonAsync(response, new { error }, ct);
            return;
        }

        if (group.Projects.Count > 1)
        {
            var reversed = group.Projects.Reverse().ToList();
            group.Projects.Clear();
            foreach (var project in reversed)
            {
                group.Projects.Add(project);
            }

            ReindexProjects(group);
            AlignScriptGroupNextTask(group);
            SaveScriptGroup(group);
        }

        await WriteJsonAsync(response, new { ok = true, count = group.Projects.Count }, ct);
    }

    private async Task HandleScriptGroupSetNextAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        ScriptGroupSetNextRequest? payload = await ReadJsonAsync<ScriptGroupSetNextRequest>(request, ct);
        if (payload == null || string.IsNullOrWhiteSpace(payload.Name))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        var clear = payload.Clear == true || !payload.Index.HasValue || payload.Index.Value < 0;
        if (!TrySetScriptGroupNextTask(payload.Name, payload.Index, clear, out var error, out var project))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = error ?? "设置失败" }, ct);
            return;
        }

        await WriteJsonAsync(response, new
        {
            ok = true,
            name = payload.Name,
            cleared = clear,
            next = project == null
                ? null
                : new
                {
                    index = project.Value.Index,
                    project = project.Value.Name
                }
        }, ct);
    }

    private async Task HandleScriptGroupSetNextGroupAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        ScriptGroupSetNextGroupRequest? payload = await ReadJsonAsync<ScriptGroupSetNextGroupRequest>(request, ct);
        var clear = payload?.Clear == true || string.IsNullOrWhiteSpace(payload?.Name);
        if (clear)
        {
            TaskContext.Instance().Config.NextScriptGroupName = string.Empty;
            await WriteJsonAsync(response, new { ok = true, cleared = true }, ct);
            return;
        }

        var name = payload?.Name?.Trim() ?? string.Empty;
        var group = LoadScriptGroupByName(name, out var error);
        if (group == null)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = error ?? "配置组不存在" }, ct);
            return;
        }

        TaskContext.Instance().Config.NextScriptGroupName = group.Name;
        await WriteJsonAsync(response, new { ok = true, name = group.Name, cleared = false }, ct);
    }

    private async Task HandleScriptGroupRunFromAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        ScriptGroupSetNextRequest? payload = await ReadJsonAsync<ScriptGroupSetNextRequest>(request, ct);
        if (payload == null || string.IsNullOrWhiteSpace(payload.Name) || !payload.Index.HasValue)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        if (!TrySetScriptGroupNextTask(payload.Name, payload.Index, clear: false, out var error, out var project))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = error ?? "设置起点失败" }, ct);
            return;
        }

        await RunScriptGroupsByNamesAsync([payload.Name]);
        await WriteJsonAsync(response, new
        {
            ok = true,
            name = payload.Name,
            next = project == null
                ? null
                : new
                {
                    index = project.Value.Index,
                    project = project.Value.Name
                }
        }, ct);
    }

    private async Task HandleScriptGroupExportMergedAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        ScriptGroupNameRequest? payload = await ReadJsonAsync<ScriptGroupNameRequest>(request, ct);
        if (payload == null || string.IsNullOrWhiteSpace(payload.Name))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        var group = LoadScriptGroupByName(payload.Name, out var error);
        if (group == null)
        {
            await WriteJsonAsync(response, new { error }, ct);
            return;
        }

        var exportRoot = Path.Combine(
            Global.Absolute(@"log"),
            "exportMergerJson",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            "AutoPathing");
        var exported = new List<string>();
        var skipped = new List<object>();

        foreach (var project in group.Projects)
        {
            if (!string.Equals(project.Type, "Pathing", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sourcePath = Path.Combine(BetterGenshinImpact.ViewModel.Pages.MapPathingViewModel.PathJsonPath, project.FolderName, project.Name);
            if (!File.Exists(sourcePath))
            {
                skipped.Add(new { name = project.Name, folder = project.FolderName, reason = "路径文件不存在" });
                continue;
            }

            var mergedJson = JsonMerger.getMergePathingJson(sourcePath);
            var outputPath = Path.Combine(exportRoot, project.FolderName, project.Name);
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            await File.WriteAllTextAsync(outputPath, mergedJson, ct);
            exported.Add(Path.GetRelativePath(exportRoot, outputPath).Replace('\\', '/'));
        }

        await WriteJsonAsync(response, new
        {
            ok = true,
            name = group.Name,
            count = exported.Count,
            exportRoot,
            exported,
            skipped
        }, ct);
    }

    private async Task HandleScriptRunAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        ScriptRunRequest? payload;
        var encoding = request.ContentEncoding ?? Encoding.UTF8;
        using (var reader = new StreamReader(request.InputStream, encoding))
        {
            var json = await reader.ReadToEndAsync(ct);
            payload = JsonSerializer.Deserialize<ScriptRunRequest>(json, JsonOptions);
        }

        var names = new List<string>();
        if (payload != null)
        {
            if (!string.IsNullOrWhiteSpace(payload.Name))
            {
                names.Add(payload.Name);
            }

            if (payload.Names != null)
            {
                foreach (var name in payload.Names)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name);
                    }
                }
            }
        }

        if (names.Count == 0)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        await RunScriptGroupsByNamesAsync(names);

        await WriteJsonAsync(response, new { ok = true, names }, ct);
    }

    private static Task HandleTaskCancelAsync(HttpListenerResponse response, CancellationToken ct)
    {
        CancellationContext.Instance.ManualCancel();
        return WriteJsonAsync(response, new { ok = true }, ct);
    }

    private static Task HandleTaskPauseAsync(HttpListenerResponse response, CancellationToken ct)
    {
        RunnerContext.Instance.IsSuspend = true;
        return WriteJsonAsync(response, new { ok = true }, ct);
    }

    private static Task HandleTaskResumeAsync(HttpListenerResponse response, CancellationToken ct)
    {
        RunnerContext.Instance.IsSuspend = false;
        return WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandleTaskRunAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        TaskRunRequest? payload = await ReadJsonAsync<TaskRunRequest>(request, ct);
        if (payload == null || string.IsNullOrWhiteSpace(payload.Task))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        var result = await RunTaskAsync(payload.Task, payload.Params, ct);
        await WriteJsonAsync(response, result, ct);
    }

    private async Task HandleOneDragonConfigsAsync(HttpListenerResponse response, CancellationToken ct)
    {
        var configs = LoadOneDragonConfigs();
        await WriteJsonAsync(response, new
        {
            selected = TaskContext.Instance().Config.SelectedOneDragonFlowConfigName,
            configs = configs.Select(c => c.Name).ToArray()
        }, ct);
    }

    private async Task HandleOneDragonConfigGetAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var name = request.QueryString["name"];
        if (string.IsNullOrWhiteSpace(name))
        {
            name = TaskContext.Instance().Config.SelectedOneDragonFlowConfigName;
        }

        var config = LoadOneDragonConfigByName(name);
        if (config == null)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            response.Close();
            return;
        }

        var json = Newtonsoft.Json.JsonConvert.SerializeObject(config, Newtonsoft.Json.Formatting.Indented);
        await WriteStringAsync(response, json, "application/json; charset=utf-8", ct);
    }

    private async Task HandleOneDragonConfigSetAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var body = await ReadRawAsync(request, ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        OneDragonFlowConfig? config;
        try
        {
            config = Newtonsoft.Json.JsonConvert.DeserializeObject<OneDragonFlowConfig>(body);
        }
        catch
        {
            await WriteJsonAsync(response, new { error = "配置解析失败" }, ct);
            return;
        }

        if (config == null || string.IsNullOrWhiteSpace(config.Name))
        {
            await WriteJsonAsync(response, new { error = "配置名称为空" }, ct);
            return;
        }

        if (!SaveOneDragonConfig(config))
        {
            await WriteJsonAsync(response, new { error = "配置保存失败" }, ct);
            return;
        }
        TaskContext.Instance().Config.SelectedOneDragonFlowConfigName = config.Name;
        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandleOneDragonSelectAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        OneDragonSelectRequest? payload = await ReadJsonAsync<OneDragonSelectRequest>(request, ct);
        if (payload == null || string.IsNullOrWhiteSpace(payload.Name))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        TaskContext.Instance().Config.SelectedOneDragonFlowConfigName = payload.Name;
        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandleOneDragonOptionsAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var domainNames = LoadDomainNames();
        var leyLineTypes = LoadLeyLineOutcropTypes();
        var leyLineCountries = LoadLeyLineOutcropCountries();
        var optionMap = BuildOneDragonOptionMap(domainNames, leyLineTypes, leyLineCountries);
        await WriteJsonAsync(response, new
        {
            taskCatalog = LoadOneDragonTaskCatalog(),
            optionMap,
            fieldOrder = OneDragonPreferredFieldOrder,
            dayOrder = OneDragonDayNames
        }, ct);
    }

    private async Task HandleOneDragonCloneAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        OneDragonCloneRequest? payload = await ReadJsonAsync<OneDragonCloneRequest>(request, ct);
        var sourceName = payload?.Name;
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = TaskContext.Instance().Config.SelectedOneDragonFlowConfigName;
        }

        if (!TryNormalizeOneDragonConfigName(sourceName, out var normalizedSourceName, out var sourceNameError))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { ok = false, error = sourceNameError ?? "配置名非法" }, ct);
            return;
        }

        var source = LoadOneDragonConfigByName(normalizedSourceName);
        if (source == null)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            await WriteJsonAsync(response, new { ok = false, error = "源配置不存在" }, ct);
            return;
        }

        var existing = OneDragonConfigStore.ListNames();
        var requestedName = payload?.NewName;
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            requestedName = BuildOneDragonCloneName(source.Name, existing);
        }

        if (!TryNormalizeOneDragonConfigName(requestedName, out var cloneName, out var cloneNameError))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { ok = false, error = cloneNameError ?? "新配置名非法" }, ct);
            return;
        }

        if (existing.Any(x => string.Equals(x, cloneName, StringComparison.OrdinalIgnoreCase)))
        {
            response.StatusCode = (int)HttpStatusCode.Conflict;
            await WriteJsonAsync(response, new { ok = false, error = "配置名称已存在" }, ct);
            return;
        }

        var json = Newtonsoft.Json.JsonConvert.SerializeObject(source);
        var cloned = Newtonsoft.Json.JsonConvert.DeserializeObject<OneDragonFlowConfig>(json) ?? new OneDragonFlowConfig();
        cloned.Name = cloneName;
        if (!SaveOneDragonConfig(cloned))
        {
            await WriteJsonAsync(response, new { ok = false, error = "克隆配置保存失败" }, ct);
            return;
        }

        TaskContext.Instance().Config.SelectedOneDragonFlowConfigName = cloned.Name;
        await WriteJsonAsync(response, new { ok = true, name = cloned.Name, selected = cloned.Name }, ct);
    }

    private async Task HandleOneDragonRenameAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        OneDragonRenameRequest? payload = await ReadJsonAsync<OneDragonRenameRequest>(request, ct);
        var oldName = payload?.OldName;
        if (string.IsNullOrWhiteSpace(oldName))
        {
            oldName = TaskContext.Instance().Config.SelectedOneDragonFlowConfigName;
        }

        if (!TryNormalizeOneDragonConfigName(oldName, out var normalizedOldName, out var oldNameError))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { ok = false, error = oldNameError ?? "旧配置名非法" }, ct);
            return;
        }

        if (!TryNormalizeOneDragonConfigName(payload?.NewName, out var normalizedNewName, out var newNameError))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { ok = false, error = newNameError ?? "新配置名非法" }, ct);
            return;
        }

        var config = LoadOneDragonConfigByName(normalizedOldName);
        if (config == null)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            await WriteJsonAsync(response, new { ok = false, error = "配置不存在" }, ct);
            return;
        }

        var existing = OneDragonConfigStore.ListNames();
        if (existing.Any(x => string.Equals(x, normalizedNewName, StringComparison.OrdinalIgnoreCase)) &&
            !string.Equals(normalizedOldName, normalizedNewName, StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = (int)HttpStatusCode.Conflict;
            await WriteJsonAsync(response, new { ok = false, error = "配置名称已存在" }, ct);
            return;
        }

        config.Name = normalizedNewName;
        if (!OneDragonConfigStore.Rename(normalizedOldName, config))
        {
            await WriteJsonAsync(response, new { ok = false, error = "配置重命名失败" }, ct);
            return;
        }

        if (string.Equals(TaskContext.Instance().Config.SelectedOneDragonFlowConfigName, normalizedOldName, StringComparison.OrdinalIgnoreCase))
        {
            TaskContext.Instance().Config.SelectedOneDragonFlowConfigName = normalizedNewName;
        }

        await WriteJsonAsync(response, new { ok = true, name = normalizedNewName }, ct);
    }

    private async Task HandleOneDragonDeleteAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        OneDragonConfigNameRequest? payload = await ReadJsonAsync<OneDragonConfigNameRequest>(request, ct);
        var name = payload?.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = TaskContext.Instance().Config.SelectedOneDragonFlowConfigName;
        }

        if (!TryNormalizeOneDragonConfigName(name, out var normalizedName, out var nameError))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { ok = false, error = nameError ?? "配置名非法" }, ct);
            return;
        }

        var existing = OneDragonConfigStore.ListNames();
        if (existing.Count <= 1)
        {
            await WriteJsonAsync(response, new { ok = false, error = "至少保留一个一条龙配置" }, ct);
            return;
        }

        if (!existing.Any(x => string.Equals(x, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            await WriteJsonAsync(response, new { ok = false, error = "配置不存在" }, ct);
            return;
        }

        if (!OneDragonConfigStore.Delete(normalizedName))
        {
            await WriteJsonAsync(response, new { ok = false, error = "删除配置失败" }, ct);
            return;
        }

        var left = OneDragonConfigStore.ListNames();
        if (string.Equals(TaskContext.Instance().Config.SelectedOneDragonFlowConfigName, normalizedName, StringComparison.OrdinalIgnoreCase))
        {
            TaskContext.Instance().Config.SelectedOneDragonFlowConfigName = left.FirstOrDefault() ?? string.Empty;
        }

        await WriteJsonAsync(response, new
        {
            ok = true,
            selected = TaskContext.Instance().Config.SelectedOneDragonFlowConfigName,
            configs = left
        }, ct);
    }

    private async Task HandleOneDragonRunAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        OneDragonSelectRequest? payload = await ReadJsonAsync<OneDragonSelectRequest>(request, ct);
        var name = payload?.Name;
        await RunOnUiThreadAsync(() =>
        {
            var vm = new BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel
            {
                EnableHotReload = false
            };
            vm.OnNavigatedTo();
            if (!string.IsNullOrWhiteSpace(name))
            {
                var config = LoadOneDragonConfigByName(name);
                if (config != null)
                {
                    vm.SelectedConfig = config;
                    vm.SetSomeSelectedConfig(config);
                }
            }
            StartBackgroundTask(() => vm.OnOneKeyExecute());
            return Task.CompletedTask;
        });

        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandleAutoGeniusInvokationSettingsAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        AutoGeniusInvokationSettingsRequest? payload = await ReadJsonAsync<AutoGeniusInvokationSettingsRequest>(request, ct);
        if (payload != null)
        {
            var config = _configService.Get().AutoGeniusInvokationConfig;
            if (!string.IsNullOrWhiteSpace(payload.StrategyName))
            {
                config.StrategyName = payload.StrategyName;
            }
            if (payload.SleepDelay.HasValue)
            {
                config.SleepDelay = Math.Max(0, payload.SleepDelay.Value);
            }
        }

        await WriteJsonAsync(response, BuildAutoGeniusInvokationSettings(), ct);
    }

    private async Task HandleAutoWoodSettingsAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        AutoWoodSettingsRequest? payload = await ReadJsonAsync<AutoWoodSettingsRequest>(request, ct);
        if (payload != null)
        {
            var config = _configService.Get().AutoWoodConfig;
            if (payload.AfterZSleepDelay.HasValue)
            {
                config.AfterZSleepDelay = Math.Max(0, payload.AfterZSleepDelay.Value);
            }
            if (payload.WoodCountOcrEnabled.HasValue)
            {
                config.WoodCountOcrEnabled = payload.WoodCountOcrEnabled.Value;
            }
            if (payload.UseWonderlandRefresh.HasValue)
            {
                config.UseWonderlandRefresh = payload.UseWonderlandRefresh.Value;
            }
        }

        await WriteJsonAsync(response, BuildAutoWoodSettings(), ct);
    }

    private async Task HandleAutoFightSettingsAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        AutoFightSettingsRequest? payload = await ReadJsonAsync<AutoFightSettingsRequest>(request, ct);
        if (payload != null)
        {
            ApplyAutoFightSettings(payload);
        }

        await WriteJsonAsync(response, BuildAutoFightSettings(), ct);
    }

    private async Task HandleAutoDomainSettingsAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        AutoDomainSettingsRequest? payload = await ReadJsonAsync<AutoDomainSettingsRequest>(request, ct);
        if (payload != null)
        {
            ApplyAutoDomainSettings(payload);
        }

        await WriteJsonAsync(response, BuildAutoDomainSettings(), ct);
    }

    private void ApplyAutoFightSettings(AutoFightSettingsRequest payload)
    {
        var config = _configService.Get().AutoFightConfig;
        var finishDetect = config.FinishDetectConfig ?? new AutoFightConfig.FightFinishDetectConfig();
        config.FinishDetectConfig = finishDetect;

        if (payload.StrategyName != null)
        {
            config.StrategyName = payload.StrategyName.Trim();
        }

        if (payload.TeamNames != null)
        {
            config.TeamNames = payload.TeamNames.Trim();
        }

        if (payload.FightFinishDetectEnabled.HasValue)
        {
            config.FightFinishDetectEnabled = payload.FightFinishDetectEnabled.Value;
        }

        if (payload.ActionSchedulerByCd != null)
        {
            config.ActionSchedulerByCd = payload.ActionSchedulerByCd.Trim();
        }

        if (payload.OnlyPickEliteDropsMode != null)
        {
            config.OnlyPickEliteDropsMode = payload.OnlyPickEliteDropsMode.Trim();
        }

        if (payload.PickDropsAfterFightEnabled.HasValue)
        {
            config.PickDropsAfterFightEnabled = payload.PickDropsAfterFightEnabled.Value;
        }

        if (payload.PickDropsAfterFightSeconds.HasValue)
        {
            config.PickDropsAfterFightSeconds = Math.Max(0, payload.PickDropsAfterFightSeconds.Value);
        }

        if (payload.BattleThresholdForLoot.HasValue)
        {
            config.BattleThresholdForLoot = Math.Max(0, payload.BattleThresholdForLoot.Value);
        }

        if (payload.KazuhaPickupEnabled.HasValue)
        {
            config.KazuhaPickupEnabled = payload.KazuhaPickupEnabled.Value;
        }

        if (payload.QinDoublePickUp.HasValue)
        {
            config.QinDoublePickUp = payload.QinDoublePickUp.Value;
        }

        if (payload.GuardianAvatar != null)
        {
            config.GuardianAvatar = payload.GuardianAvatar.Trim();
        }

        if (payload.GuardianCombatSkip.HasValue)
        {
            config.GuardianCombatSkip = payload.GuardianCombatSkip.Value;
        }

        if (payload.SkipModel.HasValue)
        {
            config.SkipModel = payload.SkipModel.Value;
        }

        if (payload.GuardianAvatarHold.HasValue)
        {
            config.GuardianAvatarHold = payload.GuardianAvatarHold.Value;
        }

        if (payload.BurstEnabled.HasValue)
        {
            config.BurstEnabled = payload.BurstEnabled.Value;
        }

        if (payload.KazuhaPartyName != null)
        {
            config.KazuhaPartyName = payload.KazuhaPartyName.Trim();
        }

        if (payload.SwimmingEnabled.HasValue)
        {
            config.SwimmingEnabled = payload.SwimmingEnabled.Value;
        }

        if (payload.Timeout.HasValue)
        {
            config.Timeout = Math.Max(1, payload.Timeout.Value);
        }

        if (payload.BattleEndProgressBarColor != null)
        {
            finishDetect.BattleEndProgressBarColor = payload.BattleEndProgressBarColor.Trim();
        }

        if (payload.BattleEndProgressBarColorTolerance != null)
        {
            finishDetect.BattleEndProgressBarColorTolerance = payload.BattleEndProgressBarColorTolerance.Trim();
        }

        if (payload.FastCheckEnabled.HasValue)
        {
            finishDetect.FastCheckEnabled = payload.FastCheckEnabled.Value;
        }

        if (payload.RotateFindEnemyEnabled.HasValue)
        {
            finishDetect.RotateFindEnemyEnabled = payload.RotateFindEnemyEnabled.Value;
        }

        if (payload.FastCheckParams != null)
        {
            finishDetect.FastCheckParams = payload.FastCheckParams.Trim();
        }

        if (payload.CheckEndDelay != null)
        {
            finishDetect.CheckEndDelay = payload.CheckEndDelay.Trim();
        }

        if (payload.BeforeDetectDelay != null)
        {
            finishDetect.BeforeDetectDelay = payload.BeforeDetectDelay.Trim();
        }

        if (payload.RotaryFactor.HasValue)
        {
            finishDetect.RotaryFactor = Math.Max(1, payload.RotaryFactor.Value);
        }

        if (payload.IsFirstCheck.HasValue)
        {
            finishDetect.IsFirstCheck = payload.IsFirstCheck.Value;
        }

        if (payload.CheckBeforeBurst.HasValue)
        {
            finishDetect.CheckBeforeBurst = payload.CheckBeforeBurst.Value;
        }
    }

    private void ApplyAutoDomainSettings(AutoDomainSettingsRequest payload)
    {
        var config = _configService.Get().AutoDomainConfig;
        if (payload.PartyName != null)
        {
            config.PartyName = payload.PartyName.Trim();
        }

        if (payload.DomainName != null)
        {
            config.DomainName = payload.DomainName.Trim();
        }

        if (payload.SundaySelectedValue != null)
        {
            config.SundaySelectedValue = payload.SundaySelectedValue.Trim();
        }

        if (payload.FightEndDelay.HasValue)
        {
            config.FightEndDelay = Math.Max(0, payload.FightEndDelay.Value);
        }

        if (payload.ShortMovement.HasValue)
        {
            config.ShortMovement = payload.ShortMovement.Value;
        }

        if (payload.WalkToF.HasValue)
        {
            config.WalkToF = payload.WalkToF.Value;
        }

        if (payload.LeftRightMoveTimes.HasValue)
        {
            config.LeftRightMoveTimes = Math.Max(0, payload.LeftRightMoveTimes.Value);
        }

        if (payload.AutoEat.HasValue)
        {
            config.AutoEat = payload.AutoEat.Value;
        }

        if (payload.AutoArtifactSalvage.HasValue)
        {
            config.AutoArtifactSalvage = payload.AutoArtifactSalvage.Value;
        }

        if (payload.SpecifyResinUse.HasValue)
        {
            config.SpecifyResinUse = payload.SpecifyResinUse.Value;
        }

        if (payload.ResinPriorityList != null)
        {
            config.ResinPriorityList = payload.ResinPriorityList.ToList();
        }

        if (payload.OriginalResinUseCount.HasValue)
        {
            config.OriginalResinUseCount = Math.Max(0, payload.OriginalResinUseCount.Value);
        }

        if (payload.OriginalResin20UseCount.HasValue)
        {
            config.OriginalResin20UseCount = Math.Max(0, payload.OriginalResin20UseCount.Value);
        }

        if (payload.OriginalResin40UseCount.HasValue)
        {
            config.OriginalResin40UseCount = Math.Max(0, payload.OriginalResin40UseCount.Value);
        }

        if (payload.CondensedResinUseCount.HasValue)
        {
            config.CondensedResinUseCount = Math.Max(0, payload.CondensedResinUseCount.Value);
        }

        if (payload.TransientResinUseCount.HasValue)
        {
            config.TransientResinUseCount = Math.Max(0, payload.TransientResinUseCount.Value);
        }

        if (payload.FragileResinUseCount.HasValue)
        {
            config.FragileResinUseCount = Math.Max(0, payload.FragileResinUseCount.Value);
        }

        if (payload.ReviveRetryCount.HasValue)
        {
            config.ReviveRetryCount = Math.Max(0, payload.ReviveRetryCount.Value);
        }

        if (payload.StrategyName != null)
        {
            _configService.Get().AutoFightConfig.StrategyName = payload.StrategyName.Trim();
        }
    }

    private async Task HandleAutoStygianSettingsAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        AutoStygianSettingsRequest? payload = await ReadJsonAsync<AutoStygianSettingsRequest>(request, ct);
        if (payload != null)
        {
            var config = _configService.Get().AutoStygianOnslaughtConfig;
            if (!string.IsNullOrWhiteSpace(payload.StrategyName))
            {
                config.StrategyName = payload.StrategyName;
            }
            if (payload.BossNum.HasValue)
            {
                config.BossNum = Math.Clamp(payload.BossNum.Value, 1, 3);
            }
            if (!string.IsNullOrWhiteSpace(payload.FightTeamName))
            {
                config.FightTeamName = payload.FightTeamName;
            }
            if (payload.AutoArtifactSalvage.HasValue)
            {
                config.AutoArtifactSalvage = payload.AutoArtifactSalvage.Value;
            }
            if (payload.SpecifyResinUse.HasValue)
            {
                config.SpecifyResinUse = payload.SpecifyResinUse.Value;
            }
            if (payload.ResinPriorityList != null)
            {
                config.ResinPriorityList = payload.ResinPriorityList.ToList();
            }
            if (payload.OriginalResinUseCount.HasValue)
            {
                config.OriginalResinUseCount = Math.Max(0, payload.OriginalResinUseCount.Value);
            }
            if (payload.CondensedResinUseCount.HasValue)
            {
                config.CondensedResinUseCount = Math.Max(0, payload.CondensedResinUseCount.Value);
            }
            if (payload.TransientResinUseCount.HasValue)
            {
                config.TransientResinUseCount = Math.Max(0, payload.TransientResinUseCount.Value);
            }
            if (payload.FragileResinUseCount.HasValue)
            {
                config.FragileResinUseCount = Math.Max(0, payload.FragileResinUseCount.Value);
            }
        }

        await WriteJsonAsync(response, BuildAutoStygianSettings(), ct);
    }

    private async Task HandleAutoFishingSettingsAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        AutoFishingSettingsRequest? payload = await ReadJsonAsync<AutoFishingSettingsRequest>(request, ct);
        if (payload != null)
        {
            var config = _configService.Get().AutoFishingConfig;
            if (payload.Enabled.HasValue)
            {
                config.Enabled = payload.Enabled.Value;
            }
            if (payload.AutoThrowRodEnabled.HasValue)
            {
                config.AutoThrowRodEnabled = payload.AutoThrowRodEnabled.Value;
            }
            if (payload.AutoThrowRodTimeOut.HasValue)
            {
                config.AutoThrowRodTimeOut = Math.Max(1, payload.AutoThrowRodTimeOut.Value);
            }
            if (payload.WholeProcessTimeoutSeconds.HasValue)
            {
                config.WholeProcessTimeoutSeconds = Math.Max(1, payload.WholeProcessTimeoutSeconds.Value);
            }
            if (!string.IsNullOrWhiteSpace(payload.FishingTimePolicy) &&
                Enum.TryParse(payload.FishingTimePolicy, true, out FishingTimePolicy policy))
            {
                config.FishingTimePolicy = policy;
            }
            if (!string.IsNullOrWhiteSpace(payload.TorchDllFullPath))
            {
                config.TorchDllFullPath = payload.TorchDllFullPath;
            }
        }

        await WriteJsonAsync(response, BuildAutoFishingSettings(), ct);
    }

    private async Task HandleAutoMusicSettingsAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        AutoMusicSettingsRequest? payload = await ReadJsonAsync<AutoMusicSettingsRequest>(request, ct);
        if (payload != null)
        {
            var config = _configService.Get().AutoMusicGameConfig;
            if (payload.MustCanorusLevel.HasValue)
            {
                config.MustCanorusLevel = payload.MustCanorusLevel.Value;
            }
            if (!string.IsNullOrWhiteSpace(payload.MusicLevel))
            {
                config.MusicLevel = payload.MusicLevel;
            }
        }

        await WriteJsonAsync(response, BuildAutoMusicSettings(), ct);
    }

    private async Task HandleAutoArtifactSettingsAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        AutoArtifactSettingsRequest? payload = await ReadJsonAsync<AutoArtifactSettingsRequest>(request, ct);
        if (payload != null)
        {
            var config = _configService.Get().AutoArtifactSalvageConfig;
            if (!string.IsNullOrWhiteSpace(payload.ArtifactSetFilter))
            {
                config.ArtifactSetFilter = payload.ArtifactSetFilter;
            }
            if (!string.IsNullOrWhiteSpace(payload.MaxArtifactStar))
            {
                config.MaxArtifactStar = payload.MaxArtifactStar;
            }
            if (payload.MaxNumToCheck.HasValue)
            {
                config.MaxNumToCheck = Math.Max(1, payload.MaxNumToCheck.Value);
            }
            if (!string.IsNullOrWhiteSpace(payload.RecognitionFailurePolicy) &&
                Enum.TryParse(payload.RecognitionFailurePolicy, true, out RecognitionFailurePolicy policy))
            {
                config.RecognitionFailurePolicy = policy;
            }
            if (!string.IsNullOrWhiteSpace(payload.JavaScript))
            {
                config.JavaScript = payload.JavaScript;
            }
        }

        await WriteJsonAsync(response, BuildAutoArtifactSettings(), ct);
    }

    private async Task HandleGridIconsSettingsAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        GridIconsSettingsRequest? payload = await ReadJsonAsync<GridIconsSettingsRequest>(request, ct);
        if (payload != null)
        {
            var config = _configService.Get().GetGridIconsConfig;
            if (!string.IsNullOrWhiteSpace(payload.GridName) &&
                Enum.TryParse(payload.GridName, true, out BetterGenshinImpact.GameTask.Model.GameUI.GridScreenName gridName))
            {
                config.GridName = gridName;
            }
            if (payload.StarAsSuffix.HasValue)
            {
                config.StarAsSuffix = payload.StarAsSuffix.Value;
            }
            if (payload.LvAsSuffix.HasValue)
            {
                config.LvAsSuffix = payload.LvAsSuffix.Value;
            }
            if (payload.MaxNumToGet.HasValue)
            {
                config.MaxNumToGet = Math.Max(1, payload.MaxNumToGet.Value);
            }
        }

        await WriteJsonAsync(response, BuildGridIconsSettings(), ct);
    }

    private async Task HandleAutoLeyLineSettingsAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        AutoLeyLineSettingsRequest? payload = await ReadJsonAsync<AutoLeyLineSettingsRequest>(request, ct);
        if (payload != null)
        {
            var config = _configService.Get().AutoLeyLineOutcropConfig;
            if (!string.IsNullOrWhiteSpace(payload.LeyLineOutcropType))
            {
                config.LeyLineOutcropType = payload.LeyLineOutcropType;
            }

            if (!string.IsNullOrWhiteSpace(payload.Country))
            {
                config.Country = payload.Country;
            }

            if (payload.IsResinExhaustionMode.HasValue)
            {
                config.IsResinExhaustionMode = payload.IsResinExhaustionMode.Value;
            }

            if (payload.OpenModeCountMin.HasValue)
            {
                config.OpenModeCountMin = payload.OpenModeCountMin.Value;
            }

            if (payload.Count.HasValue)
            {
                config.Count = Math.Max(1, payload.Count.Value);
            }

            if (payload.UseTransientResin.HasValue)
            {
                config.UseTransientResin = payload.UseTransientResin.Value;
            }

            if (payload.UseFragileResin.HasValue)
            {
                config.UseFragileResin = payload.UseFragileResin.Value;
            }

            if (!string.IsNullOrWhiteSpace(payload.Team))
            {
                config.Team = payload.Team;
            }

            if (!string.IsNullOrWhiteSpace(payload.FriendshipTeam))
            {
                config.FriendshipTeam = payload.FriendshipTeam;
            }

            if (payload.Timeout.HasValue)
            {
                config.Timeout = Math.Max(1, payload.Timeout.Value);
            }

            if (payload.UseAdventurerHandbook.HasValue)
            {
                config.UseAdventurerHandbook = payload.UseAdventurerHandbook.Value;
            }

            if (payload.IsNotification.HasValue)
            {
                config.IsNotification = payload.IsNotification.Value;
            }

            if (payload.IsGoToSynthesizer.HasValue)
            {
                config.IsGoToSynthesizer = payload.IsGoToSynthesizer.Value;
            }

            if (payload.ScanDropsAfterRewardEnabled.HasValue)
            {
                config.ScanDropsAfterRewardEnabled = payload.ScanDropsAfterRewardEnabled.Value;
            }

            if (payload.ScanDropsAfterRewardSeconds.HasValue)
            {
                config.ScanDropsAfterRewardSeconds = Math.Clamp(payload.ScanDropsAfterRewardSeconds.Value, 0, 60);
            }
        }

        await WriteJsonAsync(response, BuildAutoLeyLineSettings(), ct);
    }

    private async Task HandleNotificationSettingsAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        NotificationSettingsRequest? payload = await ReadJsonAsync<NotificationSettingsRequest>(request, ct);
        if (payload != null)
        {
            var config = _configService.Get().NotificationConfig;
            var hasChanges = false;

            if (payload.JsNotificationEnabled.HasValue)
            {
                config.JsNotificationEnabled = payload.JsNotificationEnabled.Value;
                hasChanges = true;
            }

            if (payload.IncludeScreenShot.HasValue)
            {
                config.IncludeScreenShot = payload.IncludeScreenShot.Value;
                hasChanges = true;
            }

            if (payload.NotificationEventSubscribe != null)
            {
                config.NotificationEventSubscribe = payload.NotificationEventSubscribe;
                hasChanges = true;
            }

            if (payload.WebhookEnabled.HasValue)
            {
                config.WebhookEnabled = payload.WebhookEnabled.Value;
                hasChanges = true;
            }

            if (payload.WebhookEndpoint != null)
            {
                config.WebhookEndpoint = payload.WebhookEndpoint;
                hasChanges = true;
            }

            if (payload.WebhookSendTo != null)
            {
                config.WebhookSendTo = payload.WebhookSendTo;
                hasChanges = true;
            }

            if (payload.WebSocketNotificationEnabled.HasValue)
            {
                config.WebSocketNotificationEnabled = payload.WebSocketNotificationEnabled.Value;
                hasChanges = true;
            }

            if (payload.WebSocketEndpoint != null)
            {
                config.WebSocketEndpoint = payload.WebSocketEndpoint;
                hasChanges = true;
            }

            if (payload.WindowsUwpNotificationEnabled.HasValue)
            {
                config.WindowsUwpNotificationEnabled = payload.WindowsUwpNotificationEnabled.Value;
                hasChanges = true;
            }

            if (payload.FeishuNotificationEnabled.HasValue)
            {
                config.FeishuNotificationEnabled = payload.FeishuNotificationEnabled.Value;
                hasChanges = true;
            }

            if (payload.FeishuWebhookUrl != null)
            {
                config.FeishuWebhookUrl = payload.FeishuWebhookUrl;
                hasChanges = true;
            }

            if (payload.FeishuAppId != null)
            {
                config.FeishuAppId = payload.FeishuAppId;
                hasChanges = true;
            }

            if (payload.FeishuAppSecret != null)
            {
                config.FeishuAppSecret = payload.FeishuAppSecret;
                hasChanges = true;
            }

            if (payload.OneBotNotificationEnabled.HasValue)
            {
                config.OneBotNotificationEnabled = payload.OneBotNotificationEnabled.Value;
                hasChanges = true;
            }

            if (payload.OneBotEndpoint != null)
            {
                config.OneBotEndpoint = payload.OneBotEndpoint;
                hasChanges = true;
            }

            if (payload.OneBotUserId != null)
            {
                config.OneBotUserId = payload.OneBotUserId;
                hasChanges = true;
            }

            if (payload.OneBotGroupId != null)
            {
                config.OneBotGroupId = payload.OneBotGroupId;
                hasChanges = true;
            }

            if (payload.OneBotToken != null)
            {
                config.OneBotToken = payload.OneBotToken;
                hasChanges = true;
            }

            if (payload.WorkweixinNotificationEnabled.HasValue)
            {
                config.WorkweixinNotificationEnabled = payload.WorkweixinNotificationEnabled.Value;
                hasChanges = true;
            }

            if (payload.WorkweixinWebhookUrl != null)
            {
                config.WorkweixinWebhookUrl = payload.WorkweixinWebhookUrl;
                hasChanges = true;
            }

            if (payload.EmailNotificationEnabled.HasValue)
            {
                config.EmailNotificationEnabled = payload.EmailNotificationEnabled.Value;
                hasChanges = true;
            }

            if (payload.SmtpServer != null)
            {
                config.SmtpServer = payload.SmtpServer;
                hasChanges = true;
            }

            if (payload.SmtpPort.HasValue)
            {
                config.SmtpPort = Math.Max(0, payload.SmtpPort.Value);
                hasChanges = true;
            }

            if (payload.SmtpUsername != null)
            {
                config.SmtpUsername = payload.SmtpUsername;
                hasChanges = true;
            }

            if (payload.SmtpPassword != null)
            {
                config.SmtpPassword = payload.SmtpPassword;
                hasChanges = true;
            }

            if (payload.FromEmail != null)
            {
                config.FromEmail = payload.FromEmail;
                hasChanges = true;
            }

            if (payload.FromName != null)
            {
                config.FromName = payload.FromName;
                hasChanges = true;
            }

            if (payload.ToEmail != null)
            {
                config.ToEmail = payload.ToEmail;
                hasChanges = true;
            }

            if (payload.BarkNotificationEnabled.HasValue)
            {
                config.BarkNotificationEnabled = payload.BarkNotificationEnabled.Value;
                hasChanges = true;
            }

            if (payload.BarkApiEndpoint != null)
            {
                config.BarkApiEndpoint = payload.BarkApiEndpoint;
                hasChanges = true;
            }

            if (payload.BarkDeviceKeys != null)
            {
                config.BarkDeviceKeys = payload.BarkDeviceKeys;
                hasChanges = true;
            }

            if (payload.BarkLevel != null)
            {
                config.BarkLevel = payload.BarkLevel;
                hasChanges = true;
            }

            if (payload.BarkSound != null)
            {
                config.BarkSound = payload.BarkSound;
                hasChanges = true;
            }

            if (payload.BarkIcon != null)
            {
                config.BarkIcon = payload.BarkIcon;
                hasChanges = true;
            }

            if (payload.BarkGroup != null)
            {
                config.BarkGroup = payload.BarkGroup;
                hasChanges = true;
            }

            if (payload.BarkIsArchive != null)
            {
                config.BarkIsArchive = payload.BarkIsArchive;
                hasChanges = true;
            }

            if (payload.BarkCiphertext != null)
            {
                config.BarkCiphertext = payload.BarkCiphertext;
                hasChanges = true;
            }

            if (payload.BarkBadge.HasValue)
            {
                config.BarkBadge = payload.BarkBadge.Value;
                hasChanges = true;
            }

            if (payload.BarkVolume.HasValue)
            {
                config.BarkVolume = Math.Clamp(payload.BarkVolume.Value, 0, 10);
                hasChanges = true;
            }

            if (payload.BarkAction != null)
            {
                config.BarkAction = payload.BarkAction;
                hasChanges = true;
            }

            if (payload.BarkUrl != null)
            {
                config.BarkUrl = payload.BarkUrl;
                hasChanges = true;
            }

            if (payload.BarkCall != null)
            {
                config.BarkCall = payload.BarkCall;
                hasChanges = true;
            }

            if (payload.BarkAutoCopy != null)
            {
                config.BarkAutoCopy = payload.BarkAutoCopy;
                hasChanges = true;
            }

            if (payload.BarkCopy != null)
            {
                config.BarkCopy = payload.BarkCopy;
                hasChanges = true;
            }

            if (payload.BarkSubtitle != null)
            {
                config.BarkSubtitle = payload.BarkSubtitle;
                hasChanges = true;
            }

            if (payload.TelegramNotificationEnabled.HasValue)
            {
                config.TelegramNotificationEnabled = payload.TelegramNotificationEnabled.Value;
                hasChanges = true;
            }

            if (payload.TelegramBotToken != null)
            {
                config.TelegramBotToken = payload.TelegramBotToken;
                hasChanges = true;
            }

            if (payload.TelegramChatId != null)
            {
                config.TelegramChatId = payload.TelegramChatId;
                hasChanges = true;
            }

            if (payload.TelegramApiBaseUrl != null)
            {
                config.TelegramApiBaseUrl = payload.TelegramApiBaseUrl;
                hasChanges = true;
            }

            if (payload.TelegramProxyEnabled.HasValue)
            {
                config.TelegramProxyEnabled = payload.TelegramProxyEnabled.Value;
                hasChanges = true;
            }

            if (payload.TelegramProxyUrl != null)
            {
                config.TelegramProxyUrl = payload.TelegramProxyUrl;
                hasChanges = true;
            }

            if (payload.XxtuiNotificationEnabled.HasValue)
            {
                config.XxtuiNotificationEnabled = payload.XxtuiNotificationEnabled.Value;
                hasChanges = true;
            }

            if (payload.XxtuiApiKey != null)
            {
                config.XxtuiApiKey = payload.XxtuiApiKey;
                hasChanges = true;
            }

            if (payload.XxtuiFrom != null)
            {
                config.XxtuiFrom = payload.XxtuiFrom;
                hasChanges = true;
            }

            if (payload.XxtuiChannels != null)
            {
                config.XxtuiChannels = payload.XxtuiChannels;
                hasChanges = true;
            }

            if (payload.DingDingwebhookNotificationEnabled.HasValue)
            {
                config.DingDingwebhookNotificationEnabled = payload.DingDingwebhookNotificationEnabled.Value;
                hasChanges = true;
            }

            if (payload.DingdingWebhookUrl != null)
            {
                config.DingdingWebhookUrl = payload.DingdingWebhookUrl;
                hasChanges = true;
            }

            if (payload.DingDingSecret != null)
            {
                config.DingDingSecret = payload.DingDingSecret;
                hasChanges = true;
            }

            if (payload.DiscordWebhookNotificationEnabled.HasValue)
            {
                config.DiscordWebhookNotificationEnabled = payload.DiscordWebhookNotificationEnabled.Value;
                hasChanges = true;
            }

            if (payload.DiscordWebhookUrl != null)
            {
                config.DiscordWebhookUrl = payload.DiscordWebhookUrl;
                hasChanges = true;
            }

            if (payload.DiscordWebhookUsername != null)
            {
                config.DiscordWebhookUsername = payload.DiscordWebhookUsername;
                hasChanges = true;
            }

            if (payload.DiscordWebhookAvatarUrl != null)
            {
                config.DiscordWebhookAvatarUrl = payload.DiscordWebhookAvatarUrl;
                hasChanges = true;
            }

            if (payload.DiscordWebhookImageEncoder != null)
            {
                config.DiscordWebhookImageEncoder = payload.DiscordWebhookImageEncoder;
                hasChanges = true;
            }

            if (payload.ServerChanNotificationEnabled.HasValue)
            {
                config.ServerChanNotificationEnabled = payload.ServerChanNotificationEnabled.Value;
                hasChanges = true;
            }

            if (payload.ServerChanSendKey != null)
            {
                config.ServerChanSendKey = payload.ServerChanSendKey;
                hasChanges = true;
            }

            if (hasChanges)
            {
                TryRefreshNotificationNotifiers();
            }
        }

        await WriteJsonAsync(response, BuildNotificationSettings(), ct);
    }

    private async Task HandleNotificationTestAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var payload = await ReadJsonAsync<NotificationTestRequest>(request, ct);
        var channel = NormalizeNotificationTestChannel(payload?.Channel);
        if (string.IsNullOrWhiteSpace(channel))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new
            {
                ok = false,
                error = "invalid channel",
                channels = LoadNotificationChannels()
            }, ct);
            return;
        }

        NotificationService notificationService;
        try
        {
            notificationService = NotificationService.Instance();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "通知服务未就绪，无法执行通知测试");
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
            await WriteJsonAsync(response, new
            {
                ok = false,
                channel,
                error = "notification service unavailable"
            }, ct);
            return;
        }

        TryRefreshNotificationNotifiers();
        var (isSuccess, message) = await TestNotificationChannelAsync(notificationService, channel);
        if (!isSuccess)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
        }

        await WriteJsonAsync(response, new
        {
            ok = isSuccess,
            channel,
            isSuccess,
            message
        }, ct);
    }

    private async Task<(bool IsSuccess, string Message)> TestNotificationChannelAsync(NotificationService notificationService, string channel)
    {
        switch (channel)
        {
            case "webhook":
            {
                var result = await notificationService.TestNotifierAsync<WebhookNotifier>();
                return (result.IsSuccess, result.Message);
            }
            case "websocket":
            {
                var result = await notificationService.TestNotifierAsync<WebSocketNotifier>();
                return (result.IsSuccess, result.Message);
            }
            case "windows_uwp":
            {
                var result = await notificationService.TestNotifierAsync<WindowsUwpNotifier>();
                return (result.IsSuccess, result.Message);
            }
            case "feishu":
            {
                var result = await notificationService.TestNotifierAsync<FeishuNotifier>();
                return (result.IsSuccess, result.Message);
            }
            case "onebot":
            {
                var result = await notificationService.TestNotifierAsync<OneBotNotifier>();
                return (result.IsSuccess, result.Message);
            }
            case "workweixin":
            {
                var result = await notificationService.TestNotifierAsync<WorkWeixinNotifier>();
                return (result.IsSuccess, result.Message);
            }
            case "email":
            {
                var result = await notificationService.TestNotifierAsync<EmailNotifier>();
                return (result.IsSuccess, result.Message);
            }
            case "bark":
            {
                var result = await notificationService.TestNotifierAsync<BarkNotifier>();
                return (result.IsSuccess, result.Message);
            }
            case "telegram":
            {
                var result = await notificationService.TestNotifierAsync<TelegramNotifier>();
                return (result.IsSuccess, result.Message);
            }
            case "xxtui":
            {
                var result = await notificationService.TestNotifierAsync<XxtuiNotifier>();
                return (result.IsSuccess, result.Message);
            }
            case "dingding":
            {
                var result = await notificationService.TestNotifierAsync<DingDingWebhook>();
                return (result.IsSuccess, result.Message);
            }
            case "discord":
            {
                var result = await notificationService.TestNotifierAsync<DiscordWebhookNotifier>();
                return (result.IsSuccess, result.Message);
            }
            case "serverchan":
            {
                var result = await notificationService.TestNotifierAsync<ServerChanNotifier>();
                return (result.IsSuccess, result.Message);
            }
            default:
                return (false, $"invalid channel: {channel}");
        }
    }

    private void TryRefreshNotificationNotifiers()
    {
        try
        {
            NotificationService.Instance().RefreshNotifiers();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刷新通知渠道失败");
        }
    }

    private static string? NormalizeNotificationTestChannel(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            return null;
        }

        var normalized = channel.Trim()
            .ToLowerInvariant()
            .Replace('_', '-')
            .Replace(" ", string.Empty);
        return normalized switch
        {
            "webhook" => "webhook",
            "websocket" => "websocket",
            "web-socket" => "websocket",
            "windowsuwp" => "windows_uwp",
            "windows-uwp" => "windows_uwp",
            "uwp" => "windows_uwp",
            "feishu" => "feishu",
            "onebot" => "onebot",
            "one-bot" => "onebot",
            "workweixin" => "workweixin",
            "work-weixin" => "workweixin",
            "wecom" => "workweixin",
            "email" => "email",
            "mail" => "email",
            "bark" => "bark",
            "telegram" => "telegram",
            "tg" => "telegram",
            "xxtui" => "xxtui",
            "xx-tui" => "xxtui",
            "dingding" => "dingding",
            "ding-ding" => "dingding",
            "dingdingwebhook" => "dingding",
            "dingding-webhook" => "dingding",
            "dingtalk" => "dingding",
            "discord" => "discord",
            "discordwebhook" => "discord",
            "discord-webhook" => "discord",
            "serverchan" => "serverchan",
            "server-chan" => "serverchan",
            _ => null
        };
    }

    private async Task HandleLogStreamAsync(HttpListenerResponse response, CancellationToken ct)
    {
        if (_config?.LogStreamEnabled != true)
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            response.Close();
            return;
        }

        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/event-stream";
        response.Headers.Add("Cache-Control", "no-cache");
        response.Headers.Add("Connection", "keep-alive");
        response.SendChunked = true;

        using var writeLock = new SemaphoreSlim(1, 1);

        async Task SendLineAsync(LogLine line)
        {
            try
            {
                await writeLock.WaitAsync(ct);
                var payload = JsonSerializer.Serialize(line, JsonOptions);
                await WriteSseAsync(response, "log", payload, ct);
            }
            catch
            {
            }
            finally
            {
                if (writeLock.CurrentCount == 0)
                {
                    writeLock.Release();
                }
            }
        }

        foreach (var line in LogRelayHub.GetSnapshot(200))
        {
            await SendLineAsync(line);
        }

        EventHandler<LogLine>? handler = (_, line) => _ = SendLineAsync(line);
        LogRelayHub.LineReceived += handler;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                try
                {
                    await writeLock.WaitAsync(ct);
                    await WriteSseAsync(response, "ping", "{}", ct);
                }
                catch
                {
                }
                finally
                {
                    if (writeLock.CurrentCount == 0)
                    {
                        writeLock.Release();
                    }
                }
            }
        }
        catch
        {
        }
        finally
        {
            if (handler != null)
            {
                LogRelayHub.LineReceived -= handler;
            }

            response.OutputStream.Close();
        }
    }

    private async Task HandleScreenAsync(HttpListenerResponse response, CancellationToken ct)
    {
        if (_config?.ScreenStreamEnabled != true)
        {
            await WriteSvgMessageAsync(response, "屏幕传输未开启", ct);
            return;
        }

        if (!TaskContext.Instance().IsInitialized)
        {
            await WriteSvgMessageAsync(response, "游戏未启动", ct);
            return;
        }

        var payload = CaptureScreenPng();
        if (payload == null || payload.Length == 0)
        {
            await WriteSvgMessageAsync(response, "截图器未就绪", ct);
            return;
        }

        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "image/png";
        response.ContentLength64 = payload.Length;
        await response.OutputStream.WriteAsync(payload, 0, payload.Length, ct);
        response.OutputStream.Close();
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

    private static bool IsClusterRequest(string path)
    {
        return path.StartsWith("/api/cluster", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyClusterCorsHeaders(HttpListenerRequest request, HttpListenerResponse response)
    {
        var origin = request.Headers["Origin"];
        if (!string.IsNullOrWhiteSpace(origin))
        {
            response.Headers["Access-Control-Allow-Origin"] = origin;
            response.Headers["Vary"] = "Origin";
        }
        else
        {
            response.Headers["Access-Control-Allow-Origin"] = "*";
        }

        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization, X-BGI-Cluster-Token, X-BGI-Token";
        response.Headers["Access-Control-Expose-Headers"] = "Content-Type, Content-Length, Date";
        response.Headers["Access-Control-Max-Age"] = "86400";
    }

    private static bool AuthorizeCluster(HttpListenerRequest request, HttpListenerResponse response, WebRemoteConfig config)
    {
        if (!config.ClusterApiEnabled)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return false;
        }

        var token = ExtractClusterToken(request);
        if (string.IsNullOrWhiteSpace(config.ClusterApiToken) || string.IsNullOrWhiteSpace(token))
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return false;
        }

        if (!SecureEquals(token, config.ClusterApiToken))
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return false;
        }

        if (!IsIpAllowed(request.RemoteEndPoint?.Address, config.ClusterApiWhitelist))
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return false;
        }

        return true;
    }

    private static string? ExtractClusterToken(HttpListenerRequest request)
    {
        var token = request.Headers["X-BGI-Cluster-Token"];
        if (string.IsNullOrWhiteSpace(token))
        {
            token = request.Headers["X-BGI-Token"];
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            var auth = request.Headers["Authorization"];
            if (!string.IsNullOrWhiteSpace(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = auth.Substring("Bearer ".Length).Trim();
            }
        }

        return token;
    }

    private static bool SecureEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        if (leftBytes.Length != rightBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool IsIpAllowed(IPAddress? address, string? whitelist)
    {
        if (address == null)
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (string.IsNullOrWhiteSpace(whitelist))
        {
            return true;
        }

        var entries = whitelist.Split(new[] { ',', ';', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in entries)
        {
            var entry = raw.Trim();
            if (string.IsNullOrEmpty(entry))
            {
                continue;
            }

            if (entry == "*")
            {
                return true;
            }

            if (entry.Contains('/'))
            {
                if (TryParseCidr(entry, out var network, out var prefix) && IsInCidr(address, network, prefix))
                {
                    return true;
                }
                continue;
            }

            if (!IPAddress.TryParse(entry, out var parsed))
            {
                continue;
            }

            if (parsed.AddressFamily == AddressFamily.InterNetworkV6 && parsed.IsIPv4MappedToIPv6)
            {
                parsed = parsed.MapToIPv4();
            }

            if (parsed.Equals(address))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseCidr(string input, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 0;

        var parts = input.Split('/', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        if (!IPAddress.TryParse(parts[0], out network))
        {
            return false;
        }

        if (!int.TryParse(parts[1], out prefix))
        {
            return false;
        }

        if (network.AddressFamily == AddressFamily.InterNetworkV6 && network.IsIPv4MappedToIPv6)
        {
            network = network.MapToIPv4();
        }

        if (network.AddressFamily == AddressFamily.InterNetwork && (prefix < 0 || prefix > 32))
        {
            return false;
        }

        if (network.AddressFamily == AddressFamily.InterNetworkV6 && (prefix < 0 || prefix > 128))
        {
            return false;
        }

        return true;
    }

    private static bool IsInCidr(IPAddress address, IPAddress network, int prefix)
    {
        if (address.AddressFamily != network.AddressFamily)
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        var fullBytes = prefix / 8;
        var remainingBits = prefix % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (addressBytes[i] != networkBytes[i])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }

    private static bool HasWebAuthCredentials(WebRemoteConfig config)
    {
        return !string.IsNullOrWhiteSpace(config.Username) &&
               !string.IsNullOrWhiteSpace(config.Password);
    }

    private static bool IsAnonymousPath(string path)
    {
        return path.Equals("/login", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/api/ui/i18n", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/api/auth/me", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBrowserPageRequest(HttpListenerRequest request, string path)
    {
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var accept = request.Headers["Accept"];
        return !string.IsNullOrWhiteSpace(accept) &&
               accept.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string BuildLoginRedirectPath(HttpListenerRequest request)
    {
        var raw = request.RawUrl;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "/";
        }

        if (!raw.StartsWith("/", StringComparison.Ordinal))
        {
            return "/";
        }

        if (raw.StartsWith("/login", StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        return raw;
    }

    private static bool TryAuthorizeByBasic(HttpListenerRequest request, WebRemoteConfig config, out string username)
    {
        username = string.Empty;
        var authHeader = request.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var encoded = authHeader.Substring("Basic ".Length).Trim();
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var separatorIndex = decoded.IndexOf(':');
            if (separatorIndex < 0)
            {
                return false;
            }

            var candidateUsername = decoded.Substring(0, separatorIndex);
            var candidatePassword = decoded.Substring(separatorIndex + 1);
            if (string.IsNullOrWhiteSpace(candidateUsername) ||
                !SecureEquals(candidateUsername, config.Username) ||
                !SecureEquals(candidatePassword, config.Password))
            {
                return false;
            }

            username = candidateUsername;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryAuthorizeBySession(HttpListenerRequest request, out string username)
    {
        username = string.Empty;
        var token = request.Cookies[SessionCookieName]?.Value;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            SweepExpiredSessions(now);
            if (!_sessions.TryGetValue(token, out var ticket))
            {
                return false;
            }

            if (ticket.ExpiresAt <= now)
            {
                _sessions.Remove(token);
                return false;
            }

            var nextExpireAt = now.Add(ticket.IsPersistent ? SessionTtlRemember : SessionTtl);
            _sessions[token] = ticket with { ExpiresAt = nextExpireAt };
            username = ticket.Username;
            return true;
        }
    }

    private string CreateSession(string username, bool persistent)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(persistent ? SessionTtlRemember : SessionTtl);
        lock (_sync)
        {
            SweepExpiredSessions(now);
            _sessions[token] = new SessionTicket(username, expiresAt, persistent);
        }

        return token;
    }

    private void RemoveSession(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        lock (_sync)
        {
            _sessions.Remove(token);
        }
    }

    private void SweepExpiredSessions(DateTimeOffset now)
    {
        if (_sessions.Count == 0)
        {
            return;
        }

        var expired = new List<string>();
        foreach (var (token, ticket) in _sessions)
        {
            if (ticket.ExpiresAt <= now)
            {
                expired.Add(token);
            }
        }

        foreach (var token in expired)
        {
            _sessions.Remove(token);
        }
    }

    private static void SetSessionCookie(HttpListenerResponse response, string token, bool persistent)
    {
        var maxAge = persistent ? (int)SessionTtlRemember.TotalSeconds : (int)SessionTtl.TotalSeconds;
        response.Headers.Add("Set-Cookie", $"{SessionCookieName}={token}; Path=/; HttpOnly; SameSite=Lax; Max-Age={maxAge}");
    }

    private static void ClearSessionCookie(HttpListenerResponse response)
    {
        response.Headers.Add("Set-Cookie", $"{SessionCookieName}=; Path=/; HttpOnly; SameSite=Lax; Max-Age=0");
    }

    private bool Authorize(HttpListenerRequest request, HttpListenerResponse response, WebRemoteConfig config, string path)
    {
        if (!HasWebAuthCredentials(config))
        {
            response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            return false;
        }

        if (TryAuthorizeBySession(request, out _))
        {
            return true;
        }

        if (TryAuthorizeByBasic(request, config, out _))
        {
            return true;
        }

        if (IsBrowserPageRequest(request, path))
        {
            var redirect = BuildLoginRedirectPath(request);
            response.StatusCode = (int)HttpStatusCode.Redirect;
            response.RedirectLocation = "/login?redirect=" + Uri.EscapeDataString(redirect);
            return false;
        }

        response.StatusCode = (int)HttpStatusCode.Unauthorized;
        return false;
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await WriteStringAsync(response, json, "application/json; charset=utf-8", ct);
    }

    private static async Task WriteStringAsync(HttpListenerResponse response, string payload, string contentType, CancellationToken ct)
    {
        var buffer = Encoding.UTF8.GetBytes(payload);
        response.ContentType = contentType;
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length, ct);
        response.OutputStream.Close();
    }

    private static Task WriteSvgMessageAsync(HttpListenerResponse response, string message, CancellationToken ct)
    {
        var safe = System.Security.SecurityElement.Escape(message) ?? string.Empty;
        var svg = $"""
                  <svg xmlns="http://www.w3.org/2000/svg" width="960" height="540" viewBox="0 0 960 540">
                    <rect width="100%" height="100%" fill="#0c0f16" />
                    <text x="50%" y="50%" dominant-baseline="middle" text-anchor="middle" fill="#9aa2b1" font-family="Segoe UI, Microsoft YaHei, sans-serif" font-size="28">{safe}</text>
                  </svg>
                  """;
        return WriteStringAsync(response, svg, "image/svg+xml; charset=utf-8", ct);
    }

    private static Task WriteSseAsync(HttpListenerResponse response, string eventName, string payload, CancellationToken ct)
    {
        var data = $"event: {eventName}\ndata: {payload}\n\n";
        var buffer = Encoding.UTF8.GetBytes(data);
        return response.OutputStream.WriteAsync(buffer, 0, buffer.Length, ct);
    }

    private WebRemoteStatus BuildStatus()
    {
        var warning = _lastWarning;
        if (_config != null &&
            (string.IsNullOrWhiteSpace(_config.Username) || string.IsNullOrWhiteSpace(_config.Password)))
        {
            warning = string.IsNullOrWhiteSpace(warning)
                ? "未配置鉴权账号或密码，Web 远程控制无法启动。"
                : $"{warning}；未配置鉴权账号或密码，Web 远程控制无法启动。";
        }

        return new WebRemoteStatus
        {
            Version = Global.Version,
            ServerTime = DateTimeOffset.Now,
            IsInitialized = TaskContext.Instance().IsInitialized,
            IsSuspended = RunnerContext.Instance.IsSuspend,
            ScreenStreamEnabled = TaskContext.Instance().Config.WebRemoteConfig.ScreenStreamEnabled,
            LogStreamEnabled = TaskContext.Instance().Config.WebRemoteConfig.LogStreamEnabled,
            LanEnabled = _config?.LanEnabled ?? false,
            LanActive = _currentLanActive,
            ThirdPartyEnabled = _config?.LanEnabled ?? false,
            ThirdPartyActive = _currentLanActive,
            ListenPrefix = _currentPrefix,
            Warning = warning
        };
    }

    private static string[] GetLocalIpAddresses()
    {
        try
        {
            var host = Dns.GetHostName();
            return Dns.GetHostAddresses(host)
                .Where(ip =>
                    ip.AddressFamily == AddressFamily.InterNetwork ||
                    (ip.AddressFamily == AddressFamily.InterNetworkV6 &&
                     !ip.IsIPv6LinkLocal &&
                     !ip.IsIPv6Multicast &&
                     !ip.IsIPv6SiteLocal))
                .Select(ip => ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString())
                .Distinct()
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private BasicFeatureState BuildBasicFeatureState()
    {
        var config = _configService.Get();
        return new BasicFeatureState
        {
            AutoPick = config.AutoPickConfig.Enabled,
            AutoSkip = config.AutoSkipConfig.Enabled,
            AutoFishing = config.AutoFishingConfig.Enabled,
            AutoCook = config.AutoCookConfig.Enabled,
            AutoEat = config.AutoEatConfig.Enabled,
            QuickTeleport = config.QuickTeleportConfig.Enabled,
            MapMask = config.MapMaskConfig.Enabled
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
        });
    }

    private object BuildAutoGeniusInvokationSettings()
    {
        var config = _configService.Get().AutoGeniusInvokationConfig;
        return new
        {
            strategyName = config.StrategyName,
            sleepDelay = config.SleepDelay
        };
    }

    private object BuildAutoWoodSettings()
    {
        var config = _configService.Get().AutoWoodConfig;
        return new
        {
            afterZSleepDelay = config.AfterZSleepDelay,
            woodCountOcrEnabled = config.WoodCountOcrEnabled,
            useWonderlandRefresh = config.UseWonderlandRefresh
        };
    }

    private object BuildAutoFightSettings()
    {
        var config = _configService.Get().AutoFightConfig;
        var finishDetect = config.FinishDetectConfig ?? new AutoFightConfig.FightFinishDetectConfig();
        config.FinishDetectConfig = finishDetect;
        return new
        {
            strategyName = config.StrategyName,
            teamNames = config.TeamNames,
            fightFinishDetectEnabled = config.FightFinishDetectEnabled,
            actionSchedulerByCd = config.ActionSchedulerByCd,
            onlyPickEliteDropsMode = config.OnlyPickEliteDropsMode,
            pickDropsAfterFightEnabled = config.PickDropsAfterFightEnabled,
            pickDropsAfterFightSeconds = config.PickDropsAfterFightSeconds,
            battleThresholdForLoot = config.BattleThresholdForLoot,
            kazuhaPickupEnabled = config.KazuhaPickupEnabled,
            qinDoublePickUp = config.QinDoublePickUp,
            guardianAvatar = config.GuardianAvatar,
            guardianCombatSkip = config.GuardianCombatSkip,
            skipModel = config.SkipModel,
            guardianAvatarHold = config.GuardianAvatarHold,
            burstEnabled = config.BurstEnabled,
            kazuhaPartyName = config.KazuhaPartyName,
            swimmingEnabled = config.SwimmingEnabled,
            timeout = config.Timeout,
            battleEndProgressBarColor = finishDetect.BattleEndProgressBarColor,
            battleEndProgressBarColorTolerance = finishDetect.BattleEndProgressBarColorTolerance,
            fastCheckEnabled = finishDetect.FastCheckEnabled,
            rotateFindEnemyEnabled = finishDetect.RotateFindEnemyEnabled,
            fastCheckParams = finishDetect.FastCheckParams,
            checkEndDelay = finishDetect.CheckEndDelay,
            beforeDetectDelay = finishDetect.BeforeDetectDelay,
            rotaryFactor = finishDetect.RotaryFactor,
            isFirstCheck = finishDetect.IsFirstCheck,
            checkBeforeBurst = finishDetect.CheckBeforeBurst
        };
    }

    private object BuildAutoDomainSettings()
    {
        var config = _configService.Get().AutoDomainConfig;
        return new
        {
            fightEndDelay = config.FightEndDelay,
            shortMovement = config.ShortMovement,
            walkToF = config.WalkToF,
            leftRightMoveTimes = config.LeftRightMoveTimes,
            autoEat = config.AutoEat,
            partyName = config.PartyName,
            domainName = config.DomainName,
            sundaySelectedValue = config.SundaySelectedValue,
            autoArtifactSalvage = config.AutoArtifactSalvage,
            specifyResinUse = config.SpecifyResinUse,
            resinPriorityList = config.ResinPriorityList,
            originalResinUseCount = config.OriginalResinUseCount,
            originalResin20UseCount = config.OriginalResin20UseCount,
            originalResin40UseCount = config.OriginalResin40UseCount,
            condensedResinUseCount = config.CondensedResinUseCount,
            transientResinUseCount = config.TransientResinUseCount,
            fragileResinUseCount = config.FragileResinUseCount,
            reviveRetryCount = config.ReviveRetryCount,
            strategyName = _configService.Get().AutoFightConfig.StrategyName
        };
    }

    private object BuildAutoStygianSettings()
    {
        var config = _configService.Get().AutoStygianOnslaughtConfig;
        return new
        {
            strategyName = config.StrategyName,
            bossNum = config.BossNum,
            fightTeamName = config.FightTeamName,
            autoArtifactSalvage = config.AutoArtifactSalvage,
            specifyResinUse = config.SpecifyResinUse,
            resinPriorityList = config.ResinPriorityList,
            originalResinUseCount = config.OriginalResinUseCount,
            condensedResinUseCount = config.CondensedResinUseCount,
            transientResinUseCount = config.TransientResinUseCount,
            fragileResinUseCount = config.FragileResinUseCount
        };
    }

    private object BuildAutoFishingSettings()
    {
        var config = _configService.Get().AutoFishingConfig;
        return new
        {
            enabled = config.Enabled,
            autoThrowRodEnabled = config.AutoThrowRodEnabled,
            autoThrowRodTimeOut = config.AutoThrowRodTimeOut,
            wholeProcessTimeoutSeconds = config.WholeProcessTimeoutSeconds,
            fishingTimePolicy = config.FishingTimePolicy.ToString(),
            torchDllFullPath = config.TorchDllFullPath
        };
    }

    private object BuildAutoMusicSettings()
    {
        var config = _configService.Get().AutoMusicGameConfig;
        return new
        {
            mustCanorusLevel = config.MustCanorusLevel,
            musicLevel = config.MusicLevel
        };
    }

    private object BuildAutoArtifactSettings()
    {
        var config = _configService.Get().AutoArtifactSalvageConfig;
        return new
        {
            artifactSetFilter = config.ArtifactSetFilter,
            maxArtifactStar = config.MaxArtifactStar,
            maxNumToCheck = config.MaxNumToCheck,
            recognitionFailurePolicy = config.RecognitionFailurePolicy.ToString(),
            javaScript = config.JavaScript
        };
    }

    private object BuildGridIconsSettings()
    {
        var config = _configService.Get().GetGridIconsConfig;
        return new
        {
            gridName = config.GridName.ToString(),
            starAsSuffix = config.StarAsSuffix,
            lvAsSuffix = config.LvAsSuffix,
            maxNumToGet = config.MaxNumToGet
        };
    }

    private object BuildAutoLeyLineSettings()
    {
        var config = _configService.Get().AutoLeyLineOutcropConfig;
        return new
        {
            leyLineOutcropType = config.LeyLineOutcropType,
            country = config.Country,
            isResinExhaustionMode = config.IsResinExhaustionMode,
            openModeCountMin = config.OpenModeCountMin,
            count = config.Count,
            useTransientResin = config.UseTransientResin,
            useFragileResin = config.UseFragileResin,
            team = config.Team,
            friendshipTeam = config.FriendshipTeam,
            timeout = config.Timeout,
            useAdventurerHandbook = config.UseAdventurerHandbook,
            isNotification = config.IsNotification,
            isGoToSynthesizer = config.IsGoToSynthesizer,
            scanDropsAfterRewardEnabled = config.ScanDropsAfterRewardEnabled,
            scanDropsAfterRewardSeconds = config.ScanDropsAfterRewardSeconds
        };
    }

    private object BuildNotificationSettings()
    {
        var config = _configService.Get().NotificationConfig;
        return new
        {
            jsNotificationEnabled = config.JsNotificationEnabled,
            includeScreenShot = config.IncludeScreenShot,
            notificationEventSubscribe = config.NotificationEventSubscribe,
            webhookEnabled = config.WebhookEnabled,
            webhookEndpoint = config.WebhookEndpoint,
            webhookSendTo = config.WebhookSendTo,
            webSocketNotificationEnabled = config.WebSocketNotificationEnabled,
            webSocketEndpoint = config.WebSocketEndpoint,
            windowsUwpNotificationEnabled = config.WindowsUwpNotificationEnabled,
            feishuNotificationEnabled = config.FeishuNotificationEnabled,
            feishuWebhookUrl = config.FeishuWebhookUrl,
            feishuAppId = config.FeishuAppId,
            feishuAppSecret = config.FeishuAppSecret,
            oneBotNotificationEnabled = config.OneBotNotificationEnabled,
            oneBotEndpoint = config.OneBotEndpoint,
            oneBotUserId = config.OneBotUserId,
            oneBotGroupId = config.OneBotGroupId,
            oneBotToken = config.OneBotToken,
            workweixinNotificationEnabled = config.WorkweixinNotificationEnabled,
            workweixinWebhookUrl = config.WorkweixinWebhookUrl,
            emailNotificationEnabled = config.EmailNotificationEnabled,
            smtpServer = config.SmtpServer,
            smtpPort = config.SmtpPort,
            smtpUsername = config.SmtpUsername,
            smtpPassword = config.SmtpPassword,
            fromEmail = config.FromEmail,
            fromName = config.FromName,
            toEmail = config.ToEmail,
            barkNotificationEnabled = config.BarkNotificationEnabled,
            barkAction = config.BarkAction,
            barkApiEndpoint = config.BarkApiEndpoint,
            barkAutoCopy = config.BarkAutoCopy,
            barkBadge = config.BarkBadge,
            barkCall = config.BarkCall,
            barkCiphertext = config.BarkCiphertext,
            barkCopy = config.BarkCopy,
            barkDeviceKeys = config.BarkDeviceKeys,
            barkGroup = config.BarkGroup,
            barkIcon = config.BarkIcon,
            barkIsArchive = config.BarkIsArchive,
            barkLevel = config.BarkLevel,
            barkSound = config.BarkSound,
            barkSubtitle = config.BarkSubtitle,
            barkUrl = config.BarkUrl,
            barkVolume = config.BarkVolume,
            telegramNotificationEnabled = config.TelegramNotificationEnabled,
            telegramApiBaseUrl = config.TelegramApiBaseUrl,
            telegramProxyUrl = config.TelegramProxyUrl,
            telegramProxyEnabled = config.TelegramProxyEnabled,
            telegramBotToken = config.TelegramBotToken,
            telegramChatId = config.TelegramChatId,
            xxtuiNotificationEnabled = config.XxtuiNotificationEnabled,
            xxtuiApiKey = config.XxtuiApiKey,
            xxtuiFrom = config.XxtuiFrom,
            xxtuiChannels = config.XxtuiChannels,
            dingDingwebhookNotificationEnabled = config.DingDingwebhookNotificationEnabled,
            dingdingWebhookUrl = config.DingdingWebhookUrl,
            dingDingSecret = config.DingDingSecret,
            discordWebhookNotificationEnabled = config.DiscordWebhookNotificationEnabled,
            discordWebhookUrl = config.DiscordWebhookUrl,
            discordWebhookUsername = config.DiscordWebhookUsername,
            discordWebhookAvatarUrl = config.DiscordWebhookAvatarUrl,
            discordWebhookImageEncoder = config.DiscordWebhookImageEncoder,
            serverChanNotificationEnabled = config.ServerChanNotificationEnabled,
            serverChanSendKey = config.ServerChanSendKey
        };
    }

    private async Task<object> RunTaskAsync(string task, JsonElement argsElement, CancellationToken ct)
    {
        try
        {
            switch (task)
            {
                case "auto_gi":
                    return await RunAutoGeniusInvokationAsync(argsElement, ct);
                case "auto_wood":
                    return await RunAutoWoodAsync(argsElement, ct);
                case "auto_fight":
                    return await RunAutoFightAsync(argsElement, ct);
                case "auto_domain":
                    return await RunAutoDomainAsync(argsElement, ct);
                case "auto_stygian":
                    return await RunAutoStygianAsync(argsElement, ct);
                case "auto_music":
                    StartBackgroundTask(() => new TaskRunner().RunSoloTaskAsync(new AutoMusicGameTask(new AutoMusicGameParam())));
                    return new { ok = true };
                case "auto_album":
                    StartBackgroundTask(() => new TaskRunner().RunSoloTaskAsync(new AutoAlbumTask(new AutoMusicGameParam())));
                    return new { ok = true };
                case "auto_fishing":
                    return await RunAutoFishingAsync(argsElement, ct);
                case "auto_leyline":
                    return await RunAutoLeyLineAsync(argsElement, ct);
                case "auto_artifact_salvage":
                    StartBackgroundTask(() => new TaskRunner().RunSoloTaskAsync(new AutoArtifactSalvageTask(new AutoArtifactSalvageTaskParam(
                        int.Parse(TaskContext.Instance().Config.AutoArtifactSalvageConfig.MaxArtifactStar),
                        TaskContext.Instance().Config.AutoArtifactSalvageConfig.JavaScript,
                        TaskContext.Instance().Config.AutoArtifactSalvageConfig.ArtifactSetFilter,
                        TaskContext.Instance().Config.AutoArtifactSalvageConfig.MaxNumToCheck,
                        TaskContext.Instance().Config.AutoArtifactSalvageConfig.RecognitionFailurePolicy
                    ))));
                    return new { ok = true };
                case "get_grid_icons":
                    StartBackgroundTask(() => new TaskRunner().RunSoloTaskAsync(new GetGridIconsTask(
                        TaskContext.Instance().Config.GetGridIconsConfig.GridName,
                        TaskContext.Instance().Config.GetGridIconsConfig.StarAsSuffix,
                        TaskContext.Instance().Config.GetGridIconsConfig.MaxNumToGet
                    )));
                    return new { ok = true };
                case "grid_icons_accuracy":
                    StartBackgroundTask(() => new TaskRunner().RunSoloTaskAsync(new GridIconsAccuracyTestTask(
                        TaskContext.Instance().Config.GetGridIconsConfig.GridName,
                        TaskContext.Instance().Config.GetGridIconsConfig.MaxNumToGet
                    )));
                    return new { ok = true };
                case "auto_redeem_code":
                    return await RunAutoRedeemCodeAsync(argsElement, ct);
                default:
                    return new { ok = false, error = "未知任务" };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "启动任务失败");
            return new { ok = false, error = ex.Message };
        }
    }

    private async Task<object> RunAutoGeniusInvokationAsync(JsonElement argsElement, CancellationToken ct)
    {
        var config = _configService.Get().AutoGeniusInvokationConfig;
        if (TryGetString(argsElement, "strategyName", out var strategyName))
        {
            config.StrategyName = strategyName;
        }

        if (TryGetInt(argsElement, "sleepDelay", out var delay))
        {
            config.SleepDelay = delay;
        }

        if (!TryGetTcgStrategyPath(config.StrategyName, out var path, out var error))
        {
            return new { ok = false, error };
        }

        var content = await File.ReadAllTextAsync(path, ct);
        StartBackgroundTask(() => new TaskRunner().RunSoloTaskAsync(new AutoGeniusInvokationTask(new GeniusInvokationTaskParam(content))));
        return new { ok = true };
    }

    private Task<object> RunAutoWoodAsync(JsonElement argsElement, CancellationToken ct)
    {
        var roundNum = TryGetInt(argsElement, "roundNum", out var parsedRound) ? parsedRound : 1;
        var dailyMax = TryGetInt(argsElement, "dailyMaxCount", out var parsedMax) ? parsedMax : 2000;
        if (TryGetBool(argsElement, "useWonderlandRefresh", out var useWonderland))
        {
            _configService.Get().AutoWoodConfig.UseWonderlandRefresh = useWonderland;
        }

        StartBackgroundTask(() => new TaskRunner().RunSoloTaskAsync(new AutoWoodTask(new WoodTaskParam(roundNum, dailyMax))));
        return Task.FromResult<object>(new { ok = true });
    }

    private Task<object> RunAutoFightAsync(JsonElement argsElement, CancellationToken ct)
    {
        var payload = TryDeserializeParams<AutoFightSettingsRequest>(argsElement);
        if (payload != null)
        {
            ApplyAutoFightSettings(payload);
        }

        var config = _configService.Get().AutoFightConfig;
        if (!TryGetFightStrategyPath(config.StrategyName, out var path, out var error))
        {
            return Task.FromResult<object>(new { ok = false, error });
        }

        StartBackgroundTask(() => new TaskRunner().RunSoloTaskAsync(new AutoFightTask(new AutoFightParam(path, config))));
        return Task.FromResult<object>(new { ok = true });
    }

    private Task<object> RunAutoDomainAsync(JsonElement argsElement, CancellationToken ct)
    {
        var payload = TryDeserializeParams<AutoDomainSettingsRequest>(argsElement);
        if (payload != null)
        {
            ApplyAutoDomainSettings(payload);
        }

        var roundNum = TryGetInt(argsElement, "roundNum", out var parsedRound) ? parsedRound : 1;
        if (!TryGetFightStrategyPath(_configService.Get().AutoFightConfig.StrategyName, out var path, out var error))
        {
            return Task.FromResult<object>(new { ok = false, error });
        }

        StartBackgroundTask(() => new TaskRunner().RunSoloTaskAsync(new AutoDomainTask(new AutoDomainParam(roundNum, path))));
        return Task.FromResult<object>(new { ok = true });
    }

    private Task<object> RunAutoStygianAsync(JsonElement argsElement, CancellationToken ct)
    {
        var config = _configService.Get().AutoStygianOnslaughtConfig;
        if (TryGetString(argsElement, "strategyName", out var strategyName))
        {
            config.StrategyName = strategyName;
        }

        if (TryGetInt(argsElement, "bossNum", out var bossNum))
        {
            config.BossNum = bossNum;
        }

        if (TryGetString(argsElement, "fightTeamName", out var fightTeamName))
        {
            config.FightTeamName = fightTeamName;
        }

        if (!TryGetFightStrategyPath(config.StrategyName, out var path, out var error))
        {
            return Task.FromResult<object>(new { ok = false, error });
        }

        StartBackgroundTask(() => new TaskRunner().RunSoloTaskAsync(new AutoStygianOnslaughtTask(config, path)));
        return Task.FromResult<object>(new { ok = true });
    }

    private Task<object> RunAutoFishingAsync(JsonElement argsElement, CancellationToken ct)
    {
        var saveScreenshot = TryGetBool(argsElement, "saveScreenshotOnKeyTick", out var flag) && flag;
        var param = AutoFishingTaskParam.BuildFromConfig(TaskContext.Instance().Config.AutoFishingConfig, saveScreenshot);
        StartBackgroundTask(() => new TaskRunner().RunSoloTaskAsync(new AutoFishingTask(param)));
        return Task.FromResult<object>(new { ok = true });
    }

    private Task<object> RunAutoLeyLineAsync(JsonElement argsElement, CancellationToken ct)
    {
        var config = _configService.Get().AutoLeyLineOutcropConfig;
        if (TryGetString(argsElement, "leyLineOutcropType", out var type))
        {
            config.LeyLineOutcropType = type;
        }

        if (TryGetString(argsElement, "country", out var country))
        {
            config.Country = country;
        }

        if (TryGetBool(argsElement, "isResinExhaustionMode", out var resinExhaustionMode))
        {
            config.IsResinExhaustionMode = resinExhaustionMode;
        }

        if (TryGetBool(argsElement, "openModeCountMin", out var countMin))
        {
            config.OpenModeCountMin = countMin;
        }

        if (TryGetInt(argsElement, "count", out var count))
        {
            config.Count = Math.Max(1, count);
        }

        if (TryGetBool(argsElement, "useTransientResin", out var transientResin))
        {
            config.UseTransientResin = transientResin;
        }

        if (TryGetBool(argsElement, "useFragileResin", out var fragileResin))
        {
            config.UseFragileResin = fragileResin;
        }

        if (TryGetString(argsElement, "team", out var team))
        {
            config.Team = team;
        }

        if (TryGetString(argsElement, "friendshipTeam", out var friendshipTeam))
        {
            config.FriendshipTeam = friendshipTeam;
        }

        if (TryGetInt(argsElement, "timeout", out var timeout))
        {
            config.Timeout = Math.Max(1, timeout);
        }

        if (TryGetBool(argsElement, "useAdventurerHandbook", out var useAdventurerHandbook))
        {
            config.UseAdventurerHandbook = useAdventurerHandbook;
        }

        if (TryGetBool(argsElement, "isNotification", out var isNotification))
        {
            config.IsNotification = isNotification;
        }

        if (TryGetBool(argsElement, "isGoToSynthesizer", out var isGoToSynthesizer))
        {
            config.IsGoToSynthesizer = isGoToSynthesizer;
        }

        if (TryGetBool(argsElement, "scanDropsAfterRewardEnabled", out var scanDropsAfterRewardEnabled))
        {
            config.ScanDropsAfterRewardEnabled = scanDropsAfterRewardEnabled;
        }

        if (TryGetInt(argsElement, "scanDropsAfterRewardSeconds", out var scanDropsAfterRewardSeconds))
        {
            config.ScanDropsAfterRewardSeconds = Math.Clamp(scanDropsAfterRewardSeconds, 0, 60);
        }

        StartBackgroundTask(() =>
            new TaskRunner().RunSoloTaskAsync(new AutoLeyLineOutcropTask(new AutoLeyLineOutcropParam())));
        return Task.FromResult<object>(new { ok = true });
    }

    private Task<object> RunAutoRedeemCodeAsync(JsonElement argsElement, CancellationToken ct)
    {
        if (argsElement.ValueKind != JsonValueKind.Object || !argsElement.TryGetProperty("codes", out var codesElement) || codesElement.ValueKind != JsonValueKind.Array)
        {
            return Task.FromResult<object>(new { ok = false, error = "缺少兑换码" });
        }

        var codes = new List<string>();
        foreach (var item in codesElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var code = item.GetString();
                if (!string.IsNullOrWhiteSpace(code))
                {
                    codes.Add(code.Trim());
                }
            }
        }

        if (codes.Count == 0)
        {
            return Task.FromResult<object>(new { ok = false, error = "兑换码为空" });
        }

        StartBackgroundTask(() => new TaskRunner().RunSoloTaskAsync(new UseRedemptionCodeTask(codes)));
        return Task.FromResult<object>(new { ok = true });
    }

    private void StartBackgroundTask(Func<Task> action)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "启动任务失败");
            }
        });
    }

    private static T? TryDeserializeParams<T>(JsonElement element) where T : class
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = prop.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return prop.TryGetInt32(out value);
    }

    private static bool TryGetBool(JsonElement element, string name, out bool value)
    {
        value = false;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.True && prop.ValueKind != JsonValueKind.False)
        {
            return false;
        }

        value = prop.GetBoolean();
        return true;
    }

    private static bool TryGetFightStrategyPath(string strategyName, out string path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(strategyName))
        {
            error = "请先设置战斗策略";
            path = string.Empty;
            return false;
        }

        var folder = Global.Absolute(@"User\AutoFight\");
        if ("根据队伍自动选择".Equals(strategyName, StringComparison.OrdinalIgnoreCase))
        {
            path = folder;
        }
        else
        {
            if (!TryGetSafeTxtPathUnderFolder(folder, strategyName, out path))
            {
                error = "战斗策略名称非法";
                return false;
            }
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            error = "战斗策略文件不存在";
            return false;
        }

        return true;
    }

    private static bool TryGetTcgStrategyPath(string strategyName, out string path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(strategyName))
        {
            error = "请先设置策略名称";
            path = string.Empty;
            return false;
        }

        var folder = Global.Absolute(@"User\AutoGeniusInvokation");
        if (!TryGetSafeTxtPathUnderFolder(folder, strategyName, out path))
        {
            error = "策略名称非法";
            return false;
        }

        if (!File.Exists(path))
        {
            error = "策略文件不存在";
            return false;
        }

        return true;
    }

    private static bool TryGetSafeTxtPathUnderFolder(string folder, string name, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalizedName = name.Trim();
        if (normalizedName is "." or ".." ||
            normalizedName.Contains('/') ||
            normalizedName.Contains('\\') ||
            normalizedName.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(normalizedName) ||
            normalizedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        var root = Path.GetFullPath(folder);
        var candidate = Path.GetFullPath(Path.Combine(root, normalizedName + ".txt"));
        if (!IsSubPathOf(root, candidate))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private static string[] LoadAutoFightStrategies()
    {
        var folder = Global.Absolute(@"User\AutoFight");
        Directory.CreateDirectory(folder);
        var list = Directory.GetFiles(folder, "*.txt", SearchOption.AllDirectories)
            .Select(file => file.Replace(folder, string.Empty).Replace(".txt", string.Empty).TrimStart('\\'))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
        return ["根据队伍自动选择", .. list];
    }

    private static string[] LoadTcgStrategies()
    {
        var folder = Global.Absolute(@"User\AutoGeniusInvokation");
        Directory.CreateDirectory(folder);
        return Directory.GetFiles(folder, "*.txt", SearchOption.AllDirectories)
            .Select(file => file.Replace(folder, string.Empty).Replace(".txt", string.Empty).TrimStart('\\'))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
    }

    private static List<OneDragonFlowConfig> LoadOneDragonConfigs()
    {
        return OneDragonConfigStore.LoadAll().ToList();
    }

    private static OneDragonFlowConfig? LoadOneDragonConfigByName(string? name)
    {
        return OneDragonConfigStore.LoadByName(name);
    }

    private static bool SaveOneDragonConfig(OneDragonFlowConfig config)
    {
        return OneDragonConfigStore.Save(config);
    }

    private Task RunGameStartAsync()
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

    private static async Task<string> ReadRawAsync(HttpListenerRequest request, CancellationToken ct)
    {
        var encoding = request.ContentEncoding ?? Encoding.UTF8;
        using var reader = new StreamReader(request.InputStream, encoding);
        return await reader.ReadToEndAsync(ct);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest request, CancellationToken ct)
    {
        var json = await ReadRawAsync(request, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch
        {
            return default;
        }
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

    private sealed class WebRemoteStatus
    {
        public string Version { get; set; } = string.Empty;
        public DateTimeOffset ServerTime { get; set; }
        public bool IsInitialized { get; set; }
        public bool IsSuspended { get; set; }
        public bool ScreenStreamEnabled { get; set; }
        public bool LogStreamEnabled { get; set; }
        // Keep Lan* for backward compatibility with legacy web pages.
        public bool LanEnabled { get; set; }
        public bool LanActive { get; set; }
        public bool ThirdPartyEnabled { get; set; }
        public bool ThirdPartyActive { get; set; }
        public string ListenPrefix { get; set; } = string.Empty;
        public string? Warning { get; set; }
    }

    private sealed record CachedUiSchema(DateTimeOffset BuiltAt, object Payload);

    private sealed class BasicFeatureState
    {
        public bool AutoPick { get; set; }
        public bool AutoSkip { get; set; }
        public bool AutoFishing { get; set; }
        public bool AutoCook { get; set; }
        public bool AutoEat { get; set; }
        public bool QuickTeleport { get; set; }
        public bool MapMask { get; set; }
    }

    private sealed class BasicFeaturePatch
    {
        public bool? AutoPick { get; set; }
        public bool? AutoSkip { get; set; }
        public bool? AutoFishing { get; set; }
        public bool? AutoCook { get; set; }
        public bool? AutoEat { get; set; }
        public bool? QuickTeleport { get; set; }
        public bool? MapMask { get; set; }
    }

    private sealed class ConfigSetRequest
    {
        public string Path { get; set; } = string.Empty;
        public JsonElement Value { get; set; }
    }

    private sealed class WebAuthLoginRequest
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
        public bool Remember { get; set; }
    }

    private readonly record struct SessionTicket(string Username, DateTimeOffset ExpiresAt, bool IsPersistent);

    private sealed class ScriptRunRequest
    {
        public string? Name { get; set; }
        public string[]? Names { get; set; }
    }

    private sealed class ScriptGroupDetail
    {
        public string Name { get; set; } = string.Empty;
        public List<ScriptGroupProjectInfo> Projects { get; set; } = new();
    }

    private sealed class ScriptGroupProjectInfo
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public string FolderName { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Schedule { get; set; } = string.Empty;
        public int RunNum { get; set; }
        public bool NextFlag { get; set; }
    }

    private sealed class ScriptGroupNameRequest
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ScriptGroupRenameRequest
    {
        public string OldName { get; set; } = string.Empty;
        public string NewName { get; set; } = string.Empty;
    }

    private sealed class ScriptGroupSaveRequest
    {
        public string Json { get; set; } = string.Empty;
    }

    private sealed class ScriptGroupAddItemsRequest
    {
        public string Name { get; set; } = string.Empty;
        public string[]? JsFolders { get; set; }
        public string[]? KeyMouseNames { get; set; }
        public PathingLibraryItem[]? Pathing { get; set; }
        public string[]? ShellCommands { get; set; }
    }

    private sealed class ScriptGroupItemRequest
    {
        public string Name { get; set; } = string.Empty;
        public int Index { get; set; }
        public string? Status { get; set; }
        public string? Schedule { get; set; }
        public int? RunNum { get; set; }
    }

    private sealed class ScriptGroupBatchUpdateRequest
    {
        public string Name { get; set; } = string.Empty;
        public int[]? Indices { get; set; }
        public string? Status { get; set; }
        public string? Schedule { get; set; }
        public int? RunNum { get; set; }
    }

    private sealed class ScriptGroupReorderRequest
    {
        public string Name { get; set; } = string.Empty;
        public int FromIndex { get; set; }
        public int ToIndex { get; set; }
    }

    private sealed class ScriptGroupCopyRequest
    {
        public string Name { get; set; } = string.Empty;
        public string? NewName { get; set; }
    }

    private sealed class ScriptGroupSetNextRequest
    {
        public string Name { get; set; } = string.Empty;
        public int? Index { get; set; }
        public bool? Clear { get; set; }
    }

    private sealed class ScriptGroupSetNextGroupRequest
    {
        public string? Name { get; set; }
        public bool? Clear { get; set; }
    }

    private sealed class ScriptLibraryItem
    {
        public string Name { get; set; } = string.Empty;
        public string? Folder { get; set; }
        public string? Description { get; set; }
    }

    private sealed class PathingLibraryItem
    {
        public string Name { get; set; } = string.Empty;
        public string? Folder { get; set; }
    }

    private sealed class TaskRunRequest
    {
        public string Task { get; set; } = string.Empty;
        public JsonElement Params { get; set; }
    }

    private sealed class AutoGeniusInvokationSettingsRequest
    {
        public string? StrategyName { get; set; }
        public int? SleepDelay { get; set; }
    }

    private sealed class AutoWoodSettingsRequest
    {
        public int? AfterZSleepDelay { get; set; }
        public bool? WoodCountOcrEnabled { get; set; }
        public bool? UseWonderlandRefresh { get; set; }
    }

    private sealed class AutoFightSettingsRequest
    {
        public string? StrategyName { get; set; }
        public string? TeamNames { get; set; }
        public bool? FightFinishDetectEnabled { get; set; }
        public string? ActionSchedulerByCd { get; set; }
        public string? OnlyPickEliteDropsMode { get; set; }
        public bool? PickDropsAfterFightEnabled { get; set; }
        public int? PickDropsAfterFightSeconds { get; set; }
        public int? BattleThresholdForLoot { get; set; }
        public bool? KazuhaPickupEnabled { get; set; }
        public bool? QinDoublePickUp { get; set; }
        public string? GuardianAvatar { get; set; }
        public bool? GuardianCombatSkip { get; set; }
        public bool? SkipModel { get; set; }
        public bool? GuardianAvatarHold { get; set; }
        public bool? BurstEnabled { get; set; }
        public string? KazuhaPartyName { get; set; }
        public bool? SwimmingEnabled { get; set; }
        public int? Timeout { get; set; }
        public string? BattleEndProgressBarColor { get; set; }
        public string? BattleEndProgressBarColorTolerance { get; set; }
        public bool? FastCheckEnabled { get; set; }
        public bool? RotateFindEnemyEnabled { get; set; }
        public string? FastCheckParams { get; set; }
        public string? CheckEndDelay { get; set; }
        public string? BeforeDetectDelay { get; set; }
        public int? RotaryFactor { get; set; }
        public bool? IsFirstCheck { get; set; }
        public bool? CheckBeforeBurst { get; set; }
    }

    private sealed class AutoDomainSettingsRequest
    {
        public string? StrategyName { get; set; }
        public string? PartyName { get; set; }
        public string? DomainName { get; set; }
        public string? SundaySelectedValue { get; set; }
        public double? FightEndDelay { get; set; }
        public bool? ShortMovement { get; set; }
        public bool? WalkToF { get; set; }
        public int? LeftRightMoveTimes { get; set; }
        public bool? AutoEat { get; set; }
        public bool? AutoArtifactSalvage { get; set; }
        public bool? SpecifyResinUse { get; set; }
        public List<string>? ResinPriorityList { get; set; }
        public int? OriginalResinUseCount { get; set; }
        public int? OriginalResin20UseCount { get; set; }
        public int? OriginalResin40UseCount { get; set; }
        public int? CondensedResinUseCount { get; set; }
        public int? TransientResinUseCount { get; set; }
        public int? FragileResinUseCount { get; set; }
        public int? ReviveRetryCount { get; set; }
    }

    private sealed class AutoStygianSettingsRequest
    {
        public string? StrategyName { get; set; }
        public int? BossNum { get; set; }
        public string? FightTeamName { get; set; }
        public bool? AutoArtifactSalvage { get; set; }
        public bool? SpecifyResinUse { get; set; }
        public List<string>? ResinPriorityList { get; set; }
        public int? OriginalResinUseCount { get; set; }
        public int? CondensedResinUseCount { get; set; }
        public int? TransientResinUseCount { get; set; }
        public int? FragileResinUseCount { get; set; }
    }

    private sealed class AutoFishingSettingsRequest
    {
        public bool? Enabled { get; set; }
        public bool? AutoThrowRodEnabled { get; set; }
        public int? AutoThrowRodTimeOut { get; set; }
        public int? WholeProcessTimeoutSeconds { get; set; }
        public string? FishingTimePolicy { get; set; }
        public string? TorchDllFullPath { get; set; }
    }

    private sealed class AutoMusicSettingsRequest
    {
        public bool? MustCanorusLevel { get; set; }
        public string? MusicLevel { get; set; }
    }

    private sealed class AutoArtifactSettingsRequest
    {
        public string? ArtifactSetFilter { get; set; }
        public string? MaxArtifactStar { get; set; }
        public int? MaxNumToCheck { get; set; }
        public string? RecognitionFailurePolicy { get; set; }
        public string? JavaScript { get; set; }
    }

    private sealed class GridIconsSettingsRequest
    {
        public string? GridName { get; set; }
        public bool? StarAsSuffix { get; set; }
        public bool? LvAsSuffix { get; set; }
        public int? MaxNumToGet { get; set; }
    }

    private sealed class AutoLeyLineSettingsRequest
    {
        public string? LeyLineOutcropType { get; set; }
        public string? Country { get; set; }
        public bool? IsResinExhaustionMode { get; set; }
        public bool? OpenModeCountMin { get; set; }
        public int? Count { get; set; }
        public bool? UseTransientResin { get; set; }
        public bool? UseFragileResin { get; set; }
        public string? Team { get; set; }
        public string? FriendshipTeam { get; set; }
        public int? Timeout { get; set; }
        public bool? UseAdventurerHandbook { get; set; }
        public bool? IsNotification { get; set; }
        public bool? IsGoToSynthesizer { get; set; }
        public bool? ScanDropsAfterRewardEnabled { get; set; }
        public int? ScanDropsAfterRewardSeconds { get; set; }
    }

    private sealed class NotificationSettingsRequest
    {
        public bool? JsNotificationEnabled { get; set; }
        public bool? IncludeScreenShot { get; set; }
        public string? NotificationEventSubscribe { get; set; }
        public bool? WebhookEnabled { get; set; }
        public string? WebhookEndpoint { get; set; }
        public string? WebhookSendTo { get; set; }
        public bool? WebSocketNotificationEnabled { get; set; }
        public string? WebSocketEndpoint { get; set; }
        public bool? WindowsUwpNotificationEnabled { get; set; }
        public bool? FeishuNotificationEnabled { get; set; }
        public string? FeishuWebhookUrl { get; set; }
        public string? FeishuAppId { get; set; }
        public string? FeishuAppSecret { get; set; }
        public bool? OneBotNotificationEnabled { get; set; }
        public string? OneBotEndpoint { get; set; }
        public string? OneBotUserId { get; set; }
        public string? OneBotGroupId { get; set; }
        public string? OneBotToken { get; set; }
        public bool? WorkweixinNotificationEnabled { get; set; }
        public string? WorkweixinWebhookUrl { get; set; }
        public bool? EmailNotificationEnabled { get; set; }
        public string? SmtpServer { get; set; }
        public int? SmtpPort { get; set; }
        public string? SmtpUsername { get; set; }
        public string? SmtpPassword { get; set; }
        public string? FromEmail { get; set; }
        public string? FromName { get; set; }
        public string? ToEmail { get; set; }
        public bool? BarkNotificationEnabled { get; set; }
        public string? BarkAction { get; set; }
        public string? BarkApiEndpoint { get; set; }
        public string? BarkAutoCopy { get; set; }
        public int? BarkBadge { get; set; }
        public string? BarkCall { get; set; }
        public string? BarkCiphertext { get; set; }
        public string? BarkCopy { get; set; }
        public string? BarkDeviceKeys { get; set; }
        public string? BarkGroup { get; set; }
        public string? BarkIcon { get; set; }
        public string? BarkIsArchive { get; set; }
        public string? BarkLevel { get; set; }
        public string? BarkSound { get; set; }
        public string? BarkSubtitle { get; set; }
        public string? BarkUrl { get; set; }
        public int? BarkVolume { get; set; }
        public bool? TelegramNotificationEnabled { get; set; }
        public string? TelegramApiBaseUrl { get; set; }
        public string? TelegramProxyUrl { get; set; }
        public bool? TelegramProxyEnabled { get; set; }
        public string? TelegramBotToken { get; set; }
        public string? TelegramChatId { get; set; }
        public bool? XxtuiNotificationEnabled { get; set; }
        public string? XxtuiApiKey { get; set; }
        public string? XxtuiFrom { get; set; }
        public string? XxtuiChannels { get; set; }
        public bool? DingDingwebhookNotificationEnabled { get; set; }
        public string? DingdingWebhookUrl { get; set; }
        public string? DingDingSecret { get; set; }
        public bool? DiscordWebhookNotificationEnabled { get; set; }
        public string? DiscordWebhookUrl { get; set; }
        public string? DiscordWebhookUsername { get; set; }
        public string? DiscordWebhookAvatarUrl { get; set; }
        public string? DiscordWebhookImageEncoder { get; set; }
        public bool? ServerChanNotificationEnabled { get; set; }
        public string? ServerChanSendKey { get; set; }
    }

    private sealed class NotificationTestRequest
    {
        public string? Channel { get; set; }
    }

    private sealed class LibraryItemRequest
    {
        public string? Name { get; set; }
        public string? Folder { get; set; }
        public string? Path { get; set; }
        public string? NewName { get; set; }
    }

    private sealed class AiChatRequest
    {
        public string Message { get; set; } = string.Empty;
        public List<AiChatHistoryItem>? History { get; set; }
    }

    private sealed class AiChatHistoryItem
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
        public bool IsAssistant { get; set; }
        public bool IsSystem { get; set; }
        public bool IsMcp { get; set; }
    }

    private sealed class OneDragonSelectRequest
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class OneDragonConfigNameRequest
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class OneDragonCloneRequest
    {
        public string? Name { get; set; }
        public string? NewName { get; set; }
    }

    private sealed class OneDragonRenameRequest
    {
        public string? OldName { get; set; }
        public string? NewName { get; set; }
    }

    private sealed class ScriptGroupInfo
    {
        public string Name { get; set; } = string.Empty;
        public int Index { get; set; }
        public int ProjectCount { get; set; }
        public bool NextFlag { get; set; }
    }

    private static bool TryGetScriptGroupFilePath(string name, out string fullPath, out string? error)
    {
        fullPath = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "配置组名称为空";
            return false;
        }

        var normalizedName = name.Trim();
        if (normalizedName.Length > 120)
        {
            error = "配置组名称过长";
            return false;
        }

        if (normalizedName is "." or ".." ||
            normalizedName.Contains('/') ||
            normalizedName.Contains('\\') ||
            normalizedName.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(normalizedName) ||
            normalizedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "配置组名称包含非法路径字符";
            return false;
        }

        var folder = Path.GetFullPath(Global.Absolute(@"User\ScriptGroup"));
        var candidate = Path.GetFullPath(Path.Combine(folder, $"{normalizedName}.json"));
        if (!IsSubPathOf(folder, candidate))
        {
            error = "配置组路径越界";
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private static bool IsSubPathOf(string rootPath, string targetPath)
    {
        var normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedTarget = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedRoot, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedTarget.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
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

            var nextGroupName = TaskContext.Instance().Config.NextScriptGroupName;
            var files = Directory.GetFiles(folder, "*.json");
            var list = new List<ScriptGroupInfo>();
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var group = ScriptGroup.FromJson(json);
                    list.Add(new ScriptGroupInfo
                    {
                        Name = group.Name,
                        Index = group.Index,
                        ProjectCount = group.Projects?.Count ?? 0,
                        NextFlag = string.Equals(group.Name, nextGroupName, StringComparison.OrdinalIgnoreCase)
                    });
                }
                catch
                {
                }
            }

            return list.OrderBy(g => g.Index).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return Array.Empty<ScriptGroupInfo>();
        }
    }

    private static ScriptGroup? LoadScriptGroupByName(string name, out string? error)
    {
        error = null;
        try
        {
            if (!TryGetScriptGroupFilePath(name, out var file, out var fileError))
            {
                error = fileError ?? "配置组名称非法";
                return null;
            }

            if (!File.Exists(file))
            {
                error = "配置组不存在";
                return null;
            }

            var json = File.ReadAllText(file);
            return ScriptGroup.FromJson(json);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static void SaveScriptGroup(ScriptGroup group)
    {
        var folder = Global.Absolute(@"User\ScriptGroup");
        Directory.CreateDirectory(folder);
        ScriptGroup.ResetGroupInfo(group);
        if (!TryGetScriptGroupFilePath(group.Name, out var file, out var error))
        {
            throw new InvalidOperationException(error ?? "配置组名称非法");
        }

        File.WriteAllText(file, group.ToJson());
    }

    private static void ReindexProjects(ScriptGroup group)
    {
        for (var i = 0; i < group.Projects.Count; i++)
        {
            group.Projects[i].Index = i;
        }
    }

    private static bool ApplyScriptGroupItemPatch(ScriptGroupProject project, string? status, string? schedule, int? runNum)
    {
        var changed = false;
        if (!string.IsNullOrWhiteSpace(status))
        {
            var nextStatus = status.Trim();
            if (!string.Equals(project.Status, nextStatus, StringComparison.Ordinal))
            {
                project.Status = nextStatus;
                changed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(schedule))
        {
            var nextSchedule = schedule.Trim();
            if (!string.Equals(project.Schedule, nextSchedule, StringComparison.Ordinal))
            {
                project.Schedule = nextSchedule;
                changed = true;
            }
        }

        if (runNum.HasValue && runNum.Value > 0 && project.RunNum != runNum.Value)
        {
            project.RunNum = runNum.Value;
            changed = true;
        }

        return changed;
    }

    private static void AlignScriptGroupNextTask(ScriptGroup group)
    {
        var nextTasks = TaskContext.Instance().Config.NextScheduledTask;
        var target = nextTasks.Find(item => string.Equals(item.Item1, group.Name, StringComparison.OrdinalIgnoreCase));
        if (target == default)
        {
            return;
        }

        nextTasks.RemoveAll(item => string.Equals(item.Item1, group.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var project in group.Projects)
        {
            if (string.Equals(project.FolderName, target.Item3, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(project.Name, target.Item4, StringComparison.OrdinalIgnoreCase))
            {
                nextTasks.Add((group.Name, project.Index, project.FolderName, project.Name));
                break;
            }
        }
    }

    private static bool TrySetScriptGroupNextTask(string groupName, int? index, bool clear, out string? error, out (int Index, string Name)? project)
    {
        error = null;
        project = null;
        var normalizedName = groupName.Trim();
        var nextTasks = TaskContext.Instance().Config.NextScheduledTask;
        nextTasks.RemoveAll(item => string.Equals(item.Item1, normalizedName, StringComparison.OrdinalIgnoreCase));
        if (clear)
        {
            return true;
        }

        var group = LoadScriptGroupByName(normalizedName, out var loadError);
        if (group == null)
        {
            error = loadError ?? "配置组不存在";
            return false;
        }

        if (!index.HasValue || index.Value < 0 || index.Value >= group.Projects.Count)
        {
            error = "索引超出范围";
            return false;
        }

        var target = group.Projects[index.Value];
        nextTasks.Add((group.Name, target.Index, target.FolderName, target.Name));
        project = (target.Index, target.Name);
        return true;
    }

    private async Task RunScriptGroupsByNamesAsync(IEnumerable<string> names)
    {
        var normalized = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
        {
            return;
        }

        await RunOnUiThreadAsync(async () =>
        {
            var vm = App.GetService<BetterGenshinImpact.ViewModel.Pages.ScriptControlViewModel>();
            if (vm != null)
            {
                await vm.OnStartMultiScriptGroupWithNamesAsync(normalized);
            }
        });
    }

    private static object BuildScriptLibrary()
    {
        var js = new List<ScriptLibraryItem>();
        try
        {
            var projects = BetterGenshinImpact.ViewModel.Pages.ScriptControlViewModel.LoadAllJsScriptProjects();
            foreach (var project in projects)
            {
                js.Add(new ScriptLibraryItem
                {
                    Name = project.Manifest.Name,
                    Folder = project.FolderName,
                    Description = project.Manifest.ShortDescription
                });
            }
        }
        catch
        {
        }

        var keymouse = new List<ScriptLibraryItem>();
        try
        {
            var folder = Global.Absolute(@"User\KeyMouseScript");
            Directory.CreateDirectory(folder);
            var files = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                keymouse.Add(new ScriptLibraryItem
                {
                    Name = Path.GetFileName(file)
                });
            }
        }
        catch
        {
        }

        var pathing = new List<PathingLibraryItem>();
        try
        {
            var root = BetterGenshinImpact.ViewModel.Pages.MapPathingViewModel.PathJsonPath;
            Directory.CreateDirectory(root);
            var files = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                var folder = Path.GetRelativePath(root, info.DirectoryName ?? root);
                if (folder == "." || folder == "\\")
                {
                    folder = string.Empty;
                }
                pathing.Add(new PathingLibraryItem
                {
                    Name = info.Name,
                    Folder = folder
                });
            }
        }
        catch
        {
        }

        return new
        {
            js,
            keymouse,
            pathing
        };
    }

    private static string[] LoadDomainNames()
    {
        try
        {
            return [.. BetterGenshinImpact.GameTask.Common.Element.Assets.MapLazyAssets.Instance.DomainNameList];
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string[] LoadLeyLineOutcropTypes()
    {
        return ["启示之花", "藏金之花"];
    }

    private static string[] LoadLeyLineOutcropCountries()
    {
        return ["蒙德", "璃月", "稻妻", "须弥", "枫丹", "纳塔", "挪德卡莱"];
    }

    private static string[] LoadOneDragonCraftingBenchCountries()
    {
        return ["枫丹", "稻妻", "璃月", "蒙德"];
    }

    private static string[] LoadOneDragonAdventurersGuildCountries()
    {
        return ["挪德卡莱", "枫丹", "稻妻", "璃月", "蒙德"];
    }

    private static string[] LoadOneDragonCompletionActions()
    {
        return ["无", "关闭游戏", "关闭游戏和软件", "关机"];
    }

    private static string[] LoadOneDragonSundayValues()
    {
        return ["", "1", "2", "3"];
    }

    private static string[] LoadAutoFightEliteDropModes()
    {
        return ["Closed", "AllowAutoPickupForNonElite", "DisableAutoPickupForNonElite"];
    }

    private static string[] LoadResinPriorityOptions()
    {
        return ["浓缩树脂", "原粹树脂", "须臾树脂", "脆弱树脂"];
    }

    private static string[] LoadScriptProjectStatuses()
    {
        try
        {
            return [.. ScriptGroupProjectExtensions.StatusDescriptions.Keys];
        }
        catch
        {
            return ["Enabled", "Disabled"];
        }
    }

    private static string[] LoadScriptProjectSchedules()
    {
        try
        {
            return [.. ScriptGroupProjectExtensions.ScheduleDescriptions.Keys];
        }
        catch
        {
            return ["Daily", "EveryTwoDays", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
        }
    }

    private static string[] LoadOneDragonSereniteaPotTpTypes()
    {
        return ["地图传送", "尘歌壶道具"];
    }

    private static Dictionary<string, string[]> BuildOneDragonOptionMap(string[] domainNames, string[] leyLineTypes, string[] leyLineCountries)
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["craftingBenchCountry"] = LoadOneDragonCraftingBenchCountries(),
            ["adventurersGuildCountry"] = LoadOneDragonAdventurersGuildCountries(),
            ["completionAction"] = LoadOneDragonCompletionActions(),
            ["sundayEverySelectedValue"] = LoadOneDragonSundayValues(),
            ["sundaySelectedValue"] = LoadOneDragonSundayValues(),
            ["sereniteaPotTpType"] = LoadOneDragonSereniteaPotTpTypes(),
            ["domainName"] = domainNames
        };

        foreach (var day in OneDragonDayNames)
        {
            var domainKey = char.ToLowerInvariant(day[0]) + day[1..] + "DomainName";
            map[domainKey] = domainNames;
            map["leyLine" + day + "Type"] = leyLineTypes;
            map["leyLine" + day + "Country"] = leyLineCountries;
        }

        return map;
    }

    private static string[] LoadOneDragonTaskCatalog()
    {
        var result = new List<string>(64);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in OneDragonDefaultTaskNames)
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
            {
                result.Add(name);
            }
        }

        foreach (var config in LoadOneDragonConfigs())
        {
            if (config.TaskEnabledList == null)
            {
                continue;
            }

            foreach (var key in config.TaskEnabledList.Keys)
            {
                if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                {
                    result.Add(key);
                }
            }
        }

        foreach (var group in LoadScriptGroupInfos())
        {
            if (!string.IsNullOrWhiteSpace(group.Name) && seen.Add(group.Name))
            {
                result.Add(group.Name);
            }
        }

        return result.ToArray();
    }

    private static bool TryNormalizeOneDragonConfigName(string? source, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(source))
        {
            error = "配置名为空";
            return false;
        }

        normalized = source.Trim();
        if (normalized.Length is < 1 or > 120)
        {
            error = "配置名长度需在 1~120 之间";
            return false;
        }

        if (normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            normalized.Contains('/', StringComparison.Ordinal) ||
            normalized.Contains('\\', StringComparison.Ordinal))
        {
            error = "配置名包含非法字符";
            return false;
        }

        return true;
    }

    private static string BuildOneDragonCloneName(string sourceName, IReadOnlyList<string> existingNames)
    {
        var baseName = string.IsNullOrWhiteSpace(sourceName) ? "新配置" : sourceName.Trim();
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var candidate = baseName + "-副本";
        if (!taken.Contains(candidate))
        {
            return candidate;
        }

        for (var i = 2; i <= 999; i++)
        {
            candidate = $"{baseName}-副本{i}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{baseName}-{Guid.NewGuid():N}".Substring(0, Math.Min(40, baseName.Length + 9));
    }

    private static string BuildScriptGroupCloneName(string sourceName, IReadOnlyList<string> existingNames)
    {
        var baseName = string.IsNullOrWhiteSpace(sourceName) ? "新配置组" : sourceName.Trim();
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var candidate = baseName + "-副本";
        if (!taken.Contains(candidate))
        {
            return candidate;
        }

        for (var i = 2; i <= 999; i++)
        {
            candidate = $"{baseName}-副本{i}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{baseName}-{Guid.NewGuid():N}".Substring(0, Math.Min(40, baseName.Length + 9));
    }

    private static string[] LoadGridNames()
    {
        try
        {
            return Enum.GetNames(typeof(BetterGenshinImpact.GameTask.Model.GameUI.GridScreenName));
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string[] LoadFishingTimePolicies()
    {
        try
        {
            return Enum.GetNames(typeof(FishingTimePolicy));
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string[] LoadRecognitionFailurePolicies()
    {
        try
        {
            return Enum.GetNames(typeof(RecognitionFailurePolicy));
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string[] LoadNotificationChannels()
    {
        return
        [
            "webhook",
            "websocket",
            "windows_uwp",
            "feishu",
            "onebot",
            "workweixin",
            "email",
            "bark",
            "telegram",
            "xxtui",
            "dingding",
            "discord",
            "serverchan"
        ];
    }

    private async Task HandleUiRoutesAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var scope = ResolveOpenApiSpecScope(request);
        var clusterScope = scope == OpenApiSpecScope.Cluster;
        var getRoutes = NormalizeRouteListForScope(_getRoutes.Keys.Concat(LegacyGetRoutePaths), clusterScope);
        var postRoutes = NormalizeRouteListForScope(_postRoutes.Keys.Concat(LegacyPostRoutePaths), clusterScope);
        await WriteJsonAsync(response, new
        {
            get = getRoutes,
            post = postRoutes
        }, ct);
    }

    private static string[] NormalizeRouteListForScope(IEnumerable<string> routes, bool clusterScope)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            if (string.IsNullOrWhiteSpace(route))
            {
                continue;
            }

            if (!clusterScope)
            {
                set.Add(route);
                continue;
            }

            if (!route.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            set.Add("/api/cluster" + route.Substring("/api".Length));
        }

        return set
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task HandleApiMetaAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var scope = ResolveOpenApiSpecScope(request);
        var clusterScope = scope == OpenApiSpecScope.Cluster;
        var apiPrefix = clusterScope ? "/api/cluster" : "/api";
        var authInfo = clusterScope
            ? (object)new
            {
                mode = "cluster-token",
                headers = new[] { "X-BGI-Cluster-Token", "X-BGI-Token", "Authorization: Bearer <token>" }
            }
            : new
            {
                mode = "web-login-or-basic",
                methods = new[] { "Cookie session", "HTTP Basic Auth" }
            };
        await WriteJsonAsync(response, new
        {
            ok = true,
            scope = clusterScope ? "cluster" : "api",
            version = Global.Version,
            serverTime = DateTimeOffset.Now,
            endpoints = new
            {
                status = $"{apiPrefix}/status",
                health = $"{apiPrefix}/health",
                routes = $"{apiPrefix}/routes",
                openapi = $"{apiPrefix}/openapi.json",
                docs = $"{apiPrefix}/docs",
                redoc = $"{apiPrefix}/redoc"
            },
            auth = authInfo
        }, ct);
    }

    private async Task HandleApiHealthAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var scope = ResolveOpenApiSpecScope(request);
        var status = BuildStatus();
        await WriteJsonAsync(response, new
        {
            ok = true,
            scope = scope == OpenApiSpecScope.Cluster ? "cluster" : "api",
            version = status.Version,
            serverTime = status.ServerTime,
            isInitialized = status.IsInitialized,
            isSuspended = status.IsSuspended,
            warning = status.Warning
        }, ct);
    }

    private async Task HandleOpenApiDocumentAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var scope = ResolveOpenApiSpecScope(request);
        await WriteJsonAsync(response, BuildOpenApiDocument(scope), ct);
    }

    private Task HandleOpenApiSwaggerAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var scope = ResolveOpenApiSpecScope(request);
        var openApiUrl = ResolveOpenApiDocumentUrl(request, scope);
        var title = scope == OpenApiSpecScope.Cluster
            ? "BetterGI Cluster API - Swagger UI"
            : "BetterGI API - Swagger UI";
        return WriteStringAsync(response, BuildSwaggerUiHtml(title, openApiUrl), "text/html; charset=utf-8", ct);
    }

    private Task HandleOpenApiRedocAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var scope = ResolveOpenApiSpecScope(request);
        var openApiUrl = ResolveOpenApiDocumentUrl(request, scope);
        var title = scope == OpenApiSpecScope.Cluster
            ? "BetterGI Cluster API - ReDoc"
            : "BetterGI API - ReDoc";
        return WriteStringAsync(response, BuildRedocHtml(title, openApiUrl), "text/html; charset=utf-8", ct);
    }

    private static OpenApiSpecScope ResolveOpenApiSpecScope(HttpListenerRequest request)
    {
        var scope = request.QueryString["scope"];
        if (string.Equals(scope, "cluster", StringComparison.OrdinalIgnoreCase))
        {
            return OpenApiSpecScope.Cluster;
        }

        var path = request.Url?.AbsolutePath ?? string.Empty;
        return path.StartsWith("/api/cluster", StringComparison.OrdinalIgnoreCase)
            ? OpenApiSpecScope.Cluster
            : OpenApiSpecScope.Api;
    }

    private static string ResolveOpenApiDocumentUrl(HttpListenerRequest request, OpenApiSpecScope scope)
    {
        var path = request.Url?.AbsolutePath ?? string.Empty;
        var apiScoped = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);

        if (scope == OpenApiSpecScope.Cluster)
        {
            if (path.StartsWith("/api/cluster", StringComparison.OrdinalIgnoreCase))
            {
                return "/api/cluster/openapi.json";
            }

            return apiScoped ? "/api/openapi.json?scope=cluster" : "/openapi.json?scope=cluster";
        }

        return apiScoped ? "/api/openapi.json" : "/openapi.json";
    }

    private object BuildOpenApiDocument(OpenApiSpecScope scope)
    {
        var clusterScope = scope == OpenApiSpecScope.Cluster;
        var routeMap = BuildOpenApiRouteMap(clusterScope);
        var paths = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var path in routeMap.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var operations = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var method in routeMap[path].OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                operations[method.ToLowerInvariant()] = BuildOpenApiOperation(path, method, clusterScope);
            }

            paths[path] = operations;
        }

        var status = BuildStatus();
        return new
        {
            openapi = "3.1.0",
            info = new
            {
                title = clusterScope ? "BetterGI Cluster API" : "BetterGI Web API",
                version = status.Version,
                description = clusterScope
                    ? "FastAPI-compatible OpenAPI schema for BetterGI cluster endpoints."
                    : "FastAPI-compatible OpenAPI schema for BetterGI Web Remote endpoints."
            },
            servers = clusterScope
                ? new[]
                {
                    new
                    {
                        url = "/",
                        description = "Use /api/cluster/* routes with cluster token headers."
                    }
                }
                : new[]
                {
                    new
                    {
                        url = "/",
                        description = "Use /api/* routes with session cookie or HTTP Basic auth."
                    }
                },
            paths,
            components = new
            {
                securitySchemes = BuildOpenApiSecuritySchemes()
            }
        };
    }

    private Dictionary<string, HashSet<string>> BuildOpenApiRouteMap(bool clusterScope)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void AddRoute(string path, string method)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var normalizedPath = NormalizeOpenApiPath(path, clusterScope);
            if (!map.TryGetValue(normalizedPath, out var methods))
            {
                methods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                map[normalizedPath] = methods;
            }

            methods.Add(method.ToUpperInvariant());
        }

        foreach (var getPath in _getRoutes.Keys.Concat(LegacyGetRoutePaths).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AddRoute(getPath, "GET");
        }

        foreach (var postPath in _postRoutes.Keys.Concat(LegacyPostRoutePaths).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AddRoute(postPath, "POST");
        }

        return map;
    }

    private static string NormalizeOpenApiPath(string path, bool clusterScope)
    {
        if (!clusterScope)
        {
            return path;
        }

        return path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            ? "/api/cluster" + path.Substring("/api".Length)
            : path;
    }

    private static object BuildOpenApiOperation(string path, string method, bool clusterScope)
    {
        var normalizedMethod = method.ToUpperInvariant();
        var operation = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["summary"] = $"{normalizedMethod} {path}",
            ["operationId"] = BuildOpenApiOperationId(path, normalizedMethod),
            ["tags"] = new[] { BuildOpenApiTag(path, clusterScope) },
            ["responses"] = BuildOpenApiResponses(path),
            ["security"] = clusterScope ? BuildClusterOpenApiSecurity() : BuildWebOpenApiSecurity()
        };

        if (string.Equals(normalizedMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            operation["requestBody"] = new
            {
                required = false,
                content = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["application/json"] = new
                    {
                        schema = new
                        {
                            type = "object",
                            additionalProperties = true
                        }
                    }
                }
            };
        }

        return operation;
    }

    private static string BuildOpenApiOperationId(string path, string method)
    {
        var normalizedPath = path.Trim('/')
            .Replace('-', '_')
            .Replace('/', '_')
            .Replace('.', '_');
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            normalizedPath = "root";
        }

        return $"{method.ToLowerInvariant()}_{normalizedPath}";
    }

    private static string BuildOpenApiTag(string path, bool clusterScope)
    {
        var prefix = clusterScope ? "/api/cluster/" : "/api/";
        var normalized = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? path.Substring(prefix.Length)
            : path.Trim('/');
        var firstSegment = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstSegment))
        {
            return "default";
        }

        return firstSegment.Replace('-', '_');
    }

    private static object BuildOpenApiResponses(string path)
    {
        if (path.EndsWith("/logs/stream", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["200"] = new
                {
                    description = "SSE log stream.",
                    content = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["text/event-stream"] = new
                        {
                            schema = new
                            {
                                type = "string"
                            }
                        }
                    }
                }
            };
        }

        if (path.EndsWith("/screen", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["200"] = new
                {
                    description = "Live screen snapshot.",
                    content = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["image/png"] = new
                        {
                            schema = new
                            {
                                type = "string",
                                format = "binary"
                            }
                        },
                        ["image/svg+xml"] = new
                        {
                            schema = new
                            {
                                type = "string"
                            }
                        }
                    }
                }
            };
        }

        if (path.EndsWith("/docs", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/redoc", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["200"] = new
                {
                    description = "HTML document.",
                    content = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["text/html"] = new
                        {
                            schema = new
                            {
                                type = "string"
                            }
                        }
                    }
                }
            };
        }

        return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["200"] = new
            {
                description = "Successful response.",
                content = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["application/json"] = new
                    {
                        schema = new
                        {
                            type = "object",
                            additionalProperties = true
                        }
                    }
                }
            }
        };
    }

    private static object[] BuildWebOpenApiSecurity()
    {
        return
        [
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["CookieAuth"] = Array.Empty<string>()
            },
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["BasicAuth"] = Array.Empty<string>()
            }
        ];
    }

    private static object[] BuildClusterOpenApiSecurity()
    {
        return
        [
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["ClusterToken"] = Array.Empty<string>()
            },
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["ClusterTokenCompat"] = Array.Empty<string>()
            },
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["BearerAuth"] = Array.Empty<string>()
            }
        ];
    }

    private static Dictionary<string, object> BuildOpenApiSecuritySchemes()
    {
        return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["CookieAuth"] = new
            {
                type = "apiKey",
                @in = "cookie",
                name = SessionCookieName,
                description = "Web session cookie issued by /api/auth/login."
            },
            ["BasicAuth"] = new
            {
                type = "http",
                scheme = "basic",
                description = "HTTP Basic auth with Web remote username/password."
            },
            ["ClusterToken"] = new
            {
                type = "apiKey",
                @in = "header",
                name = "X-BGI-Cluster-Token",
                description = "Primary cluster token header."
            },
            ["ClusterTokenCompat"] = new
            {
                type = "apiKey",
                @in = "header",
                name = "X-BGI-Token",
                description = "Compatibility cluster token header."
            },
            ["BearerAuth"] = new
            {
                type = "http",
                scheme = "bearer",
                bearerFormat = "OpaqueToken",
                description = "Authorization: Bearer <cluster-token>."
            }
        };
    }

    private static string BuildSwaggerUiHtml(string title, string openApiUrl)
    {
        var safeTitle = System.Security.SecurityElement.Escape(title) ?? "BetterGI API Docs";
        var safeOpenApiUrl = EscapeJsStringLiteral(openApiUrl);
        return $$"""
                  <!doctype html>
                  <html lang="en">
                  <head>
                    <meta charset="utf-8">
                    <meta name="viewport" content="width=device-width,initial-scale=1">
                    <title>{{safeTitle}}</title>
                    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/swagger-ui-dist@5/swagger-ui.css">
                    <style>
                      html, body { margin: 0; padding: 0; background: #0b1220; }
                      #swagger-ui { min-height: 100vh; }
                      .topbar { display: none; }
                    </style>
                  </head>
                  <body>
                    <div id="swagger-ui"></div>
                    <script src="https://cdn.jsdelivr.net/npm/swagger-ui-dist@5/swagger-ui-bundle.js"></script>
                    <script>
                      window.ui = SwaggerUIBundle({
                        url: '{{safeOpenApiUrl}}',
                        dom_id: '#swagger-ui',
                        deepLinking: true,
                        persistAuthorization: true,
                        displayRequestDuration: true,
                        layout: 'BaseLayout'
                      });
                    </script>
                  </body>
                  </html>
                  """;
    }

    private static string BuildRedocHtml(string title, string openApiUrl)
    {
        var safeTitle = System.Security.SecurityElement.Escape(title) ?? "BetterGI API Docs";
        var safeOpenApiUrl = System.Security.SecurityElement.Escape(openApiUrl) ?? "/openapi.json";
        return $$"""
                  <!doctype html>
                  <html lang="en">
                  <head>
                    <meta charset="utf-8">
                    <meta name="viewport" content="width=device-width,initial-scale=1">
                    <title>{{safeTitle}}</title>
                    <style>
                      html, body { margin: 0; padding: 0; background: #f8fafc; }
                    </style>
                    <script src="https://cdn.jsdelivr.net/npm/redoc@next/bundles/redoc.standalone.js"></script>
                  </head>
                  <body>
                    <redoc spec-url="{{safeOpenApiUrl}}"></redoc>
                  </body>
                  </html>
                  """;
    }

    private static string EscapeJsStringLiteral(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("'", "\\'");
    }

    private async Task HandleUiI18nAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var locale = NormalizeSchemaLocale(request.QueryString["lang"]);
        if (string.Equals(locale, "zh", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(response, new
            {
                locale,
                loadedCulture = "zh-Hans",
                map = new Dictionary<string, string>(StringComparer.Ordinal)
            }, ct);
            return;
        }

        var configuredCulture = _configService.Get().OtherConfig.UiCultureInfoName;
        var candidates = BuildUiI18nCultureCandidates(request.QueryString["lang"], locale, configuredCulture);
        var map = LoadUiI18nMap(candidates, out var loadedCulture);
        await WriteJsonAsync(response, new
        {
            locale,
            loadedCulture,
            map
        }, ct);
    }

    private IReadOnlyDictionary<string, string> LoadUiI18nMap(IEnumerable<string> cultureCandidates, out string loadedCulture)
    {
        loadedCulture = string.Empty;
        foreach (var cultureName in cultureCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(cultureName))
            {
                continue;
            }

            var path = Path.Combine(Global.Absolute(@"User\I18n"), $"{cultureName}.json");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var rawMap = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                var map = new Dictionary<string, string>(rawMap.Count, StringComparer.Ordinal);
                foreach (var (key, value) in rawMap)
                {
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    map[key] = value;
                }

                loadedCulture = cultureName;
                return map;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "加载 WebUI 词典失败: {CultureName}", cultureName);
            }
        }

        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> BuildUiI18nCultureCandidates(string? requestedLocale, string normalizedLocale, string? configuredCulture)
    {
        if (!string.Equals(normalizedLocale, "en", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        var list = new List<string>(6);

        static void AddCandidate(List<string> values, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = value.Trim().Replace('_', '-');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            if (values.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            values.Add(normalized);
        }

        static string? ExtractBaseCulture(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = value.Trim().Replace('_', '-');
            var dashIndex = normalized.IndexOf('-');
            if (dashIndex > 0)
            {
                return normalized.Substring(0, dashIndex);
            }

            return normalized;
        }

        AddCandidate(list, requestedLocale);
        AddCandidate(list, ExtractBaseCulture(requestedLocale));
        AddCandidate(list, configuredCulture);
        AddCandidate(list, ExtractBaseCulture(configuredCulture));
        AddCandidate(list, "en");
        AddCandidate(list, "en-US");
        AddCandidate(list, "en-GB");
        return list;
    }

    private async Task HandleUiSchemaAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var locale = NormalizeSchemaLocale(request.QueryString["lang"]);
        var pageId = NormalizeSchemaPageId(request.QueryString["page"]);
        var force = IsTruthy(request.QueryString["force"]);
        await WriteJsonAsync(response, GetOrBuildUiSchema(locale, force, pageId), ct);
    }

    private object GetOrBuildUiSchema(string locale, bool forceRefresh, string? pageId = null)
    {
        var normalizedLocale = NormalizeSchemaLocale(locale);
        var normalizedPage = NormalizeSchemaPageId(pageId);
        var key = string.IsNullOrWhiteSpace(normalizedPage)
            ? normalizedLocale
            : $"{normalizedLocale}|page={normalizedPage}";
        if (!forceRefresh)
        {
            lock (_uiSchemaCacheSync)
            {
                if (_uiSchemaCache.TryGetValue(key, out var cached) &&
                    DateTimeOffset.UtcNow - cached.BuiltAt <= UiSchemaCacheLifetime)
                {
                    return cached.Payload;
                }
            }
        }

        var payload = BuildUiSchema(normalizedLocale, normalizedPage);
        lock (_uiSchemaCacheSync)
        {
            _uiSchemaCache[key] = new CachedUiSchema(DateTimeOffset.UtcNow, payload);
        }

        return payload;
    }

    private void InvalidateUiSchemaCache()
    {
        lock (_uiSchemaCacheSync)
        {
            _uiSchemaCache.Clear();
        }
    }

    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return normalized switch
        {
            "1" => true,
            "yes" => true,
            "on" => true,
            _ => bool.TryParse(normalized, out var parsed) && parsed
        };
    }

    private object BuildUiSchema(string locale, string? pageId = null)
    {
        var normalizedPage = NormalizeSchemaPageId(pageId);
        if (string.Equals(normalizedPage, "scheduler", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                version = Global.Version,
                locale,
                generatedAt = DateTimeOffset.Now,
                pages = new object[]
                {
                    BuildSchedulerPage(locale)
                }
            };
        }

        if (string.Equals(normalizedPage, "one-dragon", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                version = Global.Version,
                locale,
                generatedAt = DateTimeOffset.Now,
                pages = new object[]
                {
                    BuildOneDragonPage(locale)
                }
            };
        }

        var taskSections = BuildTaskSections(locale);
        taskSections.AddRange(BuildAutoDiscoveredTaskSections(taskSections, locale));
        var configSections = BuildDynamicConfigSections();
        return new
        {
            version = Global.Version,
            locale,
            generatedAt = DateTimeOffset.Now,
            pages = new object[]
            {
                new
                {
                    id = "dashboard",
                    title = Localize(locale, "总览", "Overview"),
                    sections = new object[]
                    {
                        new
                        {
                            id = "runtime-actions",
                            type = "actions",
                            title = Localize(locale, "运行控制", "Runtime Controls"),
                            actions = new object[]
                            {
                                new { label = Localize(locale, "启动游戏", "Start Game"), method = "POST", endpoint = "/api/game/start", style = "primary" },
                                new { label = Localize(locale, "停止游戏", "Stop Game"), method = "POST", endpoint = "/api/game/stop", style = "danger" },
                                new { label = Localize(locale, "暂停任务", "Pause Task"), method = "POST", endpoint = "/api/tasks/pause", style = "secondary" },
                                new { label = Localize(locale, "继续任务", "Resume Task"), method = "POST", endpoint = "/api/tasks/resume", style = "secondary" },
                                new { label = Localize(locale, "取消任务", "Cancel Task"), method = "POST", endpoint = "/api/tasks/cancel", style = "danger" }
                            }
                        },
                        new
                        {
                            id = "basic-features",
                            type = "settings",
                            title = Localize(locale, "基础功能", "Basic Features"),
                            getEndpoint = "/api/config/basic",
                            postEndpoint = "/api/config/basic",
                            fields = new object[]
                            {
                                new { key = "autoPick", label = Localize(locale, "自动拾取", "Auto Pick-up"), type = "bool" },
                                new { key = "autoSkip", label = Localize(locale, "自动剧情", "Auto Dialogue"), type = "bool" },
                                new { key = "autoFishing", label = Localize(locale, "自动钓鱼", "Auto Fishing"), type = "bool" },
                                new { key = "autoCook", label = Localize(locale, "自动烹饪", "Auto Cooking"), type = "bool" },
                                new { key = "autoEat", label = Localize(locale, "自动吃药", "Auto Heal"), type = "bool" },
                                new { key = "quickTeleport", label = Localize(locale, "快捷传送", "Quick Teleport"), type = "bool" },
                                new { key = "mapMask", label = Localize(locale, "地图遮罩", "Map Mask"), type = "bool" }
                            }
                        },
                        new { id = "screen", type = "screen", title = Localize(locale, "实时画面", "Live Screen"), endpoint = "/api/screen" },
                        new { id = "logs", type = "logs", title = Localize(locale, "遮罩日志", "Overlay Logs"), endpoint = "/api/logs/stream" }
                    }
                },
                new
                {
                    id = "tasks",
                    title = Localize(locale, "独立任务", "Tasks"),
                    sections = taskSections
                },
                BuildSchedulerPage(locale),
                BuildOneDragonPage(locale),
                new
                {
                    id = "libraries",
                    title = Localize(locale, "脚本与录制", "Libraries"),
                    sections = new object[]
                    {
                        new { id = "js-library", type = "library", title = Localize(locale, "JS 脚本", "JS Scripts"), kind = "js" },
                        new { id = "pathing-library", type = "library", title = Localize(locale, "地图追踪", "Pathing"), kind = "pathing" },
                        new { id = "keymouse-library", type = "library", title = Localize(locale, "键鼠录制", "KeyMouse Recording"), kind = "keymouse" }
                    }
                },
                new
                {
                    id = "ai-chat",
                    title = Localize(locale, "AI 问答", "AI Chat"),
                    sections = new object[]
                    {
                        new
                        {
                            id = "ai-chat-main",
                            type = "ai_chat",
                            title = Localize(locale, "AI 对话", "AI Conversation"),
                            endpoints = new
                            {
                                chat = "/api/ai/chat"
                            }
                        }
                    }
                },
                new
                {
                    id = "config-center",
                    title = Localize(locale, "配置中心", "Config Center"),
                    sections = configSections
                },
                new
                {
                    id = "notification-center",
                    title = Localize(locale, "通知中心", "Notification Center"),
                    sections = BuildNotificationSections(locale)
                },
                new
                {
                    id = "route-center",
                    title = Localize(locale, "接口中心", "API Center"),
                    sections = new object[]
                    {
                        new
                        {
                            id = "fastapi-docs-main",
                            type = "fastapi_docs",
                            title = Localize(locale, "FastAPI 标准接口文档", "FastAPI Standard API Docs"),
                            description = Localize(locale, "接口中心已迁移为 FastAPI 风格文档入口，适配第三方集群更直接。", "API center now uses FastAPI-style docs for easier third-party cluster integration."),
                            endpoints = new
                            {
                                docs = "/docs",
                                redoc = "/redoc",
                                openapi = "/openapi.json",
                                meta = "/api/meta",
                                routes = "/api/routes",
                                clusterDocs = "/docs?scope=cluster",
                                clusterRedoc = "/redoc?scope=cluster",
                                clusterOpenapi = "/openapi.json?scope=cluster",
                                clusterMeta = "/api/meta?scope=cluster",
                                clusterRoutes = "/api/routes?scope=cluster"
                            }
                        },
                        new
                        {
                            id = "route-explorer-main",
                            type = "route_explorer",
                            title = Localize(locale, "兼容路由调试", "Legacy Route Debugger"),
                            toolActions = new object[]
                            {
                                new
                                {
                                    label = Localize(locale, "刷新接口列表", "Refresh Routes"),
                                    method = "GET",
                                    endpoint = "/api/ui/routes",
                                    style = "primary",
                                    refresh = "schema",
                                    successMessage = Localize(locale, "接口列表已刷新", "Routes refreshed")
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    private static string NormalizeSchemaLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return "zh";
        }

        return locale.Trim().StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
    }

    private static string Localize(string locale, string zh, string en)
    {
        return string.Equals(locale, "en", StringComparison.OrdinalIgnoreCase) ? en : zh;
    }

    private static string? NormalizeSchemaPageId(string? pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId))
        {
            return null;
        }

        var normalized = pageId.Trim()
            .Replace('_', '-')
            .Replace(" ", string.Empty)
            .ToLowerInvariant();

        return normalized switch
        {
            "dashboard" => "dashboard",
            "tasks" => "tasks",
            "scheduler" => "scheduler",
            "one-dragon" => "one-dragon",
            "onedragon" => "one-dragon",
            "libraries" => "libraries",
            "ai-chat" => "ai-chat",
            "aichat" => "ai-chat",
            "config-center" => "config-center",
            "configcenter" => "config-center",
            "notification-center" => "notification-center",
            "notificationcenter" => "notification-center",
            "notification" => "notification-center",
            "route-center" => "route-center",
            "apicenter" => "route-center",
            "api" => "route-center",
            _ => null
        };
    }

    private static object BuildSchedulerPage(string locale)
    {
        return new
        {
            id = "scheduler",
            title = Localize(locale, "调度器", "Scheduler"),
            sections = new object[]
            {
                new
                {
                    id = "scheduler-main",
                    type = "scheduler",
                    title = Localize(locale, "脚本调度器", "Script Scheduler"),
                    endpoints = new
                    {
                        groups = "/api/scripts/groups",
                        detail = "/api/scripts/group/detail",
                        library = "/api/scripts/library",
                        get = "/api/scripts/group",
                        save = "/api/scripts/group/save",
                        run = "/api/scripts/run",
                        create = "/api/scripts/group/create",
                        rename = "/api/scripts/group/rename",
                        @delete = "/api/scripts/group/delete",
                        copy = "/api/scripts/group/copy",
                        addItems = "/api/scripts/group/add-items",
                        removeItem = "/api/scripts/group/remove-item",
                        updateItem = "/api/scripts/group/update-item",
                        batchUpdate = "/api/scripts/group/batch-update",
                        reorder = "/api/scripts/group/reorder",
                        reverse = "/api/scripts/group/reverse",
                        setNext = "/api/scripts/group/set-next",
                        setNextGroup = "/api/scripts/group/set-next-group",
                        runFrom = "/api/scripts/group/run-from",
                        exportMerged = "/api/scripts/group/export-merged",
                        statusOptions = "/api/options/script-project-statuses",
                        scheduleOptions = "/api/options/script-project-schedules"
                    }
                }
            }
        };
    }

    private static object BuildOneDragonPage(string locale)
    {
        return new
        {
            id = "one-dragon",
            title = Localize(locale, "一条龙", "One-Dragon"),
            sections = new object[]
            {
                new
                {
                    id = "one-dragon-main",
                    type = "one_dragon",
                    title = Localize(locale, "一条龙配置", "One-Dragon Config"),
                    endpoints = new
                    {
                        list = "/api/one-dragon/configs",
                        get = "/api/one-dragon/config",
                        save = "/api/one-dragon/config",
                        select = "/api/one-dragon/select",
                        run = "/api/one-dragon/run",
                        options = "/api/one-dragon/options",
                        clone = "/api/one-dragon/config/clone",
                        rename = "/api/one-dragon/config/rename",
                        @delete = "/api/one-dragon/config/delete"
                    }
                }
            }
        };
    }

    private static object[] BuildNotificationSections(string locale)
    {
        const string endpoint = "/api/settings/notification";
        return
        [
            new
            {
                id = "notification-browser-push",
                type = "browser_notify",
                title = Localize(locale, "浏览器推送（仅 WebUI 打开时）", "Browser Push (WebUI Open Only)"),
                description = Localize(
                    locale,
                    "依赖浏览器通知权限，仅在当前 WebUI 页面保持打开时生效。",
                    "Requires browser notification permission and works only while this WebUI page stays open.")
            },
            new
            {
                id = "notification-global",
                type = "settings",
                title = Localize(locale, "全局通知设置", "Global Notification Settings"),
                getEndpoint = endpoint,
                postEndpoint = endpoint,
                fields = new object[]
                {
                    BuildNotificationField(locale, "includeScreenShot", "通知包含截图", "Include Screenshot", "bool"),
                    BuildNotificationField(locale, "jsNotificationEnabled", "允许 JS 发送通知", "Allow JS Notifications", "bool"),
                    BuildNotificationField(locale, "notificationEventSubscribe", "通知事件订阅（逗号分隔）", "Subscribed Events (comma-separated)")
                }
            },
            new
            {
                id = "notification-webhook",
                type = "settings",
                title = Localize(locale, "Webhook", "Webhook"),
                getEndpoint = endpoint,
                postEndpoint = endpoint,
                fields = new object[]
                {
                    BuildNotificationField(locale, "webhookEnabled", "启用 Webhook", "Enable Webhook", "bool"),
                    BuildNotificationField(locale, "webhookEndpoint", "Webhook 端点", "Webhook Endpoint"),
                    BuildNotificationField(locale, "webhookSendTo", "Webhook 发送对象", "Webhook SendTo")
                },
                toolActions = new object[]
                {
                    BuildNotificationTestAction(locale, "webhook", "测试 Webhook", "Test Webhook")
                }
            },
            new
            {
                id = "notification-websocket-windows",
                type = "settings",
                title = Localize(locale, "WebSocket / Windows UWP", "WebSocket / Windows UWP"),
                getEndpoint = endpoint,
                postEndpoint = endpoint,
                fields = new object[]
                {
                    BuildNotificationField(locale, "webSocketNotificationEnabled", "启用 WebSocket 通知", "Enable WebSocket", "bool"),
                    BuildNotificationField(locale, "webSocketEndpoint", "WebSocket 端点", "WebSocket Endpoint"),
                    BuildNotificationField(locale, "windowsUwpNotificationEnabled", "启用 Windows UWP 通知", "Enable Windows UWP", "bool")
                },
                toolActions = new object[]
                {
                    BuildNotificationTestAction(locale, "websocket", "测试 WebSocket", "Test WebSocket"),
                    BuildNotificationTestAction(locale, "windows_uwp", "测试 Windows UWP", "Test Windows UWP")
                }
            },
            new
            {
                id = "notification-feishu",
                type = "settings",
                title = Localize(locale, "飞书", "Feishu"),
                getEndpoint = endpoint,
                postEndpoint = endpoint,
                fields = new object[]
                {
                    BuildNotificationField(locale, "feishuNotificationEnabled", "启用飞书通知", "Enable Feishu", "bool"),
                    BuildNotificationField(locale, "feishuWebhookUrl", "飞书 Webhook", "Feishu Webhook"),
                    BuildNotificationField(locale, "feishuAppId", "飞书 App ID", "Feishu App ID"),
                    BuildNotificationField(locale, "feishuAppSecret", "飞书 App Secret", "Feishu App Secret")
                },
                toolActions = new object[]
                {
                    BuildNotificationTestAction(locale, "feishu", "测试飞书通知", "Test Feishu")
                }
            },
            new
            {
                id = "notification-onebot",
                type = "settings",
                title = Localize(locale, "OneBot", "OneBot"),
                getEndpoint = endpoint,
                postEndpoint = endpoint,
                fields = new object[]
                {
                    BuildNotificationField(locale, "oneBotNotificationEnabled", "启用 OneBot 通知", "Enable OneBot", "bool"),
                    BuildNotificationField(locale, "oneBotEndpoint", "OneBot 请求地址", "OneBot Endpoint"),
                    BuildNotificationField(locale, "oneBotUserId", "OneBot User ID", "OneBot User ID"),
                    BuildNotificationField(locale, "oneBotGroupId", "OneBot Group ID", "OneBot Group ID"),
                    BuildNotificationField(locale, "oneBotToken", "OneBot Token", "OneBot Token")
                },
                toolActions = new object[]
                {
                    BuildNotificationTestAction(locale, "onebot", "测试 OneBot 通知", "Test OneBot")
                }
            },
            new
            {
                id = "notification-workweixin",
                type = "settings",
                title = Localize(locale, "企业微信", "Work Weixin"),
                getEndpoint = endpoint,
                postEndpoint = endpoint,
                fields = new object[]
                {
                    BuildNotificationField(locale, "workweixinNotificationEnabled", "启用企业微信通知", "Enable Work Weixin", "bool"),
                    BuildNotificationField(locale, "workweixinWebhookUrl", "企业微信 Webhook", "Work Weixin Webhook")
                },
                toolActions = new object[]
                {
                    BuildNotificationTestAction(locale, "workweixin", "测试企业微信通知", "Test Work Weixin")
                }
            },
            new
            {
                id = "notification-email",
                type = "settings",
                title = Localize(locale, "邮箱", "Email"),
                getEndpoint = endpoint,
                postEndpoint = endpoint,
                fields = new object[]
                {
                    BuildNotificationField(locale, "emailNotificationEnabled", "启用邮箱通知", "Enable Email", "bool"),
                    BuildNotificationField(locale, "smtpServer", "SMTP 服务器", "SMTP Server"),
                    BuildNotificationField(locale, "smtpPort", "SMTP 端口", "SMTP Port", "number"),
                    BuildNotificationField(locale, "smtpUsername", "SMTP 用户名", "SMTP Username"),
                    BuildNotificationField(locale, "smtpPassword", "SMTP 密码", "SMTP Password"),
                    BuildNotificationField(locale, "fromEmail", "发件邮箱", "From Email"),
                    BuildNotificationField(locale, "fromName", "发件人名称", "From Name"),
                    BuildNotificationField(locale, "toEmail", "收件邮箱", "To Email")
                },
                toolActions = new object[]
                {
                    BuildNotificationTestAction(locale, "email", "测试邮箱通知", "Test Email")
                }
            },
            new
            {
                id = "notification-bark",
                type = "settings",
                title = Localize(locale, "Bark", "Bark"),
                getEndpoint = endpoint,
                postEndpoint = endpoint,
                fields = new object[]
                {
                    BuildNotificationField(locale, "barkNotificationEnabled", "启用 Bark 通知", "Enable Bark", "bool"),
                    BuildNotificationField(locale, "barkApiEndpoint", "Bark API 端点", "Bark API Endpoint"),
                    BuildNotificationField(locale, "barkDeviceKeys", "Bark 设备 Key", "Bark Device Keys"),
                    BuildNotificationField(locale, "barkLevel", "Bark 中断级别", "Bark Level"),
                    BuildNotificationField(locale, "barkSound", "Bark 声音", "Bark Sound"),
                    BuildNotificationField(locale, "barkIcon", "Bark 图标 URL", "Bark Icon URL"),
                    BuildNotificationField(locale, "barkGroup", "Bark 分组", "Bark Group"),
                    BuildNotificationField(locale, "barkIsArchive", "Bark 存档开关", "Bark IsArchive"),
                    BuildNotificationField(locale, "barkBadge", "Bark 角标", "Bark Badge", "number"),
                    BuildNotificationField(locale, "barkVolume", "Bark 音量(0-10)", "Bark Volume (0-10)", "number"),
                    BuildNotificationField(locale, "barkAction", "Bark 点击动作", "Bark Action"),
                    BuildNotificationField(locale, "barkUrl", "Bark 跳转 URL", "Bark URL"),
                    BuildNotificationField(locale, "barkCall", "Bark 重复提醒", "Bark Call"),
                    BuildNotificationField(locale, "barkAutoCopy", "Bark 自动复制", "Bark AutoCopy"),
                    BuildNotificationField(locale, "barkCopy", "Bark 复制内容", "Bark Copy"),
                    BuildNotificationField(locale, "barkCiphertext", "Bark 加密内容", "Bark Ciphertext"),
                    BuildNotificationField(locale, "barkSubtitle", "Bark 副标题", "Bark Subtitle")
                },
                toolActions = new object[]
                {
                    BuildNotificationTestAction(locale, "bark", "测试 Bark 通知", "Test Bark")
                }
            },
            new
            {
                id = "notification-telegram",
                type = "settings",
                title = Localize(locale, "Telegram", "Telegram"),
                getEndpoint = endpoint,
                postEndpoint = endpoint,
                fields = new object[]
                {
                    BuildNotificationField(locale, "telegramNotificationEnabled", "启用 Telegram 通知", "Enable Telegram", "bool"),
                    BuildNotificationField(locale, "telegramBotToken", "Telegram Bot Token", "Telegram Bot Token"),
                    BuildNotificationField(locale, "telegramChatId", "Telegram Chat ID", "Telegram Chat ID"),
                    BuildNotificationField(locale, "telegramApiBaseUrl", "Telegram API Base URL", "Telegram API Base URL"),
                    BuildNotificationField(locale, "telegramProxyEnabled", "启用 Telegram 代理", "Enable Telegram Proxy", "bool"),
                    BuildNotificationField(locale, "telegramProxyUrl", "Telegram 代理地址", "Telegram Proxy URL")
                },
                toolActions = new object[]
                {
                    BuildNotificationTestAction(locale, "telegram", "测试 Telegram 通知", "Test Telegram")
                }
            },
            new
            {
                id = "notification-xxtui",
                type = "settings",
                title = Localize(locale, "xx 推送", "xxTui"),
                getEndpoint = endpoint,
                postEndpoint = endpoint,
                fields = new object[]
                {
                    BuildNotificationField(locale, "xxtuiNotificationEnabled", "启用 xx 推送通知", "Enable xxTui", "bool"),
                    BuildNotificationField(locale, "xxtuiApiKey", "xx 推送 API Key", "xxTui API Key"),
                    BuildNotificationField(locale, "xxtuiFrom", "xx 推送来源", "xxTui From"),
                    BuildNotificationField(locale, "xxtuiChannels", "xx 推送渠道（逗号分隔）", "xxTui Channels (comma-separated)")
                },
                toolActions = new object[]
                {
                    BuildNotificationTestAction(locale, "xxtui", "测试 xx 推送通知", "Test xxTui")
                }
            },
            new
            {
                id = "notification-dingding",
                type = "settings",
                title = Localize(locale, "钉钉", "DingDing"),
                getEndpoint = endpoint,
                postEndpoint = endpoint,
                fields = new object[]
                {
                    BuildNotificationField(locale, "dingDingwebhookNotificationEnabled", "启用钉钉通知", "Enable DingDing", "bool"),
                    BuildNotificationField(locale, "dingdingWebhookUrl", "钉钉 Webhook", "DingDing Webhook"),
                    BuildNotificationField(locale, "dingDingSecret", "钉钉 Secret", "DingDing Secret")
                },
                toolActions = new object[]
                {
                    BuildNotificationTestAction(locale, "dingding", "测试钉钉通知", "Test DingDing")
                }
            },
            new
            {
                id = "notification-discord",
                type = "settings",
                title = Localize(locale, "Discord Webhook", "Discord Webhook"),
                getEndpoint = endpoint,
                postEndpoint = endpoint,
                fields = new object[]
                {
                    BuildNotificationField(locale, "discordWebhookNotificationEnabled", "启用 Discord Webhook", "Enable Discord Webhook", "bool"),
                    BuildNotificationField(locale, "discordWebhookUrl", "Discord Webhook URL", "Discord Webhook URL"),
                    BuildNotificationField(locale, "discordWebhookUsername", "Discord 显示名称", "Discord Username"),
                    BuildNotificationField(locale, "discordWebhookAvatarUrl", "Discord 头像 URL", "Discord Avatar URL"),
                    BuildNotificationField(locale, "discordWebhookImageEncoder", "Discord 图片编码", "Discord Image Encoder")
                },
                toolActions = new object[]
                {
                    BuildNotificationTestAction(locale, "discord", "测试 Discord 通知", "Test Discord")
                }
            },
            new
            {
                id = "notification-serverchan",
                type = "settings",
                title = Localize(locale, "ServerChan", "ServerChan"),
                getEndpoint = endpoint,
                postEndpoint = endpoint,
                fields = new object[]
                {
                    BuildNotificationField(locale, "serverChanNotificationEnabled", "启用 ServerChan", "Enable ServerChan", "bool"),
                    BuildNotificationField(locale, "serverChanSendKey", "ServerChan SendKey", "ServerChan SendKey")
                },
                toolActions = new object[]
                {
                    BuildNotificationTestAction(locale, "serverchan", "测试 ServerChan", "Test ServerChan")
                }
            }
        ];
    }

    private static object BuildNotificationField(string locale, string key, string zhLabel, string enLabel, string type = "text")
    {
        return new
        {
            key,
            label = Localize(locale, zhLabel, enLabel),
            type
        };
    }

    private static object BuildNotificationTestAction(string locale, string channel, string zhLabel, string enLabel)
    {
        return new
        {
            label = Localize(locale, zhLabel, enLabel),
            method = "POST",
            endpoint = "/api/notification/test",
            payload = new { channel },
            style = "secondary",
            successMessage = Localize(locale, "测试通知已发送", "Test notification sent")
        };
    }

    private static List<object> BuildTaskSections(string locale)
    {
        return
        [
            new
            {
                id = "task-auto-gi",
                type = "task",
                title = Localize(locale, "自动七圣召唤", "Auto TCG"),
                runTask = "auto_gi",
                getEndpoint = "/api/settings/auto-gi",
                postEndpoint = "/api/settings/auto-gi",
                optionMap = new Dictionary<string, string> { ["strategyName"] = "/api/strategies/tcg" }
            },
            new
            {
                id = "task-auto-wood",
                type = "task",
                title = Localize(locale, "自动伐木", "Auto Wood"),
                runTask = "auto_wood",
                getEndpoint = "/api/settings/auto-wood",
                postEndpoint = "/api/settings/auto-wood"
            },
            new
            {
                id = "task-auto-fight",
                type = "task",
                title = Localize(locale, "自动战斗", "Auto Combat"),
                runTask = "auto_fight",
                getEndpoint = "/api/settings/auto-fight",
                postEndpoint = "/api/settings/auto-fight",
                optionMap = new Dictionary<string, string>
                {
                    ["strategyName"] = "/api/strategies/auto-fight",
                    ["onlyPickEliteDropsMode"] = "/api/options/elite-drop-mode"
                }
            },
            new
            {
                id = "task-auto-domain",
                type = "task",
                title = Localize(locale, "自动秘境", "Auto Domain"),
                runTask = "auto_domain",
                getEndpoint = "/api/settings/auto-domain",
                postEndpoint = "/api/settings/auto-domain",
                optionMap = new Dictionary<string, string>
                {
                    ["strategyName"] = "/api/strategies/auto-fight",
                    ["domainName"] = "/api/options/domain-names",
                    ["sundaySelectedValue"] = "/api/options/sunday-values"
                }
            },
            new
            {
                id = "task-auto-stygian",
                type = "task",
                title = Localize(locale, "自动幽境危战", "Auto Stygian"),
                runTask = "auto_stygian",
                getEndpoint = "/api/settings/auto-stygian",
                postEndpoint = "/api/settings/auto-stygian",
                optionMap = new Dictionary<string, string> { ["strategyName"] = "/api/strategies/auto-fight" }
            },
            new
            {
                id = "task-auto-fishing",
                type = "task",
                title = Localize(locale, "自动钓鱼", "Auto Fishing"),
                runTask = "auto_fishing",
                getEndpoint = "/api/settings/auto-fishing",
                postEndpoint = "/api/settings/auto-fishing",
                optionMap = new Dictionary<string, string> { ["fishingTimePolicy"] = "/api/options/fishing-time-policy" }
            },
            new
            {
                id = "task-auto-music",
                type = "task",
                title = Localize(locale, "自动音游", "Auto Music"),
                runTask = "auto_music",
                getEndpoint = "/api/settings/auto-music",
                postEndpoint = "/api/settings/auto-music"
            },
            new
            {
                id = "task-auto-artifact",
                type = "task",
                title = Localize(locale, "自动分解圣遗物", "Auto Artifact Salvage"),
                runTask = "auto_artifact_salvage",
                getEndpoint = "/api/settings/auto-artifact",
                postEndpoint = "/api/settings/auto-artifact",
                optionMap = new Dictionary<string, string> { ["recognitionFailurePolicy"] = "/api/options/recognition-failure-policy" }
            },
            new
            {
                id = "task-grid-icons",
                type = "task",
                title = Localize(locale, "截取物品图标", "Capture Grid Icons"),
                runTask = "get_grid_icons",
                getEndpoint = "/api/settings/grid-icons",
                postEndpoint = "/api/settings/grid-icons",
                optionMap = new Dictionary<string, string> { ["gridName"] = "/api/options/grid-names" },
                extraActions = new object[]
                {
                    new { label = Localize(locale, "识别精度测试", "Grid Accuracy Test"), method = "POST", endpoint = "/api/tasks/run", payload = new { task = "grid_icons_accuracy", @params = new { } } }
                }
            },
            new
            {
                id = "task-auto-redeem",
                type = "task",
                title = Localize(locale, "自动使用兑换码", "Auto Redeem Code"),
                runTask = "auto_redeem_code",
                getEndpoint = (string?)null,
                postEndpoint = (string?)null,
                customPayload = new object[]
                {
                    new { key = "codes", label = Localize(locale, "兑换码（每行一个）", "Redeem Codes (one per line)"), type = "textarea", required = true }
                }
            },
            new
            {
                id = "task-auto-leyline",
                type = "task",
                title = Localize(locale, "自动地脉花", "Auto Ley Line"),
                runTask = "auto_leyline",
                getEndpoint = "/api/settings/auto-leyline",
                postEndpoint = "/api/settings/auto-leyline",
                optionMap = new Dictionary<string, string>
                {
                    ["leyLineOutcropType"] = "/api/options/leyline-types",
                    ["country"] = "/api/options/leyline-countries"
                }
            }
        ];
    }

    private List<object> BuildAutoDiscoveredTaskSections(IEnumerable<object> existingSections, string locale)
    {
        var knownSettingsEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in existingSections)
        {
            var endpoint = TryReadStringProperty(section, "getEndpoint");
            if (IsSettingsEndpoint(endpoint))
            {
                knownSettingsEndpoints.Add(endpoint!);
            }
        }

        var getSettingsRoutes = _getRoutes.Keys
            .Concat(LegacyGetRoutePaths)
            .Where(IsSettingsEndpoint)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var postSettingsRouteSet = _postRoutes.Keys
            .Concat(LegacyPostRoutePaths)
            .Where(IsSettingsEndpoint)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var discovered = new List<object>();
        foreach (var endpoint in getSettingsRoutes)
        {
            if (knownSettingsEndpoints.Contains(endpoint) ||
                !postSettingsRouteSet.Contains(endpoint) ||
                NonTaskSettingsEndpoints.Contains(endpoint))
            {
                continue;
            }

            var slug = endpoint.Substring("/api/settings/".Length).Trim();
            if (string.IsNullOrWhiteSpace(slug))
            {
                continue;
            }

            var runTask = slug.StartsWith("auto-", StringComparison.OrdinalIgnoreCase)
                ? slug.Replace('-', '_')
                : null;
            discovered.Add(new
            {
                id = $"task-discovered-{NormalizeSchemaId(slug)}",
                type = "task",
                title = string.Equals(locale, "en", StringComparison.OrdinalIgnoreCase)
                    ? $"{ToFriendlyRouteLabel(slug, locale)} (Auto Discovered)"
                    : $"{ToFriendlyRouteLabel(slug, locale)}（自动发现）",
                runTask,
                getEndpoint = endpoint,
                postEndpoint = endpoint
            });
        }

        return discovered;
    }

    private static bool IsSettingsEndpoint(string? endpoint)
    {
        return !string.IsNullOrWhiteSpace(endpoint) &&
               endpoint.StartsWith("/api/settings/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryReadStringProperty(object source, string name)
    {
        var prop = source.GetType().GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
        var value = prop?.GetValue(source);
        return value as string;
    }

    private static string NormalizeSchemaId(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "item";
        }

        var sb = new StringBuilder(source.Length + 8);
        var previousDash = false;
        foreach (var ch in source)
        {
            var mapped = char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-';
            if (mapped == '-')
            {
                if (previousDash)
                {
                    continue;
                }

                previousDash = true;
                sb.Append(mapped);
                continue;
            }

            previousDash = false;
            sb.Append(mapped);
        }

        var normalized = sb.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "item" : normalized;
    }

    private static string ToFriendlyRouteLabel(string slug, string locale)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return string.Equals(locale, "en", StringComparison.OrdinalIgnoreCase) ? "Task Settings" : "任务设置";
        }

        var words = slug
            .Replace('-', ' ')
            .Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return string.Equals(locale, "en", StringComparison.OrdinalIgnoreCase) ? "Task Settings" : "任务设置";
        }

        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            words[i] = word.Length <= 1
                ? word.ToUpperInvariant()
                : char.ToUpperInvariant(word[0]) + word.Substring(1).ToLowerInvariant();
        }

        return string.Join(" ", words);
    }

    private List<object> BuildDynamicConfigSections()
    {
        var descriptors = BuildConfigFieldDescriptors(_configService.Get());
        var grouped = descriptors
            .GroupBy(x => x.Group)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sections = new List<object>(grouped.Count);
        foreach (var group in grouped)
        {
            sections.Add(new
            {
                id = $"cfg-{group.Key.ToLowerInvariant()}",
                type = "config_group",
                title = ToFriendlyLabel(group.Key),
                fields = group
                    .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new
                    {
                        path = x.Path,
                        key = x.Key,
                        label = x.Label,
                        valueType = x.ValueType,
                        value = x.Value,
                        options = x.Options
                    })
                    .ToList()
            });
        }

        return sections;
    }

    private List<ConfigFieldDescriptor> BuildConfigFieldDescriptors(AllConfig config)
    {
        var list = new List<ConfigFieldDescriptor>(512);
        CollectConfigFieldDescriptors(config, string.Empty, list, 0, new HashSet<Type>());
        return list;
    }

    private void CollectConfigFieldDescriptors(
        object? node,
        string currentPath,
        List<ConfigFieldDescriptor> output,
        int depth,
        HashSet<Type> visitingTypes)
    {
        if (node == null || depth > 8)
        {
            return;
        }

        var type = node.GetType();
        if (!visitingTypes.Add(type))
        {
            return;
        }

        try
        {
            foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            {
                if (prop.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                if (!prop.CanRead)
                {
                    continue;
                }

                var propType = prop.PropertyType;
                if (IsUnsupportedDescriptorType(propType))
                {
                    continue;
                }

                var path = string.IsNullOrEmpty(currentPath) ? prop.Name : $"{currentPath}.{prop.Name}";
                if (!TryNormalizeConfigPath(path, out var normalizedPath, out _))
                {
                    continue;
                }

                if (!IsConfigPathAllowedForRead(normalizedPath, out _))
                {
                    continue;
                }

                object? value;
                try
                {
                    value = prop.GetValue(node);
                }
                catch
                {
                    continue;
                }

                if (TryBuildFieldDescriptor(normalizedPath, prop.Name, propType, value, out var descriptor))
                {
                    output.Add(descriptor);
                    continue;
                }

                if (value == null)
                {
                    continue;
                }

                var underlying = Nullable.GetUnderlyingType(propType) ?? propType;
                if (underlying == typeof(object))
                {
                    underlying = value.GetType();
                }

                if (CanTraverseConfigNode(underlying))
                {
                    CollectConfigFieldDescriptors(value, normalizedPath, output, depth + 1, visitingTypes);
                }
            }
        }
        finally
        {
            visitingTypes.Remove(type);
        }
    }

    private static bool TryBuildFieldDescriptor(
        string path,
        string key,
        Type propType,
        object? value,
        out ConfigFieldDescriptor descriptor)
    {
        descriptor = default;
        var type = Nullable.GetUnderlyingType(propType) ?? propType;
        var group = path.Split('.')[0];
        if (type == typeof(bool))
        {
            descriptor = new ConfigFieldDescriptor(group, path, key, ToFriendlyLabel(key), "bool", value is bool b && b, null);
            return true;
        }

        if (type == typeof(string))
        {
            descriptor = new ConfigFieldDescriptor(group, path, key, ToFriendlyLabel(key), "text", value?.ToString() ?? string.Empty, null);
            return true;
        }

        if (type.IsEnum)
        {
            var selected = value?.ToString() ?? string.Empty;
            descriptor = new ConfigFieldDescriptor(group, path, key, ToFriendlyLabel(key), "enum", selected, Enum.GetNames(type));
            return true;
        }

        if (IsNumeric(type))
        {
            descriptor = new ConfigFieldDescriptor(group, path, key, ToFriendlyLabel(key), "number", value, null);
            return true;
        }

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string))
        {
            var json = SerializeCollectionValueForUi(value);
            descriptor = new ConfigFieldDescriptor(group, path, key, ToFriendlyLabel(key), "json", json, null);
            return true;
        }

        return false;
    }

    private static bool CanTraverseConfigNode(Type type)
    {
        if (!type.IsClass || type == typeof(string))
        {
            return false;
        }

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
        {
            return false;
        }

        return !IsUnsupportedDescriptorType(type);
    }

    private static bool IsUnsupportedDescriptorType(Type type)
    {
        var normalized = Nullable.GetUnderlyingType(type) ?? type;
        if (normalized == typeof(Type) ||
            normalized == typeof(IntPtr) ||
            normalized == typeof(UIntPtr) ||
            normalized.IsPointer ||
            normalized.IsByRef ||
            typeof(Delegate).IsAssignableFrom(normalized) ||
            typeof(MemberInfo).IsAssignableFrom(normalized) ||
            typeof(Module).IsAssignableFrom(normalized) ||
            typeof(Assembly).IsAssignableFrom(normalized))
        {
            return true;
        }

        if (normalized.IsArray)
        {
            var element = normalized.GetElementType();
            return element != null && IsUnsupportedDescriptorType(element);
        }

        if (normalized.IsGenericType)
        {
            foreach (var arg in normalized.GetGenericArguments())
            {
                if (IsUnsupportedDescriptorType(arg))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string SerializeCollectionValueForUi(object? value)
    {
        if (value == null)
        {
            return "null";
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        try
        {
            return JsonSerializer.Serialize(value, options);
        }
        catch
        {
            if (value is not System.Collections.IEnumerable enumerable)
            {
                return JsonSerializer.Serialize(value.ToString() ?? string.Empty, options);
            }

            var items = new List<string>();
            var count = 0;
            foreach (var item in enumerable)
            {
                if (count++ >= 256)
                {
                    items.Add("...(truncated)");
                    break;
                }

                items.Add(FormatCollectionItemForUi(item));
            }

            return JsonSerializer.Serialize(items, options);
        }
    }

    private static string FormatCollectionItemForUi(object? item)
    {
        if (item == null)
        {
            return "null";
        }

        if (item is Type runtimeType)
        {
            return runtimeType.FullName ?? runtimeType.Name;
        }

        if (item is MemberInfo memberInfo)
        {
            return $"{memberInfo.MemberType}: {memberInfo.Name}";
        }

        if (item is Delegate callback)
        {
            return $"Delegate: {callback.Method.Name}";
        }

        if (item is string text)
        {
            return text;
        }

        if (item is IFormattable formattable)
        {
            return formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        try
        {
            return JsonSerializer.Serialize(item, JsonOptions);
        }
        catch
        {
            return item.ToString() ?? string.Empty;
        }
    }

    private static bool IsNumeric(Type type)
    {
        return type == typeof(byte) ||
               type == typeof(sbyte) ||
               type == typeof(short) ||
               type == typeof(ushort) ||
               type == typeof(int) ||
               type == typeof(uint) ||
               type == typeof(long) ||
               type == typeof(ulong) ||
               type == typeof(float) ||
               type == typeof(double) ||
               type == typeof(decimal);
    }

    private static string ToFriendlyLabel(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(source.Length + 8);
        for (var i = 0; i < source.Length; i++)
        {
            var ch = source[i];
            if (i > 0 && char.IsUpper(ch) && (char.IsLower(source[i - 1]) || char.IsDigit(source[i - 1])))
            {
                sb.Append(' ');
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private async Task HandleJsLibraryAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var items = new List<object>();
        try
        {
            var projects = BetterGenshinImpact.ViewModel.Pages.ScriptControlViewModel.LoadAllJsScriptProjects();
            foreach (var project in projects)
            {
                items.Add(new
                {
                    name = project.Manifest.Name,
                    folder = project.FolderName,
                    version = project.Manifest.Version,
                    description = project.Manifest.ShortDescription
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取 JS 脚本库失败");
        }

        await WriteJsonAsync(response, items, ct);
    }

    private async Task HandleJsRunAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var payload = await ReadJsonAsync<LibraryItemRequest>(request, ct);
        var folder = payload?.Folder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Missing folder" }, ct);
            return;
        }

        var root = Path.GetFullPath(Global.ScriptPath());
        var candidate = Path.GetFullPath(Path.Combine(root, folder));
        if (!IsSubPathOf(root, candidate))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Invalid folder" }, ct);
            return;
        }

        var normalizedFolder = Path.GetRelativePath(root, candidate);
        if (string.IsNullOrWhiteSpace(normalizedFolder) || string.Equals(normalizedFolder, ".", StringComparison.Ordinal))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Invalid folder" }, ct);
            return;
        }

        try
        {
            var project = new BetterGenshinImpact.Core.Script.Project.ScriptProject(normalizedFolder);
            await _scriptService.RunMulti([new ScriptGroupProject(project)]);
            await WriteJsonAsync(response, new { ok = true }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "执行 JS 脚本失败");
            await WriteJsonAsync(response, new { ok = false, error = ex.Message }, ct);
        }
    }

    private async Task HandleJsDeleteAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var payload = await ReadJsonAsync<LibraryItemRequest>(request, ct);
        var folder = payload?.Folder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Missing folder" }, ct);
            return;
        }

        var root = Path.GetFullPath(Global.ScriptPath());
        var candidate = Path.GetFullPath(Path.Combine(root, folder));
        if (!IsSubPathOf(root, candidate))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Invalid folder" }, ct);
            return;
        }

        if (Directory.Exists(candidate))
        {
            Directory.Delete(candidate, true);
        }

        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandlePathingLibraryAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var root = BetterGenshinImpact.ViewModel.Pages.MapPathingViewModel.PathJsonPath;
        Directory.CreateDirectory(root);
        var files = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
            .Select(file =>
            {
                var info = new FileInfo(file);
                var folder = Path.GetRelativePath(root, info.DirectoryName ?? root);
                if (folder == "." || folder == "\\")
                {
                    folder = string.Empty;
                }

                var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
                return new
                {
                    name = info.Name,
                    folder,
                    relativePath
                };
            })
            .OrderBy(x => x.folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        await WriteJsonAsync(response, files, ct);
    }

    private async Task HandlePathingRunAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var payload = await ReadJsonAsync<LibraryItemRequest>(request, ct);
        var relative = payload?.Path;
        if (string.IsNullOrWhiteSpace(relative))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Missing path" }, ct);
            return;
        }

        var root = Path.GetFullPath(BetterGenshinImpact.ViewModel.Pages.MapPathingViewModel.PathJsonPath);
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSubPathOf(root, candidate) || !File.Exists(candidate))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Invalid path" }, ct);
            return;
        }

        var info = new FileInfo(candidate);
        var project = ScriptGroupProject.BuildPathingProject(info.Name, info.DirectoryName ?? string.Empty);
        await _scriptService.RunMulti([project]);
        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandlePathingDeleteAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var payload = await ReadJsonAsync<LibraryItemRequest>(request, ct);
        var relative = payload?.Path;
        if (string.IsNullOrWhiteSpace(relative))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Missing path" }, ct);
            return;
        }

        var root = Path.GetFullPath(BetterGenshinImpact.ViewModel.Pages.MapPathingViewModel.PathJsonPath);
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSubPathOf(root, candidate))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Invalid path" }, ct);
            return;
        }

        if (File.Exists(candidate))
        {
            File.Delete(candidate);
        }
        else if (Directory.Exists(candidate))
        {
            Directory.Delete(candidate, true);
        }

        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandleKeyMouseLibraryAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var root = Global.Absolute(@"User\KeyMouseScript");
        Directory.CreateDirectory(root);
        var files = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
            .Select(file =>
            {
                var info = new FileInfo(file);
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                return new
                {
                    name = info.Name,
                    relativePath = relative,
                    createTime = info.CreationTime
                };
            })
            .OrderByDescending(x => x.createTime)
            .ToList();
        await WriteJsonAsync(response, new
        {
            status = GlobalKeyMouseRecord.Instance.Status.ToString(),
            items = files
        }, ct);
    }

    private static bool TryResolveLibraryRelativePath(LibraryItemRequest? payload, out string relativePath)
    {
        relativePath = payload?.Path ?? payload?.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        relativePath = relativePath
            .Trim()
            .TrimStart('/', '\\')
            .Replace('\\', '/');
        return !string.IsNullOrWhiteSpace(relativePath);
    }

    private static bool TryNormalizeRelativePathUnderRoot(string root, string rawRelativePath, out string normalizedRelativePath)
    {
        normalizedRelativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(rawRelativePath))
        {
            return false;
        }

        var fullRoot = Path.GetFullPath(root);
        var relativeInput = rawRelativePath.Trim()
            .TrimStart('/', '\\')
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        if (string.IsNullOrWhiteSpace(relativeInput))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativeInput));
        if (!IsSubPathOf(fullRoot, candidate))
        {
            return false;
        }

        var relative = Path.GetRelativePath(fullRoot, candidate).Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relative) || string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return false;
        }

        normalizedRelativePath = relative;
        return true;
    }

    private async Task HandleKeyMousePlayAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var payload = await ReadJsonAsync<LibraryItemRequest>(request, ct);
        if (!TryResolveLibraryRelativePath(payload, out var relative))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Missing path or name" }, ct);
            return;
        }

        var root = Path.GetFullPath(Global.Absolute(@"User\KeyMouseScript"));
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSubPathOf(root, candidate) || !File.Exists(candidate))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Invalid path" }, ct);
            return;
        }

        var json = await File.ReadAllTextAsync(candidate, ct);
        StartBackgroundTask(() => KeyMouseMacroPlayer.PlayMacro(json, CancellationContext.Instance.Cts.Token));
        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandleKeyMouseDeleteAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var payload = await ReadJsonAsync<LibraryItemRequest>(request, ct);
        if (!TryResolveLibraryRelativePath(payload, out var relative))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Missing path or name" }, ct);
            return;
        }

        var root = Path.GetFullPath(Global.Absolute(@"User\KeyMouseScript"));
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSubPathOf(root, candidate))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Invalid path" }, ct);
            return;
        }

        if (File.Exists(candidate))
        {
            File.Delete(candidate);
        }

        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandleKeyMouseRenameAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var payload = await ReadJsonAsync<LibraryItemRequest>(request, ct);
        var newName = payload?.NewName;
        if (!TryResolveLibraryRelativePath(payload, out var relative) || string.IsNullOrWhiteSpace(newName))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Missing path/name or newName" }, ct);
            return;
        }

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Invalid newName" }, ct);
            return;
        }

        var root = Path.GetFullPath(Global.Absolute(@"User\KeyMouseScript"));
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSubPathOf(root, candidate) || !File.Exists(candidate))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Invalid path" }, ct);
            return;
        }

        var dest = Path.Combine(Path.GetDirectoryName(candidate) ?? root, newName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? newName : $"{newName}.json");
        var fullDest = Path.GetFullPath(dest);
        if (!IsSubPathOf(root, fullDest))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Invalid destination" }, ct);
            return;
        }

        File.Move(candidate, fullDest, overwrite: false);
        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandleKeyMouseRecordStartAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        StartBackgroundTask(() => GlobalKeyMouseRecord.Instance.StartRecord());
        await WriteJsonAsync(response, new { ok = true }, ct);
    }

    private async Task HandleKeyMouseRecordStopAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        string? fileName = null;
        if (GlobalKeyMouseRecord.Instance.Status == KeyMouseRecorderStatus.Recording)
        {
            var macro = GlobalKeyMouseRecord.Instance.StopRecord();
            var root = Global.Absolute(@"User\KeyMouseScript");
            Directory.CreateDirectory(root);
            fileName = $"BetterGI_GCM_{DateTime.Now:yyyyMMddHHmmssffff}.json";
            await File.WriteAllTextAsync(Path.Combine(root, fileName), macro, ct);
        }

        await WriteJsonAsync(response, new { ok = true, fileName }, ct);
    }

    private async Task HandleAiChatAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        var payload = await ReadJsonAsync<AiChatRequest>(request, ct);
        if (payload == null || string.IsNullOrWhiteSpace(payload.Message))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(response, new { error = "Message is required" }, ct);
            return;
        }

        try
        {
            var history = payload.History?
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Content))
                .Select(x =>
                {
                    var role = string.IsNullOrWhiteSpace(x.Role)
                        ? x.IsAssistant
                            ? "assistant"
                            : x.IsSystem
                                ? "system"
                                : x.IsMcp
                                    ? "mcp"
                                    : "user"
                        : x.Role!;
                    return new WebAiBridgeService.ChatTurn(role, x.Content ?? string.Empty);
                })
                .ToList();

            var result = await _webAiBridgeService.ChatAsync(payload.Message.Trim(), history, ct).ConfigureAwait(false);
            await WriteJsonAsync(response, new
            {
                ok = true,
                reply = result.Reply,
                status = result.Status,
                messages = result.Messages
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AI 对话请求失败");
            await WriteJsonAsync(response, new { ok = false, error = ex.Message }, ct);
        }
    }

    private readonly record struct ConfigFieldDescriptor(
        string Group,
        string Path,
        string Key,
        string Label,
        string ValueType,
        object? Value,
        string[]? Options);

    private static readonly Lazy<string> WebLoginHtmlV2 = new(LoadWebLoginHtmlV2);
    private static readonly Lazy<string> WebAutomationHtmlV2 = new(LoadWebAutomationHtmlV2);

    private static string LoadWebLoginHtmlV2()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var candidate = Path.Combine(baseDir, "Service", "Remote", "WebRemoteLogin.html");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate, Encoding.UTF8);
            }

            candidate = Path.Combine(baseDir, "WebRemoteLogin.html");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate, Encoding.UTF8);
            }
        }
        catch
        {
        }

        return WebLoginHtml;
    }

    private static string LoadWebIndexHtmlV2()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var candidate = Path.Combine(baseDir, "Service", "Remote", "WebRemoteIndex.html");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate, Encoding.UTF8);
            }

            candidate = Path.Combine(baseDir, "WebRemoteIndex.html");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate, Encoding.UTF8);
            }
        }
        catch
        {
        }

        return WebIndexHtml;
    }

    private static string LoadWebAutomationHtmlV2()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var candidate = Path.Combine(baseDir, "Service", "Remote", "WebRemoteAutomation.html");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate, Encoding.UTF8);
            }

            candidate = Path.Combine(baseDir, "WebRemoteAutomation.html");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate, Encoding.UTF8);
            }
        }
        catch
        {
        }

        return WebAutomationHtml;
    }

    private const string WebLoginHtml = """
<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>BetterGI Web 登录</title>
</head>
<body>
  <h1>BetterGI Web</h1>
  <p>登录页资源加载失败，请确认 WebRemoteLogin.html 文件存在。</p>
</body>
</html>
""";

    private const string WebIndexHtml = """
<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>BetterGI Web 远程控制</title>
  <style>
    :root { color-scheme: light; }
    body { font-family: "Segoe UI", "Microsoft YaHei", sans-serif; margin: 0; background: #0f1115; color: #e8e9ee; }
    header { padding: 20px 28px; background: #171a22; border-bottom: 1px solid #2a2f3a; }
    h1 { font-size: 18px; margin: 0; }
    h2 { margin: 0 0 12px 0; font-size: 16px; }
    main { padding: 20px 28px; display: grid; gap: 16px; }
    .card { background: #171a22; border: 1px solid #2a2f3a; border-radius: 12px; padding: 16px; }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 10px; }
    .toggle { display: flex; align-items: center; gap: 8px; background: #202532; border-radius: 8px; padding: 10px 12px; }
    .toggle input { width: 16px; height: 16px; }
    #logs { background: #0c0f16; border-radius: 8px; padding: 12px; height: 240px; overflow: auto; font-family: "Consolas", monospace; font-size: 12px; }
    #screen { width: 100%; max-width: 960px; border-radius: 8px; border: 1px solid #2a2f3a; background: #0c0f16; }
    .row { display: flex; flex-wrap: wrap; gap: 12px; }
    .btn { background: #2b3242; color: #e8e9ee; border: 1px solid #394056; border-radius: 8px; padding: 8px 12px; cursor: pointer; }
    .btn:hover { background: #343c50; }
    textarea { width: 100%; min-height: 200px; border-radius: 8px; border: 1px solid #2a2f3a; background: #0c0f16; color: #e8e9ee; padding: 10px; box-sizing: border-box; }
    input, select { width: 100%; border-radius: 8px; border: 1px solid #2a2f3a; background: #0c0f16; color: #e8e9ee; padding: 8px 10px; box-sizing: border-box; }
    .muted { color: #9aa2b1; font-size: 12px; }
    .task-grid { display: grid; gap: 12px; }
    .task { background: #202532; border: 1px solid #2a2f3a; border-radius: 10px; padding: 12px; }
    .task-title { font-weight: 600; margin-bottom: 8px; }
    .task-row { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
    .field { flex: 1 1 160px; min-width: 160px; }
    .small { font-size: 12px; color: #9aa2b1; }
  </style>
</head>
<body>
  <header>
    <h1>BetterGI Web 远程控制</h1>
    <div class="muted" id="status">状态加载中...</div>
    <div class="muted" id="network">可访问地址加载中...</div>
    <div class="muted" id="warning"></div>
  </header>
  <main>
    <section class="card">
      <h2>基础功能</h2>
      <div class="grid">
        <label class="toggle"><input type="checkbox" id="autoPick"> 自动拾取</label>
        <label class="toggle"><input type="checkbox" id="autoSkip"> 自动剧情</label>
        <label class="toggle"><input type="checkbox" id="autoFishing"> 自动钓鱼</label>
        <label class="toggle"><input type="checkbox" id="autoCook"> 自动烹饪</label>
        <label class="toggle"><input type="checkbox" id="autoEat"> 自动吃药</label>
        <label class="toggle"><input type="checkbox" id="quickTeleport"> 快速传送</label>
        <label class="toggle"><input type="checkbox" id="mapMask"> 地图遮罩</label>
      </div>
    </section>
    <section class="card">
      <h2>运行控制</h2>
      <div class="row">
        <button class="btn" id="startGame">启动游戏/截图器</button>
        <button class="btn" id="stopGame">停止截图器</button>
        <button class="btn" id="pauseTasks">暂停任务</button>
        <button class="btn" id="resumeTasks">继续任务</button>
        <button class="btn" id="cancelTasks">取消任务</button>
      </div>
      <div class="muted">启动/停止截图器会影响任务与遮罩。</div>
    </section>
    <section class="card">
      <h2>全自动任务</h2>
      <div class="task-grid">
        <div class="task">
          <div class="task-title">自动七圣召唤</div>
          <div class="task-row">
            <input class="field" id="tcgStrategy" list="tcgStrategies" placeholder="策略名称">
            <input class="field" id="tcgDelay" type="number" min="0" placeholder="延时(ms)">
            <button class="btn" id="runTcg">启动</button>
          </div>
        </div>
        <div class="task">
          <div class="task-title">自动伐木</div>
          <div class="task-row">
            <input class="field" id="woodRound" type="number" min="1" placeholder="循环次数">
            <input class="field" id="woodDaily" type="number" min="1" placeholder="每日上限">
            <label class="toggle"><input type="checkbox" id="woodWonderland"> 千星奇域刷新</label>
            <button class="btn" id="runWood">启动</button>
          </div>
        </div>
        <div class="task">
          <div class="task-title">自动战斗</div>
          <div class="task-row">
            <input class="field" id="fightStrategy" list="fightStrategies" placeholder="战斗策略">
            <input class="field" id="fightTeam" placeholder="队伍名称(可选)">
            <button class="btn" id="runFight">启动</button>
          </div>
        </div>
        <div class="task">
          <div class="task-title">自动秘境</div>
          <div class="task-row">
            <input class="field" id="domainStrategy" list="fightStrategies" placeholder="战斗策略">
            <input class="field" id="domainParty" placeholder="队伍名称">
            <input class="field" id="domainName" placeholder="副本名称">
            <input class="field" id="domainRound" type="number" min="1" placeholder="次数">
            <button class="btn" id="runDomain">启动</button>
          </div>
        </div>
        <div class="task">
          <div class="task-title">自动幽境危战</div>
          <div class="task-row">
            <input class="field" id="stygianStrategy" list="fightStrategies" placeholder="战斗策略">
            <input class="field" id="stygianBossNum" type="number" min="1" max="3" placeholder="Boss序号">
            <input class="field" id="stygianTeam" placeholder="队伍名称">
            <button class="btn" id="runStygian">启动</button>
          </div>
        </div>
        <div class="task">
          <div class="task-title">自动钓鱼</div>
          <div class="task-row">
            <label class="toggle"><input type="checkbox" id="fishingScreenshot"> 截图保存</label>
            <button class="btn" id="runFishing">启动</button>
          </div>
        </div>
        <div class="task">
          <div class="task-title">自动音游</div>
          <div class="task-row">
            <button class="btn" id="runMusic">启动</button>
            <button class="btn" id="runAlbum">专辑模式</button>
          </div>
        </div>
        <div class="task">
          <div class="task-title">自动分解圣遗物</div>
          <div class="task-row">
            <button class="btn" id="runArtifact">启动</button>
          </div>
        </div>
        <div class="task">
          <div class="task-title">获取物品图标</div>
          <div class="task-row">
            <input class="field" id="gridName" placeholder="界面名称">
            <label class="toggle"><input type="checkbox" id="gridStarSuffix"> 星级后缀</label>
            <button class="btn" id="runGridIcons">启动</button>
            <button class="btn" id="runGridAccuracy">准确率测试</button>
          </div>
        </div>
        <div class="task">
          <div class="task-title">自动使用兑换码</div>
          <div class="task-row">
            <textarea id="redeemCodes" placeholder="每行一个兑换码"></textarea>
            <button class="btn" id="runRedeem">启动</button>
          </div>
        </div>
      </div>
      <datalist id="fightStrategies"></datalist>
      <datalist id="tcgStrategies"></datalist>
    </section>
    <section class="card">
      <h2>一条龙</h2>
      <div class="row">
        <select id="dragonConfigSelect" class="field"></select>
        <button class="btn" id="dragonRefresh">刷新配置</button>
        <button class="btn" id="dragonRun">一键执行</button>
        <button class="btn" id="dragonSave">保存配置</button>
      </div>
      <div class="grid" id="dragonTasks"></div>
      <textarea id="dragonConfigJson" placeholder="一条龙配置 JSON（可选）"></textarea>
      <div class="muted">任务开关会同步到配置；也可以直接编辑 JSON 后保存。</div>
    </section>
    <section class="card">
      <h2>调度器</h2>
      <div class="row">
        <button class="btn" id="refreshGroups">刷新列表</button>
        <button class="btn" id="runGroups">运行选中</button>
      </div>
      <div class="grid" id="scriptGroups"></div>
      <div class="row">
        <input class="field" id="newGroupName" placeholder="新配置组名称">
        <button class="btn" id="createGroup">新建</button>
      </div>
      <div class="row">
        <input class="field" id="renameGroupOld" placeholder="旧名称">
        <input class="field" id="renameGroupNew" placeholder="新名称">
        <button class="btn" id="renameGroup">重命名</button>
        <input class="field" id="deleteGroupName" placeholder="删除名称">
        <button class="btn" id="deleteGroup">删除</button>
      </div>
      <div class="row">
        <select id="groupSelect" class="field"></select>
        <button class="btn" id="loadGroupJson">载入 JSON</button>
        <button class="btn" id="saveGroupJson">保存 JSON</button>
      </div>
      <textarea id="groupJson" placeholder="脚本组 JSON"></textarea>
      <div class="muted">调度器完整编辑请在 JSON 中调整项目列表与配置。</div>
    </section>
    <section class="card">
      <h2>遮罩日志</h2>
      <div id="logs"></div>
    </section>
    <section class="card">
      <h2>屏幕画面</h2>
      <img id="screen" alt="screen">
      <div class="muted">如果未开启传输屏幕显示内容，将无法看到画面。</div>
    </section>
  </main>
  <script>
    const statusEl = document.getElementById("status");
    const networkEl = document.getElementById("network");
    const warningEl = document.getElementById("warning");
    const logEl = document.getElementById("logs");
    const screenEl = document.getElementById("screen");
    const startGameBtn = document.getElementById("startGame");
    const stopGameBtn = document.getElementById("stopGame");
    const pauseTasksBtn = document.getElementById("pauseTasks");
    const resumeTasksBtn = document.getElementById("resumeTasks");
    const cancelTasksBtn = document.getElementById("cancelTasks");
    const scriptGroupsEl = document.getElementById("scriptGroups");
    const refreshGroupsBtn = document.getElementById("refreshGroups");
    const runGroupsBtn = document.getElementById("runGroups");
    const dragonConfigSelect = document.getElementById("dragonConfigSelect");
    const dragonTasksEl = document.getElementById("dragonTasks");
    const dragonConfigJson = document.getElementById("dragonConfigJson");
    const groupSelect = document.getElementById("groupSelect");
    const groupJson = document.getElementById("groupJson");
    const toggles = {
      autoPick: document.getElementById("autoPick"),
      autoSkip: document.getElementById("autoSkip"),
      autoFishing: document.getElementById("autoFishing"),
      autoCook: document.getElementById("autoCook"),
      autoEat: document.getElementById("autoEat"),
      quickTeleport: document.getElementById("quickTeleport"),
      mapMask: document.getElementById("mapMask")
    };

    async function fetchJson(url, options) {
      const res = await fetch(url, options);
      if (!res.ok) throw new Error(res.statusText);
      return await res.json();
    }

    async function postJson(url, body) {
      return await fetchJson(url, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
    }

    async function refreshStatus() {
      try {
        const data = await fetchJson("/api/status");
        const thirdPartyEnabled = data.thirdPartyEnabled ?? data.lanEnabled;
        const thirdPartyActive = data.thirdPartyActive ?? data.lanActive;
        statusEl.textContent = `版本 ${data.version} | ${data.serverTime} | 游戏已启动: ${data.isInitialized} | 暂停: ${data.isSuspended} | 第三方访问(配置): ${thirdPartyEnabled} | 第三方访问(生效): ${thirdPartyActive} | 监听: ${data.listenPrefix || "-"}`;
        warningEl.textContent = data.warning ? `提示：${data.warning}` : "";
        return data;
      } catch (err) {
        statusEl.textContent = "状态获取失败";
        warningEl.textContent = "";
        return null;
      }
    }

    async function refreshNetwork() {
      try {
        const data = await fetchJson("/api/network");
        networkEl.textContent = data.addresses && data.addresses.length
          ? `可访问地址：${data.addresses.join(", ")}`
          : "未获取到可访问地址";
      } catch (err) {
        networkEl.textContent = "可访问地址获取失败";
      }
    }

    async function refreshConfig() {
      try {
        const data = await fetchJson("/api/config/basic");
        for (const key in toggles) {
          if (Object.hasOwn(data, key)) {
            toggles[key].checked = data[key];
          }
        }
      } catch (err) {
      }
    }

    async function patchConfig(key, value) {
      try {
        await postJson("/api/config/basic", { [key]: value });
      } catch (err) {
      }
    }

    Object.entries(toggles).forEach(([key, checkbox]) => {
      checkbox.addEventListener("change", () => patchConfig(key, checkbox.checked));
    });

    function appendLog(line) {
      const time = new Date(line.timestamp).toLocaleTimeString();
      const text = `[${time}] [${line.level}] ${line.message}`;
      logEl.textContent += text + "\n";
      logEl.scrollTop = logEl.scrollHeight;
    }

    function connectLogs() {
      const es = new EventSource("/api/logs/stream");
      es.addEventListener("log", (event) => {
        try {
          const line = JSON.parse(event.data);
          appendLog(line);
        } catch (err) {
        }
      });
    }

    function refreshScreen() {
      screenEl.src = `/api/screen?ts=${Date.now()}`;
    }

    async function refreshStrategies() {
      try {
        const fight = await fetchJson("/api/strategies/auto-fight");
        const tcg = await fetchJson("/api/strategies/tcg");
        const fightList = document.getElementById("fightStrategies");
        const tcgList = document.getElementById("tcgStrategies");
        fightList.innerHTML = "";
        tcgList.innerHTML = "";
        (fight || []).forEach(item => {
          const opt = document.createElement("option");
          opt.value = item;
          fightList.appendChild(opt);
        });
        (tcg || []).forEach(item => {
          const opt = document.createElement("option");
          opt.value = item;
          tcgList.appendChild(opt);
        });
      } catch (err) {
      }
    }

    async function runTask(task, params) {
      try {
        await postJson("/api/tasks/run", { task, params: params || {} });
      } catch (err) {
      }
    }

    async function refreshGroups() {
      if (!scriptGroupsEl) return;
      scriptGroupsEl.innerHTML = "";
      if (groupSelect) groupSelect.innerHTML = "";
      try {
        const data = await fetchJson("/api/scripts/groups");
        if (!Array.isArray(data) || data.length === 0) {
          scriptGroupsEl.innerHTML = '<div class="muted">暂无配置组</div>';
          return;
        }
        data.forEach(group => {
          const label = document.createElement("label");
          label.className = "toggle";
          const checkbox = document.createElement("input");
          checkbox.type = "checkbox";
          checkbox.value = group.name;
          const text = document.createElement("span");
          text.textContent = `${group.name} (${group.projectCount || 0})`;
          label.appendChild(checkbox);
          label.appendChild(text);
          scriptGroupsEl.appendChild(label);
          if (groupSelect) {
            const option = document.createElement("option");
            option.value = group.name;
            option.textContent = group.name;
            groupSelect.appendChild(option);
          }
        });
      } catch (err) {
        scriptGroupsEl.innerHTML = '<div class="muted">加载失败</div>';
      }
    }

    async function runSelectedGroups() {
      if (!scriptGroupsEl) return;
      const checks = scriptGroupsEl.querySelectorAll("input[type=checkbox]:checked");
      if (!checks.length) return;
      const names = Array.from(checks).map(el => el.value);
      try {
        await postJson("/api/scripts/run", { names });
      } catch (err) {
      }
    }

    let currentDragonConfig = null;

    async function refreshDragonConfigs() {
      try {
        const data = await fetchJson("/api/one-dragon/configs");
        if (dragonConfigSelect) {
          dragonConfigSelect.innerHTML = "";
          (data.configs || []).forEach(name => {
            const option = document.createElement("option");
            option.value = name;
            option.textContent = name;
            dragonConfigSelect.appendChild(option);
          });
          if (data.selected) {
            dragonConfigSelect.value = data.selected;
          }
        }
        await loadDragonConfig(dragonConfigSelect.value);
      } catch (err) {
      }
    }

    function renderDragonTasks(config) {
      if (!dragonTasksEl) return;
      dragonTasksEl.innerHTML = "";
      const list = (config && config.TaskEnabledList) ? config.TaskEnabledList : {};
      const keys = Object.keys(list || {});
      if (keys.length === 0) {
        dragonTasksEl.innerHTML = '<div class="muted">当前配置没有任务</div>';
        return;
      }
      keys.forEach(name => {
        const label = document.createElement("label");
        label.className = "toggle";
        const checkbox = document.createElement("input");
        checkbox.type = "checkbox";
        checkbox.checked = !!list[name];
        checkbox.addEventListener("change", () => {
          if (currentDragonConfig && currentDragonConfig.TaskEnabledList) {
            currentDragonConfig.TaskEnabledList[name] = checkbox.checked;
            dragonConfigJson.value = JSON.stringify(currentDragonConfig, null, 2);
          }
        });
        const text = document.createElement("span");
        text.textContent = name;
        label.appendChild(checkbox);
        label.appendChild(text);
        dragonTasksEl.appendChild(label);
      });
    }

    async function loadDragonConfig(name) {
      if (!name) return;
      try {
        const data = await fetch(`/api/one-dragon/config?name=${encodeURIComponent(name)}`);
        const jsonText = await data.text();
        const config = JSON.parse(jsonText);
        currentDragonConfig = config;
        if (dragonConfigJson) dragonConfigJson.value = JSON.stringify(config, null, 2);
        renderDragonTasks(config);
      } catch (err) {
      }
    }

    async function saveDragonConfig() {
      try {
        const text = dragonConfigJson.value.trim();
        if (!text) return;
        await fetch("/api/one-dragon/config", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: text
        });
        await refreshDragonConfigs();
      } catch (err) {
      }
    }

    async function runDragon() {
      try {
        const name = dragonConfigSelect.value;
        await postJson("/api/one-dragon/run", { name });
      } catch (err) {
      }
    }

    async function loadGroupJson() {
      if (!groupSelect || !groupSelect.value) return;
      try {
        const res = await fetch(`/api/scripts/group?name=${encodeURIComponent(groupSelect.value)}`);
        if (!res.ok) return;
        groupJson.value = await res.text();
      } catch (err) {
      }
    }

    async function saveGroupJson() {
      if (!groupJson.value.trim()) return;
      try {
        await postJson("/api/scripts/group/save", { json: groupJson.value });
        await refreshGroups();
      } catch (err) {
      }
    }

    async function createGroup() {
      const name = document.getElementById("newGroupName").value.trim();
      if (!name) return;
      await postJson("/api/scripts/group/create", { name });
      document.getElementById("newGroupName").value = "";
      await refreshGroups();
    }

    async function renameGroup() {
      const oldName = document.getElementById("renameGroupOld").value.trim();
      const newName = document.getElementById("renameGroupNew").value.trim();
      if (!oldName || !newName) return;
      await postJson("/api/scripts/group/rename", { oldName, newName });
      document.getElementById("renameGroupOld").value = "";
      document.getElementById("renameGroupNew").value = "";
      await refreshGroups();
    }

    async function deleteGroup() {
      const name = document.getElementById("deleteGroupName").value.trim();
      if (!name) return;
      await postJson("/api/scripts/group/delete", { name });
      document.getElementById("deleteGroupName").value = "";
      await refreshGroups();
    }

    startGameBtn.addEventListener("click", () => fetchJson("/api/game/start", { method: "POST" }));
    stopGameBtn.addEventListener("click", () => fetchJson("/api/game/stop", { method: "POST" }));
    pauseTasksBtn.addEventListener("click", () => fetchJson("/api/tasks/pause", { method: "POST" }));
    resumeTasksBtn.addEventListener("click", () => fetchJson("/api/tasks/resume", { method: "POST" }));
    cancelTasksBtn.addEventListener("click", () => fetchJson("/api/tasks/cancel", { method: "POST" }));
    refreshGroupsBtn.addEventListener("click", refreshGroups);
    runGroupsBtn.addEventListener("click", runSelectedGroups);
    document.getElementById("createGroup").addEventListener("click", createGroup);
    document.getElementById("renameGroup").addEventListener("click", renameGroup);
    document.getElementById("deleteGroup").addEventListener("click", deleteGroup);
    document.getElementById("loadGroupJson").addEventListener("click", loadGroupJson);
    document.getElementById("saveGroupJson").addEventListener("click", saveGroupJson);
    document.getElementById("dragonRefresh").addEventListener("click", refreshDragonConfigs);
    document.getElementById("dragonRun").addEventListener("click", runDragon);
    document.getElementById("dragonSave").addEventListener("click", saveDragonConfig);
    dragonConfigSelect.addEventListener("change", () => loadDragonConfig(dragonConfigSelect.value));

    document.getElementById("runTcg").addEventListener("click", () => {
      runTask("auto_gi", {
        strategyName: document.getElementById("tcgStrategy").value.trim(),
        sleepDelay: Number(document.getElementById("tcgDelay").value || 0)
      });
    });
    document.getElementById("runWood").addEventListener("click", () => {
      runTask("auto_wood", {
        roundNum: Number(document.getElementById("woodRound").value || 1),
        dailyMaxCount: Number(document.getElementById("woodDaily").value || 2000),
        useWonderlandRefresh: document.getElementById("woodWonderland").checked
      });
    });
    document.getElementById("runFight").addEventListener("click", () => {
      runTask("auto_fight", {
        strategyName: document.getElementById("fightStrategy").value.trim(),
        teamNames: document.getElementById("fightTeam").value.trim()
      });
    });
    document.getElementById("runDomain").addEventListener("click", () => {
      runTask("auto_domain", {
        strategyName: document.getElementById("domainStrategy").value.trim(),
        partyName: document.getElementById("domainParty").value.trim(),
        domainName: document.getElementById("domainName").value.trim(),
        roundNum: Number(document.getElementById("domainRound").value || 1)
      });
    });
    document.getElementById("runStygian").addEventListener("click", () => {
      runTask("auto_stygian", {
        strategyName: document.getElementById("stygianStrategy").value.trim(),
        bossNum: Number(document.getElementById("stygianBossNum").value || 1),
        fightTeamName: document.getElementById("stygianTeam").value.trim()
      });
    });
    document.getElementById("runFishing").addEventListener("click", () => {
      runTask("auto_fishing", { saveScreenshotOnKeyTick: document.getElementById("fishingScreenshot").checked });
    });
    document.getElementById("runMusic").addEventListener("click", () => runTask("auto_music"));
    document.getElementById("runAlbum").addEventListener("click", () => runTask("auto_album"));
    document.getElementById("runArtifact").addEventListener("click", () => runTask("auto_artifact_salvage"));
    document.getElementById("runGridIcons").addEventListener("click", async () => {
      await postJson("/api/config/path", { path: "GetGridIconsConfig.GridName", value: document.getElementById("gridName").value.trim() });
      await postJson("/api/config/path", { path: "GetGridIconsConfig.StarAsSuffix", value: document.getElementById("gridStarSuffix").checked });
      runTask("get_grid_icons");
    });
    document.getElementById("runGridAccuracy").addEventListener("click", () => runTask("grid_icons_accuracy"));
    document.getElementById("runRedeem").addEventListener("click", () => {
      const codes = document.getElementById("redeemCodes").value.split(/\r?\n/).map(x => x.trim()).filter(Boolean);
      runTask("auto_redeem_code", { codes });
    });

    refreshStatus();
    refreshNetwork();
    refreshConfig();
    refreshGroups();
    refreshDragonConfigs();
    refreshStrategies();
    connectLogs();
    refreshScreen();
    setInterval(async () => {
      await refreshStatus();
      refreshScreen();
    }, 5000);
    setInterval(refreshNetwork, 15000);
  </script>
</body>
</html>
""";

    private const string WebAutomationHtml = """
<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>BetterGI 调度与一条龙</title>
</head>
<body>
  <h1>BetterGI 调度与一条龙页面</h1>
  <p>页面资源加载失败，请确认 WebRemoteAutomation.html 文件存在。</p>
</body>
</html>
""";
}

