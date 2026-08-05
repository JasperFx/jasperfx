using System;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Strong-typed identifiers and their aggregates

/// <summary>
/// Guid-backed strong-typed identifier, hand-rolled rather than source-generated.
/// </summary>
/// <remarks>
/// Both products' own tests declare these with the <c>StronglyTypedIds</c> source generator, which
/// this library cannot require of a consumer. What a store actually needs is documented by
/// <c>JasperFx.Core.Reflection.ValueTypeInfo.ForType</c>: exactly one public gettable instance
/// property, plus either a constructor taking that property's type or a public static builder
/// taking it. A <c>readonly record struct</c> with a single positional parameter satisfies both
/// clauses and brings equality and <c>ToString</c> along, so the suite hand-rolls against the
/// documented shape instead of taking a package dependency.
/// </remarks>
public readonly record struct CompliancePaymentId(Guid Value);

/// <summary>
/// String-backed strong-typed identifier. Same shape, different simple type.
/// </summary>
public readonly record struct ComplianceInvoiceId(string Value);

public record PaymentRaised(decimal Amount);

public record PaymentSettled(decimal Amount);

public record InvoiceIssued(string Customer);

public record InvoicePaid;

/// <summary>
/// Aggregate keyed by a Guid-backed strong-typed identifier.
/// </summary>
public partial class CompliancePayment
{
    public CompliancePaymentId Id { get; set; }
    public decimal Outstanding { get; set; }
    public bool Settled { get; set; }

    public static CompliancePayment Create(IEvent<PaymentRaised> e) =>
        new() { Id = new CompliancePaymentId(e.StreamId), Outstanding = e.Data.Amount };

    public void Apply(PaymentSettled e)
    {
        Outstanding -= e.Amount;
        Settled = Outstanding <= 0;
    }
}

/// <summary>
/// Aggregate keyed by a string-backed strong-typed identifier, so the store has to route the
/// stream *key* through the wrapper rather than the stream id.
/// </summary>
public partial class ComplianceInvoice
{
    public ComplianceInvoiceId Id { get; set; }
    public string Customer { get; set; } = string.Empty;
    public bool Paid { get; set; }

    public static ComplianceInvoice Create(IEvent<InvoiceIssued> e) =>
        new() { Id = new ComplianceInvoiceId(e.StreamKey!), Customer = e.Data.Customer };

    public void Apply(InvoicePaid e) => Paid = true;
}

#endregion

