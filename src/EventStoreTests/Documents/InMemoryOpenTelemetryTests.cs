using System.Diagnostics;
using JasperFx.Events;
using JasperFx.Events.InMemory;
using Shouldly;

namespace EventStoreTests.Documents;

/// <summary>
/// The one store in this repository answering <see cref="IEventStore" />, so the one place jasperfx#1054's
/// "a store produces one source" can be held against a real store. It used to build a Meter and an
/// ActivitySource per instance, which is the Polecat and Fisher defect over again for a second store.
/// </summary>
public class InMemoryOpenTelemetryTests
{
    [Fact]
    public void stores_produce_one_source_for_a_listener_subscribed_by_name()
    {
        IEventStore first = new InMemoryDocumentStore();
        IEventStore second = new InMemoryDocumentStore();

        // Resolved before the listener subscribes, because it is told about every live source of the name
        // when it does, which is what this is counting.
        _ = first.ActivitySource;
        _ = second.ActivitySource;

        var heard = new List<ActivitySource>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source =>
            {
                if (source.Name != first.ActivitySource.Name) return false;

                lock (heard) heard.Add(source);
                return true;
            },
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        heard.ShouldBe([first.ActivitySource]);
    }

    [Fact]
    public void stores_share_one_meter()
    {
        IEventStore first = new InMemoryDocumentStore();
        IEventStore second = new InMemoryDocumentStore();

        second.Meter.ShouldBeSameAs(first.Meter);
        first.Meter.Name.ShouldBe(first.ActivitySource.Name);
    }
}
