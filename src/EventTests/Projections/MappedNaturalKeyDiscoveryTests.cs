using System.Linq;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Projections;

public class MappedNaturalKeyDiscoveryTests
{
    [Fact]
    public void an_owned_key_is_not_mapped()
    {
        var projection = new MnkOrderProjection();

        projection.NaturalKeyDefinition.ShouldNotBeNull();
        projection.NaturalKeyDefinition!.IsMapped.ShouldBeFalse();
        projection.NaturalKeyDefinition.SourceProjectionType.ShouldBeNull();
        projection.NaturalKeyDefinition.LookupAggregateType.ShouldBe(typeof(MnkOrder));
        projection.MappedNaturalKeyDefinitions.ShouldBeEmpty();
    }

    [Fact]
    public void discovers_a_mapped_key_from_the_generic_attribute()
    {
        var projection = new MnkOrderSummaryProjection();

        projection.NaturalKeyDefinition.ShouldBeNull();

        var mapped = projection.MappedNaturalKeyDefinitions.ShouldHaveSingleItem();
        mapped.IsMapped.ShouldBeTrue();
        mapped.SourceProjectionType.ShouldBe(typeof(MnkOrder));
        mapped.AggregateType.ShouldBe(typeof(MnkOrderSummary));
        mapped.Member.Name.ShouldBe(nameof(MnkOrderSummary.Number));
        mapped.OuterType.ShouldBe(typeof(MnkOrderNumber));
        mapped.InnerType.ShouldBe(typeof(string));

        // The source projection maintains the lookup, so a mapped key never collects mappings of its own.
        mapped.EventMappings.ShouldBeEmpty();
        mapped.DiscoveryProblems.ShouldBeEmpty();
    }

    [Fact]
    public void discovers_a_mapped_key_from_the_source_projection_property()
    {
        var projection = new MnkShipmentProjection();

        var mapped = projection.MappedNaturalKeyDefinitions.ShouldHaveSingleItem();
        mapped.SourceProjectionType.ShouldBe(typeof(MnkOrderProjection));
    }

    [Fact]
    public void the_lookup_aggregate_follows_the_resolved_source_definition()
    {
        var owner = new MnkOrderProjection().NaturalKeyDefinition!;
        var mapped = new MnkOrderSummaryProjection().MappedNaturalKeyDefinitions.Single();

        mapped.LookupAggregateType.ShouldBe(typeof(MnkOrderSummary));

        mapped.SourceProjectionDefinition = owner;

        mapped.LookupAggregateType.ShouldBe(typeof(MnkOrder));
    }

    [Fact]
    public void an_aggregate_can_own_one_key_and_map_others()
    {
        var projection = new MnkInvoiceProjection();

        projection.NaturalKeyDefinition.ShouldNotBeNull();
        projection.NaturalKeyDefinition!.Member.Name.ShouldBe(nameof(MnkInvoice.InvoiceNumber));
        projection.NaturalKeyDefinition.EventMappings.ShouldHaveSingleItem().EventType.ShouldBe(typeof(MnkInvoiceRaised));

        projection.MappedNaturalKeyDefinitions.Select(x => x.Member.Name)
            .ShouldBe([nameof(MnkInvoice.OrderNumber), nameof(MnkInvoice.CustomerCode)], ignoreOrder: true);

        Should.NotThrow(() => projection.AssembleAndAssertValidity());
    }

    [Fact]
    public void configuring_a_projection_with_only_mapped_keys_explains_why_it_cannot()
    {
        var projection = new MnkOrderSummaryProjection();

        var ex = Should.Throw<InvalidProjectionException>(
            () => projection.NaturalKeyFor(x => x.SetBy<MnkOrderPlaced>(e => e.Number)));

        ex.Message.ShouldContain("only has mapped natural keys");
    }

    [Fact]
    public void more_than_one_owned_key_fails_at_configuration_time()
    {
        var ex = Should.Throw<InvalidProjectionException>(
            () => new MnkTwoOwnedKeysProjection().AssembleAndAssertValidity());

        ex.Message.ShouldContain(nameof(MnkTwoOwnedKeys.First));
        ex.Message.ShouldContain(nameof(MnkTwoOwnedKeys.Second));
        ex.Message.ShouldContain("at most one");
    }

    [Fact]
    public void two_keys_of_the_same_type_fail_at_configuration_time()
    {
        var ex = Should.Throw<InvalidProjectionException>(
            () => new MnkSameKeyTypeProjection().AssembleAndAssertValidity());

        ex.Message.ShouldContain(nameof(MnkSameKeyType.Own));
        ex.Message.ShouldContain(nameof(MnkSameKeyType.Borrowed));
        ex.Message.ShouldContain("distinct type");
    }