/// <summary>
/// Strong-typed identifiers on aggregates — a wrapper struct standing in for the raw Guid or string
/// stream identity, across every entry point that accepts one.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately broad rather than a smoke test. Strong-typed ids have been a recurring source of
/// problems in both products, and the failures cluster at the *edges* — one lifecycle works and
/// another does not, or the Guid-backed wrapper works and the string-backed one does not — so a
/// suite that proves one path proves very little. Every combination of {Guid-backed,
/// string-backed} × {live, inline, async} × {fetch-for-writing, fetch-latest, aggregate-stream} that
/// the shared surface can express is covered here.
/// </para>
/// <para>
/// The one seam addition is <c>ComplianceStoreConfig.RegisterValueType&lt;T&gt;()</c>, which no-ops
/// on stores that discover value types automatically. That asymmetry is real: Marten needs the type
/// registered before it can use it in LINQ and identity mapping, Polecat derives the same
/// information from <c>ValueTypeInfo</c> when building the document mapping and exposes no
/// equivalent call. Same shape as the existing <c>LiveAggregation&lt;T&gt;()</c> precedent, which
/// exists for the mirror-image reason.
/// </para>
/// </remarks>
public abstract class StrongTypedIdentityCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_strong_typed";

        config.AddEventType<PaymentRaised>();
        config.AddEventType<PaymentSettled>();

        config.RegisterValueType<CompliancePaymentId>();
        config.Snapshot<CompliancePayment>(SnapshotLifecycle.Inline);
    };

    /// <summary>
    /// The Guid-backed aggregate registered async instead, in the same schema.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _asyncConfiguration = config =>
    {
        config.SchemaName = "compliance_strong_typed";

        config.AddEventType<PaymentRaised>();
        config.AddEventType<PaymentSettled>();

        config.RegisterValueType<CompliancePaymentId>();
        config.Snapshot<CompliancePayment>(SnapshotLifecycle.Async);
    };

    /// <summary>
    /// The string-backed aggregate. Stream identity is a store-level setting, so the string-keyed
    /// half cannot share a store with the Guid-keyed half.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _stringConfiguration = config =>
    {
        config.SchemaName = "compliance_strong_typed_string";
        config.StreamIdentity = StreamIdentity.AsString;

        config.AddEventType<InvoiceIssued>();
        config.AddEventType<InvoicePaid>();

        config.RegisterValueType<ComplianceInvoiceId>();
        config.Snapshot<ComplianceInvoice>(SnapshotLifecycle.Inline);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    private async Task<Guid> aPaymentAsync(params object[] events)
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream<CompliancePayment>(streamId,
            events.Length == 0 ? [new PaymentRaised(100m)] : events);
        await SaveChangesAsync(session);

        return streamId;
    }

    private async Task<string> anInvoiceAsync(params object[] events)
    {
        var key = $"invoice/{Guid.NewGuid():N}";

        await using var session = OpenSession();
        EventsFor(session).StartStream<ComplianceInvoice>(key,
            events.Length == 0 ? [new InvoiceIssued("Hilda")] : events);
        await SaveChangesAsync(session);

        return key;
    }

    // ---------- Guid-backed ----------

    [Fact]
    public async Task live_aggregation_hydrates_the_strong_typed_id()
    {
        var streamId = await aPaymentAsync(new PaymentRaised(100m), new PaymentSettled(40m));

        await using var query = OpenSession();
        var payment = await EventsFor(query).AggregateStreamAsync<CompliancePayment>(streamId, token: Cancellation);

        payment.ShouldNotBeNull();
        payment.Id.Value.ShouldBe(streamId);
        payment.Outstanding.ShouldBe(60m);
    }

    [Fact]
    public async Task an_inline_snapshot_persists_and_reads_back_by_the_strong_typed_id()
    {
        var streamId = await aPaymentAsync(new PaymentRaised(100m), new PaymentSettled(100m));

        await using var session = OpenSession();
        var payment = await EventsFor(session)
            .FetchLatest<CompliancePayment>(streamId, Cancellation);

        payment.ShouldNotBeNull();
        payment.Id.Value.ShouldBe(streamId);
        payment.Settled.ShouldBeTrue();
    }

    [Fact]
    public async Task fetch_for_writing_by_the_strong_typed_id()
    {
        var streamId = await aPaymentAsync(new PaymentRaised(100m));

        await using var session = OpenSession();
        var stream = await EventsFor(session)
            .FetchForWriting<CompliancePayment>(streamId, Cancellation);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Id.Value.ShouldBe(streamId);
        stream.Aggregate.Outstanding.ShouldBe(100m);
    }

    [Fact]
    public async Task appending_through_a_strong_typed_write_handle_advances_the_aggregate()
    {
        var streamId = await aPaymentAsync(new PaymentRaised(100m));

        await using (var session = OpenSession())
        {
            var stream = await EventsFor(session)
                .FetchForWriting<CompliancePayment>(streamId, Cancellation);

            stream.AppendOne(new PaymentSettled(25m));
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var payment = await EventsFor(query)
            .FetchLatest<CompliancePayment>(streamId, Cancellation);

        payment!.Outstanding.ShouldBe(75m);
    }

    [Fact]
    public async Task fetch_for_exclusive_writing_by_the_strong_typed_id()
    {
        var streamId = await aPaymentAsync(new PaymentRaised(50m));

        await using var session = OpenSession();
        var stream = await EventsFor(session)
            .FetchForExclusiveWriting<CompliancePayment>(streamId, Cancellation);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Id.Value.ShouldBe(streamId);

        stream.AppendOne(new PaymentSettled(50m));
        await SaveChangesAsync(session);

        await using var query = OpenSession();
        var payment = await EventsFor(query)
            .FetchLatest<CompliancePayment>(streamId, Cancellation);
        payment!.Settled.ShouldBeTrue();
    }

    [Fact]
    public async Task fetch_for_writing_an_unknown_strong_typed_id_yields_an_empty_handle()
    {
        await using var session = OpenSession();
        var stream = await EventsFor(session)
            .FetchForWriting<CompliancePayment>(Guid.NewGuid(), Cancellation);

        stream.Aggregate.ShouldBeNull();
        stream.StartingVersion.ShouldBe(0);
    }

    [Fact]
    public async Task an_async_snapshot_persists_with_the_strong_typed_id()
    {
        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");

        await theFixture.ConfigureAsync(_asyncConfiguration);

        var streamId = await aPaymentAsync(new PaymentRaised(80m), new PaymentSettled(30m));

        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await using var session = OpenSession();
        var payment = await EventsFor(session)
            .FetchLatest<CompliancePayment>(streamId, Cancellation);

        payment.ShouldNotBeNull();
        payment.Id.Value.ShouldBe(streamId);
        payment.Outstanding.ShouldBe(50m);
    }

    // ---------- String-backed ----------

    [Fact]
    public async Task a_string_backed_strong_typed_id_hydrates_from_a_live_fold()
    {
        await theFixture.ConfigureAsync(_stringConfiguration);

        var key = await anInvoiceAsync(new InvoiceIssued("Hilda"), new InvoicePaid());

        await using var query = OpenSession();
        var invoice = await EventsFor(query).AggregateStreamAsync<ComplianceInvoice>(key, token: Cancellation);

        invoice.ShouldNotBeNull();
        invoice.Id.Value.ShouldBe(key);
        invoice.Customer.ShouldBe("Hilda");
        invoice.Paid.ShouldBeTrue();
    }

    [Fact]
    public async Task a_string_backed_inline_snapshot_reads_back_by_the_strong_typed_id()
    {
        await theFixture.ConfigureAsync(_stringConfiguration);

        var key = await anInvoiceAsync(new InvoiceIssued("Grace"), new InvoicePaid());

        await using var session = OpenSession();
        var invoice = await EventsFor(session)
            .FetchLatest<ComplianceInvoice>(key, Cancellation);

        invoice.ShouldNotBeNull();
        invoice.Id.Value.ShouldBe(key);
        invoice.Paid.ShouldBeTrue();
    }

    [Fact]
    public async Task fetch_for_writing_by_a_string_backed_strong_typed_id()
    {
        await theFixture.ConfigureAsync(_stringConfiguration);

        var key = await anInvoiceAsync(new InvoiceIssued("Ada"));

        await using var session = OpenSession();
        var stream = await EventsFor(session)
            .FetchForWriting<ComplianceInvoice>(key, Cancellation);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Id.Value.ShouldBe(key);
        stream.Aggregate.Customer.ShouldBe("Ada");

        stream.AppendOne(new InvoicePaid());
        await SaveChangesAsync(session);

        await using var query = OpenSession();
        var invoice = await EventsFor(query)
            .FetchLatest<ComplianceInvoice>(key, Cancellation);
        invoice!.Paid.ShouldBeTrue();
    }

    [Fact]
    public async Task the_two_backings_do_not_collide_on_identity()
    {
        await theFixture.ConfigureAsync(_stringConfiguration);

        var first = await anInvoiceAsync(new InvoiceIssued("First"));
        var second = await anInvoiceAsync(new InvoiceIssued("Second"));

        await using var session = OpenSession();

        var one = await EventsFor(session)
            .FetchLatest<ComplianceInvoice>(first, Cancellation);
        var two = await EventsFor(session)
            .FetchLatest<ComplianceInvoice>(second, Cancellation);

        one!.Customer.ShouldBe("First");
        two!.Customer.ShouldBe("Second");
    }
}
