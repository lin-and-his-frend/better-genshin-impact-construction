using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
using BetterGenshinImpact.GameTask.AutoArtifactSalvage;
using BetterGenshinImpact.GameTask.AutoDomain;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFishing;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation;
using BetterGenshinImpact.GameTask.AutoMusicGame;
using BetterGenshinImpact.GameTask.AutoStygianOnslaught;
using BetterGenshinImpact.GameTask.AutoWood;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.GetGridIcons;
using BetterGenshinImpact.GameTask.UseRedeemCode;
using BetterGenshinImpact.Service.Interface;
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

    private readonly IConfigService _configService;
    private readonly ILogger<WebRemoteService> _logger;
    private readonly object _sync = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private WebRemoteConfig? _config;
    private int _currentPort;
    private bool _currentLanEnabled;
    private bool _currentLanActive;
    private string _currentPrefix = string.Empty;
    private string? _lastWarning;

    public WebRemoteService(IConfigService configService, ILogger<WebRemoteService> logger)
    {
        _configService = configService;
        _logger = logger;
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
        if (e.PropertyName is nameof(WebRemoteConfig.Enabled) or nameof(WebRemoteConfig.Port) or nameof(WebRemoteConfig.LanEnabled))
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
        if (!string.IsNullOrWhiteSpace(warning))
        {
            _logger.LogWarning(warning);
        }

        if (_config.LanEnabled)
        {
            _ = TryEnsureFirewallRuleAsync(_config.Port);
        }

        _logger.LogInformation("Web 远程控制已启动，端口 {Port}，局域网访问 {Lan}", _config.Port, _currentLanActive);
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
            prefixes.Add($"http://+:{port}/");
            prefixes.Add($"http://localhost:{port}/");
            prefixes.Add($"http://127.0.0.1:{port}/");
        }
        else
        {
            prefixes.Add($"http://localhost:{port}/");
            prefixes.Add($"http://127.0.0.1:{port}/");
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
                lanActive = lanEnabled && prefix.StartsWith("http://+:", StringComparison.OrdinalIgnoreCase);
                if (lanEnabled && !prefix.StartsWith("http://+:", StringComparison.OrdinalIgnoreCase))
                {
                    warning = "局域网监听失败，已降级为仅本地可访问。如需局域网访问，请以管理员运行或执行 netsh http add urlacl url=http://+:" + port + "/ user=Everyone";
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
            ? "Web 远程控制启动失败：可能缺少 URL ACL 或端口被占用。请尝试以管理员运行或执行 netsh http add urlacl url=http://+:" + port + "/ user=Everyone"
            : "Web 远程控制启动失败：端口被占用或系统限制。请更换端口。";
        return false;
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
                if (!Authorize(request, response, _config))
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
        switch (path)
        {
            case "":
            case "/":
                await WriteStringAsync(response, WebIndexHtmlV2.Value, "text/html; charset=utf-8", ct);
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
            case "/api/game/start":
                await HandleGameStartAsync(response, ct);
                break;
            case "/api/game/stop":
                await HandleGameStopAsync(response, ct);
                break;
            default:
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
                break;
        }
    }

    private async Task HandlePostAsync(string path, HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
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
        var path = request.QueryString["path"];
        var config = _configService.Get();
        if (!ConfigPathAccessor.TryGetValue(config, path, out var value, out var error))
        {
            await WriteJsonAsync(response, new { error }, ct);
            return;
        }

        await WriteJsonAsync(response, value ?? new { }, ct);
    }

    private async Task HandleConfigSetAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
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
            response.Close();
            return;
        }

        var success = false;
        string? error = null;
        RunOnUiThread(() =>
        {
            var config = _configService.Get();
            success = ConfigPathAccessor.TrySetValue(config, payload.Path, payload.Value, out error);
        });

        if (!success)
        {
            await WriteJsonAsync(response, new { error }, ct);
            return;
        }

        await WriteJsonAsync(response, new { ok = true }, ct);
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

        var path = Path.Combine(Global.Absolute(@"User\ScriptGroup"), $"{name}.json");
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

        var file = Path.Combine(Global.Absolute(@"User\ScriptGroup"), $"{payload.Name}.json");
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

        var file = Path.Combine(Global.Absolute(@"User\ScriptGroup"), $"{payload.Name}.json");
        if (File.Exists(file))
        {
            File.Delete(file);
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

        var folder = Global.Absolute(@"User\ScriptGroup");
        var oldFile = Path.Combine(folder, $"{payload.OldName}.json");
        var newFile = Path.Combine(folder, $"{payload.NewName}.json");
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

        var file = Path.Combine(Global.Absolute(@"User\ScriptGroup"), $"{group.Name}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
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
                RunNum = p.RunNum
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
            foreach (var name in payload.KeyMouseNames.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                group.AddProject(ScriptGroupProject.BuildKeyMouseProject(name));
            }
        }

        if (payload.Pathing != null)
        {
            foreach (var item in payload.Pathing)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Name))
                {
                    continue;
                }

                group.AddProject(ScriptGroupProject.BuildPathingProject(item.Name, item.Folder ?? string.Empty));
            }
        }

        ReindexProjects(group);
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
        if (!string.IsNullOrWhiteSpace(payload.Status))
        {
            project.Status = payload.Status;
        }

        if (!string.IsNullOrWhiteSpace(payload.Schedule))
        {
            project.Schedule = payload.Schedule;
        }

        if (payload.RunNum.HasValue && payload.RunNum.Value > 0)
        {
            project.RunNum = payload.RunNum.Value;
        }

        SaveScriptGroup(group);
        await WriteJsonAsync(response, new { ok = true }, ct);
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
        SaveScriptGroup(group);
        await WriteJsonAsync(response, new { ok = true }, ct);
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

        await RunOnUiThreadAsync(async () =>
        {
            var vm = App.GetService<BetterGenshinImpact.ViewModel.Pages.ScriptControlViewModel>();
            if (vm != null)
            {
                await vm.OnStartMultiScriptGroupWithNamesAsync(names.ToArray());
            }
        });

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

        SaveOneDragonConfig(config);
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

    private async Task HandleOneDragonRunAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        OneDragonSelectRequest? payload = await ReadJsonAsync<OneDragonSelectRequest>(request, ct);
        var name = payload?.Name;
        await RunOnUiThreadAsync(() =>
        {
            var vm = new BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel();
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
            var config = _configService.Get().AutoFightConfig;
            if (!string.IsNullOrWhiteSpace(payload.StrategyName))
            {
                config.StrategyName = payload.StrategyName;
            }
            if (!string.IsNullOrWhiteSpace(payload.TeamNames))
            {
                config.TeamNames = payload.TeamNames;
            }
            if (payload.FightFinishDetectEnabled.HasValue)
            {
                config.FightFinishDetectEnabled = payload.FightFinishDetectEnabled.Value;
            }
            if (!string.IsNullOrWhiteSpace(payload.ActionSchedulerByCd))
            {
                config.ActionSchedulerByCd = payload.ActionSchedulerByCd;
            }
            if (!string.IsNullOrWhiteSpace(payload.OnlyPickEliteDropsMode))
            {
                config.OnlyPickEliteDropsMode = payload.OnlyPickEliteDropsMode;
            }
            if (payload.PickDropsAfterFightEnabled.HasValue)
            {
                config.PickDropsAfterFightEnabled = payload.PickDropsAfterFightEnabled.Value;
            }
            if (payload.PickDropsAfterFightSeconds.HasValue)
            {
                config.PickDropsAfterFightSeconds = Math.Max(0, payload.PickDropsAfterFightSeconds.Value);
            }
        }

        await WriteJsonAsync(response, BuildAutoFightSettings(), ct);
    }

    private async Task HandleAutoDomainSettingsAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        AutoDomainSettingsRequest? payload = await ReadJsonAsync<AutoDomainSettingsRequest>(request, ct);
        if (payload != null)
        {
            var config = _configService.Get().AutoDomainConfig;
            if (!string.IsNullOrWhiteSpace(payload.PartyName))
            {
                config.PartyName = payload.PartyName;
            }
            if (!string.IsNullOrWhiteSpace(payload.DomainName))
            {
                config.DomainName = payload.DomainName;
            }
            if (!string.IsNullOrWhiteSpace(payload.SundaySelectedValue))
            {
                config.SundaySelectedValue = payload.SundaySelectedValue;
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
            if (!string.IsNullOrWhiteSpace(payload.StrategyName))
            {
                _configService.Get().AutoFightConfig.StrategyName = payload.StrategyName;
            }
        }

        await WriteJsonAsync(response, BuildAutoDomainSettings(), ct);
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

            using var bitmap = mat.ToBitmap();
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
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

    private static bool Authorize(HttpListenerRequest request, HttpListenerResponse response, WebRemoteConfig config)
    {
        if (!config.AuthEnabled)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(config.Username) || string.IsNullOrWhiteSpace(config.Password))
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            response.Headers.Add("WWW-Authenticate", "Basic realm=\"BetterGI\"");
            return false;
        }

        var authHeader = request.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            response.Headers.Add("WWW-Authenticate", "Basic realm=\"BetterGI\"");
            return false;
        }

        try
        {
            var encoded = authHeader.Substring("Basic ".Length).Trim();
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var separatorIndex = decoded.IndexOf(':');
            if (separatorIndex < 0)
            {
                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                response.Headers.Add("WWW-Authenticate", "Basic realm=\"BetterGI\"");
                return false;
            }

            var username = decoded.Substring(0, separatorIndex);
            var password = decoded.Substring(separatorIndex + 1);
            if (username != config.Username || password != config.Password)
            {
                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                response.Headers.Add("WWW-Authenticate", "Basic realm=\"BetterGI\"");
                return false;
            }

            return true;
        }
        catch
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            response.Headers.Add("WWW-Authenticate", "Basic realm=\"BetterGI\"");
            return false;
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await WriteStringAsync(response, json, "application/json; charset=utf-8", ct);
    }

    private static async Task WriteStringAsync(HttpListenerResponse response, string payload, string contentType, CancellationToken ct)
    {
        var buffer = Encoding.UTF8.GetBytes(payload);
        response.StatusCode = (int)HttpStatusCode.OK;
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
            ListenPrefix = _currentPrefix,
            Warning = _lastWarning
        };
    }

    private static string[] GetLocalIpAddresses()
    {
        try
        {
            var host = Dns.GetHostName();
            return Dns.GetHostAddresses(host)
                .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(ip => ip.ToString())
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
        return new
        {
            strategyName = config.StrategyName,
            teamNames = config.TeamNames,
            fightFinishDetectEnabled = config.FightFinishDetectEnabled,
            actionSchedulerByCd = config.ActionSchedulerByCd,
            onlyPickEliteDropsMode = config.OnlyPickEliteDropsMode,
            pickDropsAfterFightEnabled = config.PickDropsAfterFightEnabled,
            pickDropsAfterFightSeconds = config.PickDropsAfterFightSeconds
        };
    }

    private object BuildAutoDomainSettings()
    {
        var config = _configService.Get().AutoDomainConfig;
        return new
        {
            partyName = config.PartyName,
            domainName = config.DomainName,
            sundaySelectedValue = config.SundaySelectedValue,
            autoArtifactSalvage = config.AutoArtifactSalvage,
            specifyResinUse = config.SpecifyResinUse,
            resinPriorityList = config.ResinPriorityList,
            originalResinUseCount = config.OriginalResinUseCount,
            condensedResinUseCount = config.CondensedResinUseCount,
            transientResinUseCount = config.TransientResinUseCount,
            fragileResinUseCount = config.FragileResinUseCount,
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

        var folder = Global.Absolute(@"User\AutoGeniusInvokation");
        var path = Path.Combine(folder, config.StrategyName + ".txt");
        if (!File.Exists(path))
        {
            return new { ok = false, error = "策略文件不存在" };
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
        var config = _configService.Get().AutoFightConfig;
        if (TryGetString(argsElement, "strategyName", out var strategyName))
        {
            config.StrategyName = strategyName;
        }

        if (TryGetString(argsElement, "teamNames", out var teamNames))
        {
            config.TeamNames = teamNames;
        }

        if (!TryGetFightStrategyPath(config.StrategyName, out var path, out var error))
        {
            return Task.FromResult<object>(new { ok = false, error });
        }

        StartBackgroundTask(() => new TaskRunner().RunSoloTaskAsync(new AutoFightTask(new AutoFightParam(path, config))));
        return Task.FromResult<object>(new { ok = true });
    }

    private Task<object> RunAutoDomainAsync(JsonElement argsElement, CancellationToken ct)
    {
        var config = _configService.Get().AutoDomainConfig;
        if (TryGetString(argsElement, "strategyName", out var strategyName))
        {
            _configService.Get().AutoFightConfig.StrategyName = strategyName;
        }

        if (TryGetString(argsElement, "partyName", out var partyName))
        {
            config.PartyName = partyName;
        }

        if (TryGetString(argsElement, "domainName", out var domainName))
        {
            config.DomainName = domainName;
        }

        if (TryGetString(argsElement, "sundaySelectedValue", out var sundayValue))
        {
            config.SundaySelectedValue = sundayValue;
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
            path = Path.Combine(folder, strategyName + ".txt");
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            error = "战斗策略文件不存在";
            return false;
        }

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
        var folder = BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel.OneDragonFlowConfigFolder;
        Directory.CreateDirectory(folder);
        var configs = new List<OneDragonFlowConfig>();
        foreach (var file in Directory.GetFiles(folder, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var config = Newtonsoft.Json.JsonConvert.DeserializeObject<OneDragonFlowConfig>(json);
                if (config != null)
                {
                    configs.Add(config);
                }
            }
            catch
            {
            }
        }

        if (configs.Count == 0)
        {
            configs.Add(new OneDragonFlowConfig { Name = "默认配置" });
        }

        return configs;
    }

    private static OneDragonFlowConfig? LoadOneDragonConfigByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            var configs = LoadOneDragonConfigs();
            return configs.FirstOrDefault();
        }

        var folder = BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel.OneDragonFlowConfigFolder;
        var file = Path.Combine(folder, $"{name}.json");
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(file);
            return Newtonsoft.Json.JsonConvert.DeserializeObject<OneDragonFlowConfig>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveOneDragonConfig(OneDragonFlowConfig config)
    {
        var folder = BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel.OneDragonFlowConfigFolder;
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{config.Name}.json");
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(config, Newtonsoft.Json.Formatting.Indented);
        File.WriteAllText(file, json);
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
        public bool LanEnabled { get; set; }
        public bool LanActive { get; set; }
        public string ListenPrefix { get; set; } = string.Empty;
        public string? Warning { get; set; }
    }

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
    }

    private sealed class ScriptGroupItemRequest
    {
        public string Name { get; set; } = string.Empty;
        public int Index { get; set; }
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
    }

    private sealed class AutoDomainSettingsRequest
    {
        public string? StrategyName { get; set; }
        public string? PartyName { get; set; }
        public string? DomainName { get; set; }
        public string? SundaySelectedValue { get; set; }
        public bool? AutoArtifactSalvage { get; set; }
        public bool? SpecifyResinUse { get; set; }
        public List<string>? ResinPriorityList { get; set; }
        public int? OriginalResinUseCount { get; set; }
        public int? CondensedResinUseCount { get; set; }
        public int? TransientResinUseCount { get; set; }
        public int? FragileResinUseCount { get; set; }
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

    private sealed class OneDragonSelectRequest
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ScriptGroupInfo
    {
        public string Name { get; set; } = string.Empty;
        public int Index { get; set; }
        public int ProjectCount { get; set; }
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
                        ProjectCount = group.Projects?.Count ?? 0
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
            var file = Path.Combine(Global.Absolute(@"User\ScriptGroup"), $"{name}.json");
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
        var file = Path.Combine(folder, $"{group.Name}.json");
        File.WriteAllText(file, group.ToJson());
    }

    private static void ReindexProjects(ScriptGroup group)
    {
        for (var i = 0; i < group.Projects.Count; i++)
        {
            group.Projects[i].Index = i;
        }
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

    private static readonly Lazy<string> WebIndexHtmlV2 = new(LoadWebIndexHtmlV2);

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
    <div class="muted" id="network">局域网地址加载中...</div>
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
        statusEl.textContent = `版本 ${data.version} | ${data.serverTime} | 游戏已启动: ${data.isInitialized} | 暂停: ${data.isSuspended} | LAN 启用: ${data.lanEnabled} | LAN 有效: ${data.lanActive} | 监听: ${data.listenPrefix || "-"}`;
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
          ? `局域网地址：${data.addresses.join(", ")}`
          : "未获取到局域网地址";
      } catch (err) {
        networkEl.textContent = "局域网地址获取失败";
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

    startGameBtn.addEventListener("click", () => fetchJson("/api/game/start", { method: "GET" }));
    stopGameBtn.addEventListener("click", () => fetchJson("/api/game/stop", { method: "GET" }));
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
}

