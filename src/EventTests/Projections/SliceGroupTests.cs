using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using NSubstitute;
using Shouldly;

namespace EventTests.Projections;

public class SliceGroupTests : IProjectionStorage<User, string>, IStorageOperations
{
    private readonly SliceGroup<LetterCounts, Guid> theGroup;
    private readonly Dictionary<string, User> theUsers = new();
    private string[] _loadedUserIds = [];
    private string _tenantId;
    private bool _enableSideEffectsOnInlineProjections;
    private string _tenantId1;
    private bool _enableSideEffectsOnInlineProjections1;

    private EventSlice<LetterCounts, Guid> BuildSlice(string text, string userName)
    {
        var events = text.ToLetterEventsWithWrapper();
        if (userName.IsNotEmpty())
        {
            events = events.Concat([Event.For(new Assigned(userName))]);
        }

        return new EventSlice<LetterCounts, Guid>(Guid.NewGuid(), StorageConstants.DefaultTenantId, events);
    }

    private Guid AddSlice(string text, string userName)
    {
        var slice = BuildSlice(text, userName);
        theGroup.Slices[slice.Id] = slice;
        return slice.Id;
    }

    private SliceGroup<LetterCounts, string> BuildStringGroup()
    {
        var group = new SliceGroup<LetterCounts, string>();
        group.Operations = this;

        return group;
    }

    private static void AddStringSlice(SliceGroup<LetterCounts, string> group, string id, params IEvent[] events)
    {
        group.Slices[id] = new EventSlice<LetterCounts, string>(id, StorageConstants.DefaultTenantId, events);
    }

    public SliceGroupTests()
    {
        theGroup = new SliceGroup<LetterCounts, Guid>();
        theGroup.Operations = this;
    }

    [Fact]
    public async Task fetch_aggregate_cache_with_no_cache_hits()
    {
        var id1 = AddSlice("AAABCCDDDD", "Bill");
        var id2 = AddSlice("AAABCCDDDD", "Tom");
        var id3 = AddSlice("AEEABCCDDDD", "Todd");
        var id4 = AddSlice("AAABCCDDDD", "");
        var id5 = AddSlice("EAABCCDDDD", "");
        var id6 = AddSlice("BAABCCDDDD", "Todd");
        var id7 = AddSlice("ACCCEEEEAABCCDDDD", "Bill");

        theUsers["Bill"] = new User("Bill", "William");
        theUsers["Tom"] = new User("Tom", "Thomas");
        theUsers["Todd"] = new User("Todd", "Todd");

        var cache = await theGroup.EnrichWith<User>()
            .ForEvent<Assigned>()
            .ForEntityId(x => x.UserName)
            .FetchEntitiesAsync();
        
        cache.Contains("Bill").ShouldBeTrue();
        cache.Contains("Tom").ShouldBeTrue();
        cache.Contains("Todd").ShouldBeTrue();
    }
    
    [Fact]
    public async Task enrich_aggregate_cache_with_no_cache_hits()
    {
        var id1 = AddSlice("AAABCCDDDD", "Bill");
        var id2 = AddSlice("AAABCCDDDD", "Tom");
        var id3 = AddSlice("AEEABCCDDDD", "Todd");
        var id4 = AddSlice("AAABCCDDDD", "");
        var id5 = AddSlice("EAABCCDDDD", "");
        var id6 = AddSlice("BAABCCDDDD", "Todd");
        var id7 = AddSlice("ACCCEEEEAABCCDDDD", "Bill");

        theUsers["Bill"] = new User("Bill", "William");
        theUsers["Tom"] = new User("Tom", "Thomas");
        theUsers["Todd"] = new User("Todd", "Todd");

        await theGroup.EnrichWith<User>()
            .ForEvent<Assigned>()
            .ForEntityId(x => x.UserName)
            .EnrichAsync((slice, e, user) =>
            {
                slice.ReplaceEvent(e, new AssignedToUser(user));
            });
        
        theGroup.Slices[id1].Events().OfType<IEvent<AssignedToUser>>().Single().Data.User.UserName.ShouldBe("Bill");
        theGroup.Slices[id2].Events().OfType<IEvent<AssignedToUser>>().Single().Data.User.UserName.ShouldBe("Tom");
        theGroup.Slices[id6].Events().OfType<IEvent<AssignedToUser>>().Single().Data.User.UserName.ShouldBe("Todd");
        theGroup.Slices[id7].Events().OfType<IEvent<AssignedToUser>>().Single().Data.User.UserName.ShouldBe("Bill");
    }
    
    [Fact]
    public async Task fetch_aggregate_cache_returns_null_with_no_hits()
    {
        var id1 = AddSlice("AAABCCDDDD", "");
        var id2 = AddSlice("AAABCCDDDD", "");
        var id3 = AddSlice("AEEABCCDDDD", "");
        var id4 = AddSlice("AAABCCDDDD", "");
        var id5 = AddSlice("EAABCCDDDD", "");
        var id6 = AddSlice("BAABCCDDDD", "");
        var id7 = AddSlice("ACCCEEEEAABCCDDDD", "");

        var cache = await theGroup.EnrichWith<User>()
            .ForEvent<Assigned>()
            .ForEntityId(x => x.UserName)
            .FetchEntitiesAsync();

        cache.ShouldBeOfType<NulloAggregateCache<string, User>>();
    }
    
