using JasperFx.Core.Reflection;
using Shouldly;

namespace CoreTests.Reflection;

public class ValueTypeInfoTests
{
    [Fact]
    public void create_for_ctor()
    {
        var valueTypeInfo = ValueTypeInfo.ForType(typeof(InvoiceId));
        
        valueTypeInfo.OuterType.ShouldBe(typeof(InvoiceId));
        valueTypeInfo.SimpleType.ShouldBe(typeof(Guid));
        valueTypeInfo.ValueProperty.Name.ShouldBe(nameof(InvoiceId.Value));

        var inner = Guid.NewGuid();
        valueTypeInfo.CreateWrapper<InvoiceId, Guid>()(inner).Value.ShouldBe(inner);
        
        valueTypeInfo.UnWrapper<InvoiceId, Guid>()(new InvoiceId(inner)).ShouldBe(inner);
    }

    [Fact]
    public void create_for_constructor_function()
    {
        var valueTypeInfo = ValueTypeInfo.ForType(typeof(OrderId));
        
        valueTypeInfo.OuterType.ShouldBe(typeof(OrderId));
        valueTypeInfo.SimpleType.ShouldBe(typeof(string));
        valueTypeInfo.ValueProperty.Name.ShouldBe(nameof(OrderId.Inner));
        
        var inner = Guid.NewGuid().ToString();
        valueTypeInfo.CreateWrapper<OrderId, string>()(inner).Inner.ShouldBe(inner);
        
        valueTypeInfo.UnWrapper<OrderId, string>()(OrderId.From(inner)).ShouldBe(inner);
    }
}

public record InvoiceId(Guid Value);

public class OrderId
{
    private OrderId(string inner)
    {
        Inner = inner;
    }

    public static OrderId From(string inner) => new OrderId(inner);

    public string Inner { get; }
}