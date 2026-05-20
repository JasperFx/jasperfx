using JasperFx.Events;
using Shouldly;

namespace EventTests;

// Compile-pins the lifted IDocumentSchemaResolver surface by implementing it, and smoke-checks
// the qualified/bare behavior a real resolver provides.
public class DocumentSchemaResolverContractTests
{
    [Fact]
    public void implementable_with_qualified_and_bare_names()
    {
        IDocumentSchemaResolver resolver = new FakeResolver("public");

        resolver.DatabaseSchemaName.ShouldBe("public");
        resolver.EventsSchemaName.ShouldBe("public");

        resolver.For<DocumentSchemaResolverContractTests>().ShouldBe("public.documentschemaresolvercontracttests");
        resolver.For<DocumentSchemaResolverContractTests>(qualified: false).ShouldBe("documentschemaresolvercontracttests");
        resolver.For(typeof(DocumentSchemaResolverContractTests), qualified: false).ShouldBe("documentschemaresolvercontracttests");

        resolver.ForEvents().ShouldBe("public.mt_events");
        resolver.ForStreams().ShouldBe("public.mt_streams");
        resolver.ForEventProgression(qualified: false).ShouldBe("mt_event_progression");
    }

    private sealed class FakeResolver(string schema) : IDocumentSchemaResolver
    {
        public string DatabaseSchemaName { get; } = schema;
        public string EventsSchemaName { get; } = schema;

        public string For<TDocument>(bool qualified = true) => For(typeof(TDocument), qualified);

        public string For(Type documentType, bool qualified = true)
            => Qualify(documentType.Name.ToLowerInvariant(), qualified);

        public string ForEvents(bool qualified = true) => Qualify("mt_events", qualified);
        public string ForStreams(bool qualified = true) => Qualify("mt_streams", qualified);
        public string ForEventProgression(bool qualified = true) => Qualify("mt_event_progression", qualified);

        private string Qualify(string table, bool qualified) => qualified ? $"{DatabaseSchemaName}.{table}" : table;
    }
}
