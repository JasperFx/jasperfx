using System;
using System.Threading.Tasks;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Natural key registration aggregates

public readonly record struct RegistrationKey(string Value);

public readonly record struct RegistrationOtherKey(string Value);

public readonly record struct RegistrationGuidKey(Guid Value);

public record RegistrationKeyed(RegistrationKey Key);

public record RegistrationOtherKeyed(RegistrationOtherKey Key);

public record RegistrationGuidKeyed(RegistrationGuidKey Key);

/// <summary>Owns <see cref="RegistrationKey" />, for the mapped keys below.</summary>
public partial class RegistrationKeyOwner
{
    public Guid Id { get; set; }

    [NaturalKey]
    public RegistrationKey Key { get; set; }

    [NaturalKeySource]
    public void Apply(RegistrationKeyed e) => Key = e.Key;
}

/// <summary>Owns <see cref="RegistrationKey" />, declared <c>TKey?</c>.</summary>
public partial class RegistrationNullableKeyOwner
{
    public Guid Id { get; set; }

    [NaturalKey]
    public RegistrationKey? Key { get; set; }

    [NaturalKeySource]
    public void Apply(RegistrationKeyed e) => Key = e.Key;
}

/// <summary>Maps a <c>TKey?</c> property to an owner that declares <c>TKey</c>.</summary>
public partial class RegistrationNullableMappedView
{
    public Guid Id { get; set; }

    [MappedNaturalKey<RegistrationKeyOwner>]
    public RegistrationKey? Key { get; set; }

    public int Count { get; set; }

    public void Apply(RegistrationKeyed e)
    {
        Key = e.Key;
        Count++;
    }
}

/// <summary>Maps a <c>TKey</c> property to an owner that declares <c>TKey?</c>.</summary>
public partial class RegistrationMappedToNullableView
{
    public Guid Id { get; set; }

    [MappedNaturalKey<RegistrationNullableKeyOwner>]
    public RegistrationKey Key { get; set; }

    public int Count { get; set; }

    public void Apply(RegistrationKeyed e)
    {
        Key = e.Key;
        Count++;
    }
}

public partial class RegistrationNoKeyOwner
{
    public Guid Id { get; set; }

    public void Apply(RegistrationKeyed _) { }
}

// Each of these is refused when the store is built. The analyzer reports most of them at compile time too.
#pragma warning disable JFXEVT010, JFXEVT011, JFXEVT012, JFXEVT013, JFXEVT014, JFXEVT015

public partial class RegistrationTwoOwnedKeys
{
    public Guid Id { get; set; }

    [NaturalKey]
    public RegistrationKey Key { get; set; }

    [NaturalKey]
    public RegistrationOtherKey Other { get; set; }

    [NaturalKeySource]
    public void Apply(RegistrationKeyed e) => Key = e.Key;
}

public partial class RegistrationSharedKeyType
{
    public Guid Id { get; set; }

    [NaturalKey]
    public RegistrationKey Key { get; set; }

    [MappedNaturalKey<RegistrationKeyOwner>]
    public RegistrationKey Mapped { get; set; }

    [NaturalKeySource]
    public void Apply(RegistrationKeyed e) => Key = e.Key;
}

public partial class RegistrationGuidKeyAggregate
{
    public Guid Id { get; set; }

    [NaturalKey]
    public RegistrationGuidKey Key { get; set; }

    [NaturalKeySource]
    public void Apply(RegistrationGuidKeyed e) => Key = e.Key;
}

public partial class RegistrationPrivateSource
{
    public Guid Id { get; set; }

    [NaturalKey]
    public RegistrationKey Key { get; set; }

    [NaturalKeySource]
    private void Apply(RegistrationKeyed e) => Key = e.Key;
}

public partial class RegistrationSourceWithoutAnEvent
{
    public Guid Id { get; set; }

    [NaturalKey]
    public RegistrationKey Key { get; set; }

    [NaturalKeySource]
    public void Reset() => Key = default;

    public void Apply(RegistrationKeyed e) => Key = e.Key;
}

public partial class RegistrationKeyNeverSet
{
    public Guid Id { get; set; }

    [NaturalKey]
    public RegistrationKey Key { get; set; }

    public void Apply(RegistrationOtherKeyed _) { }
}

public partial class RegistrationMappedOnlyWithASource
{
    public Guid Id { get; set; }

    [MappedNaturalKey<RegistrationKeyOwner>]
    public RegistrationKey Key { get; set; }

    [NaturalKeySource]
    public void Apply(RegistrationKeyed e) => Key = e.Key;
}

public partial class RegistrationMappedToAnUnregisteredOwner
{
    public Guid Id { get; set; }

    [MappedNaturalKey<RegistrationKeyOwner>]
    public RegistrationKey Key { get; set; }

    public void Apply(RegistrationKeyed e) => Key = e.Key;
}

public partial class RegistrationMappedToAnOwnerWithoutAKey
{
    public Guid Id { get; set; }

    [MappedNaturalKey<RegistrationNoKeyOwner>]
    public RegistrationKey Key { get; set; }

