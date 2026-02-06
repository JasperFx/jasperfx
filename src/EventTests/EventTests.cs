using JasperFx.Events;
using Shouldly;

namespace EventTests;

public class EventTests
{
    [Fact]
    public void wrap_an_object_as_an_event()
    {
        var a = new AEvent();
        var e = JasperFx.Events.Event.For(a);
        e.ShouldBeOfType<Event<AEvent>>().Data.ShouldBe(a);
    }
    
    [Fact]
    public void wrap_an_object_as_an_event_with_tenant()
    {
        var a = new AEvent();
        var e = JasperFx.Events.Event.For("foo", a);
        e.ShouldBeOfType<Event<AEvent>>().Data.ShouldBe(a);
        e.TenantId.ShouldBe("foo");
    }

    [Fact]
    public void as_event()
    {
        var a = new AEvent();
        var e = a.AsEvent();
        e.ShouldBeOfType<Event<AEvent>>().Data.ShouldBe(a);
    }

    [Fact]
    public void at_timestamp()
    {
        var a = new AEvent();
        var e = a.AsEvent().AtTimestamp(DateTimeOffset.Now);
        e.Data.ShouldBe(a);
        e.Timestamp.ShouldNotBe(default);
    }

    [Fact]
    public void with_header()
    {
        var a = new AEvent();
        var e = a.AsEvent().WithHeader("color", "blue");
        e.GetHeader("color").ShouldBe("blue");
    }
}