using System;
using JasperFx.CodeGeneration;
using Shouldly;
using Xunit;

// Deliberately NOT importing JasperFx.RuntimeCompiler at the file level: the point of jasperfx#877
// is that the simple name used to resolve to whichever of the two types a file's usings happened to
// pull in. Here the unqualified name is the JasperFx.CodeGeneration one, and the compiler-layer type
// is named in full.
namespace CodegenTests;

public class CodeGenerationExceptionTests
{
    [Fact]
    public void the_runtime_compiler_exception_is_a_code_generation_exception()
    {
        var ex = new JasperFx.RuntimeCompiler.CodeGenerationException("SomeGenerator", new Exception("boom"));

        // A single catch of the unqualified name now covers both layers, so it stops mattering
        // which one a using directive resolves.
        ex.ShouldBeAssignableTo<CodeGenerationException>();
    }

    [Fact]
    public void the_runtime_compiler_exception_keeps_its_message_subject_and_inner()
    {
        var inner = new InvalidOperationException("boom");
        var ex = new JasperFx.RuntimeCompiler.CodeGenerationException("SomeGenerator", inner);

        ex.Message.ShouldBe("Error while trying to generate code for 'SomeGenerator'");
        ex.Subject.ShouldBe("SomeGenerator");
        ex.InnerException.ShouldBeSameAs(inner);
    }

    [Fact]
    public void the_code_file_exception_names_the_file_and_its_type()
    {
        var inner = new InvalidOperationException("boom");
        var ex = new CodeGenerationException(new FakeCodeFile(), inner);

        ex.Message.ShouldStartWith("Error trying to generate the code for file fake.cs of type ");
        ex.Message.ShouldEndWith(nameof(FakeCodeFile));
        ex.InnerException.ShouldBeSameAs(inner);
    }

    private class FakeCodeFile : ICodeFile
    {
        public string FileName => "fake.cs";

        public void AssembleTypes(GeneratedAssembly assembly)
        {
            throw new NotSupportedException();
        }

        public System.Threading.Tasks.Task<bool> AttachTypes(GenerationRules rules,
            System.Reflection.Assembly assembly, IServiceProvider? services, string containingNamespace)
        {
            throw new NotSupportedException();
        }

        public bool AttachTypesSynchronously(GenerationRules rules, System.Reflection.Assembly assembly,
            IServiceProvider? services, string containingNamespace)
        {
            throw new NotSupportedException();
        }
    }
}
