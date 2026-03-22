using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Model.Ai;
using BetterGenshinImpact.Service.Ai;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.ViewModel.Pages;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.Remote;

internal sealed class WebAiBridgeService
{
    public sealed record ChatTurn(string Role, string Content);

    public sealed record ChatResult(string Reply, string Status, IReadOnlyList<ChatTurn> Messages);

    private static readonly TimeSpan McpToolsRefreshInterval = TimeSpan.FromMinutes(2);

    private readonly AiChatViewModel _viewModel;
    private readonly ILogger<WebAiBridgeService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastMcpRefreshUtc = DateTimeOffset.MinValue;

    public WebAiBridgeService(
        IConfigService configService,
        AiChatService aiChatService,
        McpLocalClient mcpLocalClient,
        ILogger<WebAiBridgeService> logger)
    {
        _viewModel = new AiChatViewModel(configService, aiChatService, mcpLocalClient);
        _logger = logger;
    }

    public async Task<ChatResult> ChatAsync(string message, IReadOnlyList<ChatTurn>? history, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new InvalidOperationException("Message is required");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureMcpToolsReadyAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _viewModel.ResetConversationForBridge(BuildHistory(history));
            await _viewModel.SendBridgeMessageAsync(message.Trim(), ct).ConfigureAwait(false);

            var bridgeSnapshot = _viewModel.GetBridgeSnapshot();
            var snapshot = bridgeSnapshot.Messages
                .Select(x => new ChatTurn(NormalizeRole(x.Role), x.Content ?? string.Empty))
                .ToList();
            var reply = snapshot.LastOrDefault(x => string.Equals(x.Role, "assistant", StringComparison.OrdinalIgnoreCase))?.Content
                        ?? string.Empty;
            var status = bridgeSnapshot.StatusText;
            return new ChatResult(reply, status, snapshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureMcpToolsReadyAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_viewModel.GetBridgeMcpToolCount() > 0 && now - _lastMcpRefreshUtc < McpToolsRefreshInterval)
        {
            return;
        }

        try
        {
            await _viewModel.RefreshMcpToolsForBridgeAsync(ct).ConfigureAwait(false);
            _lastMcpRefreshUtc = now;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "刷新 MCP 工具列表失败，继续使用现有工具缓存");
        }
    }

    private static IReadOnlyList<AiChatMessage> BuildHistory(IReadOnlyList<ChatTurn>? history)
    {
        if (history == null || history.Count == 0)
        {
            return [];
        }

        var messages = new List<AiChatMessage>(history.Count);
        foreach (var item in history)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Content))
            {
                continue;
            }

            var role = NormalizeRole(item.Role);
            messages.Add(new AiChatMessage(role, item.Content.Trim()));
        }

        return messages;
    }

    private static string NormalizeRole(string? rawRole)
    {
        var role = (rawRole ?? string.Empty).Trim().ToLowerInvariant();
        return role switch
        {
            "assistant" => "assistant",
            "system" => "system",
            "mcp" => "mcp",
            _ => "user"
        };
    }
}
