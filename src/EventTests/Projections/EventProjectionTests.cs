using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Projections;

public class EventProjectionTests
{
    [Theory]
    [InlineData(typeof(ConventionalEventProjection))]
    [InlineData(typeof(OverridesApplyAsyncEventProjection))]
    public void good_options(Type type)
    {
        Activator.CreateInstance(type).As<EventProjection>().AssembleAndAssertValidity();
    }

    [Theory]
    [InlineData(typeof(OverridesAndUsesConventions),
        "Event projections can be written by either overriding the ApplyAsync() method or by using conventional methods, but not both")]
    public void bad_options(Type type, string message)
    {
        var ex = Should.Throw<InvalidProjectionException>(() =>
        {
            Activator.CreateInstance(type).As<EventProjection>().AssembleAndAssertValidity();
        });

        ex.Message.ShouldBe(message);
    }

    [Fact]
    public async Task apply_event_exception_wrapping()
    {
        ProjectionExceptions.RegisterTransientExceptionType<SpecialEventException>();

        var projection = new ErrorCausingProjection();

        await Should.ThrowAsync<SpecialEventException>(async () =>
        {
            await projection.As<IJasperFxProjection<FakeOperations>>()
                .ApplyAsync(new FakeOperations(), [new Event<AEvent>(new AEvent())], CancellationToken.None);
        });
        
        var ex = await Should.ThrowAsync<ApplyEventException>(async () =>
        {
            await projection.As<IJasperFxProjection<FakeOperations>>()
                .ApplyAsync(new FakeOperations(), [new Event<BEvent>(new BEvent())], CancellationToken.None);
        });

        ex.InnerException.ShouldBeOfType<InvalidOperationException>();
    }
}

public partial class ErrorCausingProjection : EventProjection
{
    public void Project(FakeOperations operations, AEvent e)
    {
        throw new SpecialEventException("bang.");
    }

    public void Project(FakeOperations operations, BEvent e)
    {
        throw new InvalidOperationException("no good");
    }
}

public class SpecialEventException : Exception
{
    public SpecialEventException(string? message) : base(message)
    {
    }
}

public class EmptyEventProjection : EventProjection
{
    
}

public partial class ConventionalEventProjection : EventProjection
{
    public void Project(AEvent e, FakeOperations ops)
    {
        // nothing
    }
}

public class OverridesApplyAsyncEventProjection : EventProjection
{
    public override ValueTask ApplyAsync(FakeOperations operations, IEvent e, CancellationToken cancellation)
    {
        return base.ApplyAsync(operations, e, cancellation);
    }
}

public class OverridesAndUsesConventions : EventProjection
{
    public override ValueTask ApplyAsync(FakeOperations operations, IEvent e, CancellationToken cancellation)
    {
        return base.ApplyAsync(operations, e, cancellation);
    }
    
    public void Project(AEvent e, FakeOperations ops)
    {
        // nothing
    }
}

public class EventProjection : JasperFxEventProjectionBase<FakeOperations, FakeSession>
{
    protected override void storeEntity<T>(FakeOperations ops, T entity)
    {
        throw new NotImplementedException();
    }
}
/// <summary>
/// jasperfx#626 — JasperFxEventProjectionBase's constructor never touched Options, so an
/// EventProjection registered NO teardown targets: a rebuild deleted the progression row and then
/// re-projected into a table still holding the previous run's documents, and the ProjectionScenario
/// harness wipe (which reads Options.StorageTypes) did nothing after an event projection. Aggregation
/// projections have always registered their single TDoc; nothing in the API surface signalled the
/// difference, so every event projection author had to know it independently.
/// </summary>
public class EventProjectionTeardownTests
{
    private static Type[] cleanupTypes(ProjectionBase projection)
        => projection.Options.CleanUps.OfType<DeleteDocuments>().Select(x => x.DocumentType).ToArray();

    [Fact]
    public void published_types_become_teardown_targets()
    {
        var projection = new CreatesDocumentsProjection();

        // Nothing is registered until assembly -- the source generator emits its
        // RegisterPublishedType calls into the subclass constructor, after the base one
        projection.Options.CleanUps.ShouldBeEmpty();

        projection.AssembleAndAssertValidity();

        cleanupTypes(projection).ShouldBe([typeof(DocOne)]);
        projection.Options.StorageTypes.ShouldContain(typeof(DocOne));
    }

    [Fact]
    public void every_published_type_is_registered_not_just_the_first()
    {
        var projection = new CreatesTwoDocumentsProjection();
        projection.AssembleAndAssertValidity();

        cleanupTypes(projection).ShouldBe([typeof(DocOne), typeof(DocTwo)], ignoreOrder: true);
    }

    [Fact]
    public void a_projection_that_publishes_nothing_registers_nothing()
    {
        var projection = new ConventionalEventProjection();
        projection.AssembleAndAssertValidity();

        projection.Options.CleanUps.ShouldBeEmpty();
    }

    [Fact]
    public void the_opt_out_wins_over_the_default()
    {
        // For a projection writing into storage that must not be truncated on rebuild
        var projection = new AppendOnlyProjection();
        projection.AssembleAndAssertValidity();

        projection.Options.CleanUps.ShouldBeEmpty();
        projection.Options.StorageTypes.ShouldBeEmpty();
    }

    [Fact]
    public void a_hand_registered_type_is_not_duplicated()
    {
        var projection = new CreatesDocumentsProjection();
        projection.Options.DeleteViewTypeOnTeardown<DocOne>();

        projection.AssembleAndAssertValidity();

        cleanupTypes(projection).ShouldBe([typeof(DocOne)]);
    }

    [Fact]
    public void assembling_twice_does_not_duplicate_the_registrations()
    {
        // ProjectionGraph assembles on more than one path; a second pass must be a no-op
        var projection = new CreatesDocumentsProjection();
        projection.AssembleAndAssertValidity();
        projection.AssembleAndAssertValidity();

        cleanupTypes(projection).ShouldBe([typeof(DocOne)]);
    }

    [Fact]
    public void an_explicitly_registered_type_survives_the_opt_out()
    {
        // The documented "some but not all" recipe: opt out, then declare what you do want wiped
        var projection = new AppendOnlyProjection();
        projection.Options.DeleteViewTypeOnTeardown<DocTwo>();

        projection.AssembleAndAssertValidity();

        cleanupTypes(projection).ShouldBe([typeof(DocTwo)]);
    }
}

public class DocOne;

public class DocTwo;

public partial class CreatesDocumentsProjection : EventProjection
{
    public DocOne Create(AEvent e) => new();
}

public partial class CreatesTwoDocumentsProjection : EventProjection
{
    public DocOne Create(AEvent e) => new();

    public DocTwo Create(BEvent e) => new();
}

public partial class AppendOnlyProjection : EventProjection
{
    public AppendOnlyProjection()
    {
        DeletePublishedTypesOnTeardown = false;
    }

    public DocOne Create(AEvent e) => new();
}
