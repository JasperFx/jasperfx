using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using NSubstitute;
using Shouldly;

namespace EventTests;

/// <summary>
/// The dotnet-name to Type cache lifted from Polecat and Fisher, which cache misses, and Marten, which
/// threw on one (#1043). Caching a miss raises the question those two never answered: Fisher's comment
/// calls a stale miss impossible, but a type registered after a row naming it was read leaves one.
/// </summary>
public class EventRegistryDotNetNameTests
{
    private const string Foreign = "Some.Other.Deployment.QuestStarted, Some.Other.Deployment";

    private static string dotNetNameOf(Type type) => $"{type.FullName}, {type.Assembly.GetName().Name}";

    [Fact]
    public void an_unregistered_name_that_loads_resolves()
    {
        new EventRegistry().ResolveEventType(dotNetNameOf(typeof(QuestStarted))).ShouldBe(typeof(QuestStarted));
    }

    [Fact]
    public void a_name_nothing_can_load_is_null_rather_than_a_failure()
    {
        new EventRegistry().ResolveEventType(Foreign).ShouldBeNull();
    }

    [Fact]
    public void null_and_empty_names_are_null_and_never_probe()
    {
        var registry = new ProbingRegistry();

        registry.ResolveEventType(null).ShouldBeNull();
        registry.ResolveEventType("").ShouldBeNull();

        registry.Probes.ShouldBeEmpty();
    }

    [Fact]
    public void a_hit_is_probed_once()
    {
        var registry = new ProbingRegistry { Probe = _ => typeof(QuestStarted) };

        for (var i = 0; i < 5; i++)
        {
            registry.ResolveEventType("Quests.Started, Quests").ShouldBe(typeof(QuestStarted));
        }

        registry.Probes.Count.ShouldBe(1);
    }

    [Fact]
    public void a_miss_is_probed_once()
    {
        // A stream full of foreign events asks this once per row
        var registry = new ProbingRegistry();

        for (var i = 0; i < 5; i++)
        {
            registry.ResolveEventType(Foreign).ShouldBeNull();
        }

        registry.Probes.Count.ShouldBe(1);
    }

    [Fact]
    public void a_registered_type_never_reaches_the_probe()
    {
        var registry = new ProbingRegistry();
        registry.AddEventType(typeof(QuestStarted));

        var type = typeof(QuestStarted);

        registry.ResolveEventType(registry.EventMappingFor(type).DotNetTypeName).ShouldBe(type);
        registry.ResolveEventType(type.AssemblyQualifiedName).ShouldBe(type);
        registry.ResolveEventType(type.FullName).ShouldBe(type);

        registry.Probes.ShouldBeEmpty();
    }

    [Fact]
    public void a_later_registration_replaces_the_miss_for_its_own_name()
    {
        // The probe can never find this type, which is what a type in a non-default load context looks
        // like. Polecat's and Fisher's caches answer null here forever.
        var registry = new ProbingRegistry();
        var name = dotNetNameOf(typeof(QuestStarted));

        registry.ResolveEventType(name).ShouldBeNull();

        registry.AddEventType(typeof(QuestStarted));

        registry.ResolveEventType(name).ShouldBe(typeof(QuestStarted));
        registry.Probes.Count.ShouldBe(1);
    }

    [Fact]
    public void a_later_registration_expires_a_miss_for_another_name_in_the_same_assembly()
    {
        // Registering QuestStarted is what got the assembly loaded, so its neighbour can now be found by
        // name even though nothing ever registered it
        var assemblyLoaded = false;
        var neighbour = dotNetNameOf(typeof(QuestEnded));

        var registry = new ProbingRegistry { Probe = _ => assemblyLoaded ? typeof(QuestEnded) : null };

        registry.ResolveEventType(neighbour).ShouldBeNull();

        assemblyLoaded = true;
        registry.AddEventType(typeof(QuestStarted));

        registry.ResolveEventType(neighbour).ShouldBe(typeof(QuestEnded));
    }

    [Fact]
    public void an_expired_miss_is_probed_once_more_and_cached_again()
    {
        var registry = new ProbingRegistry();

        registry.ResolveEventType(Foreign).ShouldBeNull();

        registry.AddEventType(typeof(QuestStarted));

        for (var i = 0; i < 5; i++)
        {
            registry.ResolveEventType(Foreign).ShouldBeNull();
        }

        registry.Probes.Count.ShouldBe(2);
    }

