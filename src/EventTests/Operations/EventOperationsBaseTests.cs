using System.Linq.Expressions;
using JasperFx.Events;
using JasperFx.Events.Protected;
using JasperFx.Events.Tags;
using Shouldly;

namespace EventTests.Operations;

public record QuestStarted(string Name);

public record MemberJoined(string Name);

public class Quest;

/// <summary>
///     The shared surface of jasperfx#1047, run through <see cref="IEventStoreOperations" /> the way a
///     session sees it, once for each of the two ways a store can adopt it.
/// </summary>
public abstract class EventOperationsBaseContract
{
    private static readonly QuestStarted Started = new("Find the ring");
    private static readonly MemberJoined Joined = new("Sam");
    private static readonly object[] OneEvent = [Started];
    private static readonly IEnumerable<object> OneEventLazily = OneEvent.Select(x => x);

    private static readonly CancellationToken Cancellation = new CancellationTokenSource().Token;

    protected abstract RecordingOperations NewOperations(StreamIdentity identity, string tenantId);

    protected abstract IEventStoreOperations Adopt(RecordingOperations operations);

    private (IEventStoreOperations Store, RecordingOperations Seams) guidKeyed(string tenantId = "root")
    {
        var seams = NewOperations(StreamIdentity.AsGuid, tenantId);
        return (Adopt(seams), seams);
    }

    private (IEventStoreOperations Store, RecordingOperations Seams) stringKeyed(string tenantId = "root")
    {
        var seams = NewOperations(StreamIdentity.AsString, tenantId);
        return (Adopt(seams), seams);
    }

    // A synchronous member refuses by throwing and an asynchronous one with a faulted Task, and the
    // tables below want to treat them alike.
    private static Task sync(Action action)
    {
        try
        {
            action();
            return Task.CompletedTask;
        }
        catch (Exception e)
        {
            return Task.FromException(e);
        }
    }

    private static Task noWriting(IEventStream<Quest> stream) => Task.CompletedTask;

    private static readonly Dictionary<string, Func<IEventStoreOperations, Guid, Task>> GuidMembers = new()
    {
        ["StartStream<T>(id, params)"] = (o, id) => sync(() => o.StartStream<Quest>(id, Started)),
        ["StartStream<T>(id, enumerable)"] = (o, id) => sync(() => o.StartStream<Quest>(id, OneEventLazily)),
        ["StartStream(type, id, params)"] = (o, id) => sync(() => o.StartStream(typeof(Quest), id, Started)),
        ["StartStream(type, id, enumerable)"] = (o, id) => sync(() => o.StartStream(typeof(Quest), id, OneEventLazily)),
        ["StartStream(id, params)"] = (o, id) => sync(() => o.StartStream(id, Started)),
        ["StartStream(id, enumerable)"] = (o, id) => sync(() => o.StartStream(id, OneEventLazily)),
        ["Append(id, params)"] = (o, id) => sync(() => o.Append(id, Started)),
        ["Append(id, enumerable)"] = (o, id) => sync(() => o.Append(id, OneEventLazily)),
        ["Append(id, version, params)"] = (o, id) => sync(() => o.Append(id, 5, Started)),
        ["Append(id, version, enumerable)"] = (o, id) => sync(() => o.Append(id, 5, OneEventLazily)),
        ["AppendOptimistic(id, token, params)"] = (o, id) => o.AppendOptimistic(id, Cancellation, Started),
        ["AppendOptimistic(id, params)"] = (o, id) => o.AppendOptimistic(id, Started),
        ["AppendExclusive(id, token, params)"] = (o, id) => o.AppendExclusive(id, Cancellation, Started),
        ["AppendExclusive(id, params)"] = (o, id) => o.AppendExclusive(id, Started),
        ["FetchForWriting(id)"] = (o, id) => o.FetchForWriting<Quest>(id),
        ["FetchForWriting(id, version)"] = (o, id) => o.FetchForWriting<Quest>(id, 1L),
        ["FetchForExclusiveWriting(id)"] = (o, id) => o.FetchForExclusiveWriting<Quest>(id),
        ["WriteToAggregate(id, action)"] = (o, id) => o.WriteToAggregate<Quest>(id, _ => { }),
        ["WriteToAggregate(id, func)"] = (o, id) => o.WriteToAggregate<Quest>(id, noWriting),
        ["WriteToAggregate(id, version, action)"] = (o, id) => o.WriteToAggregate<Quest>(id, 1, _ => { }),
        ["WriteToAggregate(id, version, func)"] = (o, id) => o.WriteToAggregate<Quest>(id, 1, noWriting),
        ["WriteExclusivelyToAggregate(id, action)"] = (o, id) => o.WriteExclusivelyToAggregate<Quest>(id, _ => { }),
        ["WriteExclusivelyToAggregate(id, func)"] = (o, id) => o.WriteExclusivelyToAggregate<Quest>(id, noWriting)
    };

