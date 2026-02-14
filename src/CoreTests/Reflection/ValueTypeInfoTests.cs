using FSharpTypes;
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

    [Fact]
    public void create_for_fsharp_guid_discriminated_union()
    {
        var valueTypeInfo = ValueTypeInfo.ForType(typeof(FSharpGuidId));

        valueTypeInfo.OuterType.ShouldBe(typeof(FSharpGuidId));
        valueTypeInfo.SimpleType.ShouldBe(typeof(Guid));
        valueTypeInfo.ValueProperty.Name.ShouldBe("Item");

        var inner = Guid.NewGuid();
        var wrapper = valueTypeInfo.CreateWrapper<FSharpGuidId, Guid>()(inner);
        valueTypeInfo.UnWrapper<FSharpGuidId, Guid>()(wrapper).ShouldBe(inner);
    }

    [Fact]
    public void create_for_fsharp_string_discriminated_union()
    {
        var valueTypeInfo = ValueTypeInfo.ForType(typeof(FSharpStringId));

        valueTypeInfo.OuterType.ShouldBe(typeof(FSharpStringId));
        valueTypeInfo.SimpleType.ShouldBe(typeof(string));
        valueTypeInfo.ValueProperty.Name.ShouldBe("Item");

        var inner = Guid.NewGuid().ToString();
        var wrapper = valueTypeInfo.CreateWrapper<FSharpStringId, string>()(inner);
        valueTypeInfo.UnWrapper<FSharpStringId, string>()(wrapper).ShouldBe(inner);
    }

    [Fact]
    public void create_for_fsharp_int_discriminated_union()
    {
        var valueTypeInfo = ValueTypeInfo.ForType(typeof(FSharpIntId));

        valueTypeInfo.OuterType.ShouldBe(typeof(FSharpIntId));
        valueTypeInfo.SimpleType.ShouldBe(typeof(int));
        valueTypeInfo.ValueProperty.Name.ShouldBe("Item");

        var inner = 42;
        var wrapper = valueTypeInfo.CreateWrapper<FSharpIntId, int>()(inner);
        valueTypeInfo.UnWrapper<FSharpIntId, int>()(wrapper).ShouldBe(inner);
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