    [Fact]
    public void registering_a_known_type_again_does_not_expire_anything()
    {
        var registry = new ProbingRegistry();
        registry.AddEventType(typeof(QuestStarted));

        registry.ResolveEventType(Foreign).ShouldBeNull();

        registry.AddEventType(typeof(QuestStarted));
        registry.EventMappingFor(typeof(QuestStarted));

        registry.ResolveEventType(Foreign).ShouldBeNull();
        registry.Probes.Count.ShouldBe(1);
    }

    [Fact]
    public void a_registration_during_the_probe_is_not_lost_to_the_miss_it_is_racing()
    {
        // The window a stamp taken after the probe would have: this reader is about to record a miss for
        // the world as it was before the registration, and the next read must not believe it
        var registered = false;
        var neighbour = dotNetNameOf(typeof(QuestEnded));

        ProbingRegistry registry = null!;
        registry = new ProbingRegistry
        {
            Probe = _ =>
            {
                if (registered)
                {
                    return typeof(QuestEnded);
                }

                registered = true;
                registry.AddEventType(typeof(QuestStarted));
                return null;
            }
        };

        registry.ResolveEventType(neighbour).ShouldBeNull();

        registry.ResolveEventType(neighbour).ShouldBe(typeof(QuestEnded));
    }

    [Fact]
    public void a_store_with_its_own_mappings_invalidates_through_the_same_call()
    {
        // All three stores override EventMappingFor and never reach the base class's, so this call is the
        // only way the cache can hear about one of their registrations
        var registry = new OwnMappingsRegistry();
        var name = dotNetNameOf(typeof(QuestStarted));

        registry.ResolveEventType(name).ShouldBeNull();

        registry.AddEventType(typeof(QuestStarted));

        registry.ResolveEventType(name).ShouldBe(typeof(QuestStarted));
    }

    [Fact]
    public void a_registered_type_wins_over_what_the_name_loads_as()
    {
        var registry = new ProbingRegistry { Probe = _ => typeof(QuestEnded) };
        var name = dotNetNameOf(typeof(QuestStarted));

        registry.ResolveEventType(name).ShouldBe(typeof(QuestEnded));

        registry.AddEventType(typeof(QuestStarted));

        registry.ResolveEventType(name).ShouldBe(typeof(QuestStarted));
    }

    [Fact]
    public void a_store_can_recognize_a_name_that_no_longer_loads()
    {
        var registry = new MovedTypeRegistry();

        registry.ResolveEventType("Marten.Events.Archived, Marten").ShouldBe(typeof(Archived));
    }

    [Fact]
    public void a_name_that_merely_contains_a_moved_type_is_not_that_type()
    {
        // Marten matched ".Archived" and ".Tombstone" as substrings, which reads a row for a user's
        // Acme.ArchivedOrderPlaced as the Archived marker. The default here has no such rule, and the
        // seam's example compares whole names.
        new MovedTypeRegistry().ResolveEventType("Acme.ArchivedOrderPlaced, Acme").ShouldBeNull();
        new EventRegistry().ResolveEventType("Acme.Events.TombstoneRaised, Acme").ShouldBeNull();
    }

    [Fact]
    public void type_for_dot_net_name_returns_what_resolves()
    {
        new EventRegistry().TypeForDotNetName(dotNetNameOf(typeof(QuestStarted))).ShouldBe(typeof(QuestStarted));
    }

    [Fact]
    public void type_for_dot_net_name_refuses_what_does_not()
    {
        Should.Throw<UnknownEventTypeException>(() => new EventRegistry().TypeForDotNetName(Foreign))
            .EventTypeName.ShouldBe(Foreign);
    }

    [Fact]
    public void type_for_dot_net_name_does_not_probe_for_every_failed_row()
    {
        var registry = new ProbingRegistry();

        for (var i = 0; i < 5; i++)
        {
            Should.Throw<UnknownEventTypeException>(() => registry.TypeForDotNetName(Foreign));
        }

        registry.Probes.Count.ShouldBe(1);
    }

    public record QuestStarted;

    public record QuestEnded;

    private class ProbingRegistry : EventRegistry
    {
        public List<string> Probes { get; } = [];

        public Func<string, Type?> Probe { get; init; } = _ => null;