    private static IEvent<Assigned> AssignedEventWithHeader(string headerUserName, string dataUserName = "WRONG")
    {
        var e = Substitute.For<IEvent<Assigned>, IEvent>();

        e.Data.Returns(new Assigned(dataUserName));

        var headers = new Dictionary<string, object>
        {
            ["userName"] = headerUserName
        };

        e.Headers.Returns(headers);

        return e;
    }

    [Fact]
    public async Task enrich_aggregate_cache_using_forentityidfromevent()
    {
        var id1 = AddSlice("AAABCCDDDD", "");
        var id2 = AddSlice("AAABCCDDDD", "");
        var id3 = AddSlice("AEEABCCDDDD", "");

        theUsers["Bill"] = new User("Bill", "William");
        theUsers["Tom"] = new User("Tom", "Thomas");
        theUsers["Todd"] = new User("Todd", "Todd");

        theGroup.Slices[id1].AddEvent(AssignedEventWithHeader("Bill"));
        theGroup.Slices[id2].AddEvent(AssignedEventWithHeader("Tom"));
        theGroup.Slices[id3].AddEvent(AssignedEventWithHeader("Todd"));

        await theGroup.EnrichWith<User>()
            .ForEvent<Assigned>()
            .ForEntityIdFromEvent(e => (string)e.Headers["userName"])
            .EnrichAsync((slice, e, user) =>
            {
                slice.ReplaceEvent(e, new AssignedToUser(user));
            });

        theGroup.Slices[id1].Events().OfType<IEvent<AssignedToUser>>().Single().Data.User.UserName.ShouldBe("Bill");
        theGroup.Slices[id2].Events().OfType<IEvent<AssignedToUser>>().Single().Data.User.UserName.ShouldBe("Tom");
        theGroup.Slices[id3].Events().OfType<IEvent<AssignedToUser>>().Single().Data.User.UserName.ShouldBe("Todd");
    }

    [Fact]
    public async Task enrich_aggregate_cache_using_forentityidfromstreamid()
    {
        var group = BuildStringGroup();
        AddStringSlice(group, "Bill", Event.For(new Assigned("WRONG")));

        theUsers["Bill"] = new User("Bill", "William");

        await group.EnrichWith<User>()
            .ForEvent<Assigned>()
            .ForEntityIdFromStreamId()
            .AddReferences();

        group.Slices["Bill"].Events().OfType<IEvent<References<User>>>().Single().Data.Entity.UserName.ShouldBe("Bill");
        _loadedUserIds.ShouldBe(["Bill"]);
    }

    [Fact]
    public async Task enrich_aggregate_cache_for_multiple_event_types_using_stream_id()
    {
        var group = BuildStringGroup();
        AddStringSlice(group, "Bill", Event.For(new AEvent()));
        AddStringSlice(group, "Tom", Event.For(new BEvent()));
        AddStringSlice(group, "Todd", Event.For(new CEvent()));

        theUsers["Bill"] = new User("Bill", "William");
        theUsers["Tom"] = new User("Tom", "Thomas");
        theUsers["Todd"] = new User("Todd", "Todd");

        await group.EnrichWith<User>()
            .ForEvents<
                AEvent,
                BEvent,
                CEvent,
                DEvent,
                EEvent,
                CreateEvent,
                StartLetters,
                FullStop,
                Assigned,
                AssignedToUser,
                LetterCounts,
                MyAggregate,
                Session,
                StringId,
                GuidId>()
            .ForEntityIdFromStreamId()
            .AddReferences();

        group.Slices["Bill"].Events().OfType<IEvent<References<User>>>().Single().Data.Entity.UserName.ShouldBe("Bill");
        group.Slices["Tom"].Events().OfType<IEvent<References<User>>>().Single().Data.Entity.UserName.ShouldBe("Tom");
        group.Slices["Todd"].Events().OfType<IEvent<References<User>>>().Single().Data.Entity.UserName.ShouldBe("Todd");
        _loadedUserIds.Order().ToArray().ShouldBe(["Bill", "Todd", "Tom"]);
    }

    [Fact]
    public async Task enrich_aggregate_cache_for_multiple_event_type_objects_using_stream_id()
    {
        var group = BuildStringGroup();
        AddStringSlice(group, "Bill", Event.For(new AEvent()));
        AddStringSlice(group, "Tom", Event.For(new BEvent()));
        AddStringSlice(group, "Todd", Event.For(new CEvent()));

        theUsers["Bill"] = new User("Bill", "William");
        theUsers["Tom"] = new User("Tom", "Thomas");
        theUsers["Todd"] = new User("Todd", "Todd");

        await group.EnrichWith<User>()
            .ForEvents(typeof(AEvent), typeof(BEvent))
            .ForEntityIdFromStreamId()
            .AddReferences();

        group.Slices["Bill"].Events().OfType<IEvent<References<User>>>().Single().Data.Entity.UserName.ShouldBe("Bill");
        group.Slices["Tom"].Events().OfType<IEvent<References<User>>>().Single().Data.Entity.UserName.ShouldBe("Tom");
        group.Slices["Todd"].Events().OfType<IEvent<References<User>>>().Any().ShouldBeFalse();
        _loadedUserIds.Order().ToArray().ShouldBe(["Bill", "Tom"]);
    }

