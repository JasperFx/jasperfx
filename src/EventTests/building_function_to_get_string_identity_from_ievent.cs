using JasperFx.Events;
using Shouldly;

namespace EventTests;

public class building_function_to_get_string_identity_from_ievent
{
    [Fact]
    public void get_id_as_guid()
    {
        var func = IEvent.CreateAggregateIdentitySource<Guid>();
        var e = new Event<AEvent>(new AEvent()) { StreamId = Guid.NewGuid() };
        func(e).ShouldBe(e.StreamId);
    }

    [Fact]
    public void get_id_as_string()
    {
        var func = IEvent.CreateAggregateIdentitySource<string>();
        var e = new Event<AEvent>(new AEvent()) { StreamKey = Guid.NewGuid().ToString() };
        func(e).ShouldBe(e.StreamKey);
    }

    [Fact]
    public void get_id_as_strong_typed_guid_wrapper()
    {
        var func = IEvent.CreateAggregateIdentitySource<InvoiceId>();
        var e = new Event<AEvent>(new AEvent()) { StreamId = Guid.NewGuid() };
        func(e).Value.ShouldBe(e.StreamId);
    }
    
    [Fact]
    public void get_id_as_strong_typed_string_wrapper()
    {
        var func = IEvent.CreateAggregateIdentitySource<OrderId>();
        var e = new Event<AEvent>(new AEvent()) { StreamKey = Guid.NewGuid().ToString() };
        func(e).Value.ShouldBe(e.StreamKey);
    }
}

public record InvoiceId(Guid Value);
public record OrderId(string Value);