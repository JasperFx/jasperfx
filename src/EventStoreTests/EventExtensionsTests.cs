using System.Linq.Expressions;
using System.Reflection;
using EventStoreTests.TestingSupport;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventStoreTests;

public class EventExtensionsTests
{
    [Theory]
    [InlineData(typeof(AEvent), typeof(AEvent))]
    [InlineData(typeof(IEvent<AEvent>), typeof(AEvent))]
    [InlineData(typeof(Event<AEvent>), typeof(AEvent))]
    [InlineData(typeof(IEvent), null)]
    public void unwrap_event_type(Type rawType, Type? expectedType)
    {
        rawType.UnwrapEventType().ShouldBe(expectedType);
    }

    private Type eventTypeFor(Expression<Action<EventExtensionsTests>> expression)
    {
        var method = ReflectionHelper.GetMethod(expression);
        return method.GetEventType(typeof(MyAggregate));
    }

    [Fact]
    public void clone_event_with_new_data()
    {
        var parent = new Event<Travel>(Travel.Random(1));
        parent.StreamId = Guid.NewGuid();
        parent.StreamKey = Guid.NewGuid().ToString();
        parent.TenantId = Guid.NewGuid().ToString();
        parent.Timestamp = DateTime.Today; // always use low fidelity dates for comparisons
        parent.Version = 3;
        parent.Sequence = 15;

        var child = parent.CloneEventWithNewData(parent.Data.Movements.First());
        child.StreamId.ShouldBe(parent.StreamId);
        child.StreamKey.ShouldBe(parent.StreamKey);
        child.TenantId.ShouldBe(parent.TenantId);
        child.Timestamp.ShouldBe(parent.Timestamp);
        child.Version.ShouldBe(parent.Version);
        child.Sequence.ShouldBe(parent.Sequence);
    }

    [Fact]
    public void get_event_type_from_method()
    {
        eventTypeFor(x => x.UseConcreteEventType(null, null)).ShouldBe(typeof(AEvent));
        eventTypeFor(x => x.UseConcreteEventType2(null, null)).ShouldBe(typeof(AEvent));
        eventTypeFor(x => x.UseConcreteEventType3(null, null, null)).ShouldBe(typeof(AEvent));
        eventTypeFor(x => x.UseInterfaceEventType(null, null)).ShouldBe(typeof(ITabulator));
        
        eventTypeFor(x => x.UseEventWrapperConcrete(null, null)).ShouldBe(typeof(AEvent));
        eventTypeFor(x => x.UseEventWrapperConcrete2(null, null)).ShouldBe(typeof(AEvent));
    }
    
    // Reflection targets for get_event_type_from_method, not tests. Private keeps them out of
    // xUnit1013's sights; the expression trees above still bind them because they are built inside
    // this class, and GetEventType only reads the parameter list.
    private void UseConcreteEventType(AEvent e, MyAggregate aggregate){}
    private void UseConcreteEventType2(MyAggregate aggregate, AEvent e){}
    private void UseConcreteEventType3(MyAggregate aggregate, AEvent e, IEvent metadata){}
    private void UseInterfaceEventType(ITabulator e, MyAggregate aggregate){}

    private void UseEventWrapperConcrete(IEvent<AEvent> e, MyAggregate aggregate){}
    private void UseEventWrapperConcrete2(IEvent<AEvent> e, MyAggregate aggregate){}
}