    private static readonly Dictionary<string, Func<IEventStoreOperations, string, Task>> KeyMembers = new()
    {
        ["StartStream<T>(key, params)"] = (o, key) => sync(() => o.StartStream<Quest>(key, Started)),
        ["StartStream<T>(key, enumerable)"] = (o, key) => sync(() => o.StartStream<Quest>(key, OneEventLazily)),
        ["StartStream(type, key, params)"] = (o, key) => sync(() => o.StartStream(typeof(Quest), key, Started)),
        ["StartStream(type, key, enumerable)"] = (o, key) => sync(() => o.StartStream(typeof(Quest), key, OneEventLazily)),
        ["StartStream(key, params)"] = (o, key) => sync(() => o.StartStream(key, Started)),
        ["StartStream(key, enumerable)"] = (o, key) => sync(() => o.StartStream(key, OneEventLazily)),
        ["Append(key, params)"] = (o, key) => sync(() => o.Append(key, Started)),
        ["Append(key, enumerable)"] = (o, key) => sync(() => o.Append(key, OneEventLazily)),
        ["Append(key, version, params)"] = (o, key) => sync(() => o.Append(key, 5, Started)),
        ["Append(key, version, enumerable)"] = (o, key) => sync(() => o.Append(key, 5, OneEventLazily)),
        ["AppendOptimistic(key, token, params)"] = (o, key) => o.AppendOptimistic(key, Cancellation, Started),
        ["AppendOptimistic(key, params)"] = (o, key) => o.AppendOptimistic(key, Started),
        ["AppendExclusive(key, token, params)"] = (o, key) => o.AppendExclusive(key, Cancellation, Started),
        ["AppendExclusive(key, params)"] = (o, key) => o.AppendExclusive(key, Started),
        ["FetchForWriting(key)"] = (o, key) => o.FetchForWriting<Quest>(key),
        ["FetchForWriting(key, version)"] = (o, key) => o.FetchForWriting<Quest>(key, 1L),
        ["FetchForExclusiveWriting(key)"] = (o, key) => o.FetchForExclusiveWriting<Quest>(key),
        ["WriteToAggregate(key, action)"] = (o, key) => o.WriteToAggregate<Quest>(key, _ => { }),
        ["WriteToAggregate(key, func)"] = (o, key) => o.WriteToAggregate<Quest>(key, noWriting),
        ["WriteToAggregate(key, version, action)"] = (o, key) => o.WriteToAggregate<Quest>(key, 1, _ => { }),
        ["WriteToAggregate(key, version, func)"] = (o, key) => o.WriteToAggregate<Quest>(key, 1, noWriting),
        ["WriteExclusivelyToAggregate(key, action)"] = (o, key) => o.WriteExclusivelyToAggregate<Quest>(key, _ => { }),
        ["WriteExclusivelyToAggregate(key, func)"] = (o, key) => o.WriteExclusivelyToAggregate<Quest>(key, noWriting)
    };

    public static TheoryData<string> GuidMemberNames => new(GuidMembers.Keys);

    public static TheoryData<string> KeyMemberNames => new(KeyMembers.Keys);

    // ---- the StreamIdentity guard ----

    [Theory]
    [MemberData(nameof(GuidMemberNames))]
    public async Task a_guid_member_on_a_string_keyed_store_is_refused_before_any_seam_runs(string member)
    {
        var (store, seams) = stringKeyed();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => GuidMembers[member](store, Guid.NewGuid()));

