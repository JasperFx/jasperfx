using JasperFx.CodeGeneration;
using JasperFx.Core;

namespace JasperFx;

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
    public AutoCreate ResourceAutoCreate { get; set; } = AutoCreate.CreateOrUpdate;

    /// <summary>
    /// Add an assertion at bootstrapping time to assert that all expected pre-generated
    /// types from code generation already exist in the application assembly. Default is false.
    /// </summary>
    public bool AssertAllPreGeneratedTypesExist { get; set; } = false;

    /// <summary>
    ///     Root folder where generated code should be placed. By default, this is the IHostEnvironment.ContentRootPath
    /// </summary>
    public string? GeneratedCodeOutputPath { get; set; } = AppContext.BaseDirectory
        .AppendPath("Internal", "Generated");
    
}