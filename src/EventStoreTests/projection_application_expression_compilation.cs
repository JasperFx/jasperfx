using JasperFx.Events;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventStoreTests;

public class projection_application_expression_compilation
{
    private readonly Operations theOperations = new();
    
    [Fact]
    public async Task build_projection_for_inline_lambdas()
    {
        var source = new SyncProjection1();
        var projection = source.BuildProjection();

        var aEvent = new AEvent();
        IEvent[] events = [new Event<AEvent>(aEvent), new Event<BEvent>(new BEvent())];

        await projection.ApplyAsync(theOperations, events, CancellationToken.None);
        
        theOperations.Stored.OfType<GotA>().Single().Id.ShouldBe(aEvent.Id);
        theOperations.Stored.OfType<GotB>().Single().Id.ShouldBe(events[1].Id);
    }
}

public class SyncProjection1 : ProjectionSource<Operations, DocumentStore, Database>
{
    public SyncProjection1()
    {

    }

    public void Project(AEvent e, Operations ops) => ops.Store(new GotA(e.Id));
    public void Project(IEvent<BEvent> e, Operations ops) => ops.Store(e.Id);
}

public record GotA(Guid Id);

public record GotB(Guid Id);
    
public class Operations
{
    public readonly List<object> Stored = new();
    
    public void Store(object document)
    {
        Stored.Add(document);
    }
}

public class DocumentStore
{
    
}

public class Database
{
    
}