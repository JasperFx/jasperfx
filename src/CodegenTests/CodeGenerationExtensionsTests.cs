using System.Reflection;
using JasperFx.CodeGeneration;
using Shouldly;
using Xunit;

namespace CodegenTests;

public class CodeGenerationExtensionsTests
{
    [Fact]
    public void FindPreGeneratedType_returns_a_known_exported_type()
    {
        var assembly = typeof(CodeGenerationExtensionsTests).Assembly;
        var type = assembly.FindPreGeneratedType(
            typeof(CodeGenerationExtensionsTests).Namespace!,
            nameof(CodeGenerationExtensionsTests));

        type.ShouldBe(typeof(CodeGenerationExtensionsTests));
    }

    [Fact]
    public void FindPreGeneratedType_returns_null_for_unknown_name()
    {
        var assembly = typeof(CodeGenerationExtensionsTests).Assembly;
        var type = assembly.FindPreGeneratedType("Some.Made.Up.Namespace", "DefinitelyDoesNotExist");

        type.ShouldBeNull();
    }

    [Fact]
    public void FindPreGeneratedType_is_repeatable_against_the_same_assembly()
    {
        // Lookups are now backed by a per-assembly indexed cache. The cache should
        // be transparent: repeated calls return the same Type without enumerating
        // ExportedTypes again.
        var assembly = typeof(CodeGenerationExtensionsTests).Assembly;

        var first = assembly.FindPreGeneratedType(
            typeof(CodeGenerationExtensionsTests).Namespace!,
            nameof(CodeGenerationExtensionsTests));
        var second = assembly.FindPreGeneratedType(
            typeof(CodeGenerationExtensionsTests).Namespace!,
            nameof(CodeGenerationExtensionsTests));

        first.ShouldBeSameAs(second);
        first.ShouldBe(typeof(CodeGenerationExtensionsTests));
    }

    [Fact]
    public void FindPreGeneratedType_handles_a_distinct_assembly_independently()
    {
        // Smoke test that the per-assembly index keys correctly: looking up a
        // type defined in System.Private.CoreLib via the BCL assembly should
        // find it without the cache crossing wires with the test assembly above.
        var coreLib = typeof(string).Assembly;
        var stringType = coreLib.FindPreGeneratedType("System", "String");

        stringType.ShouldBe(typeof(string));
    }
}
