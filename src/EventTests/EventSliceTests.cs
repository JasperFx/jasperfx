using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Shouldly;

namespace EventTests;

public class EventSliceTests
{
    [Fact]
    public void add_event_on_guid_no_identifer()
    {
        var slice = new EventSlice<SimpleAggregate, Guid>(Guid.NewGuid(),
            "foo");

        slice.As<IEventSlice<SimpleAggregate>>().AppendEvent(new AEvent());

        var last = slice.As<IEventSlice<SimpleAggregate>>().RaisedEvents().Last();

        last.StreamId.ShouldBe(slice.Id);
    }

    [Fact]
    public void add_event_on_string_no_identifer()
    {
        var slice = new EventSlice<SimpleAggregate, string>(Guid.NewGuid().ToString(),
            "foo");

        slice.As<IEventSlice<SimpleAggregate>>().AppendEvent(new AEvent());

        var last = slice.As<IEventSlice<SimpleAggregate>>().RaisedEvents().Last();

        last.StreamKey.ShouldBe(slice.Id);
    }

    [Fact]
    public void raise_event_on_supplied_guid_identifier()
    {
        var slice = new EventSlice<SimpleAggregate, string>(Guid.NewGuid().ToString(),
            "foo");

        var streamId = Guid.NewGuid();
        slice.As<IEventSlice<SimpleAggregate>>().AppendEvent(streamId, new AEvent());

        var last = slice.As<IEventSlice<SimpleAggregate>>().RaisedEvents().Last();

        last.StreamId.ShouldBe(streamId);
    }

    [Fact]
    public void raise_event_on_supplied_string_identifier()
    {
        var slice = new EventSlice<SimpleAggregate, string>(Guid.NewGuid().ToString(),
            "foo");

        var streamKey = Guid.NewGuid().ToString();
        slice.As<IEventSlice<SimpleAggregate>>().AppendEvent(streamKey, new AEvent());

        var last = slice.As<IEventSlice<SimpleAggregate>>().RaisedEvents().Last();

        last.StreamKey.ShouldBe(streamKey);
    }

    [Fact]
    public void raise_event_on_supplied_guid_backed_strong_identifier()
    {
        var id = new GuidId(Guid.NewGuid());
        var slice = new EventSlice<SimpleAggregate, GuidId>(id,
            "foo");
        
        slice.As<IEventSlice<SimpleAggregate>>().AppendEvent(new AEvent());

        var last = slice.As<IEventSlice<SimpleAggregate>>().RaisedEvents().Last();

        last.StreamId.ShouldBe(id.Value);
    }
    
    [Fact]
    public void raise_event_on_supplied_string_backed_strong_identifier()
    {
        var id = new StringId(Guid.NewGuid().ToString());
        var slice = new EventSlice<SimpleAggregate, StringId>(id,
            "foo");
        
        slice.As<IEventSlice<SimpleAggregate>>().AppendEvent(new AEvent());

        var last = slice.As<IEventSlice<SimpleAggregate>>().RaisedEvents().Last();

        last.StreamKey.ShouldBe(id.Value);
    }

    [Fact]
    public void reference_a_document_and_retrieve_it_back_out()
    {
        var id = new StringId(Guid.NewGuid().ToString());
        var slice = new EventSlice<SimpleAggregate, StringId>(id,
            "foo");

        var user = new User("admin", "Comic Book Guy");
        slice.Reference(user);
        
        slice.TryFindReference<User>(out var refUser).ShouldBeTrue();
        refUser.ShouldBe(user);

        slice.Events().Last().ShouldBeOfType<Event<References<User>>>()
            .Data.Entity.ShouldBe(user);
    }
    
    [Fact]
    public void update_a_document_and_retrieve_it_back_out()
    {
        var id = new StringId(Guid.NewGuid().ToString());
        var slice = new EventSlice<SimpleAggregate, StringId>(id,
            "foo");

        var user = new User("admin", "Comic Book Guy");
        slice.AddEvent(Event.For(new Updated<User>(StorageConstants.DefaultTenantId, user)));
        
        slice.TryFindReference<User>(out var refUser).ShouldBeTrue();
        refUser.ShouldBe(user);
    }
    

    [Fact]
    public void miss_on_try_find_reference()
    {
        var id = new StringId(Guid.NewGuid().ToString());
        var slice = new EventSlice<SimpleAggregate, StringId>(id,
            "foo");
        
        slice.TryFindReference<User>(out var user).ShouldBeFalse();
    }

    [Fact]
    public void all_references_with_none()
    {
        var id = new StringId(Guid.NewGuid().ToString());
        var slice = new EventSlice<SimpleAggregate, StringId>(id,
            "foo");
        
        slice.AllReferenced<User>().Any().ShouldBeFalse();
    }

    [Fact]
    public void all_references_with_hits()
    {
        var id = new StringId(Guid.NewGuid().ToString());
        var slice = new EventSlice<SimpleAggregate, StringId>(id,
            "foo");

        var user1 = new User("admin", "Comic Book Guy");
        var user2 = new User("power", "Power Guy");
        slice.Reference(user1);
        slice.Reference(user2);

        var referenced = slice.AllReferenced<User>().ToArray();
        referenced[0].ShouldBe(user1);
        referenced[1].ShouldBe(user2);
    }
}

public record User(string UserName, string RealName);

public record StringId(string Value);
public record GuidId(Guid Value);