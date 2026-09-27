using JasperFx.Events.Documents;
using Shouldly;

namespace EventStoreTests.Documents;

/// <summary>
/// The default implementations of the tenant-scoped session openers (jasperfx#898).
/// </summary>
/// <remarks>
/// <para>
/// The two overloads were added additively to a contract that already ships, so they carry throwing
/// defaults and a store that has no conjoined document tenancy keeps compiling. That is the right
/// trade, and it is also the one that needs a test: a default which answered with the default
/// tenant's session instead of throwing would hand every caller another tenant's rows, silently,
/// on every store that had not implemented the member.
/// </para>
/// <para>
/// The second fact is the covariance trap, and it is the one worth having. C# interface
/// implementation is not return-type covariant, so a store that implements the GENERIC factory's
/// product-typed <c>TOperations LightweightSession(string)</c> has satisfied the generic member and
/// left the non-generic one bound to the default below — on a store whose tenancy is entirely
/// correct. The compliance suites hold the non-generic contract, so they would fail it. Pinning the
/// shape here makes the trap a documented fact rather than a surprise, and is why both
/// <see cref="IDocumentSessionFactory.LightweightSession(string)" /> and
/// <see cref="InMemoryDocumentStore" /> carry a warning about it.
/// </para>
/// </remarks>
public class DocumentSessionFactoryDefaultsTests
{
    private readonly IDocumentSessionFactory theFactory = new NotYetOverriddenFactory();

    [Fact]
    public void the_tenant_scoped_openers_throw_rather_than_falling_back_to_the_default_tenant()
    {
        Should.Throw<NotSupportedException>(() => theFactory.LightweightSession("acme"))
            .Message.ShouldContain(typeof(NotYetOverriddenFactory).FullName!);

        Should.Throw<NotSupportedException>(() => theFactory.QuerySession("acme"))
            .Message.ShouldContain(typeof(NotYetOverriddenFactory).FullName!);
    }

    /// <summary>
    /// A store that implemented the generic factory's tenant overloads and forgot the non-generic
    /// forwarders — the shape the remarks above describe.
    /// </summary>
    [Fact]
    public void implementing_only_the_generic_overload_leaves_the_contract_on_the_throwing_default()
    {
        var store = new GenericOnlyFactory();

        // The product-typed member works, which is exactly why this is easy to miss.
        store.LightweightSession("acme").ShouldNotBeNull();

        // The contract the compliance suites hold does not.
        Should.Throw<NotSupportedException>(() => ((IDocumentSessionFactory)store).LightweightSession("acme"));
    }

    private class NotYetOverriddenFactory : IDocumentSessionFactory
    {
        public IDocumentSessionOperations LightweightSession() => throw new NotImplementedException();

        public IDocumentReadOperations QuerySession() => throw new NotImplementedException();
    }

    private class GenericOnlyFactory : IDocumentSessionFactory<InMemoryDocumentSession, InMemoryDocumentSession>
    {
        private readonly InMemoryDocumentStore _store = new();

        public InMemoryDocumentSession LightweightSession() => _store.LightweightSession();

        public InMemoryDocumentSession QuerySession() => _store.QuerySession();

        public InMemoryDocumentSession LightweightSession(string tenantId) => _store.LightweightSession(tenantId);

        public InMemoryDocumentSession QuerySession(string tenantId) => _store.QuerySession(tenantId);

        IDocumentSessionOperations IDocumentSessionFactory.LightweightSession() => LightweightSession();

        IDocumentReadOperations IDocumentSessionFactory.QuerySession() => QuerySession();

        // Deliberately no tenant-scoped forwarders. That omission is the subject of the test.
    }
}
