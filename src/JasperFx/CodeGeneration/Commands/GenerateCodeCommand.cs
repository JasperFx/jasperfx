using JasperFx;
using JasperFx.CodeGeneration.Model;
using JasperFx.CommandLine;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

[assembly: JasperFxAssembly]

namespace JasperFx.CodeGeneration.Commands;

[Description("Utilities for working with JasperFx.CodeGeneration and JasperFx.RuntimeCompiler", Name = "codegen")]
public class GenerateCodeCommand : JasperFxCommand<GenerateCodeInput>
{
    public GenerateCodeCommand()
    {
        Usage("Preview").Arguments();
        Usage("All actions").Arguments(x => x.Action);
    }

    public override bool Execute(GenerateCodeInput input)
    {
        using var host = input.BuildHost();

        var collections = host.Services.GetServices<ICodeFileCollection>().ToArray();
        if (!collections.Any())
        {
            AnsiConsole.Write($"[red]No registered {nameof(ICodeFileCollection)} services were detected, aborting.[/]");
            return false;
        }

        var builder = new DynamicCodeBuilder(host.Services, collections)
        {
            ServiceVariableSource = host.Services.GetService<IServiceVariableSource>()
        };

        switch (input.Action)
        {
            case CodeAction.preview:
                var code = input.TypeFlag.IsEmpty()
                    ? builder.GenerateAllCode()
                    : builder.GenerateCodeFor(input.TypeFlag);
                Console.WriteLine(code);
                break;

            case CodeAction.delete:
                builder.DeleteAllGeneratedCode();
                break;

            case CodeAction.write:
                builder.WriteGeneratedCode(file => Console.WriteLine("Wrote generated code file to " + file));
                break;

            default:
                throw new ArgumentOutOfRangeException();
        }


        return true;
    }

    // Stupid workaround for oddball usages of Marten where it has compiled file collections
    // that themselves have to be compiled.
}