        protected override Type? LoadEventType(string dotNetTypeName)
        {
            Probes.Add(dotNetTypeName);
            return Probe(dotNetTypeName);
        }
    }

    private class OwnMappingsRegistry : EventRegistry
    {
        private readonly Dictionary<Type, IEventType> _mappings = new();

        public override IEventType EventMappingFor(Type eventType)
        {
            if (!_mappings.TryGetValue(eventType, out var mapping))
            {
                mapping = typeof(EventTypeData<>).CloseAndBuildAs<IEventType>(eventType);
                _mappings[eventType] = mapping;
                RegisterDotNetTypeNames(mapping);
            }

            return mapping;
        }

        protected override Type? LoadEventType(string dotNetTypeName) => null;
    }

    private class MovedTypeRegistry : EventRegistry
    {
        protected override Type? LoadEventType(string dotNetTypeName)
            => dotNetTypeName == "Marten.Events.Archived, Marten"
                ? typeof(Archived)
                : base.LoadEventType(dotNetTypeName);
    }
}

/// <summary>
/// The alias caches lifted from all three stores (#1043). <see cref="EventRegistry.AliasFor" /> exists so the
/// caches could move while the names did not: a store's alias is persisted, and the stores' differ on purpose.
/// </summary>
public class EventRegistryAggregateAliasTests
{
    [Fact]
    public void a_registry_that_chose_no_naming_function_invents_no_alias()
    {
        var ex = Should.Throw<NotSupportedException>(() => new EventRegistry().AggregateAliasFor(typeof(QuestParty)));

        ex.Message.ShouldContain("aggregate alias");
    }

    [Fact]
    public void each_store_keeps_its_own_persisted_name()
    {
        new TableAliasRegistry().AggregateAliasFor(typeof(QuestParty)).ShouldBe("quest_party");
        new SimpleNameRegistry().AggregateAliasFor(typeof(QuestParty)).ShouldBe("QuestParty");
        new FullNameRegistry().AggregateAliasFor(typeof(QuestParty)).ShouldBe(typeof(QuestParty).FullName);
    }

    [Fact]
    public void the_naming_function_runs_once_per_type()
    {
        var registry = new SimpleNameRegistry();

        registry.AggregateAliasFor(typeof(QuestParty));
        registry.AggregateAliasFor(typeof(QuestParty));
        registry.AggregateAliasFor(typeof(Guild));
        registry.AggregateAliasFor(typeof(QuestParty));

        registry.NamingCalls.ShouldBe(2);
    }

    [Fact]
    public void a_failed_naming_call_is_not_cached()
    {
        var registry = new SimpleNameRegistry { Failure = new InvalidOperationException("not yet") };

        Should.Throw<InvalidOperationException>(() => registry.AggregateAliasFor(typeof(QuestParty)));

        registry.Failure = null;

        registry.AggregateAliasFor(typeof(QuestParty)).ShouldBe("QuestParty");
    }

    [Fact]
    public void an_alias_resolves_back_to_its_aggregate_type()
    {
        var registry = new TableAliasRegistry();
        var alias = registry.AggregateAliasFor(typeof(QuestParty));

        registry.AggregateTypeFor(alias).ShouldBe(typeof(QuestParty));
        registry.TryResolveAggregateType(alias).ShouldBe(typeof(QuestParty));
    }

    [Fact]
    public void an_unknown_alias_throws_from_the_strict_lookup_and_is_null_from_the_tolerant_one()
    {
        var registry = new SimpleNameRegistry();

        Should.Throw<ArgumentOutOfRangeException>(() => registry.AggregateTypeFor("Mystery"))
            .Message.ShouldContain("Mystery");

        registry.TryResolveAggregateType("Mystery").ShouldBeNull();
    }

    [Fact]
    public void a_null_or_empty_alias_is_null_and_never_searches()
    {
        var registry = new DiscoveringRegistry();

        registry.TryResolveAggregateType(null).ShouldBeNull();
        registry.TryResolveAggregateType("").ShouldBeNull();

        registry.Searches.ShouldBeEmpty();
    }

