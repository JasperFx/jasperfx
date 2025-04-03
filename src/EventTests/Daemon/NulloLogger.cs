using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace EventTests.Daemon;

internal class NulloLogger: ILogger, IDisposable
{
    public void Dispose()
    {
        // Nothing
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
        Func<TState, Exception, string> formatter)
    {
        var message = $"{logLevel}: {formatter(state, exception)}";
        Debug.WriteLine(message);
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public IDisposable BeginScope<TState>(TState state)
    {
        return this;
    }
}