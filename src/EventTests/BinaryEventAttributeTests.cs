using System.Reflection;
using JasperFx.Events;
using Shouldly;

namespace EventTests;

/// <summary>
/// <see cref="BinaryEventAttribute" /> is deliberately unsealed (jasperfx#672).
/// </summary>
/// <remarks>
/// A store that shipped its own <c>BinaryEventAttribute</c> before this one was promoted cannot
/// delete it without breaking its users, and could not derive from a sealed one either — leaving it
/// resolving two attribute types on every event forever. These pin the property that makes
/// subclassing an actual fix rather than a second thing to check: a lookup for the promoted
/// attribute has to find a derived one.
/// </remarks>
public class BinaryEventAttributeTests
{
    [Fact]
    public void the_promoted_attribute_can_be_derived_from()
    {
        typeof(BinaryEventAttribute).IsSealed.ShouldBeFalse();
    }

    [Fact]
    public void an_event_marked_with_the_promoted_attribute_is_found()
    {
        typeof(Shipped).GetCustomAttribute<BinaryEventAttribute>().ShouldNotBeNull();
    }

    /// <remarks>
    /// The load-bearing one. Attribute lookup matches by assignability, so a store subclassing this
    /// collapses its two checks back to one rather than gaining a third.
    /// </remarks>
    [Fact]
    public void an_event_marked_with_a_derived_attribute_is_found_by_the_promoted_one()
    {
        typeof(Delivered).GetCustomAttribute<BinaryEventAttribute>().ShouldNotBeNull();

        Attribute.IsDefined(typeof(Delivered), typeof(BinaryEventAttribute)).ShouldBeTrue();
    }

    [Fact]
    public void an_unmarked_event_is_not_found()
    {
        typeof(Cancelled).GetCustomAttribute<BinaryEventAttribute>().ShouldBeNull();
    }

    private class StoreOwnBinaryEventAttribute : BinaryEventAttribute;

    [BinaryEvent]
    private record Shipped(string Carrier);

    [StoreOwnBinaryEvent]
    private record Delivered(string Signature);

    private record Cancelled(string Reason);
}
