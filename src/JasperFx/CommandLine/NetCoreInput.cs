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

            if (value is PreBuiltHostBuilder && EnvironmentFlag.IsNotEmpty())
            {
                AnsiConsole.MarkupLine($"[bold red]JasperFx cannot override the environment name when running against a pre-build IHost. Try setting dotnet run --environment Name before the \"--\" separator in your command arguments to pass it directly to the dotnet command line[/]");
                AnsiConsole.MarkupLine("");
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
        // LogLevel
        if (LogLevelFlag.HasValue)
        {
            AnsiConsole.MarkupLine($"[gray]Overwriting the minimum log level to {LogLevelFlag.Value}[/]");
            try
            {
                if (HostBuilder is PreBuiltHostBuilder builder)
                {
                    var options = builder.Host.Services.GetService(typeof(LoggerFilterOptions)) as LoggerFilterOptions;
                    options ??=
                        (builder.Host.Services.GetService(typeof(IOptionsMonitor<LoggerFilterOptions>)) as
                            IOptionsMonitor<LoggerFilterOptions>)
                        ?.CurrentValue;

                    if (options != null)
                    {
                        options.MinLevel = LogLevel.Error;
                    }
                }
                else
                {
                    HostBuilder.ConfigureLogging(x => x.SetMinimumLevel(LogLevelFlag.Value));
                }
            }
            catch (Exception)
            {
                AnsiConsole.Markup("[gray]Unable to override the logging level[/]");
            }
        }

        if (VerboseFlag)
        {
            Console.WriteLine("Verbose flag is on.");

            // The --verbose flag adds console and
            // debug logging, as well as setting
            // the minimum logging level down to debug
            HostBuilder.ConfigureLogging(x => { x.SetMinimumLevel(LogLevel.Debug); });
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