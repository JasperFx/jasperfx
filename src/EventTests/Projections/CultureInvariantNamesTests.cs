using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;

namespace EventTests.Projections;

// jasperfx#1248: projection and shard names are identifiers, so neither looking them up nor
// turning them into meter names may depend on the current culture. tr-TR lower-cases I to the
// dotless ı, and its culture-aware comparison does not treat "I" and "i" as equal.
public class CultureInvariantNamesTests
{
    private static void inTurkish(Action action)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void find_a_projection_by_name_in_any_case_in_any_culture()
    {
        var graph = new NamesGraph();
        var projection = new InvoiceProjection();
        graph.All.Add(projection);

        inTurkish(() =>
        {
            graph.TryFindProjection("invoices", out var found).ShouldBeTrue();
            found.ShouldBeSameAs(projection);

            graph.TryFindProjection("INVOICES", out found).ShouldBeTrue();
            found.ShouldBeSameAs(projection);
        });
    }

    [Fact]
    public void meter_names_do_not_depend_on_the_current_culture()
    {
        using var meter = new Meter($"jasperfx.tests.culture.{Guid.NewGuid():N}");
        using var activitySource = new ActivitySource(meter.Name);

        var store = Substitute.For<IEventStore>();
        store.Meter.Returns(meter);
        store.ActivitySource.Returns(activitySource);
        store.MetricsPrefix.Returns("fake");

        inTurkish(() =>
        {
            var metrics = new SubscriptionMetrics(store, new ShardName("Invoices", "All", 1), new Uri("database://main"));

            metrics.GapMetricName.ShouldBe("fake.invoices.all.gap");
            metrics.SkippedMetricName.ShouldBe("fake.invoices.all.skipped");
        });
    }

    private sealed class NamesGraph() : ProjectionGraph<IJasperFxProjection<FakeOperations>, FakeOperations, FakeSession>(
        Substitute.For<IEventRegistry>(), "tests")
    {
        protected override void onAddProjection(object projection)
        {
            // Nothing
        }
    }

    public class Invoice
    {
        public Guid Id { get; set; }
    }

    public class InvoiceProjection : SingleStreamProjection<Invoice, Guid>
    {
        public InvoiceProjection()
        {
            ((ProjectionBase)this).Name = "Invoices";
        }

        public override Invoice? Evolve(Invoice? snapshot, Guid id, IEvent e) => snapshot;
    }
}
