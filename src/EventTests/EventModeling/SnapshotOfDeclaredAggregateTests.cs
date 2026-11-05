using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling;

/// <summary>
/// jasperfx#1117 — an inline snapshot of a declared aggregate is placed in the declaring chapter
/// instead of landing in "(no domain) / (no chapter)".
/// </summary>
public class SnapshotOfDeclaredAggregateTests
{
    public class Lead;
    public class LeadCaptured;
    public class CaptureLead;
    public class Report;

    private static EventModelDescriptor declared()
    {
        var model = new EventModelBuilder();
        model.InDomain("Leads").InChapter("Leads");
        model.ForAggregate<Lead>();
        model.Command<CaptureLead>().Emits<LeadCaptured>();
        return model.Build("M").WithProvenance(EventModelProvenance.Declared);
    }

    private static EventModelDescriptor derived(params Type[] readModels)
        => new EventModelDescriptor("M", readModels.Select(x => EventModelSliceDescriptor.Named(x.Name) with
        {
            Pattern = SlicePattern.View,
            ReadModelTypes = [TypeDescriptor.For(x)],
        }).ToList()).WithProvenance(EventModelProvenance.Derived);

    [Fact]
    public void snapshot_of_a_declared_aggregate_joins_the_declaring_chapter()
    {
        var merged = EventModelDescriptor.Merge("M", [declared(), derived(typeof(Lead))]);

        var view = merged.Slices.Single(x => x.Name == nameof(Lead));
        view.Domain.ShouldBe("Leads");
        view.Chapter.ShouldBe("Leads");
    }

    [Fact]
    public void a_view_that_is_not_a_declared_aggregate_stays_unplaced()
    {
        var merged = EventModelDescriptor.Merge("M", [declared(), derived(typeof(Report))]);

        var view = merged.Slices.Single(x => x.Name == nameof(Report));
        view.Domain.ShouldBeNull();
        view.Chapter.ShouldBeNull();
    }
}
