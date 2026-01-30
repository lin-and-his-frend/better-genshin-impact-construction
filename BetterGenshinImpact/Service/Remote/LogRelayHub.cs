using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.Service.Remote;

internal sealed record LogLine(DateTimeOffset Timestamp, string Level, string Message);

internal static class LogRelayHub
{
    private static readonly object Sync = new();
    private static readonly LinkedList<LogLine> Buffer = new();
    private const int MaxBufferLines = 500;

    public static event EventHandler<LogLine>? LineReceived;

    public static void Publish(LogLine line)
    {
        lock (Sync)
        {
            Buffer.AddLast(line);
            while (Buffer.Count > MaxBufferLines)
            {
                Buffer.RemoveFirst();
            }
        }

        try
        {
            LineReceived?.Invoke(null, line);
        }
        catch
        {
        }
    }

    public static IReadOnlyList<LogLine> GetSnapshot(int maxLines)
    {
        if (maxLines <= 0)
        {
            return Array.Empty<LogLine>();
        }

        lock (Sync)
        {
            var total = Buffer.Count;
            if (total == 0)
            {
                return Array.Empty<LogLine>();
            }

            var skip = Math.Max(0, total - maxLines);
            var list = new List<LogLine>(Math.Min(total, maxLines));
            var index = 0;
            foreach (var line in Buffer)
            {
                if (index++ < skip)
                {
                    continue;
                }

                list.Add(line);
            }

            return list;
        }
    }
}
