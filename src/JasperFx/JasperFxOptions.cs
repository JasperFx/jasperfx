using System.Diagnostics;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CommandLine;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Core.TypeScanning;
using JasperFx.Descriptors;
using JasperFx.Environment;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JasperFx;

public class JasperFxOptions : SystemPartBase
{
    public static bool HasReferenceToJasperFxTool(Assembly assembly)
    {
        var names = assembly.GetReferencedAssemblies();
        foreach (var name in names)
        {
            var reference = Assembly.Load(name);
            if (reference != null && reference.HasAttribute<JasperFxToolAttribute>()) return true;
        }

        return false;
    }
    
    /// <summary>
    ///     You may use this to "help" Wolverine & other Critter Stack tools in testing scenarios to force
    ///     it to consider this assembly as the main application assembly rather
    ///     that assuming that the IDE or test runner assembly is the application assembly
    /// </summary>
    public static Assembly? RememberedApplicationAssembly;
    
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
    public TenantIdStyle TenantIdStyle
    {
        get => _tenantIdStyle;
        set => _tenantIdStyle = value;
    }

    /// <summary>
    /// Resource settings for development time. Defaults are CreateOrUpdate
    /// for resources, Dynamic for code generation
    /// </summary>
    [ChildDescription]
    public Profile Development
    {
        get => _development;
        private set => _development = value;
    }

    /// <summary>
    /// Resource settings for production time. Defaults are CreateOrUpdate
    /// for resources, Dynamic for code generation. Change these to optimize
    /// code start times at production start
    /// </summary>
    [ChildDescription]
    public Profile Production
    {
        get => _production;
        private set => _production = value;
    }

    [ChildDescription]
    public Profile ActiveProfile
    {
        get => _activeProfile;
        private set => _activeProfile = value;
    }

    /// <summary>
    /// Use to override the environment name that JasperFx "thinks" means that you are in
    /// development mode. Default is to use "Development"
    /// </summary>
    public string? DevelopmentEnvironmentName
    {
        get => _developmentEnvironmentName;
        set => _developmentEnvironmentName = value;
    }

    /// <summary>
    ///     The main application assembly. By default this is the entry assembly for the application,
    ///     but you may need to change this in testing scenarios
    /// </summary>
    public Assembly? ApplicationAssembly
    {
        get => _applicationAssembly;
        set
        {
            _applicationAssembly = value;
            _serviceName ??= _applicationAssembly.GetName().Name;
        }
    }

    private void establishApplicationAssembly(string? assemblyName)
    {
        if (assemblyName.IsNotEmpty())
        {
            ApplicationAssembly ??= Assembly.Load(assemblyName);
        }
        else if (RememberedApplicationAssembly != null)
        {
            ApplicationAssembly = RememberedApplicationAssembly;
        }
        else
        {
            RememberedApplicationAssembly = ApplicationAssembly = DetermineCallingAssembly();
        }

        if (ApplicationAssembly == null)
        {
            throw new InvalidOperationException("Unable to determine an application assembly");
        }
    }
    
    internal static Assembly? DetermineCallingAssembly()
    {
        if (HasReferenceToJasperFxTool(Assembly.GetEntryAssembly())) return Assembly.GetEntryAssembly();
        
        var stack = new StackTrace();
        var frames = stack.GetFrames();
        var jasperfxFrame = frames.LastOrDefault(x =>
            x.HasMethod() && x.GetMethod()?.DeclaringType?.Assembly.GetName().Name == "JasperFx");

        var index = Array.IndexOf(frames, jasperfxFrame);

        for (var i = index + 1; i < frames.Length; i++)
        {
            var candidate = frames[i];
            var assembly = candidate.GetMethod()?.DeclaringType?.Assembly;

            if (assembly is null)
            {
                continue;
            }

            if (assembly.HasAttribute<IgnoreAssemblyAttribute>())
            {
                continue;
            }

            var assemblyName = assembly.GetName().Name;

            if (assemblyName is null)
            {
                continue;
            }

            if (assemblyName.StartsWith("System") || assemblyName.StartsWith("Microsoft") || assemblyName.StartsWith("ReSharperTestRunner"))
            {
                continue;
            }

            return assembly;
        }

        return Assembly.GetEntryAssembly();
    }

