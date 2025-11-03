using JasperFx;
using JasperFx.Events;
using Shouldly;

namespace EventTests;

public class when_replacing_event_in_slice_for_enrichment
{
    private readonly EventSlice<SimpleAggregate, Guid> theSlice;
    private readonly IEvent<SimpleEvent> theOriginalEvent;

    public when_replacing_event_in_slice_for_enrichment()
    {
        var e1 = new SimpleEvent(1, Guid.NewGuid());
        var e2 = new SimpleEvent(2, Guid.NewGuid());
        var e3 = new SimpleEvent(3, Guid.NewGuid());
        var e4 = new SimpleEvent(4, Guid.NewGuid());
        theSlice = new EventSlice<SimpleAggregate, Guid>(Guid.NewGuid(), StorageConstants.DefaultTenantId);
        theOriginalEvent = Event.For(e3);
        theOriginalEvent.Sequence = 111;
        theOriginalEvent.Version = 22;
        theOriginalEvent.Timestamp = DateTime.Today.ToUniversalTime();
        theOriginalEvent.CausationId = Guid.NewGuid().ToString();
        theOriginalEvent.CorrelationId = Guid.NewGuid().ToString();
        theOriginalEvent.AggregateTypeName = "Something";
        theOriginalEvent.UserName = Guid.NewGuid().ToString();
        theOriginalEvent.Headers = new Dictionary<string, object>();
        theOriginalEvent.SetHeader("color", "blue");
        
        theSlice.AddEvent(Event.For(e1));
        theSlice.AddEvent(Event.For(e2));
        theSlice.AddEvent(theOriginalEvent);
        theSlice.AddEvent(Event.For(e4));
    }

    [Fact]
    public void replace_event_with_new_data()
    {
        var newEvent = theSlice.ReplaceEvent(theOriginalEvent,
            new ComplexEvent(theOriginalEvent.Data.Number, new User(theOriginalEvent.Data.UserId, "Hank")));
        
        theSlice.Events()[2].ShouldBe(newEvent);
        
        newEvent.Sequence.ShouldBe(111);
        newEvent.Headers["color"].ShouldBe("blue");
    }

    [Fact]
    public void replace_by_index()
    {
        theSlice.ReplaceEvent(0, new ComplexEvent(1, new User(Guid.NewGuid(), "Bill")));
        
        theSlice.Events()[0].ShouldBeOfType<Event<ComplexEvent>>()
            .Data.User.UserName.ShouldBe("Bill");
    }
    
    

    
    public record SimpleEvent(long Number, Guid UserId);

    public record ComplexEvent(long Number, User User);

    public record User(Guid Id, string UserName);
}