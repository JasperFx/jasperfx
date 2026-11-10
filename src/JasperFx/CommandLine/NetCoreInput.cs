using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace JasperFx.CommandLine;

public class NetCoreInput : IHostBuilderInput
{
    private const string CannotBeNullOrEmptyAndMustBeInTheFormKeyValue = "Cannot be null or empty and must be in the form KEY=VALUE";
    
    [Description("Overwrite individual configuration items")]
    public Dictionary<string, string?> ConfigFlag = new();

    private IHostBuilder _hostBuilder = null!;

    [Description("Value in the form <KEY=VALUE> to set an environment variable for this process"), FlagAlias("env-variable",'v')]
    public string? EnvironmentVariableFlag
    {
        set
        {
            if (value.IsEmpty())
            {
                throw new ArgumentOutOfRangeException(nameof(EnvironmentVariableFlag), CannotBeNullOrEmptyAndMustBeInTheFormKeyValue);
            }

            if (!value.Contains('='))
            {
                throw new ArgumentOutOfRangeException(nameof(EnvironmentVariableFlag), CannotBeNullOrEmptyAndMustBeInTheFormKeyValue);
            }
            
            var parts = value.Split('=');
            if (parts.Length != 2)
            {
                throw new ArgumentOutOfRangeException(nameof(EnvironmentVariableFlag), CannotBeNullOrEmptyAndMustBeInTheFormKeyValue);
            }

            if (parts.Any(x => x.IsEmpty()))
            {
                throw new ArgumentOutOfRangeException(nameof(EnvironmentVariableFlag), CannotBeNullOrEmptyAndMustBeInTheFormKeyValue);
            }
            
            System.Environment.SetEnvironmentVariable(parts[0], parts[1]);
        }
        get => EnvironmentVariableFlag;
    }
    
    [Description("Override the IHostEnvironment.ContentRoot"), FlagAlias("contentRoot")]
    public string? ContentRootFlag { get; set; }

    [Description("Override the IHostEnvironment.ApplicationName"), FlagAlias("applicationName")]
    public string? ApplicationNameFlag { get; set; }
    
    [Description("Override the IHostEnvironment.EnvironmentName")]
    [MemberNotNull(nameof(EnvironmentVariableFlag))]
    public string? EnvironmentFlag { get; set; }

    [Description("Write out much more information at startup and enables console logging")]
    public bool VerboseFlag { get; set; }

    [Description("Override the log level")]
    public LogLevel? LogLevelFlag { get; set; }

    [IgnoreOnCommandLine] public Assembly ApplicationAssembly { get; set; } = null!;

    /// <summary>
    ///     The IHostBuilder configured by your application. Can be used to build or start
    ///     up the application
    /// </summary>
    [IgnoreOnCommandLine]
    public IHostBuilder HostBuilder
    {
        get => _hostBuilder;
        set
        {
            _hostBuilder = value;

            // With WebApplication, the same args usually went through CreateBuilder(args) first,
            // so the flag has already taken effect (WebApplicationFactory passes --environment
            // this way). Only warn when the built host disagrees with the requested environment.
            if (value is PreBuiltHostBuilder prebuilt && EnvironmentFlag.IsNotEmpty())
            {
                var actual = prebuilt.Host.Services.GetService<IHostEnvironment>()?.EnvironmentName;
                if (!EnvironmentFlag.EqualsIgnoreCase(actual ?? string.Empty))
                {
                    AnsiConsole.MarkupLine($"[bold red]JasperFx cannot override the environment name to '{Markup.Escape(EnvironmentFlag)}' when running against a pre-built IHost that is already running as '{Markup.Escape(actual ?? "unknown")}'. Try setting dotnet run --environment Name before the \"--\" separator in your command arguments to pass it directly to the dotnet command line[/]");
                    AnsiConsole.MarkupLine("");
                }
            }
        }
    }

    public virtual void ApplyHostBuilderInput()
    {
        // Just can't work here
        if (HostBuilder is PreBuiltHostBuilder) return;
        
        #region sample_what_the_cli_is_doing
        if (ContentRootFlag.IsNotEmpty())
        {
            HostBuilder.UseContentRoot(ContentRootFlag);
        }

        // The --log-level flag value overrides your application's
        // LogLevel, and --verbose implies --log-level Debug. An explicit
        // --log-level wins when both are given
        var overriddenLogLevel = LogLevelFlag ?? (VerboseFlag ? LogLevel.Debug : (LogLevel?)null);

        if (VerboseFlag)
        {
            Console.WriteLine("Verbose flag is on.");
        }

        if (overriddenLogLevel.HasValue)
        {
            var level = overriddenLogLevel.Value;
            AnsiConsole.MarkupLine($"[gray]Overwriting the minimum log level to {level}[/]");

            // SetMinimumLevel() only sets LoggerFilterOptions.MinLevel, which the logging
            // pipeline ignores whenever a configured rule applies -- and a Logging:LogLevel:Default
            // in appsettings.json is exactly such a rule. PostConfigure runs after the
            // configuration binding, so the flag's level really is the level for everything
            HostBuilder.ConfigureServices(services =>
            {
                services.PostConfigure<LoggerFilterOptions>(options => OverrideLogLevel(options, level));
            });
        }

        // The --environment flag is used to set the environment
        // property on the IHostedEnvironment within your system
        if (EnvironmentFlag.IsNotEmpty())
        {
            Console.WriteLine($"Overwriting the environment to `{EnvironmentFlag}`");
            HostBuilder.UseEnvironment(EnvironmentFlag);
        }

        if (ConfigFlag.Any())
        {
            HostBuilder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(ConfigFlag));
        }

        #endregion
    }

    /// <summary>
    ///     Forces every logger to the given level by replacing any configured
    ///     LoggerFilterRule (Logging:LogLevel:* in appsettings.json, provider or
    ///     category specific rules) with one catch-all rule at that level. Used by the
    ///     --log-level and --verbose command line flags
    /// </summary>
    public static void OverrideLogLevel(LoggerFilterOptions options, LogLevel level)
    {
        options.MinLevel = level;
        options.Rules.Clear();
        options.Rules.Add(new LoggerFilterRule(providerName: null, categoryName: null, logLevel: level, filter: null));
    }

    public IHost BuildHost()
    {
        ApplyHostBuilderInput();
        
        var host = HostBuilder.Build();

        if (ApplicationNameFlag.IsNotEmpty())
        {
            var hostEnvironment = host.Services.GetService<IHostEnvironment>();
            if (hostEnvironment != null) hostEnvironment.ApplicationName = ApplicationNameFlag;
        }

        return host;
    }
}