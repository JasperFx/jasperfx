using JasperFx;
using Shouldly;

namespace CoreTests;

public class LongVersionedTests
{
    [Fact]
    public void irevisioned_is_still_int()
    {
        // #348 explicitly keeps IRevisioned.Version as int (Marten V8 signature). Guard it.
        typeof(IRevisioned).GetProperty(nameof(IRevisioned.Version))!.PropertyType.ShouldBe(typeof(int));
    }

    [Fact]
    public void ilongversioned_is_long()
    {
        typeof(ILongVersioned).GetProperty(nameof(ILongVersioned.Version))!.PropertyType.ShouldBe(typeof(long));
    }

    [Fact]
    public void concurrency_exception_message_references_irevisioned_for_int_docs()
    {
        var message = ConcurrencyException.ToMessage(typeof(RevisionedDoc), "id-1");

        message.ShouldContain("Optimistic concurrency check failed");
        message.ShouldContain(nameof(IRevisioned));
        message.ShouldNotContain(nameof(ILongVersioned));
    }

    [Fact]
    public void concurrency_exception_message_references_ilongversioned_for_long_docs()
    {
        var message = ConcurrencyException.ToMessage(typeof(LongVersionedDoc), "id-2");

        message.ShouldContain("Optimistic concurrency check failed");
        message.ShouldContain(nameof(ILongVersioned));
    }

    [Fact]
    public void concurrency_exception_message_plain_for_unversioned_docs()
    {
        var message = ConcurrencyException.ToMessage(typeof(PlainDoc), "id-3");

        message.ShouldContain("Optimistic concurrency check failed");
        message.ShouldNotContain(nameof(IRevisioned));
        message.ShouldNotContain(nameof(ILongVersioned));
    }

    private sealed class RevisionedDoc : IRevisioned
    {
        public int Version { get; set; }
    }

    private sealed class LongVersionedDoc : ILongVersioned
    {
        public long Version { get; set; }
    }

    private sealed class PlainDoc
    {
        public string Name { get; set; } = "";
    }
}
