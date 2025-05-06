using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Util;
using JasperFx.Core;

namespace JasperFx.CodeGeneration;

public class DynamicCodeBuilder
{
    public DynamicCodeBuilder(IServiceProvider services, ICodeFileCollection[] collections)
    {
        Services = services;
        Collections = collections;
    }

    public IServiceVariableSource? ServiceVariableSource { get; set; }

    public string[] ChildNamespaces => Collections.Select(x => x.ChildNamespace).ToArray();

    public IServiceProvider Services { get; }

    public ICodeFileCollection[] Collections { get; }

    public string GenerateAllCode()
    {
        WithinCodegenCommand = true;

        try
        {
            var writer = new StringWriter();

            foreach (var generator in Collections)
            {
                var code = generateCode(generator);
                writer.WriteLine(code);
                writer.WriteLine();
                writer.WriteLine();
            }

            return writer.ToString();
        }
        finally
        {
            WithinCodegenCommand = false;
        }
    }

    public void DeleteAllGeneratedCode()
    {
        WithinCodegenCommand = true;

        try
        {
            foreach (var directory in Collections.Select(x => x.Rules.GeneratedCodeOutputPath).Distinct())
            {
                FileSystem.CleanDirectory(directory);
                FileSystem.DeleteDirectoryIfExists(directory);

                Console.WriteLine($"Deleted directory {directory}");
            }
        }
        finally
        {
            WithinCodegenCommand = false;
        }
    }

    public string GenerateCodeFor(string childNamespace)
    {
        WithinCodegenCommand = true;

        try
        {
            var generator = Collections.FirstOrDefault(x => x.ChildNamespace.EqualsIgnoreCase(childNamespace));
            if (generator == null)
            {
                throw new ArgumentOutOfRangeException(
                    $"Unknown {nameof(childNamespace)} '{childNamespace}'. Known code types are {String.Join(", ", ChildNamespaces)}");
            }

            return generateCode(generator);
        }
        finally
        {
            WithinCodegenCommand = false;
        }
    }

    public void WriteGeneratedCode(Action<string> onFileWritten)
    {
        WithinCodegenCommand = true;

        try
        {
            foreach (var collection in Collections)
            {
                var directory = collection.Rules.GeneratedCodeOutputPath.ToFullPath();
                FileSystem.CreateDirectoryIfNotExists(directory);

                var exportDirectory = collection.ToExportDirectory(directory);


                foreach (var file in collection.BuildFiles())
                {
                    var generatedAssembly = collection.StartAssembly(collection.Rules);
                    file.AssembleTypes(generatedAssembly);

                    var code = collection is ICodeFileCollectionWithServices ? generatedAssembly.GenerateCode(ServiceVariableSource) : generatedAssembly.GenerateCode(null);
                    var fileName = Path.Combine(exportDirectory, file.FileName.Replace(" ", "_") + ".cs");
                    File.WriteAllText(fileName, code);
                    onFileWritten(fileName);
                }
            }
        }
        finally
        {
            WithinCodegenCommand = false;
        }
    }

    private string generateCode(ICodeFileCollection collection)
    {
        if (collection.ChildNamespace.IsEmpty())
        {
            throw new InvalidOperationException(
                $"Missing {nameof(ICodeFileCollection.ChildNamespace)} for {collection}");
        }

        var @namespace = collection.ToNamespace(collection.Rules);

        var generatedAssembly = new GeneratedAssembly(collection.Rules, @namespace);
        var files = collection.BuildFiles();
        foreach (var file in files)
        {
            try
            {
                file.AssembleTypes(generatedAssembly);
            }
            catch (Exception e)
            {
                throw new CodeGenerationException(file, e);
            }
        }

        // This was important. Each source code collection should explicitly opt into using IoC services rather
        // than making that automatic
        var services = collection is ICodeFileCollectionWithServices ? ServiceVariableSource : null;
        return generatedAssembly.GenerateCode(services);
    }

    /// <summary>
    ///     Attempts to generate all the known code types in the system
    /// </summary>
    /// <param name="withAssembly"></param>
    /// <exception cref="GeneratorCompilationFailureException"></exception>
    public void TryBuildAndCompileAll(Action<GeneratedAssembly, IServiceVariableSource?> withAssembly)
    {
        foreach (var collection in Collections)
        {
            var generatedAssembly = collection.AssembleTypes(collection.Rules);

            try
            {
                withAssembly(generatedAssembly, ServiceVariableSource);
            }
            catch (Exception e)
            {
                throw new GeneratorCompilationFailureException(collection, e);
            }
        }
    }

    /// <summary>
    ///     Attach pre-built types in the application assembly
    /// </summary>
    /// <param name="assembly">
    ///     The assembly containing the pre-built types. If null, this falls back to the entry assembly of
    ///     the running application
    /// </param>
    public async Task LoadPrebuiltTypes(Assembly? assembly = null)
    {
        foreach (var collection in Collections)
        {
            foreach (var file in collection.BuildFiles())
            {
                var @namespace = $"{collection.Rules.GeneratedNamespace}.{collection.ChildNamespace}";
                await file.AttachTypes(collection.Rules, assembly ?? collection.Rules.ApplicationAssembly, Services,
                    @namespace);
            }
        }
    }

    public static bool WithinCodegenCommand { get; set; } = false;
}