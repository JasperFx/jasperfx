using JasperFx.Core.Reflection;
using Shouldly;

namespace CoreTests.Core.Reflection;

public class enumerable_types
{
    [Theory]
    [InlineData(typeof(IWidget[]), true)]
    [InlineData(typeof(IList<IWidget>), true)]
    [InlineData(typeof(IEnumerable<IWidget>), true)]
    [InlineData(typeof(List<IWidget>), true)]
    [InlineData(typeof(IWidget), false)]
    public void is_enumerable(Type type, bool expected)
    {
        type.IsEnumerable().ShouldBe(expected);
    }

    [Theory]
    [InlineData(typeof(IWidget[]))]
    [InlineData(typeof(IList<IWidget>))]
    [InlineData(typeof(IEnumerable<IWidget>))]
    [InlineData(typeof(List<IWidget>))]
    public void determine_element_type(Type enumerableType)
    {
        enumerableType.DetermineElementType().ShouldBe(typeof(IWidget));
    }
    
    public interface IWidget{}
}