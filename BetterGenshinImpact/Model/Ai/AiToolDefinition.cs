using System.Text.Json.Nodes;

namespace BetterGenshinImpact.Model.Ai;

public sealed class AiToolDefinition
{
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public JsonNode? Parameters { get; init; }
}
