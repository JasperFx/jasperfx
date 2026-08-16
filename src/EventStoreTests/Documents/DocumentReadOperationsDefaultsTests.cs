using System.Linq.Expressions;
using JasperFx.Events.Documents;
using Shouldly;

namespace EventStoreTests.Documents;

/// <summary>
/// The default implementation of
/// <see cref="IDocumentReadOperations.LoadAsync{T}(object,CancellationToken)" /> (jasperfx#665).
/// </summary>
/// <remarks>
/// <see cref="InMemoryDocumentStore" /> overrides that member, as every real store will, so the
/// compliance suites never execute the default — which is exactly why it needs its own tests. The
/// stub below is the shape a store has on the day it takes the JasperFx bump and before it writes
/// the override, and the behavior asserted here is what that store's consumers get in the meantime.
/// </remarks>
public class DocumentReadOperationsDefaultsTests
{
    private readonly IDocumentReadOperations theOperations = new NotYetOverriddenSession();

    [Fact]
    public async Task forwards_a_boxed_guid_to_the_guid_overload()
    {
        object id = Guid.NewGuid();

        (await theOperations.LoadAsync<string>(id)).ShouldBe($"guid:{id}");
    }

    [Fact]
    public async Task forwards_a_boxed_string_to_the_string_overload()
    {
        object id = "gadget-42";

        (await theOperations.LoadAsync<string>(id)).ShouldBe("string:gadget-42");
    }

    [Fact]
    public async Task throws_for_a_strong_typed_identity_rather_than_guessing()
    {
        var ex = await Should.ThrowAsync<NotSupportedException>(
            () => theOperations.LoadAsync<string>(new CouponCode(Guid.NewGuid())));

        // The message has to name the store, the document type and the identity type: this surfaces
        // on the consumer's side of a contract the consumer cannot fix, so it has to point at the
        // store that owes the override rather than merely say the shape was unrecognized.
        ex.Message.ShouldContain(typeof(NotYetOverriddenSession).FullName!);
        ex.Message.ShouldContain(typeof(string).FullName!);
        ex.Message.ShouldContain(typeof(CouponCode).FullName!);
    }

    /// <remarks>
    /// The local has to be <c>object</c>-typed to get here: a bare <c>null</c> literal is
    /// convertible to <see cref="string" />, which is more specific than <see cref="object" />, so
    /// <c>LoadAsync&lt;T&gt;(null)</c> binds to the string overload and never reaches this member.
    /// </remarks>
    [Fact]
    public async Task throws_argument_null_for_a_null_identity()
    {
        object id = null!;

        await Should.ThrowAsync<ArgumentNullException>(() => theOperations.LoadAsync<string>(id));
    }

    public readonly record struct CouponCode(Guid Value);

    /// <summary>
    /// Implements only what the contract has no default for, so every call to the <c>object</c>
    /// overload lands on the default implementation.
    /// </summary>
    private class NotYetOverriddenSession : IDocumentReadOperations
    {
        public Task<T?> LoadAsync<T>(Guid id, CancellationToken token = default) where T : notnull
            => Task.FromResult((T?)(object)$"guid:{id}");

        public Task<T?> LoadAsync<T>(string id, CancellationToken token = default) where T : notnull
            => Task.FromResult((T?)(object)$"string:{id}");

        public IQueryable<T> Query<T>() where T : notnull => throw new NotImplementedException();

        public ValueTask DisposeAsync() => default;
    }
}
