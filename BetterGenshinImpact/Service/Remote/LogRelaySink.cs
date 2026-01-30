using System;
using Serilog.Core;
using Serilog.Events;

namespace BetterGenshinImpact.Service.Remote;

internal sealed class LogRelaySink : ILogEventSink
{
    private readonly LogEventLevel _minimumLevel;
    private readonly IFormatProvider? _formatProvider;

    public LogRelaySink(LogEventLevel minimumLevel, IFormatProvider? formatProvider = null)
    {
        _minimumLevel = minimumLevel;
        _formatProvider = formatProvider;
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < _minimumLevel)
        {
            return;
        }

        var message = logEvent.RenderMessage(_formatProvider);
        if (logEvent.Exception != null)
        {
            message = $"{message}{Environment.NewLine}{logEvent.Exception}";
        }

        LogRelayHub.Publish(new LogLine(logEvent.Timestamp, logEvent.Level.ToString(), message));
    }
}
