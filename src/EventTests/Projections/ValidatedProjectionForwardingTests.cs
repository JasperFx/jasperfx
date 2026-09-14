using JasperFx.Events;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;

namespace EventTests.Projections;

// jasperfx#845: a bare IJasperFxProjection is registered through a ProjectionWrapper, and the
// wrapper is not its inner projection's type — so AssertValidity's OfType<IValidatedProjection<T>>
// never saw the projection the user wrote and its configuration checks silently never ran. It
// failed OPEN, which is the worst way for a validation to fail.
public class ValidatedProjectionForwardingTests
{
    public sealed record TestOptions(string Name);

    [Fact]
    public void a_wrapped_projection_is_asked_to_validate_itself()
    {
        var graph = new FakeGraph();
        var projection = new ValidatingProjection(["nope"]);

        graph.Add(projection, ProjectionLifecycle.Inline);

        var ex = Should.Throw<InvalidProjectionException>(() => graph.AssertValidity(new TestOptions("store")));

        ex.Message.ShouldContain("nope");
        projection.Asked.ShouldBe(1);
    }

    [Fact]
    public void the_projection_receives_the_options_it_was_validated_against()
    {
        var graph = new FakeGraph();
        var projection = new ValidatingProjection([]);

        graph.Add(projection, ProjectionLifecycle.Inline);
        graph.AssertValidity(new TestOptions("the-store"));

        projection.SeenOptions!.Name.ShouldBe("the-store");
    }

    [Fact]
    public void a_wrapped_projection_with_nothing_to_report_does_not_block_registration()
    {
        var graph = new FakeGraph();
        graph.Add(new ValidatingProjection([]), ProjectionLifecycle.Inline);

        Should.NotThrow(() => graph.AssertValidity(new TestOptions("store")));
    }

    [Fact]
    public void validation_for_another_options_type_is_left_alone()
    {
        // A projection validating against Marten's StoreOptions must not be asked by Polecat's graph.
        var graph = new FakeGraph();
        var projection = new ValidatingProjection(["nope"]);

        graph.Add(projection, ProjectionLifecycle.Inline);

        Should.NotThrow(() => graph.AssertValidity("a string, not TestOptions"));
        projection.Asked.ShouldBe(0);
    }

    [Fact]
    public void a_projection_is_asked_exactly_once()
    {
        var graph = new FakeGraph();
        var projection = new ValidatingProjection([]);

        graph.Add(projection, ProjectionLifecycle.Inline);
        graph.AssertValidity(new TestOptions("store"));

        projection.Asked.ShouldBe(1);
    }

    public sealed class ValidatingProjection(string[] messages)
        : IJasperFxProjection<FakeOperations>, IValidatedProjection<TestOptions>
    {
        public int Asked { get; private set; }
        public TestOptions? SeenOptions { get; private set; }

        public Task ApplyAsync(FakeOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation)
            => Task.CompletedTask;

        public IEnumerable<string> ValidateConfiguration(TestOptions options)
        {
            Asked++;
            SeenOptions = options;
            return messages;
        }
    }

    private sealed class FakeGraph : ProjectionGraph<IJasperFxProjection<FakeOperations>, FakeOperations, FakeSession>
    {
        public FakeGraph() : base(Substitute.For<IEventRegistry>(), "tests")
        {
        }

        protected override void onAddProjection(object projection)
        {
        }
    }
}