    /// <summary>
    /// Attempts to resolve the project root directory by climbing up from the current path
    /// looking for .csproj or .sln files. This is useful for Console apps where
    /// ContentRootPath defaults to the bin folder.
    /// </summary>
    /// <param name="currentPath">The current path (typically from IHostEnvironment.ContentRootPath)</param>
    /// <returns>The resolved project root path, or null if resolution fails or is not applicable</returns>
    public static string? ResolveProjectRoot(string currentPath)
    {
        // Only attempt resolution if we appear to be in a bin folder
        var separatorChar = Path.DirectorySeparatorChar;
        var binPattern = $"{separatorChar}bin{separatorChar}";
        
        if (!currentPath.Contains(binPattern))
        {
            return null;
        }

        var directory = new DirectoryInfo(currentPath);
        
        while (directory != null)
        {
            // Look for .csproj files first (more specific to project root)
            if (directory.GetFiles("*.csproj").Any())
            {
                return directory.FullName;
            }
            
            // Fall back to .sln if no .csproj found at this level
            if (directory.GetFiles("*.sln").Any())
            {
                return directory.FullName;
            }
            
            directory = directory.Parent;
        }
        
        return null;
    }

    /// <summary>
    ///     Descriptive name of the running service. Used in diagnostics and testing support. Default is the entry assembly name. 
    /// </summary>
    public string ServiceName
    {
        get => _serviceName;
        set => _serviceName = value;
    }

    /// <summary>
    ///     Root folder where generated code should be placed. By default, this is the IHostEnvironment.ContentRootPath + "Internal/Generated"
    /// </summary>
    public string? GeneratedCodeOutputPath
    {
        get => _generatedCodeOutputPath;
        set => _generatedCodeOutputPath = value;
    }

    /// <summary>
    ///     When true, automatically resolves the project root during codegen commands by climbing
    ///     up from bin folders to find the directory containing .csproj or .sln files.
    ///     Useful for Console apps where ContentRootPath defaults to the bin folder.
    ///     Default is false for backward compatibility.
    /// </summary>
    public bool AutoResolveProjectRoot
    {
        get => _autoResolveProjectRoot;
        set => _autoResolveProjectRoot = value;
    }

    internal void ReadHostEnvironment(IHostEnvironment environment)
    {
        if (GeneratedCodeOutputPath == null)
        {
            var basePath = environment.ContentRootPath;
            
            // When running codegen commands and auto-resolve is enabled,
            // attempt to find the actual project root if we're in a bin folder
            if (AutoResolveProjectRoot && DynamicCodeBuilder.WithinCodegenCommand)
            {
                var resolvedRoot = ResolveProjectRoot(basePath);
                if (resolvedRoot != null)
                {
                    basePath = resolvedRoot;
                }
            }
            
            GeneratedCodeOutputPath = basePath.AppendPath("Internal", "Generated");
        }
        
        if (ApplicationAssembly == null)
        {
            establishApplicationAssembly(null);
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


    public string? OptionsFile
    {
        get => _optionsFile;
        set => _optionsFile = value;
    }

    /// <summary>
    /// Optional 
    /// </summary>
    public Action<CommandFactory>? Factory
    {
        get => _factory;
        set => _factory = value;
    }

    /// <summary>
    /// Default command name to execute from just "dotnet run" or if you omit the
    /// JasperFx command name. The default is "run"
    /// </summary>
    public string DefaultCommand
    {
        get => _defaultCommand;
        set => _defaultCommand = value;
    }

    private readonly List<LambdaCheck> _checks = new List<LambdaCheck>();
    private TenantIdStyle _tenantIdStyle = TenantIdStyle.CaseSensitive;
    private Profile _development = new Profile
    {
        ResourceAutoCreate = AutoCreate.CreateOrUpdate,
        GeneratedCodeMode = TypeLoadMode.Dynamic,
        SourceCodeWritingEnabled = true
    };

    private Profile _production = new Profile
    {
        ResourceAutoCreate = AutoCreate.CreateOrUpdate,
        GeneratedCodeMode = TypeLoadMode.Dynamic,
        SourceCodeWritingEnabled = true
    };

    private Profile _activeProfile;
    private string? _developmentEnvironmentName = "Development";
    private Assembly? _applicationAssembly;
    private string _serviceName;
    private string? _generatedCodeOutputPath;
    private string? _optionsFile;
    private Action<CommandFactory>? _factory;
    private string _defaultCommand = "run";
    private bool _autoResolveProjectRoot = false;

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