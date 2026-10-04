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
    
    [Fact]
    public void create_for_type_with_static_properties()
    {
        var valueTypeInfo = ValueTypeInfo.ForType(typeof(ValueWithStatic));
        
        valueTypeInfo.OuterType.ShouldBe(typeof(ValueWithStatic));
        valueTypeInfo.SimpleType.ShouldBe(typeof(int));
        valueTypeInfo.ValueProperty.Name.ShouldBe(nameof(ValueWithStatic.Value));

        var inner = 123;
        valueTypeInfo.CreateWrapper<ValueWithStatic, int>()(inner).Value.ShouldBe(inner);
        
        valueTypeInfo.UnWrapper<ValueWithStatic, int>()(new ValueWithStatic(inner)).ShouldBe(inner);
    }

    // GH-942: the reflective paths are what CreateWrapper / UnWrapper use when
    // RuntimeFeature.IsDynamicCodeSupported is false (Native AOT)

    [Fact]
    public void reflective_wrapper_and_unwrapper_for_ctor()
    {
        var valueTypeInfo = ValueTypeInfo.ForType(typeof(InvoiceId));

        var inner = Guid.NewGuid();
        valueTypeInfo.ReflectiveWrapper<InvoiceId, Guid>()(inner).Value.ShouldBe(inner);
        valueTypeInfo.ReflectiveUnWrapper<InvoiceId, Guid>()(new InvoiceId(inner)).ShouldBe(inner);
    }

    [Fact]
    public void reflective_wrapper_and_unwrapper_for_readonly_record_struct()
    {
        var valueTypeInfo = ValueTypeInfo.ForType(typeof(AlertId));

        var inner = Guid.NewGuid();
        valueTypeInfo.ReflectiveWrapper<AlertId, Guid>()(inner).Value.ShouldBe(inner);
        valueTypeInfo.ReflectiveUnWrapper<AlertId, Guid>()(new AlertId(inner)).ShouldBe(inner);
    }

    [Fact]
    public void reflective_wrapper_and_unwrapper_for_builder_method()
    {
        var valueTypeInfo = ValueTypeInfo.ForType(typeof(OrderId));

        var inner = Guid.NewGuid().ToString();
        valueTypeInfo.ReflectiveWrapper<OrderId, string>()(inner).Inner.ShouldBe(inner);
        valueTypeInfo.ReflectiveUnWrapper<OrderId, string>()(OrderId.From(inner)).ShouldBe(inner);
    }

    [Fact]
    public void reflective_wrapper_and_unwrapper_for_fsharp_discriminated_union()
    {
        var valueTypeInfo = ValueTypeInfo.ForType(typeof(FSharpIntId));

        var wrapped = valueTypeInfo.ReflectiveWrapper<FSharpIntId, int>()(42);
        valueTypeInfo.ReflectiveUnWrapper<FSharpIntId, int>()(wrapped).ShouldBe(42);
    }
}

public readonly record struct AlertId(Guid Value);

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

public record ValueWithStatic(int Value)
{
    public static string Name => "Should be ignored";
}
