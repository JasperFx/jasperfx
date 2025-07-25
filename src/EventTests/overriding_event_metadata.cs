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
}