    [Fact]
    public void a_key_mapped_to_its_own_projection_fails_at_configuration_time()
    {
        var projection = new MnkSelfMappedProjection();

        projection.MappedNaturalKeyDefinitions.ShouldBeEmpty();

        var ex = Should.Throw<InvalidProjectionException>(() => projection.AssembleAndAssertValidity());
        ex.Message.ShouldContain(nameof(MnkSelfMapped.Number));
        ex.Message.ShouldContain("its own projection");
    }
}

// ───────────────────────── fixtures ─────────────────────────

public record MnkOrderNumber(string Value);

public record MnkCustomerCode(string Value);

public record MnkInvoiceNumber(string Value);

public record MnkOrderPlaced(MnkOrderNumber Number, MnkCustomerCode Customer);

public record MnkInvoiceRaised(MnkInvoiceNumber Number, MnkOrderNumber Order, MnkCustomerCode Customer);

// The owner: an inline snapshot that maintains the order number lookup.
public sealed record MnkOrder
{
    public Guid Id { get; set; }

    [NaturalKey]
    public MnkOrderNumber Number { get; set; } = default!;

    [NaturalKeySource]
    public static MnkOrderNumber KeyFor(MnkOrderPlaced e) => e.Number;
}

public class MnkOrderProjection : SingleStreamProjection<MnkOrder, Guid>
{
    public override MnkOrder? Evolve(MnkOrder? snapshot, Guid id, IEvent e) => snapshot;
}

// An async view of the same stream, fetchable through the owner's lookup.
public sealed record MnkOrderSummary
{
    public Guid Id { get; set; }

    [MappedNaturalKey<MnkOrder>]
    public MnkOrderNumber Number { get; set; } = default!;
}

public class MnkOrderSummaryProjection : SingleStreamProjection<MnkOrderSummary, Guid>
{
    public override MnkOrderSummary? Evolve(MnkOrderSummary? snapshot, Guid id, IEvent e) => snapshot;
}

public sealed record MnkShipment
{
    public Guid Id { get; set; }

    [NaturalKey(SourceProjection = typeof(MnkOrderProjection))]
    public MnkOrderNumber Order { get; set; } = default!;
}

public class MnkShipmentProjection : SingleStreamProjection<MnkShipment, Guid>
{
    public override MnkShipment? Evolve(MnkShipment? snapshot, Guid id, IEvent e) => snapshot;
}

public sealed record MnkCustomer
{
    public Guid Id { get; set; }

    [NaturalKey]
    public MnkCustomerCode Code { get; set; } = default!;
}

// One owned key plus two mapped ones, all of distinct types.
public sealed record MnkInvoice
{
    public Guid Id { get; set; }

    [NaturalKey]
    public MnkInvoiceNumber InvoiceNumber { get; set; } = default!;

    [MappedNaturalKey<MnkOrder>]
    public MnkOrderNumber OrderNumber { get; set; } = default!;

    [MappedNaturalKey<MnkCustomer>]
    public MnkCustomerCode CustomerCode { get; set; } = default!;

    [NaturalKeySource]
    public static MnkInvoiceNumber KeyFor(MnkInvoiceRaised e) => e.Number;
}

public class MnkInvoiceProjection : SingleStreamProjection<MnkInvoice, Guid>
{
    public override MnkInvoice? Evolve(MnkInvoice? snapshot, Guid id, IEvent e) => snapshot;
}

// Deliberately invalid: the runtime checks below are what these fixtures test, so the build-time
// diagnostics for the same mistakes are suppressed.
#pragma warning disable JFXEVT010, JFXEVT011, JFXEVT012

public sealed record MnkTwoOwnedKeys
{
    public Guid Id { get; set; }

    [NaturalKey]
    public MnkOrderNumber First { get; set; } = default!;

    [NaturalKey]
    public MnkCustomerCode Second { get; set; } = default!;
}

public class MnkTwoOwnedKeysProjection : SingleStreamProjection<MnkTwoOwnedKeys, Guid>
{
    public override MnkTwoOwnedKeys? Evolve(MnkTwoOwnedKeys? snapshot, Guid id, IEvent e) => snapshot;
}

public sealed record MnkSameKeyType
{
    public Guid Id { get; set; }

    [NaturalKey]
    public MnkOrderNumber Own { get; set; } = default!;

    [MappedNaturalKey<MnkOrder>]
    public MnkOrderNumber Borrowed { get; set; } = default!;
}

public class MnkSameKeyTypeProjection : SingleStreamProjection<MnkSameKeyType, Guid>
{
    public override MnkSameKeyType? Evolve(MnkSameKeyType? snapshot, Guid id, IEvent e) => snapshot;
}

public sealed record MnkSelfMapped
{
    public Guid Id { get; set; }

    [MappedNaturalKey<MnkSelfMapped>]
    public MnkOrderNumber Number { get; set; } = default!;
}

public class MnkSelfMappedProjection : SingleStreamProjection<MnkSelfMapped, Guid>
{
    public override MnkSelfMapped? Evolve(MnkSelfMapped? snapshot, Guid id, IEvent e) => snapshot;
}

#pragma warning restore JFXEVT010, JFXEVT011, JFXEVT012
