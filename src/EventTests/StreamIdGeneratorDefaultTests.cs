using JasperFx.Core;
using JasperFx.Events;
using Shouldly;

namespace EventTests;

/// <summary>
/// jasperfx#1220: the generator for the Guid stream ids JasperFx makes up defaults to PostgreSQL order, both on
/// <see cref="EventRegistry" /> and for a registry that implements <see cref="IEventRegistry" /> directly and was
/// compiled before the member existed.
/// </summary>
public class StreamIdGeneratorDefaultTests
{
    [Fact]
    public void the_event_registry_defaults_to_postgres_order()
    {
        new EventRegistry().StreamIdGenerator.ShouldBeSameAs(SequentialGuid.PostgresOrder);
    }

    [Fact]
    public void the_event_registry_takes_another_generator()
    {
        var registry = new EventRegistry { StreamIdGenerator = SequentialGuid.SqlServerOrder };

        ((IEventRegistry)registry).StreamIdGenerator.ShouldBeSameAs(SequentialGuid.SqlServerOrder);
    }

    [Fact]
    public void a_registry_that_does_not_implement_it_gets_postgres_order()
    {
        IEventRegistry registry = new BareRegistry();

        registry.StreamIdGenerator.ShouldBeSameAs(SequentialGuid.PostgresOrder);
    }

    private class BareRegistry : IEventRegistry
    {
        public IEvent BuildEvent(object eventData) => throw new NotSupportedException();
        public EventAppendMode AppendMode { get; set; }
        public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
        public StreamIdentity StreamIdentity { get; set; }
        public Type AggregateTypeFor(string aggregateTypeName) => throw new NotSupportedException();
        public string AggregateAliasFor(Type aggregateType) => throw new NotSupportedException();
        public IEventType EventMappingFor(Type eventType) => throw new NotSupportedException();
        public void AddEventType(Type eventType) => throw new NotSupportedException();
    }
}
