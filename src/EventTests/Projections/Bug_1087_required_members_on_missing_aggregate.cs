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

// Required members whose values an object initializer could not reproduce in the generated file:
// one needs a using, one reads a constant of the aggregate, one is set in the constructor body
public class Bug1087InitializedDoc
{
    public const int NoPriority = -1;

    public Bug1087InitializedDoc()
    {
        Tags = ["from-constructor"];
    }

    public required Guid Id { get; set; }
    public required Guid Token { get; set; } = Guid.NewGuid();
    public required int Priority { get; set; } = NoPriority;
    public required List<string> Tags { get; set; }
    public required List<string> Emails { get; set; } = [];
}

public partial class Bug1087InitializedDocProjection : SingleStreamProjection<Bug1087InitializedDoc, Guid>
{
    public Bug1087InitializedDoc Create(Bug1087Created e) =>
        new() { Id = e.Id, Token = Guid.Empty, Priority = 0, Tags = [], Emails = [] };

    public void Apply(Bug1087Touched e, Bug1087InitializedDoc doc) => doc.Emails.Add(e.Email);
}

public class Bug_1087_required_members_on_missing_aggregate
{
    private static async Task<TDoc?> determine<TDoc>(SingleStreamProjection<TDoc, Guid> projection, params object[] events)
        where TDoc : class
    {
        projection.AssembleAndAssertValidity();

        var (snapshot, _) = await projection.DetermineActionAsync(
            new FakeSession(),
            null,
            Guid.NewGuid(),
            new NulloIdentitySetter<TDoc, Guid>(),
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

    [Fact]
    public async Task initializers_that_need_a_using_or_a_member_of_the_aggregate_and_the_constructor_body_all_run()
    {
        var snapshot = await determine(new Bug1087InitializedDocProjection(), new Bug1087Touched(Guid.NewGuid(), "one@example.com"));

        snapshot.ShouldNotBeNull();
        snapshot.Token.ShouldNotBe(Guid.Empty);
        snapshot.Priority.ShouldBe(Bug1087InitializedDoc.NoPriority);
        snapshot.Tags.ShouldBe(["from-constructor"]);
        snapshot.Emails.ShouldBe(["one@example.com"]);
    }
}
