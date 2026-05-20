using JasperFx.Metadata;
using Shouldly;

namespace CoreTests;

// Compile-pins the lifted marker-interface member shapes: if a member is renamed or its
// type changes, this sample implementation stops compiling. Also a smoke check that the
// interfaces are assignable as expected.
public class MetadataMarkerTests
{
    [Fact]
    public void sample_document_implements_all_three_markers()
    {
        var doc = new SampleDoc
        {
            Deleted = true,
            DeletedAt = DateTimeOffset.UtcNow,
            Version = Guid.NewGuid(),
            CorrelationId = "corr",
            CausationId = "cause",
            LastModifiedBy = "tester"
        };

        doc.ShouldBeAssignableTo<ISoftDeleted>();
        doc.ShouldBeAssignableTo<IVersioned>();
        doc.ShouldBeAssignableTo<ITracked>();
        doc.Deleted.ShouldBeTrue();
        doc.Version.ShouldNotBe(Guid.Empty);
        doc.LastModifiedBy.ShouldBe("tester");
    }

    private sealed class SampleDoc : ISoftDeleted, IVersioned, ITracked
    {
        public bool Deleted { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
        public Guid Version { get; set; }
        public string CorrelationId { get; set; } = "";
        public string CausationId { get; set; } = "";
        public string LastModifiedBy { get; set; } = "";
    }
}
