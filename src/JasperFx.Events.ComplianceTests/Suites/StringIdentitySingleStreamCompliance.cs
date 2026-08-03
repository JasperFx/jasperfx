using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region String identity events and aggregates

public record StringQuestStarted(string Name);

public record StringMembersJoined(int Day, string Location, string[] Members);

public record StringMembersDeparted(string[] Members);

public record StringArrivedAtLocation(string Location, int Day);

public record StringMonsterSlain(string Name);

public record StringQuestEnded(string Name);

/// <summary>
/// Aggregate keyed by the stream's string key rather than a Guid.
/// </summary>
public partial class StringQuestParty
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> Members { get; set; } = new();
    public string? Location { get; set; }
    public List<string> MonstersSlain { get; set; } = new();
}

/// <summary>
/// The same aggregate shape, but self-aggregating, so it can be registered with plain
/// <c>Snapshot&lt;T&gt;()</c> and the store has to derive <c>string</c> as the identity type from the
/// document itself.
/// </summary>
public partial class SelfAggregatingStringQuest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> Members { get; set; } = new();

    public static SelfAggregatingStringQuest Create(StringQuestStarted e) => new() { Name = e.Name };

    public void Apply(StringMembersJoined e) => Members.AddRange(e.Members);
}

/// <summary>
/// A custom single stream projection closed over <c>string</c>. The base type comes from the
/// consumer-supplied <c>ComplianceStringPartyProjectionBase</c> global alias, because each product
/// declares its own <c>SingleStreamProjection&lt;TDoc, TId&gt;</c> subclass of
/// <c>JasperFxSingleStreamProjectionBase</c> and this file cannot reach the suite's generics.
/// </summary>
public partial class StringQuestPartyProjection: ComplianceStringPartyProjectionBase
{
    public static StringQuestParty Create(IEvent<StringQuestStarted> @event) =>
        new() { Id = @event.StreamKey!, Name = @event.Data.Name };

    public void Apply(StringMembersJoined e, StringQuestParty party)
    {
        party.Members.AddRange(e.Members);
        party.Location = e.Location;
    }

    public void Apply(StringMembersDeparted e, StringQuestParty party)
    {
        foreach (var member in e.Members)
        {
            party.Members.Remove(member);
        }
    }

    public void Apply(StringArrivedAtLocation e, StringQuestParty party) => party.Location = e.Location;

    public void Apply(StringMonsterSlain e, StringQuestParty party) => party.MonstersSlain.Add(e.Name);

    public bool ShouldDelete(StringQuestEnded e) => true;
}

#endregion

