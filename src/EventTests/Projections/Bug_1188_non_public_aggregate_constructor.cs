using JasperFx.Events;
using JasperFx.Events.Aggregation;
using Shouldly;

namespace EventTests.Projections;

// #1188: an aggregate with only a non-public parameterless constructor was built through
// RuntimeHelpers.GetUninitializedObject, so neither the constructor body nor any initializer ran and
// the first Apply dereferenced null. The evolver now calls the constructor through an [UnsafeAccessor].
public record Bug1188NameAdded(string Name);

public class Bug1188PrivateCtorDoc
{
    private Bug1188PrivateCtorDoc()
    {
        Source = "private-constructor";
    }

    public Guid Id { get; set; }
    public string? Source { get; private set; }
    public int Priority { get; private set; } = 7;

    private readonly List<string> _names = new();
    public IReadOnlyList<string> Names => _names;

    public void Apply(Bug1188NameAdded e) => _names.Add(e.Name);
}

public partial class Bug1188PrivateCtorProjection : SingleStreamProjection<Bug1188PrivateCtorDoc, Guid>;

public class Bug1188ProtectedCtorDoc
{
    protected Bug1188ProtectedCtorDoc()
    {
    }

    public Guid Id { get; set; }
    public List<string> Names { get; } = [];
}

public partial class Bug1188ProtectedCtorProjection : SingleStreamProjection<Bug1188ProtectedCtorDoc, Guid>
{
    public void Apply(Bug1188NameAdded e, Bug1188ProtectedCtorDoc doc) => doc.Names.Add(e.Name);
}

public class Bug1188InternalCtorDoc
{
    internal Bug1188InternalCtorDoc()
    {
    }

    public Guid Id { get; set; }
    public List<string> Names { get; } = [];

    public Bug1188InternalCtorDoc Apply(Bug1188NameAdded e)
    {
        Names.Add(e.Name);
        return this;
    }
}

public partial class Bug1188InternalCtorProjection : SingleStreamProjection<Bug1188InternalCtorDoc, Guid>;

public class Bug_1188_non_public_aggregate_constructor
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
    public async Task a_private_constructor_body_and_every_initializer_run()
    {
        var snapshot = await determine(new Bug1188PrivateCtorProjection(), new Bug1188NameAdded("a"), new Bug1188NameAdded("b"));

        snapshot.ShouldNotBeNull();
        snapshot.Source.ShouldBe("private-constructor");
        snapshot.Priority.ShouldBe(7);
        snapshot.Names.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task a_protected_constructor_runs_its_initializers()
    {
        var snapshot = await determine(new Bug1188ProtectedCtorProjection(), new Bug1188NameAdded("a"));

        snapshot.ShouldNotBeNull();
        snapshot.Names.ShouldBe(["a"]);
    }

    [Fact]
    public async Task an_internal_constructor_runs_its_initializers()
    {
        var snapshot = await determine(new Bug1188InternalCtorProjection(), new Bug1188NameAdded("a"));

        snapshot.ShouldNotBeNull();
        snapshot.Names.ShouldBe(["a"]);
    }
}
