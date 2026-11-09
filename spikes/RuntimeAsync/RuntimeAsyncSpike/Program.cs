using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// Shaped like JasperFx/Wolverine generated handlers: async Task, ValueTask, Task<T>, await inside
// try/finally and try/catch, a using, and a non-async Task-returning pass-through.
const string code = """
using System;
using System.Threading.Tasks;

namespace Generated
{
    public class GeneratedHandler
    {
        public async Task<int> HandleAsync(int input)
        {
            await Task.Yield();
            var total = input;
            try
            {
                await Task.Delay(1);
                total += 1;
            }
            finally
            {
                await Task.Yield();
                total += 10;
            }

            try
            {
                await Thrower();
            }
            catch (InvalidOperationException)
            {
                await Task.Yield();
                total += 100;
            }

            await using (var d = new Disposable())
            {
                total += await Value(1000);
            }

            return total;
        }

        public async ValueTask<int> Value(int x)
        {
            await Task.Yield();
            return x;
        }

        public async Task Thrower()
        {
            await Task.Yield();
            throw new InvalidOperationException();
        }

        public Task PassThrough() => Task.CompletedTask;
    }

    public class Disposable : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Task.Yield();
    }
}
""";

Console.WriteLine($"Runtime:      {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"Roslyn:       {typeof(CSharpCompilation).Assembly.GetName().Version} ({typeof(CSharpCompilation).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion})");
Console.WriteLine($"LanguageVersion.Latest maps to: {LanguageVersion.Latest.MapSpecifiedToEffectiveVersion()}");
var runtimeAsyncField = typeof(RuntimeFeature).GetField("Async", BindingFlags.Public | BindingFlags.Static);
Console.WriteLine($"RuntimeFeature.Async field: {(runtimeAsyncField == null ? "absent" : runtimeAsyncField.GetValue(null))}");
Console.WriteLine($"System.Runtime.CompilerServices.AsyncHelpers: {(typeof(object).Assembly.GetType("System.Runtime.CompilerServices.AsyncHelpers") == null ? "absent" : "present")}");
Console.WriteLine();

// AssemblyGenerator references implementation assemblies that are loaded in the process; the TPA
// list is the same set of files.
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
    .Split(Path.PathSeparator)
    .Where(p => p.EndsWith(".dll") && !p.Contains("Microsoft.CodeAnalysis"))
    .Select(p => MetadataReference.CreateFromFile(p))
    .ToArray();

var on = CSharpParseOptions.Default.WithFeatures([new("runtime-async", "on")]);
var suppressExperimental = new Dictionary<string, ReportDiagnostic> { ["SYSLIB5007"] = ReportDiagnostic.Suppress };
var variants = new (string Name, CSharpParseOptions Options, Dictionary<string, ReportDiagnostic>? Diagnostics)[]
{
    ("default (what AssemblyGenerator does today)", CSharpParseOptions.Default, null),
    ("Features runtime-async=on", on, null),
    ("Features runtime-async=on + LangVersion Preview", on.WithLanguageVersion(LanguageVersion.Preview), null),
    ("Features runtime-async=on + SYSLIB5007 suppressed", on, suppressExperimental),
};

var failed = false;
foreach (var (name, options, diagnostics) in variants)
{
    Console.WriteLine($"== {name}");
    var tree = CSharpSyntaxTree.ParseText(code, options);
    var compilation = CSharpCompilation.Create("Generated_" + Guid.NewGuid().ToString("N"), [tree], references,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithSpecificDiagnosticOptions(diagnostics ?? new Dictionary<string, ReportDiagnostic>()));

    using var stream = new MemoryStream();
    var result = compilation.Emit(stream);
    foreach (var d in result.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning)
                 .GroupBy(d => $"{d.Severity} {d.Id}: {d.GetMessage()}"))
    {
        Console.WriteLine($"   {d.Key} (x{d.Count()})");
    }

    if (!result.Success)
    {
        Console.WriteLine("   EMIT FAILED");
        failed = true;
        continue;
    }

    Inspect(stream.ToArray());

    stream.Position = 0;
    var context = new AssemblyLoadContext(name, isCollectible: true);
    try
    {
        var assembly = context.LoadFromStream(stream);
        var handler = Activator.CreateInstance(assembly.GetType("Generated.GeneratedHandler")!)!;
        var value = await (Task<int>)handler.GetType().GetMethod("HandleAsync")!.Invoke(handler, [1])!;
        Console.WriteLine($"   ran HandleAsync(1) = {value} (expected 1112) {(value == 1112 ? "OK" : "WRONG")}");
        failed |= value != 1112;
    }
    catch (Exception e)
    {
        Console.WriteLine($"   RUN FAILED: {e.GetType().Name}: {e.Message}");
        failed = true;
    }
    finally
    {
        context.Unload();
    }

    Console.WriteLine();
}

return failed ? 1 : 0;

static void Inspect(byte[] image)
{
    using var pe = new PEReader(new MemoryStream(image));
    var md = pe.GetMetadataReader();
    var stateMachines = md.TypeDefinitions
        .Select(md.GetTypeDefinition)
        .Count(t => md.GetString(t.Name).Contains(">d__"));
    Console.WriteLine($"   compiler-generated state machine types: {stateMachines}");
    foreach (var handle in md.MethodDefinitions)
    {
        var method = md.GetMethodDefinition(handle);
        var typeName = md.GetString(md.GetTypeDefinition(method.GetDeclaringType()).Name);
        if (typeName.Contains('<')) continue;
        var methodName = md.GetString(method.Name);
        if (methodName == ".ctor") continue;
        // MethodImplAttributes.Async = 0x2000 (ECMA-335 augment for runtime-async)
        var isRuntimeAsync = ((int)method.ImplAttributes & 0x2000) != 0;
        var hasStateMachineAttr = method.GetCustomAttributes()
            .Select(md.GetCustomAttribute)
            .Any(a => a.Constructor.Kind == HandleKind.MemberReference &&
                      md.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent is { Kind: HandleKind.TypeReference } p &&
                      md.GetString(md.GetTypeReference((TypeReferenceHandle)p).Name) == "AsyncStateMachineAttribute");
        Console.WriteLine($"   {typeName}.{methodName}: MethodImpl.Async={isRuntimeAsync}, [AsyncStateMachine]={hasStateMachineAttr}");
    }
}
