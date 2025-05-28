using JasperFx.CodeGeneration.Model;
using Shouldly;

namespace CodegenTests;

public class ConstantTests
{
    [Fact]
    public void get_constant_for_type()
    {
        var variable = Constant.ForType(GetType());
        variable.VariableType.ShouldBe(typeof(Type));
        variable.Usage.ShouldBe("typeof(CodegenTests.ConstantTests)");
    }
}