        ex.Message.ShouldContain("configured for string stream identity");
        ex.Message.ShouldContain("opts.Events.StreamIdentity = StreamIdentity.AsGuid");
        seams.Calls.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(KeyMemberNames))]
    public async Task a_string_member_on_a_guid_keyed_store_is_refused_before_any_seam_runs(string member)
    {
        var (store, seams) = guidKeyed();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => KeyMembers[member](store, "quest-1"));

        ex.Message.ShouldContain("configured for Guid stream identity");
        ex.Message.ShouldContain("opts.Events.StreamIdentity = StreamIdentity.AsString");
        seams.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void a_refused_fetch_is_a_faulted_task_and_not_a_synchronous_throw()
    {
        var (store, _) = stringKeyed();

        store.FetchForWriting<Quest>(Guid.NewGuid()).IsFaulted.ShouldBeTrue();
        store.FetchForExclusiveWriting<Quest>(Guid.NewGuid()).IsFaulted.ShouldBeTrue();
    }

    // ---- empty identities ----

    [Theory]
    [MemberData(nameof(GuidMemberNames))]
    public async Task an_empty_guid_is_refused_before_any_seam_runs(string member)
    {
        var (store, seams) = guidKeyed();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => GuidMembers[member](store, Guid.Empty));

        seams.Calls.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(KeyMemberNames))]
    public async Task an_empty_key_is_refused_before_any_seam_runs(string member)
    {
        var (store, seams) = stringKeyed();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => KeyMembers[member](store, ""));

        seams.Calls.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(KeyMemberNames))]
    public async Task a_null_key_is_refused_before_any_seam_runs(string member)
    {
        var (store, seams) = stringKeyed();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => KeyMembers[member](store, null!));

        seams.Calls.ShouldBeEmpty();
    }

    // ---- the events ----

    [Fact]
    public void starting_a_stream_with_no_events_is_refused_on_either_identity()
    {
        var (byId, idSeams) = guidKeyed();
        var (byKey, keySeams) = stringKeyed();

        Should.Throw<EmptyEventStreamException>(() => byId.StartStream(Guid.NewGuid()));
        Should.Throw<EmptyEventStreamException>(() => byId.StartStream<Quest>());
        Should.Throw<EmptyEventStreamException>(() => byKey.StartStream("quest-1"));
        Should.Throw<EmptyEventStreamException>(() => byKey.StartStream(typeof(Quest)));

        idSeams.Calls.ShouldBeEmpty();
        keySeams.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void appending_no_events_reaches_the_store()
    {
        // Marten's fetch plans open a stream action this way, before anything has been appended
        var (store, seams) = guidKeyed();
        var id = Guid.NewGuid();

        store.Append(id).Id.ShouldBe(id);

        seams.Calls.ShouldBe([$"AppendCore({id}, 0)"]);
    }

    [Fact]
    public void null_events_are_refused_the_same_way_by_every_shape()
    {
        var (byId, _) = guidKeyed();
        var (byKey, _) = stringKeyed();

        Should.Throw<ArgumentNullException>(() => byId.StartStream(Guid.NewGuid(), (object[])null!));
        Should.Throw<ArgumentNullException>(() => byId.StartStream(Guid.NewGuid(), (IEnumerable<object>)null!));
        Should.Throw<ArgumentNullException>(() => byId.Append(Guid.NewGuid(), (object[])null!));
        Should.Throw<ArgumentNullException>(() => byId.Append(Guid.NewGuid(), (IEnumerable<object>)null!));
        Should.Throw<ArgumentNullException>(() => byKey.StartStream("quest-1", (object[])null!));
        Should.Throw<ArgumentNullException>(() => byKey.Append("quest-1", (IEnumerable<object>)null!));
    }

    // ---- a stream the caller did not name ----

    [Fact]
    public void an_unnamed_stream_gets_a_guid_on_a_guid_keyed_store()
    {
        var (store, _) = guidKeyed();

        store.StartStream(Started).Id.ShouldNotBe(Guid.Empty);
        store.StartStream<Quest>(Started).Id.ShouldNotBe(Guid.Empty);
        store.StartStream(typeof(Quest), OneEventLazily).Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void an_unnamed_stream_gets_a_key_on_a_string_keyed_store()
    {
        // Marten refused here, and Polecat built an action with a Guid id and no key
        var (store, _) = stringKeyed();

        store.StartStream(Started).Key.ShouldNotBeNullOrEmpty();
        store.StartStream<Quest>(Started).Key.ShouldNotBeNullOrEmpty();
        store.StartStream(typeof(Quest), OneEventLazily).Key.ShouldNotBeNullOrEmpty();
    }

    // ---- UseMandatoryStreamTypeDeclaration ----

    [Fact]
    public void a_store_that_requires_a_stream_type_refuses_only_the_overloads_that_declare_none()
    {
        var (byId, idSeams) = guidKeyed();
        var (byKey, keySeams) = stringKeyed();
        idSeams.RequireStreamType = true;
        keySeams.RequireStreamType = true;

        var id = Guid.NewGuid();

        Should.Throw<StreamTypeRequired>(() => byId.StartStream(id, Started));
        Should.Throw<StreamTypeRequired>(() => byId.StartStream(id, OneEventLazily));
        Should.Throw<StreamTypeRequired>(() => byId.StartStream(Started));
        Should.Throw<StreamTypeRequired>(() => byId.StartStream(OneEventLazily));
        Should.Throw<StreamTypeRequired>(() => byKey.StartStream("quest-1", Started));
        Should.Throw<StreamTypeRequired>(() => byKey.StartStream("quest-1", OneEventLazily));
        Should.Throw<StreamTypeRequired>(() => byKey.StartStream(Started));
        Should.Throw<StreamTypeRequired>(() => byKey.StartStream(OneEventLazily));
    }

    [Fact]
    public void every_overload_that_declares_a_stream_type_hands_it_to_the_store()
    {
        var (byId, idSeams) = guidKeyed();
        var (byKey, keySeams) = stringKeyed();
        idSeams.RequireStreamType = true;
        keySeams.RequireStreamType = true;

        StreamAction[] started =
        [
            byId.StartStream<Quest>(Guid.NewGuid(), Started),
            byId.StartStream<Quest>(Guid.NewGuid(), OneEventLazily),
            byId.StartStream(typeof(Quest), Guid.NewGuid(), Started),
            byId.StartStream(typeof(Quest), Guid.NewGuid(), OneEventLazily),
            byId.StartStream<Quest>(Started),
            byId.StartStream<Quest>(OneEventLazily),
            byId.StartStream(typeof(Quest), Started),
            byId.StartStream(typeof(Quest), OneEventLazily),
            byKey.StartStream<Quest>("quest-1", Started),
            byKey.StartStream<Quest>("quest-2", OneEventLazily),
            byKey.StartStream(typeof(Quest), "quest-3", Started),
            byKey.StartStream(typeof(Quest), "quest-4", OneEventLazily),
            byKey.StartStream<Quest>(Started),
            byKey.StartStream<Quest>(OneEventLazily),
            byKey.StartStream(typeof(Quest), Started),
            byKey.StartStream(typeof(Quest), OneEventLazily)
        ];

        started.ShouldAllBe(x => x.AggregateType == typeof(Quest));
    }

    // ---- the expected version on Append ----

    [Fact]
    public void the_expected_version_is_the_version_after_the_append_by_default()
    {
        var (byId, _) = guidKeyed();
        var (byKey, _) = stringKeyed();

        byId.Append(Guid.NewGuid(), 5, Started, Joined).ExpectedVersionOnServer.ShouldBe(3);
        byId.Append(Guid.NewGuid(), 5, (IEnumerable<object>)[Started, Joined]).ExpectedVersionOnServer.ShouldBe(3);
        byKey.Append("quest-1", 5, Started, Joined).ExpectedVersionOnServer.ShouldBe(3);
        byKey.Append("quest-2", 5, (IEnumerable<object>)[Started, Joined]).ExpectedVersionOnServer.ShouldBe(3);
    }

    [Fact]
    public void the_expected_version_counts_what_is_already_pending_on_the_stream()
    {
        var (store, _) = guidKeyed();
        var id = Guid.NewGuid();

        store.Append(id, Started);

        store.Append(id, 5, Joined).ExpectedVersionOnServer.ShouldBe(3);
    }

    [Fact]
    public void an_expected_version_below_the_event_count_is_refused_by_guid()
    {
        var (store, _) = guidKeyed();

        Should.Throw<ArgumentOutOfRangeException>(() => store.Append(Guid.NewGuid(), 1, Started, Joined))
            .ParamName.ShouldBe("expectedVersion");
    }

    [Fact]
    public void an_expected_version_below_the_event_count_is_refused_by_key()
    {
        // Marten's string overload had no such guard, so a negative version went to the database
        var (store, _) = stringKeyed();

        Should.Throw<ArgumentOutOfRangeException>(() => store.Append("quest-1", 1, Started, Joined))
            .ParamName.ShouldBe("expectedVersion");
    }

    // ---- AppendOptimistic and AppendExclusive ----

    [Fact]
    public async Task an_optimistic_append_guards_on_the_version_the_store_read()
    {
        var (store, seams) = guidKeyed();
        var id = Guid.NewGuid();
        seams.ExistingVersion = 4;

        await store.AppendOptimistic(id, Cancellation, Started);

        seams.Calls.ShouldBe([$"ReadStreamVersionCore({id}, False)", $"AppendCore({id}, 1)"]);
        seams.Tokens.ShouldBe([Cancellation]);
        seams.Streams.Single().ExpectedVersionOnServer.ShouldBe(4);
    }

    [Fact]
    public async Task an_exclusive_append_asks_the_store_for_the_lock()
    {
        var (store, seams) = stringKeyed();
        seams.ExistingVersion = 4;

        await store.AppendExclusive("quest-1", Cancellation, Started);

        seams.Calls.ShouldBe(["ReadStreamVersionCore(quest-1, True)", "AppendCore(quest-1, 1)"]);
        seams.Tokens.ShouldBe([Cancellation]);
        seams.Streams.Single().ExpectedVersionOnServer.ShouldBe(4);
    }

    [Fact]
    public async Task the_expected_version_of_a_concurrent_append_is_not_adjusted_for_its_events()
    {
        // It is what the store read, whichever way the store reads Append(stream, version, ...)
        var (store, seams) = guidKeyed();
        seams.ExistingVersion = 4;

        await store.AppendOptimistic(Guid.NewGuid(), Started, Joined);

        seams.Streams.Single().ExpectedVersionOnServer.ShouldBe(4);
    }

    [Fact]
    public async Task nothing_is_appended_to_a_stream_the_store_says_does_not_exist()
    {
        var (byId, idSeams) = guidKeyed();
        var (byKey, keySeams) = stringKeyed();
        idSeams.StreamExists = false;
        keySeams.StreamExists = false;

        await Should.ThrowAsync<NonExistentStreamException>(() => byId.AppendOptimistic(Guid.NewGuid(), Started));
        await Should.ThrowAsync<NonExistentStreamException>(() => byId.AppendExclusive(Guid.NewGuid(), Started));
        await Should.ThrowAsync<NonExistentStreamException>(() => byKey.AppendOptimistic("quest-1", Started));
        await Should.ThrowAsync<NonExistentStreamException>(() => byKey.AppendExclusive("quest-1", Started));

        idSeams.Streams.ShouldBeEmpty();
        keySeams.Streams.ShouldBeEmpty();
    }

    // ---- FetchForWriting ----

    [Fact]
    public async Task each_fetch_passes_its_concurrency_intent_to_the_store_untouched()
    {
        var (store, seams) = guidKeyed();
        var id = Guid.NewGuid();

        await store.FetchForWriting<Quest>(id, Cancellation);
        await store.FetchForWriting<Quest>(id, 7L, Cancellation);
        await store.FetchForExclusiveWriting<Quest>(id, Cancellation);

        seams.Calls.ShouldBe(
        [
            $"FetchForWritingCore<Quest, Guid>({id}, , False)",
            $"FetchForWritingCore<Quest, Guid>({id}, 7, False)",
            $"FetchForWritingCore<Quest, Guid>({id}, , True)"
        ]);
        seams.Tokens.ShouldBe([Cancellation, Cancellation, Cancellation]);
    }

    [Fact]
    public async Task each_fetch_by_key_passes_its_concurrency_intent_to_the_store_untouched()
    {
        var (store, seams) = stringKeyed();

        await store.FetchForWriting<Quest>("quest-1", Cancellation);
        await store.FetchForWriting<Quest>("quest-1", 7L, Cancellation);
        await store.FetchForExclusiveWriting<Quest>("quest-1", Cancellation);

        seams.Calls.ShouldBe(
        [
            "FetchForWritingCore<Quest, String>(quest-1, , False)",
            "FetchForWritingCore<Quest, String>(quest-1, 7, False)",
            "FetchForWritingCore<Quest, String>(quest-1, , True)"
        ]);
    }

    [Fact]
    public async Task a_fetch_hands_back_the_stream_the_store_built()
    {
        var (store, seams) = guidKeyed();

        var stream = await store.FetchForWriting<Quest>(Guid.NewGuid());

        stream.ShouldBeSameAs(seams.LastFetched);
    }

    // ---- WriteToAggregate ----

    [Fact]
    public async Task writing_runs_between_the_fetch_and_the_save()
    {
        var (store, seams) = guidKeyed();
        var id = Guid.NewGuid();

        await store.WriteToAggregate<Quest>(id, _ => seams.Calls.Add("writing"), Cancellation);

        seams.Calls.ShouldBe([$"FetchForWritingCore<Quest, Guid>({id}, , False)", "writing", "SaveChangesCore"]);
        seams.Tokens.ShouldBe([Cancellation, Cancellation]);
    }

    [Fact]
    public async Task async_writing_is_awaited_before_the_save()
    {
        var (store, seams) = stringKeyed();

        await store.WriteToAggregate<Quest>("quest-1", async _ =>
        {
            await Task.Yield();
            seams.Calls.Add("writing");
        }, Cancellation);

        seams.Calls.ShouldBe(["FetchForWritingCore<Quest, String>(quest-1, , False)", "writing", "SaveChangesCore"]);
    }

    [Fact]
    public async Task writing_at_an_expected_version_fetches_at_that_version()
    {
        var (store, seams) = guidKeyed();
        var id = Guid.NewGuid();

        await store.WriteToAggregate<Quest>(id, 3, _ => { }, Cancellation);

        seams.Calls[0].ShouldBe($"FetchForWritingCore<Quest, Guid>({id}, 3, False)");
    }

    [Fact]
    public async Task writing_exclusively_fetches_exclusively()
    {
        var (store, seams) = guidKeyed();
        var id = Guid.NewGuid();

        await store.WriteExclusivelyToAggregate<Quest>(id, _ => { }, Cancellation);
        await store.WriteExclusivelyToAggregate<Quest>(id, noWriting, Cancellation);

        seams.Calls.ShouldBe(
        [
            $"FetchForWritingCore<Quest, Guid>({id}, , True)", "SaveChangesCore",
            $"FetchForWritingCore<Quest, Guid>({id}, , True)", "SaveChangesCore"
        ]);
    }

    [Fact]
    public async Task a_writer_that_throws_leaves_the_unit_of_work_uncommitted()
    {
        var (store, seams) = guidKeyed();

        await Should.ThrowAsync<DivideByZeroException>(() =>
            store.WriteToAggregate<Quest>(Guid.NewGuid(), _ => throw new DivideByZeroException()));
        await Should.ThrowAsync<DivideByZeroException>(() =>
            store.WriteToAggregate<Quest>(Guid.NewGuid(), _ => Task.FromException(new DivideByZeroException())));

        seams.Calls.ShouldNotContain("SaveChangesCore");
    }

    [Fact]
    public async Task a_failed_fetch_never_reaches_the_writer()
    {
        var (store, seams) = guidKeyed();
        seams.FetchFailure = new DivideByZeroException();
        var written = false;

        await Should.ThrowAsync<DivideByZeroException>(() =>
            store.WriteToAggregate<Quest>(Guid.NewGuid(), _ => written = true));

        written.ShouldBeFalse();
        seams.Calls.ShouldNotContain("SaveChangesCore");
    }

    // ---- the tenant ----

    [Fact]
    public void a_stream_keeps_the_tenant_of_the_operations_it_was_started_on()
    {
        var (root, seams) = guidKeyed("root");
        var scoped = Adopt(seams.ForTenant("scoped"));

        root.StartStream(Guid.NewGuid(), Started).TenantId.ShouldBe("root");
        scoped.StartStream(Guid.NewGuid(), Started).TenantId.ShouldBe("scoped");
        scoped.StartStream<Quest>(Started).TenantId.ShouldBe("scoped");
    }

    [Fact]
    public void a_stream_keeps_the_tenant_of_the_operations_it_was_appended_on()
    {
        var (root, seams) = stringKeyed("root");
        var scoped = Adopt(seams.ForTenant("scoped"));

        root.Append("quest-1", Started).TenantId.ShouldBe("root");
        scoped.Append("quest-2", Started).TenantId.ShouldBe("scoped");
        scoped.Append("quest-3", 2, Started, Joined).TenantId.ShouldBe("scoped");
    }

    [Fact]
    public async Task the_seams_of_a_tenant_scope_are_the_ones_that_run()
    {
        // The version read and the fetch are where a tenant filter lives, so running these on the
        // parent would read the parent's tenant's stream and guard the scope's write with its version.
        var (root, seams) = guidKeyed("root");
        var scope = seams.ForTenant("scoped");
        var scoped = Adopt(scope);
        var id = Guid.NewGuid();

        await scoped.AppendOptimistic(id, Started);
        await scoped.AppendExclusive(id, Started);
        await scoped.FetchForWriting<Quest>(id);
        await scoped.WriteToAggregate<Quest>(id, _ => { });

        seams.Calls.ShouldBeEmpty();
        scope.Calls.ShouldContain($"ReadStreamVersionCore({id}, False)");
        scope.Calls.ShouldContain($"ReadStreamVersionCore({id}, True)");
        scope.Calls.ShouldContain($"FetchForWritingCore<Quest, Guid>({id}, , False)");
        scope.Calls.ShouldContain("SaveChangesCore");
        scope.Streams.ShouldAllBe(x => x.TenantId == "scoped");
    }

    [Fact]
    public void a_boundary_over_a_tenant_scope_appends_to_that_tenant()
    {
        // The other half of jasperfx#1044: EventBoundary trusts Append and StartStream to stamp the tenant
        var (_, seams) = guidKeyed("root");
        var scoped = Adopt(seams.ForTenant("scoped"));
        var registry = new PassThroughRegistry();

        var boundary = new EventBoundary<Quest>(scoped, registry, [], null, []);
        boundary.AppendOne(registry.BuildEvent(Started).WithTag("ward-7"));

        seams.Streams.Single().TenantId.ShouldBe("scoped");
    }
}

/// <summary>
///     Fisher's route: no base class of its own, and <c>session.Events</c> returns the concrete type, so
///     the members have to be real public methods on it.
/// </summary>
public class deriving_from_the_operations_base : EventOperationsBaseContract
{
    protected override RecordingOperations NewOperations(StreamIdentity identity, string tenantId)
        => new DerivedStore(identity, tenantId);

    protected override IEventStoreOperations Adopt(RecordingOperations operations)
        => (IEventStoreOperations)operations;

    [Fact]
    public void the_surface_is_callable_on_the_concrete_type()
    {
        var store = new DerivedStore(StreamIdentity.AsGuid, "root");

        var stream = store.StartStream<Quest>(Guid.NewGuid(), new QuestStarted("Find the ring"));

        store.Append(stream.Id, new MemberJoined("Sam")).Events.Count.ShouldBe(2);
    }

    private sealed class DerivedStore(StreamIdentity identity, string tenantId, List<StreamAction>? unitOfWork = null)
        : RecordingOperations(identity, tenantId, unitOfWork), IRestOfTheContract
    {
        public override RecordingOperations ForTenant(string scope) => new DerivedStore(identity, scope, UnitOfWork);
    }
}

/// <summary>
///     Marten's and Polecat's route: their class derives from their <c>QueryEventStore</c>, so the
///     surface arrives through <see cref="IEventOperationsHost" />.
/// </summary>
public class hosting_the_operations_base : EventOperationsBaseContract
{
    protected override RecordingOperations NewOperations(StreamIdentity identity, string tenantId)
        => new(identity, tenantId);

    protected override IEventStoreOperations Adopt(RecordingOperations operations) => new HostedStore(operations);

    private class StoresOwnBaseClass;

    private sealed class HostedStore(RecordingOperations operations)
        : StoresOwnBaseClass, IEventOperationsHost, IRestOfTheContract
    {
        public EventOperationsBase Operations => operations;
    }
}

public class StreamTypeRequired : Exception;

public class PassThroughRegistry : EventRegistry
{
    public override IEvent BuildEvent(object eventData) => eventData as IEvent ?? base.BuildEvent(eventData);
}

/// <summary>
///     A store over a list. Every seam records itself, and the tenant comes from a field of this instance
///     as it does on a Polecat or Fisher tenant scope, while the unit of work is shared with the parent.
/// </summary>
public class RecordingOperations(StreamIdentity identity, string tenantId, List<StreamAction>? unitOfWork = null)
    : EventOperationsBase
{
    private readonly PassThroughRegistry _registry = new();

    protected List<StreamAction> UnitOfWork { get; } = unitOfWork ?? [];

    public List<string> Calls { get; } = [];
    public List<CancellationToken> Tokens { get; } = [];
    public bool RequireStreamType { get; set; }
    public bool StreamExists { get; set; } = true;
    public long ExistingVersion { get; set; } = 1;
    public Exception? FetchFailure { get; set; }
    public object? LastFetched { get; private set; }

    public IReadOnlyList<StreamAction> Streams => UnitOfWork;

    public virtual RecordingOperations ForTenant(string scope) => new(identity, scope, UnitOfWork);

    protected override StreamIdentity StreamIdentity => identity;

    protected override StreamAction StartStreamCore(Type? aggregateType, Guid id, object[] events)
    {
        Calls.Add($"StartStreamCore({aggregateType?.Name}, {id}, {events.Length})");
        if (aggregateType == null && RequireStreamType) throw new StreamTypeRequired();

        return track(StreamAction.Start(_registry, id, events), aggregateType);
    }

    protected override StreamAction StartStreamCore(Type? aggregateType, string streamKey, object[] events)
    {
        Calls.Add($"StartStreamCore({aggregateType?.Name}, {streamKey}, {events.Length})");
        if (aggregateType == null && RequireStreamType) throw new StreamTypeRequired();

        return track(StreamAction.Start(_registry, streamKey, events), aggregateType);
    }

    protected override StreamAction AppendCore(Guid stream, object[] events)
    {
        Calls.Add($"AppendCore({stream}, {events.Length})");

        var pending = UnitOfWork.FirstOrDefault(x => x.Id == stream);
        return pending != null
            ? pending.AddEvents(events.Select(_registry.BuildEvent).ToArray())
            : track(StreamAction.Append(_registry, stream, events), null);
    }

    protected override StreamAction AppendCore(string stream, object[] events)
    {
        Calls.Add($"AppendCore({stream}, {events.Length})");

        var pending = UnitOfWork.FirstOrDefault(x => x.Key == stream);
        return pending != null
            ? pending.AddEvents(events.Select(_registry.BuildEvent).ToArray())
            : track(StreamAction.Append(_registry, stream, events), null);
    }

    protected override Task<long> ReadStreamVersionCore<TId>(TId stream, bool exclusive, CancellationToken token)
    {
        Calls.Add($"ReadStreamVersionCore({stream}, {exclusive})");
        Tokens.Add(token);

        return StreamExists
            ? Task.FromResult(ExistingVersion)
            : Task.FromException<long>(new NonExistentStreamException(stream));
    }

    protected override Task<IEventStream<T>> FetchForWritingCore<T, TId>(TId id, long? expectedVersion, bool exclusive,
        CancellationToken cancellation)
    {
        Calls.Add($"FetchForWritingCore<{typeof(T).Name}, {typeof(TId).Name}>({id}, {expectedVersion}, {exclusive})");
        Tokens.Add(cancellation);

        if (FetchFailure != null) return Task.FromException<IEventStream<T>>(FetchFailure);

        IEventStream<T> stream = new StubEventStream<T>(default);
        LastFetched = stream;

        return Task.FromResult(stream);
    }

    protected override Task SaveChangesCore(CancellationToken cancellation)
    {
        Calls.Add("SaveChangesCore");
        Tokens.Add(cancellation);

        return Task.CompletedTask;
    }

    private StreamAction track(StreamAction stream, Type? aggregateType)
    {
        stream.TenantId = tenantId;
        stream.AggregateType = aggregateType;
        UnitOfWork.Add(stream);

        return stream;
    }
}

/// <summary>
///     What neither route supplies, and what a real store implements anyway.
/// </summary>
public interface IRestOfTheContract : IEventStoreOperations
{
    private static Exception NotPartOfTheBase => new NotSupportedException("Not part of EventOperationsBase");

    IEvent IEventStoreOperations.BuildEvent(object data) => throw NotPartOfTheBase;
    void IEventStoreOperations.ArchiveStream(Guid streamId) => throw NotPartOfTheBase;
    void IEventStoreOperations.ArchiveStream(string streamKey) => throw NotPartOfTheBase;
    void IEventStoreOperations.OverwriteEvent(IEvent e) => throw NotPartOfTheBase;
    Guid IEventStoreOperations.CompletelyReplaceEvent<T>(long sequence, T eventBody) => throw NotPartOfTheBase;

    void IEventStoreOperations.AssignTagWhere(Expression<Func<IEvent, bool>> expression, object tag)
        => throw NotPartOfTheBase;

    Task<IEventStream<T>> IEventStoreOperations.FetchForWriting<T, TId>(TId id, CancellationToken cancellation)
        => throw NotPartOfTheBase;

    Task<IEventStream<T>> IEventStoreOperations.FetchForExclusiveWriting<T, TId>(TId id, CancellationToken cancellation)
        => throw NotPartOfTheBase;

    ValueTask<T?> IEventStoreOperations.FetchLatest<T>(Guid id, CancellationToken cancellation) where T : class
        => throw NotPartOfTheBase;

    ValueTask<T?> IEventStoreOperations.FetchLatest<T>(string id, CancellationToken cancellation) where T : class
        => throw NotPartOfTheBase;

    ValueTask<T?> IEventStoreOperations.FetchLatest<T, TId>(TId id, CancellationToken cancellation) where T : class
        => throw NotPartOfTheBase;

    ValueTask<T?> IEventStoreOperations.ProjectLatest<T>(Guid id, CancellationToken cancellation) where T : class
        => throw NotPartOfTheBase;

    ValueTask<T?> IEventStoreOperations.ProjectLatest<T>(string id, CancellationToken cancellation) where T : class
        => throw NotPartOfTheBase;

    Task<bool> IEventStoreOperations.EventsExistAsync(EventTagQuery query, CancellationToken cancellation)
        => throw NotPartOfTheBase;

    Task<IReadOnlyList<IEvent>> IEventStoreOperations.QueryByTagsAsync(EventTagQuery query,
        CancellationToken cancellation)
        => throw NotPartOfTheBase;

    Task<T?> IEventStoreOperations.AggregateByTagsAsync<T>(EventTagQuery query, CancellationToken cancellation)
        where T : class
        => throw NotPartOfTheBase;

    Task<IEventBoundary<T>> IEventStoreOperations.FetchForWritingByTags<T>(EventTagQuery query,
        CancellationToken cancellation)
        => throw NotPartOfTheBase;

    Task<IReadOnlyList<IEvent>> IQueryEventStore.FetchStreamAsync(Guid streamId, long version,
        DateTimeOffset? timestamp, long fromVersion, CancellationToken token)
        => throw NotPartOfTheBase;

    Task<IReadOnlyList<IEvent>> IQueryEventStore.FetchStreamAsync(string streamKey, long version,
        DateTimeOffset? timestamp, long fromVersion, CancellationToken token)
        => throw NotPartOfTheBase;

    Task<T?> IQueryEventStore.AggregateStreamAsync<T>(Guid streamId, long version, DateTimeOffset? timestamp,
        T? state, long fromVersion, CancellationToken token) where T : class
        => throw NotPartOfTheBase;

    Task<T?> IQueryEventStore.AggregateStreamAsync<T>(string streamKey, long version, DateTimeOffset? timestamp,
        T? state, long fromVersion, CancellationToken token) where T : class
        => throw NotPartOfTheBase;

    Task<T?> IQueryEventStore.AggregateStreamToLastKnownAsync<T>(Guid streamId, long version,
        DateTimeOffset? timestamp, CancellationToken token) where T : class
        => throw NotPartOfTheBase;

    Task<T?> IQueryEventStore.AggregateStreamToLastKnownAsync<T>(string streamKey, long version,
        DateTimeOffset? timestamp, CancellationToken token) where T : class
        => throw NotPartOfTheBase;

    Task<IEvent<T>?> IQueryEventStore.LoadAsync<T>(Guid id, CancellationToken token) where T : class
        => throw NotPartOfTheBase;

    Task<IEvent?> IQueryEventStore.LoadAsync(Guid id, CancellationToken token) => throw NotPartOfTheBase;

    Task<StreamState?> IQueryEventStore.FetchStreamStateAsync(Guid streamId, CancellationToken token)
        => throw NotPartOfTheBase;

    Task<StreamState?> IQueryEventStore.FetchStreamStateAsync(string streamKey, CancellationToken token)
        => throw NotPartOfTheBase;
}
