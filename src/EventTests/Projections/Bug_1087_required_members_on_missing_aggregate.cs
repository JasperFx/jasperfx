using JasperFx.Events;
using JasperFx.Events.Aggregation;
using Shouldly;

namespace EventTests.Projections;

// #1087: an Apply-only event for an aggregate that does not exist yet built it with
// `new T { Required = default!, ... }`, which overwrote member initializers with null.
public record Bug1087Created(Guid Id);
public record Bug1087Touched(Guid Id, string Email);

public class Bug1087Doc
{
    public required Guid Id { get; set; }
    public required List<string> Emails { get; set; } = [];
}

public partial class Bug1087DocProjection : SingleStreamProjection<Bug1087Doc, Guid>
{
    public Bug1087Doc Create(Bug1087Created e) => new() { Id = e.Id, Emails = [] };

    public Bug1087Doc Apply(Bug1087Touched e, Bug1087Doc doc)
    {
        doc.Emails.Add(e.Email);
        return doc;
    }
}

// A catch-all Create used to make the Apply-fallback arms unreachable (CS8120): this file would not compile
public partial class Bug1087IgnoringProjection : SingleStreamProjection<Bug1087Doc, Guid>
{
    public Bug1087Doc Create(Bug1087Created e) => new() { Id = e.Id, Emails = [] };

    public Bug1087Doc? Create(object _) => null;

    public Bug1087Doc Apply(Bug1087Touched e, Bug1087Doc doc)
    {
        doc.Emails.Add(e.Email);
        return doc;
    }
}

public class Bug_1087_required_members_on_missing_aggregate
{
    private static async Task<Bug1087Doc?> determine(SingleStreamProjection<Bug1087Doc, Guid> projection, params object[] events)
    {
        projection.AssembleAndAssertValidity();

        var (snapshot, _) = await projection.DetermineActionAsync(
            new FakeSession(),
            null,
            Guid.NewGuid(),
            new NulloIdentitySetter<Bug1087Doc, Guid>(),
            events.Select((data, i) =>
            {
                var e = (IEvent)Activator.CreateInstance(typeof(Event<>).MakeGenericType(data.GetType()), data)!;
                e.Sequence = i + 1;
                return e;
            }).ToList(),
            CancellationToken.None);

        return snapshot;
    }

    [Fact]
    public async Task member_initializers_survive_building_a_missing_aggregate_for_an_apply_only_event()
    {
        var snapshot = await determine(new Bug1087DocProjection(), new Bug1087Touched(Guid.NewGuid(), "one@example.com"));

        snapshot.ShouldNotBeNull();
        snapshot.Emails.ShouldBe(["one@example.com"]);
    }

    [Fact]
    public async Task a_catch_all_create_wins_for_an_event_on_a_missing_aggregate()
    {
        var snapshot = await determine(new Bug1087IgnoringProjection(), new Bug1087Touched(Guid.NewGuid(), "one@example.com"));

        snapshot.ShouldBeNull();
    }

    [Fact]
    public async Task apply_still_runs_against_an_existing_aggregate_with_a_catch_all_create()
    {
        var id = Guid.NewGuid();
        var snapshot = await determine(new Bug1087IgnoringProjection(), new Bug1087Created(id),
            new Bug1087Touched(id, "one@example.com"));

        snapshot.ShouldNotBeNull();
        snapshot.Emails.ShouldBe(["one@example.com"]);
    }
}
