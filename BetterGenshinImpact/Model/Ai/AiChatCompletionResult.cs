using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.Model.Ai;

public sealed class AiChatCompletionResult
{
    public string Content { get; init; } = string.Empty;

    public IReadOnlyList<AiToolCall> ToolCalls { get; init; } = Array.Empty<AiToolCall>();
}
