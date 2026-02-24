using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Helpers.Win32;
using BetterGenshinImpact.Service.Interface;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service;

internal sealed class ConfigHotReloadService : IHostedService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IConfigService _configService;
    private readonly ILogger<ConfigHotReloadService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private DateTimeOffset? _lastUpdatedUtc;

    public ConfigHotReloadService(IConfigService configService, ILogger<ConfigHotReloadService> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _lastUpdatedUtc = UserStorage.GetMainConfigUpdatedUtc();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts == null)
        {
            return;
        }

        _cts.Cancel();
        if (_loopTask != null)
        {
            try
            {
                await _loopTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var updatedUtc = UserStorage.GetMainConfigUpdatedUtc();
                if (updatedUtc == null || updatedUtc == _lastUpdatedUtc)
                {
                    continue;
                }

                _lastUpdatedUtc = updatedUtc;
                UIDispatcherHelper.BeginInvoke(() =>
                {
                    try
                    {
                        _configService.ReloadFromStorage();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "配置热加载失败");
                        ConsoleHelper.WriteError($"配置热加载失败: {ex.Message}");
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }
}
