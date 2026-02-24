using System;

namespace BetterGenshinImpact.Model.Ai;

public sealed class AiChatMessage
{
    public AiChatMessage(string role, string content)
    {
        Role = role ?? string.Empty;
        Content = content ?? string.Empty;
        Timestamp = DateTimeOffset.Now;
    }

    public string Role { get; }

    public string Content { get; }

    public DateTimeOffset Timestamp { get; }

    public bool IsUser => Role.Equals("user", StringComparison.OrdinalIgnoreCase);

    public bool IsAssistant => Role.Equals("assistant", StringComparison.OrdinalIgnoreCase);

    public bool IsSystem => Role.Equals("system", StringComparison.OrdinalIgnoreCase);

    public bool IsMcp => Role.Equals("mcp", StringComparison.OrdinalIgnoreCase);

    public string DisplayRole => Role.ToLowerInvariant() switch
    {
        "user" => "你",
        "assistant" => "AI",
        "system" => "系统",
        "mcp" => "MCP",
        _ => Role
    };
}
