using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace EventTests;

public class event_parameter_naming_convention
{
    public record FooEvent(System.Guid Id);

    public class Helper { }

    // Two concrete candidate parameters, so the event cannot be inferred by type
    // alone. The conventional event parameter name decides which one is the event.
    public void by_event_name(Helper helper, FooEvent @event) { }
    public void by_e_name(Helper helper, FooEvent e) { }
    public void by_ev_name(Helper helper, FooEvent ev) { }

    [Theory]
    [InlineData(nameof(by_event_name))]
    [InlineData(nameof(by_e_name))]
    [InlineData(nameof(by_ev_name))]
    public void conventional_event_parameter_names_disambiguate(string methodName)
    {
        var method = GetType().GetMethod(methodName)!;
        method.GetEventType(null).ShouldBe(typeof(FooEvent));
    }
}
