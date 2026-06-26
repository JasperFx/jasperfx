using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Shouldly;

namespace CoreTests.Descriptors;

/// <summary>
/// Pins the wire shape of <see cref="DocumentStoreUsage"/> — specifically that
/// constructing one from a live document-store instance does NOT recursively
/// reflect the subject's public properties into <see cref="OptionsDescription.Children"/>
/// or <see cref="OptionsDescription.Properties"/>.
/// </summary>
/// <remarks>
/// CritterWatch's Documents tab consumes this descriptor and previously
/// rendered <c>Storage</c> / <c>Advanced</c> / <c>Diagnostics</c> / <c>Options</c>
/// child blocks because the old <c>(Uri, object)</c> ctor chained to
/// <see cref="OptionsDescription(object)"/>, which calls the reflective auto-
/// reader. Callers populate first-class fields explicitly, so the auto-walk
/// adds noise, not signal.
/// </remarks>
public class DocumentStoreUsageTests
{
    [Fact]
    public void ctor_sets_subject_uri_version_without_reflecting_into_properties()
    {
        var subject = new FakeDocumentStore();
        var usage = new DocumentStoreUsage(new Uri("marten://main"), subject);

        // Identity fields are populated...
        // FullNameInCode renders nested types with `.` (so generated code can
        // refer to them) — the tests pin against that, not Type.FullName which
        // uses `+`.
        usage.Subject.ShouldBe(typeof(FakeDocumentStore).FullNameInCode());
        usage.SubjectUri.ShouldBe(new Uri("marten://main"));
        usage.Version.ShouldBe(typeof(FakeDocumentStore).Assembly.GetName().Version?.ToString());

        // ...but the subject's runtime handles are NOT reflected in.
        usage.Properties.ShouldBeEmpty();
        usage.Children.ShouldBeEmpty();
        usage.Sets.ShouldBeEmpty();
    }

    [Fact]
    public void ctor_throws_for_null_subject()
    {
        Should.Throw<ArgumentNullException>(
            () => new DocumentStoreUsage(new Uri("marten://main"), null!));
    }

    [Fact]
    public void document_metadata_capabilities_default_to_null()
    {
        // jasperfx#475: until the implementing store populates it, consumers
        // must be able to tell "capabilities unknown" apart from "everything off".
        new DocumentStoreUsage(new Uri("marten://main"), new FakeDocumentStore())
            .DocumentMetadata.ShouldBeNull();
    }

    [Fact]
    public void document_metadata_capabilities_default_flags()
    {
        // Common document metadata -> default true; the opt-in tracking columns
        // (correlation/causation/last-modified-by) -> default false.
        var capabilities = new DocumentMetadataCapabilities();

        capabilities.StoreType.ShouldBe("");

        capabilities.Version.ShouldBeTrue();
        capabilities.LastModified.ShouldBeTrue();
        capabilities.TenantId.ShouldBeTrue();
        capabilities.SoftDelete.ShouldBeTrue();

        capabilities.CorrelationId.ShouldBeFalse();
        capabilities.CausationId.ShouldBeFalse();
        capabilities.LastModifiedBy.ShouldBeFalse();
    }

    [Fact]
    public void document_metadata_capabilities_round_trip_through_json()
    {
        var usage = new DocumentStoreUsage(new Uri("marten://main"), new FakeDocumentStore())
        {
            DocumentMetadata = new DocumentMetadataCapabilities
            {
                StoreType = "Marten",
                CorrelationId = true,
                CausationId = true,
                LastModifiedBy = true,
                // Flip a couple of the otherwise-common flags off to prove they
                // serialize as set, not as their defaults.
                SoftDelete = false,
                TenantId = false
            }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(usage);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<DocumentStoreUsage>(json)!;

        roundTripped.DocumentMetadata.ShouldNotBeNull();
        roundTripped.DocumentMetadata.StoreType.ShouldBe("Marten");
        roundTripped.DocumentMetadata.CorrelationId.ShouldBeTrue();
        roundTripped.DocumentMetadata.CausationId.ShouldBeTrue();
        roundTripped.DocumentMetadata.LastModifiedBy.ShouldBeTrue();
        roundTripped.DocumentMetadata.SoftDelete.ShouldBeFalse();
        roundTripped.DocumentMetadata.TenantId.ShouldBeFalse();
    }

    /// <summary>
    /// Stand-in for Marten's DocumentStore — exposes the kind of runtime
    /// handles (IStorage / IAdvanced / IDiagnostics / IOptions) that used to
    /// leak into the descriptor's Children dictionary.
    /// </summary>
    private class FakeDocumentStore
    {
        public string Storage { get; } = "irrelevant";
        public string Advanced { get; } = "irrelevant";
        public string Diagnostics { get; } = "irrelevant";
        public string Options { get; } = "irrelevant";
    }
}
