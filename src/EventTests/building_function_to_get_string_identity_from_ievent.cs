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

    // GH-950: a readonly record struct is the shape Native AOT cannot share an instantiation for, and
    // what both identity sources used to compile an expression tree over unconditionally.
    [Fact]
    public void get_id_as_readonly_record_struct_guid_wrapper()
    {
        var func = IEvent.CreateAggregateIdentitySource<KilnId>();
        var e = new Event<AEvent>(new AEvent()) { StreamId = Guid.NewGuid() };
        func(e).Value.ShouldBe(e.StreamId);
    }

    [Fact]
    public void get_id_as_readonly_record_struct_string_wrapper()
    {
        var func = IEvent.CreateAggregateIdentitySource<KettleKey>();
        var e = new Event<AEvent>(new AEvent()) { StreamKey = Guid.NewGuid().ToString() };
        func(e).Value.ShouldBe(e.StreamKey);
    }

    [Fact]
    public void stream_action_id_as_strong_typed_guid_wrappers()
    {
        var action = new StreamAction(Guid.NewGuid(), StreamActionType.Append);

        StreamAction.CreateAggregateIdentitySource<InvoiceId>()(action).Value.ShouldBe(action.Id);
        StreamAction.CreateAggregateIdentitySource<KilnId>()(action).Value.ShouldBe(action.Id);
    }

    [Fact]
    public void stream_action_id_as_strong_typed_string_wrappers()
    {
        var action = new StreamAction(Guid.NewGuid().ToString(), StreamActionType.Append);

        StreamAction.CreateAggregateIdentitySource<OrderId>()(action).Value.ShouldBe(action.Key);
        StreamAction.CreateAggregateIdentitySource<KettleKey>()(action).Value.ShouldBe(action.Key);
    }

    [Fact]
    public void stream_action_id_as_guid_and_string()
    {
        var byId = new StreamAction(Guid.NewGuid(), StreamActionType.Append);
        StreamAction.CreateAggregateIdentitySource<Guid>()(byId).ShouldBe(byId.Id);

        var byKey = new StreamAction("kettle-1", StreamActionType.Append);
        StreamAction.CreateAggregateIdentitySource<string>()(byKey).ShouldBe("kettle-1");
    }
}

public record InvoiceId(Guid Value);
public record OrderId(string Value);
public readonly record struct KilnId(Guid Value);
public readonly record struct KettleKey(string Value);