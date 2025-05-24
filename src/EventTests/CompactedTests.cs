using EventTests.Projections;
using JasperFx.Events;
using Shouldly;

namespace EventTests;

public class CompactedTests
{
    private List<IEvent> theEvents = [
        new Event<AEvent>(new AEvent()), 
        new Event<AEvent>(new AEvent()), 
        new Event<BEvent>(new BEvent()),
        new Event<AEvent>(new AEvent()),
        new Event<AEvent>(new AEvent()),
        new Event<AEvent>(new AEvent()),
    
    ];

    [Fact]
    public void maybe_fast_forward_nothing()
    {
        var snapshot = new LetterCounts();

        var (snapshot2, events2) 
            = Compacted<LetterCounts>.MaybeFastForward(snapshot, theEvents);
        
        snapshot2.ShouldBeSameAs(snapshot);
        events2.ShouldBeSameAs(theEvents);
    }

    [Fact]
    public void compact_is_last()
    {
        var letterCounts = new LetterCounts { };
            
        var compacted =
            new Event<Compacted<LetterCounts>>(new Compacted<LetterCounts>(letterCounts, Guid.NewGuid(), ""));
        
        theEvents.Add(compacted);
        
        var snapshot = new LetterCounts();
        
        var (snapshot2, events2) 
            = Compacted<LetterCounts>.MaybeFastForward(snapshot, theEvents);

        snapshot2.ShouldBeSameAs(compacted.Data.Snapshot);
        events2.Any().ShouldBeFalse();
    }

    [Fact]
    public void compact_at_first()
    {
        var letterCounts = new LetterCounts { };
            
        var compacted =
            new Event<Compacted<LetterCounts>>(new Compacted<LetterCounts>(letterCounts, Guid.NewGuid(), ""));
        
        theEvents.Insert(0, compacted);
        
        var snapshot = new LetterCounts();
        
        var (snapshot2, events2) 
            = Compacted<LetterCounts>.MaybeFastForward(snapshot, theEvents);

        snapshot2.ShouldBeSameAs(compacted.Data.Snapshot);
        events2.Count.ShouldBe(6);
    }

    [Fact]
    public void compact_in_the_middle()
    {
        var letterCounts = new LetterCounts { };
            
        var compacted =
            new Event<Compacted<LetterCounts>>(new Compacted<LetterCounts>(letterCounts, Guid.NewGuid(), ""));
        
        theEvents.Insert(3, compacted);
        
        var snapshot = new LetterCounts();
        
        var (snapshot2, events2) 
            = Compacted<LetterCounts>.MaybeFastForward(snapshot, theEvents);

        snapshot2.ShouldBeSameAs(compacted.Data.Snapshot);
        events2.Count.ShouldBe(3);
    }
}