    public void Apply(RegistrationKeyed e) => Key = e.Key;
}

public partial class RegistrationMappedToTheWrongKeyType
{
    public Guid Id { get; set; }

    [MappedNaturalKey<RegistrationKeyOwner>]
    public RegistrationOtherKey Key { get; set; }

    public void Apply(RegistrationOtherKeyed e) => Key = e.Key;
}

#pragma warning restore JFXEVT010, JFXEVT011, JFXEVT012, JFXEVT013, JFXEVT014, JFXEVT015

#endregion

/// <summary>
/// What a store refuses when it is built with a natural key it could not maintain or fetch by, and the
/// <c>TKey</c>/<c>TKey?</c> mixes it accepts. Every refusal is an <see cref="InvalidProjectionException" />
/// naming the problem, raised while the store is configured rather than on the first append or fetch.
/// </summary>
/// <remarks>
/// Gated on <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.SupportsNaturalKeys" />, and the
/// mapped-key facts also on
/// <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.SupportsMappedNaturalKeys" />. Each fact
/// configures its own store.
/// </remarks>
public abstract class NaturalKeyRegistrationCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static void addEventTypes(ComplianceStoreConfig config)
    {
        config.SchemaName = "compliance_natural_key_registration";
        config.AddEventType<RegistrationKeyed>();
        config.AddEventType<RegistrationOtherKeyed>();
        config.AddEventType<RegistrationGuidKeyed>();
    }

    protected override Action<ComplianceStoreConfig> Configuration => addEventTypes;

    public override async ValueTask InitializeAsync()
    {
        await theFixture.InitializeAsync();
    }

    private void skipUnlessNaturalKeysAreSupported(bool mapped = false)
    {
        Assert.SkipUnless(theFixture.SupportsNaturalKeys,
            "This event store does not implement natural key lookups for [NaturalKey] aggregates");
        if (mapped)
        {
            Assert.SkipUnless(theFixture.SupportsMappedNaturalKeys,
                "This event store does not resolve mapped natural keys through their source projection");
        }
    }

    private static InvalidProjectionException? findInvalidProjection(Exception? e)
    {
        while (e != null)
        {
            if (e is InvalidProjectionException invalid) return invalid;
            if (e is AggregateException { InnerExceptions.Count: 1 } aggregate) e = aggregate.InnerExceptions[0];
            else e = e.InnerException;
        }

        return null;
    }

    private async Task shouldBeRefusedAsync(Action<ComplianceStoreConfig> register, params string[] messageParts)
    {
        Exception? thrown = null;
        try
        {
            await theFixture.ConfigureAsync(config =>
            {
                addEventTypes(config);
                register(config);
            });
        }
        catch (Exception e)
        {
            thrown = e;
        }

        thrown.ShouldNotBeNull("The store was built");
        var invalid = findInvalidProjection(thrown);
        invalid.ShouldNotBeNull($"Expected an InvalidProjectionException, but got {thrown}");
        foreach (var part in messageParts)
        {
            invalid.Message.ShouldContain(part);
        }
    }

    private async Task configureAsync(Action<ComplianceStoreConfig> register)
    {
        await theFixture.ConfigureAsync(config =>
        {
            addEventTypes(config);
            register(config);
        });
        await theFixture.CleanEventDataAsync();
    }

    private static RegistrationKey aKey() => new($"REG-{Guid.NewGuid():N}");

    // ---------- refused ----------

    [Fact]
    public async Task two_owned_keys_are_refused()
    {
        skipUnlessNaturalKeysAreSupported();
        await shouldBeRefusedAsync(c => c.Snapshot<RegistrationTwoOwnedKeys>(SnapshotLifecycle.Inline),
            "both owned natural keys");
    }

    [Fact]
    public async Task two_keys_of_one_type_are_refused()
    {
        skipUnlessNaturalKeysAreSupported(mapped: true);
        await shouldBeRefusedAsync(c =>
        {
            c.Snapshot<RegistrationKeyOwner>(SnapshotLifecycle.Inline);
            c.Snapshot<RegistrationSharedKeyType>(SnapshotLifecycle.Inline);
        }, "share the key type");
    }

    [Fact]
    public async Task a_key_type_that_cannot_be_stored_is_refused()
    {
        skipUnlessNaturalKeysAreSupported();
        await shouldBeRefusedAsync(c => c.Snapshot<RegistrationGuidKeyAggregate>(SnapshotLifecycle.Inline),
            "must be an int, long or string");
    }

    [Fact]
    public async Task a_private_source_is_refused()
    {
        skipUnlessNaturalKeysAreSupported();
        await shouldBeRefusedAsync(c => c.Snapshot<RegistrationPrivateSource>(SnapshotLifecycle.Inline),
            "it is not public");
    }

    [Fact]
    public async Task a_source_without_an_event_is_refused()
    {
        skipUnlessNaturalKeysAreSupported();
        await shouldBeRefusedAsync(c => c.Snapshot<RegistrationSourceWithoutAnEvent>(SnapshotLifecycle.Inline),
            "no event parameter");
    }

    [Fact]
    public async Task a_key_no_event_sets_is_refused()
    {
        skipUnlessNaturalKeysAreSupported();
        await shouldBeRefusedAsync(c => c.Snapshot<RegistrationKeyNeverSet>(SnapshotLifecycle.Inline),
            "is never set by any event");
    }

    [Fact]
    public async Task a_source_on_an_aggregate_with_only_mapped_keys_is_refused()
    {
        skipUnlessNaturalKeysAreSupported(mapped: true);
        await shouldBeRefusedAsync(c =>
        {
            c.Snapshot<RegistrationKeyOwner>(SnapshotLifecycle.Inline);
            c.Snapshot<RegistrationMappedOnlyWithASource>(SnapshotLifecycle.Async);
        }, "only has mapped natural keys");
    }

    [Fact]
    public async Task a_mapped_key_whose_owner_is_not_registered_is_refused()
    {
        skipUnlessNaturalKeysAreSupported(mapped: true);
        await shouldBeRefusedAsync(c => c.Snapshot<RegistrationMappedToAnUnregisteredOwner>(SnapshotLifecycle.Async),
            "no aggregate projection for that type is registered");
    }

    [Fact]
    public async Task a_mapped_key_whose_owner_has_no_key_is_refused()
    {
        skipUnlessNaturalKeysAreSupported(mapped: true);
        await shouldBeRefusedAsync(c =>
        {
            c.Snapshot<RegistrationNoKeyOwner>(SnapshotLifecycle.Inline);
            c.Snapshot<RegistrationMappedToAnOwnerWithoutAKey>(SnapshotLifecycle.Async);
        }, "does not own a natural key");
    }

    [Fact]
    public async Task a_mapped_key_of_another_type_than_the_owners_is_refused()
    {
        skipUnlessNaturalKeysAreSupported(mapped: true);
        await shouldBeRefusedAsync(c =>
        {
            c.Snapshot<RegistrationKeyOwner>(SnapshotLifecycle.Inline);
            c.Snapshot<RegistrationMappedToTheWrongKeyType>(SnapshotLifecycle.Async);
        }, "but its source");
    }

    // ---------- TKey and TKey? ----------

    [Fact]
    public async Task an_owned_key_declared_nullable_is_fetched_by_its_underlying_type()
    {
        skipUnlessNaturalKeysAreSupported();
        await configureAsync(c => c.Snapshot<RegistrationNullableKeyOwner>(SnapshotLifecycle.Inline));

        var key = aKey();
        var streamId = Guid.NewGuid();
        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<RegistrationNullableKeyOwner>(streamId, new RegistrationKeyed(key));
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var owner = await EventsFor(query).FetchLatest<RegistrationNullableKeyOwner, RegistrationKey>(key, Cancellation);
        owner.ShouldNotBeNull();
        owner.Id.ShouldBe(streamId);
        owner.Key.ShouldBe(key);
    }

    [Fact]
    public async Task a_nullable_mapped_key_resolves_through_an_owner_that_declares_the_key()
    {
        skipUnlessNaturalKeysAreSupported(mapped: true);
        await configureAsync(c =>
        {
            c.Snapshot<RegistrationKeyOwner>(SnapshotLifecycle.Inline);
            c.Snapshot<RegistrationNullableMappedView>(SnapshotLifecycle.Async);
        });

        await aMappedFetchAsync<RegistrationKeyOwner, RegistrationNullableMappedView>(x => x.Id, x => x.Count);
    }

    [Fact]
    public async Task a_mapped_key_resolves_through_an_owner_that_declares_the_key_nullable()
    {
        skipUnlessNaturalKeysAreSupported(mapped: true);
        await configureAsync(c =>
        {
            c.Snapshot<RegistrationNullableKeyOwner>(SnapshotLifecycle.Inline);
            c.Snapshot<RegistrationMappedToNullableView>(SnapshotLifecycle.Async);
        });

        await aMappedFetchAsync<RegistrationNullableKeyOwner, RegistrationMappedToNullableView>(x => x.Id, x => x.Count);
    }

    private async Task aMappedFetchAsync<TOwner, TView>(Func<TView, Guid> idOf, Func<TView, int> countOf)
        where TOwner : class where TView : class
    {
        var key = aKey();
        var streamId = Guid.NewGuid();
        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<TOwner>(streamId, new RegistrationKeyed(key));
            await SaveChangesAsync(session);
        }

        await using (var session = OpenSession())
        {
            var stream = await EventsFor(session).FetchForWriting<TView, RegistrationKey>(key, Cancellation);
            stream.Id.ShouldBe(streamId);
            stream.Aggregate.ShouldNotBeNull();
            countOf(stream.Aggregate).ShouldBe(1);
        }

        await using var query = OpenSession();
        var view = await EventsFor(query).FetchLatest<TView, RegistrationKey>(key, Cancellation);
        view.ShouldNotBeNull();
        idOf(view).ShouldBe(streamId);
    }
}
