using CoreTests.Reflection;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CoreTests.Core;

public class LoggingExtensionsTests
{
    [Fact]
    public void works_fine_with_null_services()
    {
        IServiceProvider services = default;
        var logger = services.GetLoggerOrDefault<LoggingExtensionsTests>();
        logger.LogInformation("Hey, we're all good");
    }

    [Fact]
    public void works_fine_with_services_that_have_no_logging_registered()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var logger = services.GetLoggerOrDefault<LoggingExtensionsTests>();
        logger.LogInformation("Hey, we're all good");
    }

    [Fact]
    public void works_with_logging_registration_if_it_can()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        var services = collection.BuildServiceProvider();
   
        var logger = services.GetLoggerOrDefault<LoggingExtensionsTests>();
        logger.LogInformation("Hey, we're all good");

    }
}