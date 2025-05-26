using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CommandLine;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Environment;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JasperFx;

public class JasperFxOptions : SystemPartBase
{
    public JasperFxOptions() : base("JasperFx Options", new Uri("system://jasperfx"))
    {
        ActiveProfile = Development;
    }


    private readonly List<string> _requiredFiles = new();
    public string[] RequiredFiles => _requiredFiles.ToArray();

    /// <summary>
    /// Tell JasperFx that the following file path is required, and this will
    /// add a file exists check to the environment tests for this application
    /// </summary>
    /// <param name="path"></param>
    public void RequireFile(string path)
    {
        _requiredFiles.Fill(path);
    }

    
    /// <summary>
    /// Tenant Id naming rules for this application. Default is to use case-sensitive names and not
    /// to correct any supplied tenant id
    /// </summary>
    public TenantIdStyle TenantIdStyle { get; set; } = TenantIdStyle.CaseSensitive;

    /// <summary>
    /// Resource settings for development time. Defaults are CreateOrUpdate
    /// for resources, Dynamic for code generation
    /// </summary>
    [ChildDescription]
    public Profile Development { get; private set; } = new Profile
    {
        ResourceAutoCreate = AutoCreate.CreateOrUpdate,
        GeneratedCodeMode = TypeLoadMode.Dynamic,
        SourceCodeWritingEnabled = true
    };

    /// <summary>
    /// Resource settings for production time. Defaults are CreateOrUpdate
    /// for resources, Dynamic for code generation. Change these to optimize
    /// code start times at production start
    /// </summary>
    [ChildDescription]
    public Profile Production { get; private set; } = new Profile
    {
        ResourceAutoCreate = AutoCreate.CreateOrUpdate,
        GeneratedCodeMode = TypeLoadMode.Dynamic,
        SourceCodeWritingEnabled = true
    };
    
    [ChildDescription]
    public Profile ActiveProfile { get; private set; }

    /// <summary>
    /// Use to override the environment name that JasperFx "thinks" means that you are in
    /// development mode. Default is to use "Development"
    /// </summary>
    public string? DevelopmentEnvironmentName { get; set; } = "Development";
    
    /// <summary>
    ///     The main application assembly. By default this is the entry assembly for the application,
    ///     but you may need to change this in testing scenarios
    /// </summary>
    public Assembly? ApplicationAssembly { get; set; } 
    
    /// <summary>
    ///     Descriptive name of the running service. Used in diagnostics and testing support. Default is the entry assembly name. 
    /// </summary>
    public string ServiceName { get; set; } = Assembly.GetEntryAssembly()!.GetName().Name ?? "WolverineService";
    
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
                while (!path!.EndsWith("bin"))
                {
                    path = path.ParentDirectory();
                }

                // Go up once to get to the test project directory, then up again to the "src" level,
                // then "down" to the application directory
                path = path.ParentDirectory()!.ParentDirectory()!.AppendPath(assembly.GetName().Name!);
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
    
    
    public string? OptionsFile { get; set; }
    
    /// <summary>
    /// Optional 
    /// </summary>
    public Action<CommandFactory>? Factory { get; set; }
    
    /// <summary>
    /// Default command name to execute from just "dotnet run" or if you omit the
    /// JasperFx command name. The default is "run"
    /// </summary>
    public string DefaultCommand { get; set; } = "run";

    private readonly List<LambdaCheck> _checks = new List<LambdaCheck>();

    public void RegisterEnvironmentCheck(string description, Func<IServiceProvider, CancellationToken, Task> action)
    {
        _checks.Add(new LambdaCheck(description, action));
    }

    public override async Task AssertEnvironmentAsync(IServiceProvider services, EnvironmentCheckResults results, CancellationToken token)
    {
        foreach (var file in _requiredFiles)
        {
            if (File.Exists(file))
            {
                results.RegisterSuccess($"File '{file}' can be found");
            }
            else
            {
                results.RegisterFailure($"File '{file}' can be found", new FileNotFoundException("Required file cannot be found", file));
            }
        }

        foreach (var lambdaCheck in _checks)
        {
            await lambdaCheck.Assert(services, results, token);
        }

        if (ActiveProfile.AssertAllPreGeneratedTypesExist)
        {
            var collections = services.GetServices<ICodeFileCollection>().ToArray();
            foreach (var collection in collections)
            {
                try
                {
                    collection.AssertPreBuildTypesExist(services);
                    results.RegisterSuccess($"All pre build types exist for {collection}");
                }
                catch (Exception e)
                {
                    results.RegisterFailure($"All pre build types exist for {collection}", e);
                }
            }
        }
    }
}