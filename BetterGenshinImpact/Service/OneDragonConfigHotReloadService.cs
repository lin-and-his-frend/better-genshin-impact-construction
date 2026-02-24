using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service;

internal sealed class OneDragonConfigHotReloadService : IHostedService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public static event Action? ConfigsChanged;

    private readonly ILogger<OneDragonConfigHotReloadService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private DateTimeOffset? _lastUpdatedUtc;

    public OneDragonConfigHotReloadService(ILogger<OneDragonConfigHotReloadService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _lastUpdatedUtc = OneDragonConfigStore.GetLatestUpdatedUtc();
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
                var updatedUtc = OneDragonConfigStore.GetLatestUpdatedUtc();
                if (updatedUtc == _lastUpdatedUtc)
                {
                    continue;
                }

                _lastUpdatedUtc = updatedUtc;
                try
                {
                    ConfigsChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "一条龙配置热加载通知失败");
                }
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
