using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace DocSamples;

public class CodegenOverviewSamples
{
    #region sample_codegen_overview

    public static string GenerateGreeterCode()
    {
        var rules = new GenerationRules("MyApp.Generated");
        var assembly = new GeneratedAssembly(rules);

        // Add a new type that implements IGreeter
        var type = assembly.AddType("HelloGreeter", typeof(IGreeter));

        // Get the method defined by the interface
        var method = type.MethodFor("Greet");

        // Add a frame that writes a line of code
        method.Frames.Code("return \"Hello, \" + {0};", Use.Type<string>());

        // Generate the C# source code
        var code = assembly.GenerateCode();

        return code;
    }

    public interface IGreeter
    {
        string Greet(string name);
    }

    #endregion
}
