using Microsoft.Extensions.Logging;

namespace JasperFx;

/// <summary>
/// Helper marker interface in JasperFx to help downstream tools
/// potentially attach an ILogger instance to runtime objects
/// </summary>
public interface IHasLogger
{
    ILogger? Logger { get; }

    void AttachLogger(ILoggerFactory loggerFactory);
}