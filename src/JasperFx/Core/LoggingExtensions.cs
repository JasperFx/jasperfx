using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Logging.Debug;
using Microsoft.Extensions.Options;

namespace JasperFx.Core;

public static class LoggingExtensions
{
    /// <summary>
    /// Get an ILogger of the expected type T if the service provider is not null,
    /// or fall back to a logger w/ Debug & Console output if none is found
    /// </summary>
    /// <param name="services"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static ILogger<T> GetLoggerOrDefault<T>(this IServiceProvider? services)
    {
        return services?.GetService<ILoggerFactory>()?.CreateLogger<T>() ?? new Logger<T>(new LoggerFactory([new DebugLoggerProvider(), new ConsoleLoggerProvider(new StubConsoleOptions())]));
    }

    internal class StubConsoleOptions : IOptionsMonitor<ConsoleLoggerOptions>
    {
        public ConsoleLoggerOptions Get(string? name)
        {
            return CurrentValue;
        }

        public IDisposable? OnChange(Action<ConsoleLoggerOptions, string?> listener)
        {
            return new Disposable();
        }

        public ConsoleLoggerOptions CurrentValue { get; } = new();
    }

    public class Disposable : IDisposable
    {
        public void Dispose()
        {
            // Nothing
        }
    }
}