using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Shouldly;

namespace CodegenTests;

public class CastVariableTests
{
    [Fact]
    public void does_the_cast()
    {
        var inner = Variable.For<Basketball>();
        var cast = new CastVariable(inner, typeof(Ball));

        cast.Usage.ShouldBe($"(({typeof(Ball).FullNameInCode()}){inner.Usage})");
    }
}