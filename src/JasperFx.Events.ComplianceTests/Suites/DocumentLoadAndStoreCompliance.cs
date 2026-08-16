using System;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Documents;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// <see cref="IDocumentWriteOperations.Store{T}" /> and
/// <see cref="IDocumentReadOperations.LoadAsync{T}(Guid,System.Threading.CancellationToken)" /> —
/// all three identity styles, the upsert semantics of <c>Store</c>, and the miss case.
/// </summary>
/// <remarks>
/// <c>Store</c> is an upsert on every Critter Stack store, not an insert: storing an identity that
/// already exists replaces it rather than throwing. That is the behavior consumers rely on and the
/// one place three implementations could plausibly read the contract differently, so it is asserted
/// directly rather than left implicit.
/// </remarks>
public abstract class DocumentLoadAndStoreCompliance<TFixture> : DocumentStorageComplianceSuite<TFixture>
    where TFixture : DocumentStorageComplianceFixture, new()
{
    private static readonly Action<DocumentComplianceConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_documents";
        config.AddDocumentType<ComplianceWidget>();
        config.AddDocumentType<ComplianceGadget>();
        config.AddDocumentType<ComplianceCoupon>();
        config.RegisterValueType<CouponCode>();
    };

    protected override Action<DocumentComplianceConfig> Configuration => _configuration;

    [Fact]
    public async Task store_and_load_by_guid_identity()
    {
        var id = Guid.NewGuid();
        await PersistAsync(new ComplianceWidget { Id = id, Name = "Cog", Color = "blue", Weight = 11 });

        await using var query = QuerySession();
        var loaded = await query.LoadAsync<ComplianceWidget>(id, Cancellation);

        loaded.ShouldNotBeNull();
        loaded.Id.ShouldBe(id);
        loaded.Name.ShouldBe("Cog");
    }

    [Fact]
    public async Task store_and_load_by_string_identity()
    {
        await PersistAsync(new ComplianceGadget { Id = "gadget-42", Kind = "ratchet", Weight = 3 });

        await using var query = QuerySession();
        var loaded = await query.LoadAsync<ComplianceGadget>("gadget-42", Cancellation);

        loaded.ShouldNotBeNull();
        loaded.Id.ShouldBe("gadget-42");
        loaded.Kind.ShouldBe("ratchet");
    }

    [Fact]
    public async Task store_and_load_by_strong_typed_identity()
    {
        var id = new CouponCode(Guid.NewGuid());
        await PersistAsync(new ComplianceCoupon { Id = id, Description = "Launch week", PercentOff = 20 });

        await using var query = QuerySession();
        var loaded = await query.LoadAsync<ComplianceCoupon>(id, Cancellation);

        loaded.ShouldNotBeNull();
        loaded.Id.ShouldBe(id);
        loaded.Description.ShouldBe("Launch week");
    }

    [Fact]
    public async Task load_returns_null_for_a_missing_strong_typed_identity()
    {
        await using var query = QuerySession();

        (await query.LoadAsync<ComplianceCoupon>(new CouponCode(Guid.NewGuid()), Cancellation)).ShouldBeNull();
    }

    /// <remarks>
    /// The <c>object</c> overload is reached by a caller holding any identity in an
    /// <c>object</c>-typed local, not only by one holding a wrapper, so it has to resolve a boxed
    /// canonical identity exactly as the typed overload does. The contract's default implementation
    /// gets this right for free — but a store only reaches the two tests above by overriding it, and
    /// an override that assumes every argument is a strong-typed wrapper passes those and regresses
    /// this one.
    /// </remarks>
    [Fact]
    public async Task the_object_overload_resolves_canonical_identities_too()
    {
        var widgetId = Guid.NewGuid();
        await PersistAsync(new ComplianceWidget { Id = widgetId, Name = "Boxed" });
        await PersistAsync(new ComplianceGadget { Id = "gadget-boxed", Kind = "spanner" });

        await using var query = QuerySession();

        object guidIdentity = widgetId;
        var widget = await query.LoadAsync<ComplianceWidget>(guidIdentity, Cancellation);
        widget.ShouldNotBeNull();
        widget.Name.ShouldBe("Boxed");

        object stringIdentity = "gadget-boxed";
        var gadget = await query.LoadAsync<ComplianceGadget>(stringIdentity, Cancellation);
        gadget.ShouldNotBeNull();
        gadget.Kind.ShouldBe("spanner");
    }

    [Fact]
    public async Task load_returns_null_for_a_missing_guid_identity()
    {
        await using var query = QuerySession();

        (await query.LoadAsync<ComplianceWidget>(Guid.NewGuid(), Cancellation)).ShouldBeNull();
    }

    [Fact]
    public async Task load_returns_null_for_a_missing_string_identity()
    {
        await using var query = QuerySession();

        (await query.LoadAsync<ComplianceGadget>("nothing-here", Cancellation)).ShouldBeNull();
    }

    [Fact]
    public async Task store_accepts_many_documents_in_one_call()
    {
        var widgets = new[]
        {
            new ComplianceWidget { Id = Guid.NewGuid(), Name = "A" },
            new ComplianceWidget { Id = Guid.NewGuid(), Name = "B" },
            new ComplianceWidget { Id = Guid.NewGuid(), Name = "C" }
        };

        await using (var session = LightweightSession())
        {
            session.Store(widgets);
            await session.SaveChangesAsync(Cancellation);
        }

        await using var query = QuerySession();
        var all = await query.Query<ComplianceWidget>().ToListAsync(Cancellation);

        all.Count.ShouldBe(3);
        all.Select(x => x.Name).OrderBy(x => x).ShouldBe(new[] { "A", "B", "C" });
    }

    [Fact]
    public async Task storing_an_existing_identity_overwrites_it()
    {
        var id = Guid.NewGuid();
        await PersistAsync(new ComplianceWidget { Id = id, Name = "Original", Weight = 1 });
        await PersistAsync(new ComplianceWidget { Id = id, Name = "Replacement", Weight = 2 });

        await using var query = QuerySession();

        var loaded = await query.LoadAsync<ComplianceWidget>(id, Cancellation);
        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("Replacement");
        loaded.Weight.ShouldBe(2);

        (await query.Query<ComplianceWidget>().CountAsync(Cancellation)).ShouldBe(1);
    }

    [Fact]
    public async Task storing_the_same_identity_twice_within_one_session_keeps_the_last_write()
    {
        var id = Guid.NewGuid();

        await using (var session = LightweightSession())
        {
            session.Store(new ComplianceWidget { Id = id, Name = "First" });
            session.Store(new ComplianceWidget { Id = id, Name = "Last" });
            await session.SaveChangesAsync(Cancellation);
        }

        await using var query = QuerySession();
        var loaded = await query.LoadAsync<ComplianceWidget>(id, Cancellation);

        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("Last");
    }

    [Fact]
    public async Task a_writable_session_can_read_as_well_as_write()
    {
        var id = Guid.NewGuid();
        await PersistAsync(new ComplianceWidget { Id = id, Name = "Readable" });

        await using var session = LightweightSession();
        var loaded = await session.LoadAsync<ComplianceWidget>(id, Cancellation);

        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("Readable");
    }
}
