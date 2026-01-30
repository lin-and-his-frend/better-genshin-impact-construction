using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Service.Interface;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.Remote;

internal sealed class AiLogRelayService : IHostedService, IDisposable
{
    private readonly IConfigService _configService;
    private readonly IAiLogSink _aiLogSink;
    private readonly ILogger<AiLogRelayService> _logger;
    private WebRemoteConfig? _config;
    private bool _enabled;
    private EventHandler<LogLine>? _handler;

    public AiLogRelayService(IConfigService configService, IAiLogSink aiLogSink, ILogger<AiLogRelayService> logger)
    {
        _configService = configService;
        _aiLogSink = aiLogSink;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _config = _configService.Get().WebRemoteConfig;
        _enabled = _config.AiLogRelayEnabled;
        _config.PropertyChanged += OnConfigChanged;
        _handler = (_, line) => _ = RelayAsync(line);
        LogRelayHub.LineReceived += _handler;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_handler != null)
        {
            LogRelayHub.LineReceived -= _handler;
            _handler = null;
        }

        if (_config != null)
        {
            _config.PropertyChanged -= OnConfigChanged;
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_handler != null)
        {
            LogRelayHub.LineReceived -= _handler;
            _handler = null;
        }

        if (_config != null)
        {
            _config.PropertyChanged -= OnConfigChanged;
            _config = null;
        }
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WebRemoteConfig.AiLogRelayEnabled) && _config != null)
        {
            _enabled = _config.AiLogRelayEnabled;
        }
    }

    private async Task RelayAsync(LogLine line)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            await _aiLogSink.PublishAsync(line, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "转发日志到 AI 失败");
        }
    }
}
