using Microsoft.Extensions.Logging;
namespace Xur.Domain;

/// <summary>Redacted application errors for journald, with a bounded fallback when the agent is unavailable.</summary>
public sealed class ApplicationLog(string service, TextWriter? output = null) : ILoggerProvider
{
    private readonly object gate = new();
    private readonly Queue<string> entries = new();
    private int size;
    private readonly TextWriter writer = output ?? Console.Error;
    private const int Limit = 256 * 1024;

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public string Read() { lock(gate) return string.Join('\n', entries); }
    public void Dispose() { }
    public void Write(string category, LogLevel level, string message, Exception? exception = null)
    {
        var text = Redaction.Logs($"{DateTimeOffset.UtcNow:O} {service} {level} {category}: {message}" +
            (exception is null ? "" : "\n" + exception));
        if(text.Length > Limit) text = text[..(Limit-32)] + "\n[entry truncated]";
        lock(gate)
        {
            entries.Enqueue(text); size += text.Length+1;
            while(size > Limit && entries.Count > 1) size -= entries.Dequeue().Length+1;
            // StandardError=journal is retained; stdout is used by the local console.
            try { writer.WriteLine(text); } catch(IOException) { }
        }
    }
    private sealed class Logger(ApplicationLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning && level != LogLevel.None;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState,Exception?,string> formatter)
        { if(IsEnabled(level)) owner.Write(category, level, formatter(state,exception), exception); }
    }
}