/// <summary>
/// Single stream aggregation against string-keyed streams — <c>StreamIdentity.AsString</c> — through
/// both routes a store offers: an explicit <c>SingleStreamProjection&lt;TDoc, string&gt;</c> subclass
/// and a self-aggregating type registered with <c>Snapshot&lt;T&gt;()</c>.
/// </summary>
/// <remarks>
/// The assertions all load the persisted document rather than re-folding the stream, so a projection
/// that never wrote anything fails. Deletion is covered too: <c>ShouldDelete</c> has to remove the
/// aggregate row, not merely stop updating it.
/// </remarks>
public abstract class StringIdentitySingleStreamCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_string_id";
        config.StreamIdentity = StreamIdentity.AsString;

        config.AddProjection(new StringQuestPartyProjection(), ProjectionLifecycle.Inline);
        config.Snapshot<SelfAggregatingStringQuest>(SnapshotLifecycle.Inline);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private static string streamKey() => "quest-" + Guid.NewGuid();

    [Fact]
    public async Task custom_projection_creates_aggregate_on_start_stream()
    {
        var key = streamKey();

        await using var session = OpenSession();
        EventsFor(session).StartStream(key, new StringQuestStarted("Destroy the Ring"));
        await SaveChangesAsync(session);

        var party = await LoadDocumentAsync<StringQuestParty>(session, key);

        party.ShouldNotBeNull();
        party.Id.ShouldBe(key);
        party.Name.ShouldBe("Destroy the Ring");
    }

    [Fact]
    public async Task custom_projection_applies_multiple_events()
    {
        var key = streamKey();

        await using var session = OpenSession();
        EventsFor(session).StartStream(key,
            new StringQuestStarted("Fellowship"),
            new StringMembersJoined(1, "Rivendell", ["Aragorn", "Legolas", "Gimli"]));
        await SaveChangesAsync(session);

        var party = await LoadDocumentAsync<StringQuestParty>(session, key);

        party.ShouldNotBeNull();
        party.Name.ShouldBe("Fellowship");
        party.Members.ShouldBe(new List<string> { "Aragorn", "Legolas", "Gimli" });
        party.Location.ShouldBe("Rivendell");
    }

    [Fact]
    public async Task custom_projection_updates_on_append()
    {
        var key = streamKey();

        await using var session = OpenSession();
        EventsFor(session).StartStream(key,
            new StringQuestStarted("Adventure"),
            new StringMembersJoined(1, "Start", ["Hero"]));
        await SaveChangesAsync(session);

        EventsFor(session).Append(key,
            new StringArrivedAtLocation("Dungeon", 2),
            new StringMonsterSlain("Goblin"));
        await SaveChangesAsync(session);

        var party = await LoadDocumentAsync<StringQuestParty>(session, key);

        party.ShouldNotBeNull();
        party.Location.ShouldBe("Dungeon");
        party.MonstersSlain.ShouldContain("Goblin");
    }

    [Fact]
    public async Task custom_projection_should_delete_removes_aggregate()
    {
        var key = streamKey();

        await using var session = OpenSession();
        EventsFor(session).StartStream(key,
            new StringQuestStarted("Doomed Quest"),
            new StringMembersJoined(1, "Start", ["Hero"]));
        await SaveChangesAsync(session);

        EventsFor(session).Append(key, new StringQuestEnded("Doomed Quest"));
        await SaveChangesAsync(session);

        var party = await LoadDocumentAsync<StringQuestParty>(session, key);
        party.ShouldBeNull();
    }

    [Fact]
    public async Task multiple_string_keyed_streams_projected_independently()
    {
        var first = streamKey();
        var second = streamKey();

        await using var session = OpenSession();
        EventsFor(session).StartStream(first,
            new StringQuestStarted("Quest 1"),
            new StringMembersJoined(1, "Town A", ["Alpha"]));
        EventsFor(session).StartStream(second,
            new StringQuestStarted("Quest 2"),
            new StringMembersJoined(1, "Town B", ["Beta", "Gamma"]));
        await SaveChangesAsync(session);

        var partyOne = await LoadDocumentAsync<StringQuestParty>(session, first);
        var partyTwo = await LoadDocumentAsync<StringQuestParty>(session, second);

        partyOne.ShouldNotBeNull();
        partyOne.Name.ShouldBe("Quest 1");
        partyOne.Members.ShouldBe(new List<string> { "Alpha" });

        partyTwo.ShouldNotBeNull();
        partyTwo.Name.ShouldBe("Quest 2");
        partyTwo.Members.ShouldBe(new List<string> { "Beta", "Gamma" });
    }

    [Fact]
    public async Task self_aggregating_snapshot_with_string_identity()
    {
        var key = "snap-" + Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(key,
            new StringQuestStarted("Snapshot Quest"),
            new StringMembersJoined(1, "Castle", ["Knight"]));
        await SaveChangesAsync(session);

        var quest = await LoadDocumentAsync<SelfAggregatingStringQuest>(session, key);

        quest.ShouldNotBeNull();
        quest.Id.ShouldBe(key);
        quest.Name.ShouldBe("Snapshot Quest");
        quest.Members.ShouldBe(new List<string> { "Knight" });
    }
}
