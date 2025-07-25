using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;

namespace EventTests;

public class overriding_event_metadata
{
    private readonly EventRegistry theEvents;
    private readonly IMetadataContext theSession;
    private Queue<long> sequences = new();
    private DateTimeOffset theCurrentTime = DateTime.Today;
    private readonly StreamAction theAction;

    public overriding_event_metadata()
    {
        theSession = Substitute.For<IMetadataContext>();
        theSession.TenantId.Returns("TX");

        theSession.CausationIdEnabled.Returns(true);
        theSession.CorrelationIdEnabled.Returns(true);

        for (int i = 20; i < 50; i++)
        {
            sequences.Enqueue(i);
        }

        theEvents = new EventRegistry();
        
        theCurrentTime = DateTime.Today;
        theEvents.TimeProvider = new FakeTimeProvider(theCurrentTime);
        
        theAction = StreamAction.Append(theEvents, Guid.NewGuid(), new AEvent(), new BEvent(), new CEvent(),
            new DEvent());
    }

    [Fact]
    public void override_timestamp_with_rich_metadata()
    {
        theEvents.AppendMode = EventAppendMode.Rich;
        
        var oneHourAgo = theCurrentTime.Subtract(1.Hours());
        theAction.Events[0].Timestamp = oneHourAgo;
        
        theAction.PrepareEvents(5, theEvents, sequences, theSession);
        
        theAction.Events[0].Timestamp.ShouldBe(oneHourAgo);
        theAction.Events[1].Timestamp.ShouldBe(theCurrentTime);
        theAction.Events[2].Timestamp.ShouldBe(theCurrentTime);
        theAction.Events[3].Timestamp.ShouldBe(theCurrentTime);
    }
    
    [Fact]
    public void override_timestamp_with_quick_metadata()
    {
        theEvents.AppendMode = EventAppendMode.Quick;
        
        var oneHourAgo = theCurrentTime.Subtract(1.Hours());
        theAction.Events[0].Timestamp = oneHourAgo;
        
        theAction.PrepareEvents(5, theEvents, sequences, theSession);
        
        theAction.Events[0].Timestamp.ShouldBe(oneHourAgo);
        theAction.Events[1].Timestamp.ShouldBe(theCurrentTime);
        theAction.Events[2].Timestamp.ShouldBe(theCurrentTime);
        theAction.Events[3].Timestamp.ShouldBe(theCurrentTime);
    }

    [Fact]
    public void override_correlation_id()
    {
        theSession.CorrelationId = Guid.NewGuid().ToString();

        theAction.Events[2].CorrelationId = "different";
        
        theAction.PrepareEvents(5, theEvents, sequences, theSession);
        
        theAction.Events[0].CorrelationId.ShouldBe(theSession.CorrelationId);
        theAction.Events[1].CorrelationId.ShouldBe(theSession.CorrelationId);
        theAction.Events[2].CorrelationId.ShouldBe("different");
        theAction.Events[3].CorrelationId.ShouldBe(theSession.CorrelationId);
    }
    
    [Fact]
    public void override_causation_id()
    {
        theSession.CorrelationId = Guid.NewGuid().ToString();

        theAction.Events[2].CausationId = "different";
        
        theAction.PrepareEvents(5, theEvents, sequences, theSession);
        
        theAction.Events[0].CausationId.ShouldBe(theSession.CausationId);
        theAction.Events[1].CausationId.ShouldBe(theSession.CausationId);
        theAction.Events[2].CausationId.ShouldBe("different");
        theAction.Events[3].CausationId.ShouldBe(theSession.CausationId);
    }

    [Fact]
    public void override_event_id()
    {
        // This might be valuable for importing data from other systems

        var override1 = Guid.NewGuid();
        var override2 = Guid.NewGuid();


        theAction.Events[0].Id = override1;
        theAction.Events[1].Id = override2;
        
        theAction.PrepareEvents(5, theEvents, sequences, theSession);

        theAction.Events[0].Id.ShouldBe(override1);
        theAction.Events[1].Id.ShouldBe(override2);
        theAction.Events[2].Id.ShouldNotBe(Guid.Empty);
        theAction.Events[3].Id.ShouldNotBe(Guid.Empty);
    }
}