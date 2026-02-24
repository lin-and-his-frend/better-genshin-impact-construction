namespace BetterGenshinImpact.Model.Ai;

public sealed class McpToolCallResult
{
    public McpToolCallResult(bool isError, string content, string? rawJson = null)
    {
        IsError = isError;
        Content = content ?? string.Empty;
        RawJson = rawJson;
    }

    public bool IsError { get; }

    public string Content { get; }

    public string? RawJson { get; }
}
