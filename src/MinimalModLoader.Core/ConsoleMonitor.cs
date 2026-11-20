using MinimalModLoader.Abstractions;

namespace MinimalModLoader.Core;

/// <summary>
/// Thread-safe structured logger with SMAPI-inspired readable formatting.
/// </summary>
public sealed class ConsoleMonitor : IMonitor
{
    private static readonly object WriteLock = new();
    private readonly string _source;
    private readonly TextWriter _writer;
    private readonly bool _useColors;
    private readonly LogLevel _minLevel;

    public ConsoleMonitor(
        string source,
        TextWriter? writer = null,
        LogLevel minLevel = LogLevel.Info,
        bool? useColors = null)
    {
        _source = source;
        _writer = writer ?? Console.Out;
        _minLevel = minLevel;
        _useColors = useColors ?? (writer is null && !Console.IsOutputRedirected);
    }

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        if (level < _minLevel)
            return;

        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var levelTag = level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Info => "INFO ",
            LogLevel.Warn => "WARN ",
            LogLevel.Error => "ERROR",
            _ => "INFO "
        };

        var line = $"[{timestamp} {levelTag} {_source}] {message}";

        lock (WriteLock)
        {
            if (_useColors)
            {
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = level switch
                {
                    LogLevel.Trace => ConsoleColor.DarkGray,
                    LogLevel.Debug => ConsoleColor.Gray,
                    LogLevel.Info => ConsoleColor.White,
                    LogLevel.Warn => ConsoleColor.Yellow,
                    LogLevel.Error => ConsoleColor.Red,
                    _ => prev
                };
                _writer.WriteLine(line);
                Console.ForegroundColor = prev;
            }
            else
            {
                _writer.WriteLine(line);
            }
        }
    }

    public void LogError(string message, Exception? exception = null)
    {
        if (exception is null)
        {
            Log(message, LogLevel.Error);
            return;
        }

        Log($"{message}{Environment.NewLine}    {exception.GetType().Name}: {exception.Message}", LogLevel.Error);
    }
}
