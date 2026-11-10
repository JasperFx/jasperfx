using JasperFx.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CommandLineTests;

// GH-1132. SetMinimumLevel() only sets LoggerFilterOptions.MinLevel, and the logging pipeline
// prefers a configured rule to MinLevel. Logging:LogLevel:Default in appsettings.json is such a
// rule, so the --log-level flag was silently ignored by any application with that setting.
public class log_level_flag_overrides_configured_levels
{
    // Host.CreateDefaultBuilder() is what binds the Logging section to
    // LoggerFilterOptions, exactly as a real application does
    private static IHostBuilder hostWithConfiguredDefaultLevel(string level)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = level
            }));
    }

    [Fact]
    public void log_level_flag_lowers_a_configured_default_level()
    {
        #region sample_log_level_flag_overrides_configuration
        // appsettings.json says Logging:LogLevel:Default = Warning,
        // but the command line said --log-level Debug
        var input = new NetCoreInput
        {
            HostBuilder = hostWithConfiguredDefaultLevel("Warning"),
            LogLevelFlag = LogLevel.Debug
        };

        using var host = input.BuildHost();

        // The flag wins over the configured level
        var logger = host.Services.GetRequiredService<ILogger<log_level_flag_overrides_configured_levels>>();
        logger.IsEnabled(LogLevel.Debug).ShouldBeTrue();
        #endregion

        var options = host.Services.GetRequiredService<IOptions<LoggerFilterOptions>>().Value;
        options.MinLevel.ShouldBe(LogLevel.Debug);
    }

    [Fact]
    public void log_level_flag_raises_a_configured_default_level()
    {
        var input = new NetCoreInput
        {
            HostBuilder = hostWithConfiguredDefaultLevel("Debug"),
            LogLevelFlag = LogLevel.Warning
        };

        using var host = input.BuildHost();

        var logger = host.Services.GetRequiredService<ILogger<log_level_flag_overrides_configured_levels>>();
        logger.IsEnabled(LogLevel.Debug).ShouldBeFalse();
        logger.IsEnabled(LogLevel.Information).ShouldBeFalse();
        logger.IsEnabled(LogLevel.Warning).ShouldBeTrue();
    }

    [Fact]
    public void log_level_flag_overrides_category_specific_rules_too()
    {
        // The flag is a debugging aid, so it forces the level for every category,
        // including the ones configuration had quietened down
        var input = new NetCoreInput
        {
            HostBuilder = hostWithConfiguredDefaultLevel("Information")
                .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:LogLevel:Microsoft"] = "Warning"
                })),
            LogLevelFlag = LogLevel.Debug
        };

        using var host = input.BuildHost();

        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("Microsoft.Hosting.Lifetime").IsEnabled(LogLevel.Debug).ShouldBeTrue();
        factory.CreateLogger("MyApp.Something").IsEnabled(LogLevel.Debug).ShouldBeTrue();
    }

    [Fact]
    public void verbose_flag_lowers_a_configured_default_level_to_debug()
    {
        var input = new NetCoreInput
        {
            HostBuilder = hostWithConfiguredDefaultLevel("Warning"),
            VerboseFlag = true
        };

        using var host = input.BuildHost();

        var logger = host.Services.GetRequiredService<ILogger<log_level_flag_overrides_configured_levels>>();
        logger.IsEnabled(LogLevel.Debug).ShouldBeTrue();
    }

    [Fact]
    public void an_explicit_log_level_flag_wins_over_verbose()
    {
        var input = new NetCoreInput
        {
            HostBuilder = hostWithConfiguredDefaultLevel("Information"),
            VerboseFlag = true,
            LogLevelFlag = LogLevel.Warning
        };

        using var host = input.BuildHost();

        var logger = host.Services.GetRequiredService<ILogger<log_level_flag_overrides_configured_levels>>();
        logger.IsEnabled(LogLevel.Debug).ShouldBeFalse();
        logger.IsEnabled(LogLevel.Warning).ShouldBeTrue();
    }

    [Fact]
    public void configured_levels_are_untouched_without_the_flag()
    {
        var input = new NetCoreInput
        {
            HostBuilder = hostWithConfiguredDefaultLevel("Warning")
        };

        using var host = input.BuildHost();

        var logger = host.Services.GetRequiredService<ILogger<log_level_flag_overrides_configured_levels>>();
        logger.IsEnabled(LogLevel.Debug).ShouldBeFalse();
        logger.IsEnabled(LogLevel.Warning).ShouldBeTrue();
    }

    [Fact]
    public void the_flags_are_parsed_from_the_command_line()
    {
        var factory = new CommandFactory();
        factory.RegisterCommand<ParsingLogLevelCommand>();

        var run = factory.BuildRun("parsing-log-level --log-level Debug --verbose");
        var input = run.Input.ShouldBeOfType<NetCoreInput>();

        input.LogLevelFlag.ShouldBe(LogLevel.Debug);
        input.VerboseFlag.ShouldBeTrue();
    }

    [Description("Only here to parse NetCoreInput", Name = "parsing-log-level")]
    public class ParsingLogLevelCommand : JasperFxCommand<NetCoreInput>
    {
        public override bool Execute(NetCoreInput input)
        {
            return true;
        }
    }
}