    [Fact]
    public async Task enrich_with_using_entity_query()
    {
        var id1 = AddSlice("AAABCCDDDD", "Bill");
        var id2 = AddSlice("AAABCCDDDD", "Tom");
        var id3 = AddSlice("AEEABCCDDDD", "Todd");
        var id4 = AddSlice("AAABCCDDDD", "");

        theUsers["Bill"] = new User("Bill", "William");
        theUsers["Tom"] = new User("Tom", "Thomas");
        theUsers["Todd"] = new User("Todd", "Todd");

        await theGroup.EnrichWith<User>()
            .ForEvent<Assigned>()
            .EnrichUsingEntityQuery<string>((slices, events, cache, ct) =>
            {
                ct.ThrowIfCancellationRequested();

                var names = events
                    .Select(e => e.Data.UserName)
                    .Where(x => x.IsNotEmpty())
                    .Distinct()
                    .ToArray();

                var dict = names
                    .Where(theUsers.ContainsKey)
                    .ToDictionary(x => x, x => theUsers[x]);

                foreach (var slice in slices)
                {
                    foreach (var e in slice.Events().OfType<IEvent<Assigned>>().ToArray())
                    {
                        if (dict.TryGetValue(e.Data.UserName, out var user))
                        {
                            slice.ReplaceEvent(e, new AssignedToUser(user));
                        }
                    }
                }

                return Task.CompletedTask;
            });

        theGroup.Slices[id1].Events().OfType<IEvent<AssignedToUser>>().Single().Data.User.UserName.ShouldBe("Bill");
        theGroup.Slices[id2].Events().OfType<IEvent<AssignedToUser>>().Single().Data.User.UserName.ShouldBe("Tom");
        theGroup.Slices[id3].Events().OfType<IEvent<AssignedToUser>>().Single().Data.User.UserName.ShouldBe("Todd");
        theGroup.Slices[id4].Events().OfType<IEvent<AssignedToUser>>().Any().ShouldBeFalse();
    }

    void IIdentitySetter<User, string>.SetIdentity(User document, string identity)
    {
        throw new NotImplementedException();
    }

    string IIdentitySetter<User, string>.Identity(User document)
    {
        return document.UserName;
    }

    string IProjectionStorage<User, string>.TenantId => StorageConstants.DefaultTenantId;

    void IProjectionStorage<User, string>.HardDelete(User snapshot)
    {
        throw new NotImplementedException();
    }

    void IProjectionStorage<User, string>.UnDelete(User snapshot)
    {
        throw new NotImplementedException();
    }

    void IProjectionStorage<User, string>.Store(User snapshot)
    {
        throw new NotImplementedException();
    }

    void IProjectionStorage<User, string>.Delete(string identity)
    {
        throw new NotImplementedException();
    }

    void IProjectionStorage<User, string>.HardDelete(User snapshot, string tenantId)
    {
        throw new NotImplementedException();
    }

    void IProjectionStorage<User, string>.UnDelete(User snapshot, string tenantId)
    {
        throw new NotImplementedException();
    }

    void IProjectionStorage<User, string>.Store(User snapshot, string id, string tenantId)
    {
        throw new NotImplementedException();
    }

    void IProjectionStorage<User, string>.Delete(string identity, string tenantId)
    {
        throw new NotImplementedException();
    }

    Task<IReadOnlyDictionary<string, User>> IProjectionStorage<User, string>.LoadManyAsync(string[] identities, CancellationToken cancellationToken)
    {
        _loadedUserIds = identities;

        var users = identities
            .Distinct()
            .Where(theUsers.ContainsKey)
            .ToDictionary(x => x, x => theUsers[x]);

        return Task.FromResult<IReadOnlyDictionary<string, User>>(users);
    }

    void IProjectionStorage<User, string>.StoreProjection(User aggregate, IEvent? lastEvent, AggregationScope scope)
    {
        throw new NotImplementedException();
    }

    void IProjectionStorage<User, string>.ArchiveStream(string sliceId, string tenantId)
    {
        throw new NotImplementedException();
    }

    Task<User> IProjectionStorage<User, string>.LoadAsync(string id, CancellationToken cancellation)
    {
        throw new NotImplementedException();
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        throw new NotImplementedException();
    }

    Task<IProjectionStorage<TDoc, TId>> IStorageOperations.FetchProjectionStorageAsync<TDoc, TId>(string tenantId, CancellationToken cancellationToken)
    {
        return Task.FromResult((IProjectionStorage<TDoc, TId>)this);
    }

    bool IStorageOperations.EnableSideEffectsOnInlineProjections => _enableSideEffectsOnInlineProjections1;

    ValueTask<IMessageSink> IStorageOperations.GetOrStartMessageSink()
    {
        throw new NotImplementedException();
    }
}

