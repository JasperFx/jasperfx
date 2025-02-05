using JasperFx.Core.Reflection;
using Shouldly;

namespace CoreTests.Reflection;

public class TypeNameExtensionsTester
{
    [Theory]
    [InlineData(typeof(int), "int")]
    [InlineData(typeof(string), "string")]
    [InlineData(typeof(TypeExtensionsTester), nameof(TypeExtensionsTester))]
    [InlineData(typeof(List<string>), "List<string>")]
    public void short_name(Type type, string expected)
    {
        type.ShortNameInCode().ShouldBe(expected);
    }

    [Theory]
    [InlineData(typeof(int), "int")]
    [InlineData(typeof(string), "string")]
    [InlineData(typeof(TypeExtensionsTester), "CoreTests.Reflection.TypeExtensionsTester")]
    [InlineData(typeof(List<string>), "System.Collections.Generic.List<string>")]
    public void full_name(Type type, string expected)
    {
        type.FullNameInCode().ShouldBe(expected);
    }
}