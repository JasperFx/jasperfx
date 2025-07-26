using JasperFx.Events;
using Shouldly;

namespace EventTests;

public class determining_the_event_type_name_from_event_type
{
    [Theory]
    [InlineData(typeof(AEvent), "a_event")]
    [InlineData(typeof(Foo<string>), "Foo<string>")]
    [InlineData(typeof(GroupEvents.Created), "created")]
    [InlineData(typeof(UserEvents.Created), "created")]
    public void the_name_in_normal_mode_should_be(Type eventType, string eventTypeName)
    {
        eventType.GetEventTypeName().ShouldBe(eventTypeName);
    }
    
    [Theory]
    [InlineData(typeof(AEvent), "a_event")]
    [InlineData(typeof(Foo<string>), "Foo<string>")]
    [InlineData(typeof(GroupEvents.Created), "created")]
    [InlineData(typeof(UserEvents.Created), "created")]
    public void the_name_in_normal_mode_should_be_2(Type eventType, string eventTypeName)
    {
        eventType.GetEventTypeName(EventNamingStyle.ClassicTypeName).ShouldBe(eventTypeName);
    }
    
    [Theory]
    [InlineData(typeof(AEvent), "a_event")]
    [InlineData(typeof(Foo<string>), "Foo<string>")]
    [InlineData(typeof(GroupEvents.Created), "group_events.created")]
    [InlineData(typeof(UserEvents.Created), "user_events.created")]
    public void the_name_in_smarter_mode_should_be(Type eventType, string eventTypeName)
    {
        eventType.GetSmarterEventTypeName().ShouldBe(eventTypeName);
    }

    
}

public class Foo<T>;

public class GroupEvents
{
    public record Created(string Name);
}

public class UserEvents
{
    public record Created(string Name);
}