namespace BetterGenshinImpact.Model.Ai;

public sealed class AiToolCall
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string ArgumentsJson { get; init; } = "{}";
}
