using System.Reflection;
using JasperFx.CodeGeneration.Model;

namespace JasperFx.CodeGeneration;

public interface IAssemblyGenerator
{
    string? AssemblyName { get; set; }

    /// <summary>
    /// Tells Roslyn to reference the given assembly and any of its dependencies
    /// when compiling code
    /// </summary>
    /// <param name="assembly"></param>
    void ReferenceAssembly(Assembly assembly);

    /// <summary>
    /// Reference the assembly containing the type "T"
    /// </summary>
    /// <typeparam name="T"></typeparam>
    void ReferenceAssemblyContainingType<T>();

    /// <summary>
    /// Compile code built up by using an ISourceWriter to a new assembly in memory
    /// </summary>
    /// <param name="write"></param>
    /// <returns></returns>
    Assembly Generate(Action<ISourceWriter> write);

    /// <summary>
    /// Compile the code passed into this method to a new assembly in memory
    /// </summary>
    /// <param name="code"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    Assembly Generate(string code);

    string Compile(GeneratedAssembly generatedAssembly, IServiceVariableSource? services = null);
}