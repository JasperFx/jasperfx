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
    public void unwrap_event_type(Type rawType, Type expectedType)
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
    
    public void UseConcreteEventType(AEvent e, MyAggregate aggregate){}
    public void UseConcreteEventType2(MyAggregate aggregate, AEvent e){}
    public void UseConcreteEventType3(MyAggregate aggregate, AEvent e, IEvent metadata){}
    public void UseInterfaceEventType(ITabulator e, MyAggregate aggregate){}
    
    public void UseEventWrapperConcrete(IEvent<AEvent> e, MyAggregate aggregate){}
    public void UseEventWrapperConcrete2(IEvent<AEvent> e, MyAggregate aggregate){}
}