    [Fact]
    public void two_types_sharing_an_alias_keep_the_first_and_both_still_get_the_alias()
    {
        // Polecat and Fisher persist Type.Name, so two namespaces can collide. That ambiguity is in the
        // stored data, and the most this cache can do is stay stable about it.
        var registry = new SimpleNameRegistry();

        registry.AggregateAliasFor(typeof(QuestParty)).ShouldBe("QuestParty");
        registry.AggregateAliasFor(typeof(Elsewhere.QuestParty)).ShouldBe("QuestParty");

        registry.AggregateTypeFor("QuestParty").ShouldBe(typeof(QuestParty));
    }

    [Fact]
    public void an_alias_nothing_has_written_is_found_through_the_seam_and_then_cached()
    {
        // A stream written by another process, which Marten and Polecat find through the projections
        var registry = new DiscoveringRegistry { Known = { ["quest_party"] = typeof(QuestParty) } };

        registry.TryResolveAggregateType("quest_party").ShouldBe(typeof(QuestParty));
        registry.AggregateTypeFor("quest_party").ShouldBe(typeof(QuestParty));

        registry.Searches.Count.ShouldBe(1);
    }

    [Fact]
    public void a_miss_does_not_stop_a_later_alias_from_resolving()
    {
        // Marten's cache stores the null from a failed search, and its Fill will not replace an existing
        // entry, so an alias read once before its type was seen stayed null for the life of the store
        var registry = new SimpleNameRegistry();

        registry.TryResolveAggregateType("QuestParty").ShouldBeNull();

        registry.AggregateAliasFor(typeof(QuestParty));

        registry.TryResolveAggregateType("QuestParty").ShouldBe(typeof(QuestParty));
    }

    [Fact]
    public void the_written_alias_beats_a_search()
    {
        var registry = new DiscoveringRegistry { Known = { ["quest_party"] = typeof(Guild) } };

        registry.AggregateAliasFor(typeof(QuestParty));

        registry.TryResolveAggregateType("quest_party").ShouldBe(typeof(QuestParty));
        registry.Searches.ShouldBeEmpty();
    }

    [Fact]
    public void a_store_that_still_overrides_the_public_member_is_not_routed_through_the_seam()
    {
        // What an unadopted Marten, Polecat or Fisher looks like after this release
        new OverridingRegistry().AggregateAliasFor(typeof(QuestParty)).ShouldBe("overridden");
    }

    [Fact]
    public void stream_preparation_stamps_the_store_alias()
    {
        var registry = new TableAliasRegistry();
        var stream = StreamAction.Start(registry, Guid.NewGuid(), new QuestStarted());
        stream.AggregateType = typeof(QuestParty);

        stream.PrepareEvents(0, registry, new Queue<long>([1]), Substitute.For<IMetadataContext>());

        stream.AggregateTypeName.ShouldBe("quest_party");
        registry.AggregateTypeFor("quest_party").ShouldBe(typeof(QuestParty));
    }

    public record QuestStarted;

    public class QuestParty;

    public static class Elsewhere
    {
        public class QuestParty;
    }

    public class Guild;

    private class TableAliasRegistry : EventRegistry
    {
        // Marten's
        protected override string AliasFor(Type aggregateType)
            => aggregateType.IsGenericType ? aggregateType.ShortNameInCode() : aggregateType.Name.ToTableAlias();
    }

    private class SimpleNameRegistry : EventRegistry
    {
        public int NamingCalls { get; private set; }

        public Exception? Failure { get; set; }

        // Polecat's and Fisher's
        protected override string AliasFor(Type aggregateType)
        {
            NamingCalls++;
            return Failure == null ? aggregateType.Name : throw Failure;
        }
    }

    private class FullNameRegistry : EventRegistry
    {
        // The in-memory store's
        protected override string AliasFor(Type aggregateType) => aggregateType.FullName!;
    }

    private class DiscoveringRegistry : EventRegistry
    {
        public Dictionary<string, Type> Known { get; } = new();

        public List<string> Searches { get; } = [];

        protected override string AliasFor(Type aggregateType) => aggregateType.Name.ToTableAlias();

        protected override Type? FindAggregateType(string aggregateTypeName)
        {
            Searches.Add(aggregateTypeName);
            return Known.GetValueOrDefault(aggregateTypeName);
        }
    }

    private class OverridingRegistry : EventRegistry
    {
        public override string AggregateAliasFor(Type aggregateType) => "overridden";

        protected override string AliasFor(Type aggregateType) => throw new InvalidOperationException();
    }
}
