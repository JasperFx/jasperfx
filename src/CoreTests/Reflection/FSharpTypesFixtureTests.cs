using System.Reflection;
using FSharpTypes;
using Shouldly;

namespace CoreTests.Reflection;

/// <summary>
/// Guards the FSharpTypes fixture itself rather than anything in JasperFx.
///
/// FSharpTypes exists so the reflection tests have real F# types to work over. That is only
/// worth anything if the assembly can actually be reflected over -- and for a long time it
/// could not, because FSharp.Core never reached the output directory (#580). The tests that
/// use the fixture kept passing anyway, since typeof() and member lookup don't force the
/// assembly-level attributes to resolve, so the gap stayed invisible until the xunit v3
/// migration (#577) pointed JasperFxOptions.HasReferenceToJasperFxTool at CoreTests.dll and
/// its walk over referenced assemblies hit FSharpTypes.
///
/// These tests exercise the parts that DO force FSharp.Core to load, so the fixture can't
/// silently rot back into being un-reflectable.
/// </summary>
public class FSharpTypesFixtureTests
{
    private static readonly Assembly theFSharpAssembly = typeof(FSharpGuidId).Assembly;

    [Fact]
    public void the_assembly_level_attributes_can_be_resolved()
    {
        // The F# compiler stamps FSharpInterfaceDataVersionAttribute on every assembly it emits,
        // and that attribute type lives in FSharp.Core. Without FSharp.Core alongside the output
        // this threw FileNotFoundException.
        var attributes = Should.NotThrow(() => theFSharpAssembly.GetCustomAttributes().ToArray());

        attributes.ShouldContain(x => x.GetType().Name == "FSharpInterfaceDataVersionAttribute");
    }

    [Fact]
    public void every_type_in_the_fixture_can_be_loaded()
    {
        // Unlike its siblings this one passed even with FSharp.Core missing -- type loading never
        // forces the attributes to resolve. That is exactly why the gap hid for so long. Kept as a
        // sanity check on what the fixture actually contains.
        var types = Should.NotThrow(() => theFSharpAssembly.GetTypes());

        types.ShouldContain(typeof(FSharpGuidId));
        types.ShouldContain(typeof(FSharpStringId));
        types.ShouldContain(typeof(FSharpIntId));
        types.ShouldContain(typeof(FSharpSaga));
    }

    [Fact]
    public void the_fsharp_types_carry_resolvable_fsharp_core_attributes()
    {
        // CompilationMappingAttribute is what marks these as discriminated unions, and it too
        // comes from FSharp.Core. Reading it proves the reference resolves at the type level and
        // not just at the assembly level.
        var attributes = Should.NotThrow(() => typeof(FSharpGuidId).GetCustomAttributes().ToArray());

        attributes.ShouldContain(x => x.GetType().Name == "CompilationMappingAttribute");
    }

    [Fact]
    public void fsharp_core_is_an_actual_reference_of_the_fixture()
    {
        var fsharpCore = theFSharpAssembly
            .GetReferencedAssemblies()
            .SingleOrDefault(x => x.Name == "FSharp.Core");

        fsharpCore.ShouldNotBeNull();

        // This is the walk JasperFxOptions.HasReferenceToJasperFxTool performs over the entry
        // assembly's references. It is best-effort and swallows failures now, but it should not
        // have anything to swallow here.
        Should.NotThrow(() => Assembly.Load(fsharpCore).GetCustomAttributes().ToArray());
    }
}
