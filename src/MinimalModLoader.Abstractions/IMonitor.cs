namespace MinimalModLoader.Abstractions;

public enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warn,
    Error
}

/// <summary>
/// Per-mod structured logger injected by the Mod Loader.
/// </summary>
public interface IMonitor
{
    void Log(string message, LogLevel level = LogLevel.Info);
    void LogError(string message, Exception? exception = null);
}
