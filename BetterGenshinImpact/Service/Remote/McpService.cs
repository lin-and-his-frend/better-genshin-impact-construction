using System;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Service.Interface;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.Remote;

internal sealed class McpService : IHostedService, IDisposable
{
    private readonly IConfigService _configService;
    private readonly ILogger<McpService> _logger;
    private readonly IMcpRequestHandler _requestHandler;
    private readonly object _sync = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private McpConfig? _config;
    private IPAddress _listenAddress = IPAddress.Loopback;
    private int _currentPort;
    private string _currentAddress = string.Empty;

    public McpService(IConfigService configService, IMcpRequestHandler requestHandler, ILogger<McpService> logger)
    {
        _configService = configService;
        _requestHandler = requestHandler;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _config = _configService.Get().McpConfig;
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
        if (e.PropertyName is nameof(McpConfig.Enabled) or nameof(McpConfig.Port) or nameof(McpConfig.ListenAddress))
        {
            StartOrStopListener(e.PropertyName is nameof(McpConfig.Port) or nameof(McpConfig.ListenAddress));
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

        if (_listener != null && !forceRestart && _currentPort == _config.Port && _currentAddress == _config.ListenAddress)
        {
            return;
        }

        if (_listener != null)
        {
            StopListener();
        }

        if (_config.Port is < 1 or > 65535)
        {
            _logger.LogWarning("MCP 端口无效: {Port}", _config.Port);
            return;
        }

        if (!IPAddress.TryParse(_config.ListenAddress, out _listenAddress))
        {
            _listenAddress = IPAddress.Loopback;
        }

        try
        {
            var listener = new TcpListener(_listenAddress, _config.Port);
            listener.Start();
            _listener = listener;
            _cts = new CancellationTokenSource();
            _listenTask = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
            _currentPort = _config.Port;
            _currentAddress = _config.ListenAddress;
            _logger.LogInformation("MCP 监听已启动: {Address}:{Port}", _listenAddress, _config.Port);
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "启动 MCP 监听失败，请检查端口权限或占用情况");
            StopListener();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "启动 MCP 监听失败");
            StopListener();
        }
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

            _listener = null;
            _cts?.Dispose();
            _cts = null;
            _listenTask = null;
            _currentPort = 0;
            _currentAddress = string.Empty;
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
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MCP 监听异常");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        await using var stream = client.GetStream();
        try
        {
            await _requestHandler.HandleConnectionAsync(stream, ct);
        }
        catch
        {
        }
        finally
        {
            client.Close();
        }
    }
}
