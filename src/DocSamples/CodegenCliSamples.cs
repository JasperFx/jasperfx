using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;

namespace DocSamples;

public class CodegenCliSamples
{
    #region sample_type_load_mode_dynamic

    public static void ConfigureDynamicMode()
    {
        // Always generate types at runtime. Best for development.
        var rules = new GenerationRules("MyApp.Generated")
        {
            TypeLoadMode = TypeLoadMode.Dynamic
        };
    }

    #endregion

    #region sample_type_load_mode_static

    public static void ConfigureStaticMode()
    {
        // Types must be pre-built in the application assembly.
        // Throws if generated types are missing.
        var rules = new GenerationRules("MyApp.Generated")
        {
            TypeLoadMode = TypeLoadMode.Static
        };
    }

    #endregion

    #region sample_type_load_mode_auto

    public static void ConfigureAutoMode()
    {
        // Try to load pre-built types first, fall back to runtime generation
        var rules = new GenerationRules("MyApp.Generated")
        {
            TypeLoadMode = TypeLoadMode.Auto
        };
    }

    #endregion

    #region sample_codegen_cli_setup

    public static async Task<int> SetupWithCodegenCommand(string[] args)
    {
        return await Host
            .CreateDefaultBuilder()
            .ApplyJasperFxExtensions()
            .RunJasperFxCommands(args);

        // Run with: dotnet run -- codegen preview
        // Run with: dotnet run -- codegen write
        // Run with: dotnet run -- codegen delete
    }

    #endregion
}
