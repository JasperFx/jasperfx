using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace JasperFx;

public static class JasperFxServiceCollectionExtensions
{
    /// <summary>
    /// Configure JasperFx and Critter Stack tool behavior for resource management at runtime
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configure">Optional configuration of the JasperFxDefaults for resource management</param>
    /// <returns></returns>
    public static IServiceCollection JasperFxDefaults(this IServiceCollection services, Action<JasperFxOptions>? configure = null)
    {
        var optionsBuilder = services.AddOptions<JasperFxOptions>();
        if (configure != null)
        {
            optionsBuilder.Configure(configure!);
        }
        
        optionsBuilder.PostConfigure<IHostEnvironment>((o, e) => o.ReadHostEnvironment(e));
        services.AddSingleton<JasperFxOptions>(s =>
        {
            return s.GetRequiredService<IOptions<JasperFxOptions>>().Value;
        });

        return services;
    }
}

public class JasperFxOptions
{
    public JasperFxOptions()
    {
        ActiveProfile = Development;
    }

    /// <summary>
    /// Resource settings for development time. Defaults are CreateOrUpdate
    /// for resources, Dynamic for code generation
    /// </summary>
    public Profile Development { get; private set; } = new Profile
    {
        AutoCreate = AutoCreate.CreateOrUpdate,
        GeneratedCodeMode = TypeLoadMode.Dynamic,
        SourceCodeWritingEnabled = true
    };

    /// <summary>
    /// Resource settings for production time. Defaults are CreateOrUpdate
    /// for resources, Dynamic for code generation. Change these to optimize
    /// code start times at production start
    /// </summary>
    public Profile Production { get; private set; } = new Profile
    {
        AutoCreate = AutoCreate.CreateOrUpdate,
        GeneratedCodeMode = TypeLoadMode.Dynamic,
        SourceCodeWritingEnabled = true
    };
    
    public Profile ActiveProfile { get; private set; } 
    
    /// <summary>
    /// Use to override the environment name that JasperFx "thinks" means that you are in
    /// development mode. Default is to use "Development"
    /// </summary>
    public string? DevelopmentEnvironmentName { get; set; }
    
    /// <summary>
    ///     The main application assembly. By default this is the entry assembly for the application,
    ///     but you may need to change this in testing scenarios
    /// </summary>
    public Assembly? ApplicationAssembly { get; set; } 
    
    /// <summary>
    ///     Root folder where generated code should be placed. By default, this is the IHostEnvironment.ContentRootPath + "Internal/Generated"
    /// </summary>
    public string? GeneratedCodeOutputPath { get; set; }
    
    internal void ReadHostEnvironment(IHostEnvironment environment)
    {
        GeneratedCodeOutputPath ??= environment.ContentRootPath.AppendPath("Internal", "Generated");
        if (environment.ApplicationName.IsNotEmpty())
        {
            ApplicationAssembly ??= Assembly.Load(environment.ApplicationName) ?? Assembly.GetEntryAssembly();
        }

        if (DevelopmentEnvironmentName.IsNotEmpty() && environment.EnvironmentName.EqualsIgnoreCase(DevelopmentEnvironmentName))
        {
            ActiveProfile = Development;
        }
        else if (environment.IsDevelopment())
        {
            ActiveProfile = Development;
        }
        else
        {
            ActiveProfile = Production;
        }

        ApplicationAssembly ??= Assembly.GetEntryAssembly();
    }
    
    /// <summary>
    ///     Meant for testing scenarios to "help" .Net understand where the IHostEnvironment for the
    ///     Host. You may have to specify the relative path to the entry project folder from the AppContext.BaseDirectory
    /// </summary>
    /// <param name="assembly"></param>
    /// <param name="hintPath"></param>
    /// <returns></returns>
    public void SetApplicationProject(Assembly assembly,
        string? hintPath = null)
    {
        ApplicationAssembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        
        var path = AppContext.BaseDirectory.ToFullPath();
        if (hintPath.IsNotEmpty())
        {
            path = path.AppendPath(hintPath).ToFullPath();
        }
        else
        {
            try
            {
                path = path.TrimEnd(Path.DirectorySeparatorChar);
                while (!path.EndsWith("bin"))
                {
                    path = path.ParentDirectory();
                }

                // Go up once to get to the test project directory, then up again to the "src" level,
                // then "down" to the application directory
                path = path.ParentDirectory().ParentDirectory().AppendPath(assembly.GetName().Name);
            }
            catch (Exception e)
            {
                Console.WriteLine("Unable to determine the ");
                Console.WriteLine(e);
                path = AppContext.BaseDirectory.ToFullPath();
            }
        }

        GeneratedCodeOutputPath = path.AppendPath("Internal", "Generated");
    }
}

public class Profile
{
    /// <summary>
    /// Is this application allowed to write source code files? True by default for development time, false by
    /// default in production mode
    /// </summary>
    public bool SourceCodeWritingEnabled { get; set; } = true;
    
    /// <summary>
    /// Default code generation mode to either generate code at runtime (Dynamic), or attempt to load types from the application assembly. Dynamic by default.
    /// </summary>
    public TypeLoadMode GeneratedCodeMode { get; set; }

    /// <summary>
    ///     Whether or not any JasperFx or Critter Stack tool should attempt to create any missing infrastructure objects like database schema objects or message broker queues at runtime. This
    ///     property is "CreateOrUpdate" by default for more efficient development, but can be set to lower values for production usage.
    /// </summary>
    public AutoCreate AutoCreate { get; set; } = AutoCreate.CreateOrUpdate;

    /// <summary>
    /// Add an assertion at bootstrapping time to assert that all expected pre-generated
    /// types from code generation already exist in the application assembly. Default is false.
    /// </summary>
    public bool AssertAllPreGeneratedTypesExist { get; set; } = false;
    
}