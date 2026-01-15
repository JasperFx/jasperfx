using JasperFx.Events;
using JasperFx.Events.Aggregation;
using Shouldly;

namespace EventTests.Projections;

public class AggregateApplicationTests
{
    /*
     * TODOs
     * Try ShouldDelete
     * Use validation
     * Use base types for events
     */
    
    [Fact]
    public async Task use_ctor_that_takes_the_event_data_type()
    {
        var application = new AggregateApplication<LetterCounts, Session>();
        var snapshot = await application.CreateByData(new StartLetters(2, 3), new Session());

        snapshot.ShouldNotBeNull();
        snapshot.ACount.ShouldBe(2);
        snapshot.BCount.ShouldBe(3);
    }
    
    [Fact]
    public async Task use_ctor_that_takes_the_wrapped_event_data_type()
    {
        var application = new AggregateApplication<LetterCounts2, Session>();
        application.AssertValidity();
        var snapshot = await application.CreateByData(new StartLetters(2, 3), new Session());

        snapshot.ShouldNotBeNull();
        snapshot.ACount.ShouldBe(2);
        snapshot.BCount.ShouldBe(3);
    }

    [Fact]
    public async Task create_by_falling_through_to_no_arg_constructor()
    {
        var application = new AggregateApplication<LetterCounts, Session>();
        var snapshot = await application.CreateByData(new AEvent(), new Session());
        
        snapshot.ShouldNotBeNull();
        
        // Should use Apply() on this 
        snapshot.ACount.ShouldBe(1);
        snapshot.BCount.ShouldBe(0);
    }
    
    [Fact]
    public async Task use_static_create_on_aggregate_that_takes_the_event_data_type()
    {
        var application = new AggregateApplication<LetterCounts3, Session>();
        var snapshot = await application.CreateByData(new StartLetters(2, 3), new Session());

        snapshot.ShouldNotBeNull();
        snapshot.ACount.ShouldBe(2);
        snapshot.BCount.ShouldBe(3);
    }
    
    [Fact]
    public async Task use_static_create_on_aggregate_that_takes_the_wrapped_event_data_type()
    {
        var application = new AggregateApplication<LetterCounts4, Session>();
        var snapshot = await application.CreateByData(new StartLetters(2, 3), new Session());

        snapshot.ShouldNotBeNull();
        snapshot.ACount.ShouldBe(2);
        snapshot.BCount.ShouldBe(3);
    }

    [Fact]
    public async Task use_create_methods_on_projection_type()
    {
        var session = new Session();
        var application = new AggregateApplication<LetterCounts, Session>(new LetterCountsProjection());
        (await application.CreateByData(new AEvent(), session)).ACount.ShouldBe(1);
        (await application.CreateByData(new BEvent(), session)).BCount.ShouldBe(1);
        (await application.CreateByData(new CEvent(), session)).CCount.ShouldBe(1);
        (await application.CreateByData(new DEvent(), session)).DCount.ShouldBe(1);
    }

    [Fact]
    public async Task apply_on_aggregate_simple_event_data_mutation()
    {
        var session = new Session();
        var application = new AggregateApplication<LetterCounts, Session>();
        var counts = new LetterCounts();

        var copy = await application.ApplyByDataAsync(counts, new AEvent(), session);
        copy.ACount.ShouldBe(1);
    }

    [Fact]
    public async Task apply_with_wrapped_event_as_immutable_static()
    {
        var session = new Session();
        var application = new AggregateApplication<LetterCounts, Session>();
        var counts = new LetterCounts();
        
        var copy = await application.ApplyByDataAsync(counts, new BEvent(), session);
        copy.BCount.ShouldBe(1);
        copy.ShouldNotBeSameAs(counts);
    }

    [Fact]
    public async Task passing_the_session_into_the_apply_methods()
    {
        var session = new Session();
        var application = new AggregateApplication<LetterCounts, Session>();
        var counts = new LetterCounts();
        
        var copy = await application.ApplyByDataAsync(counts, new CEvent(), session);
        copy.CCount.ShouldBe(1);
        copy.Session.ShouldBeSameAs(session);
    }
    
    [Fact]
    public async Task passing_the_session_into_the_apply_methods_as_static_with_value_task_return()
    {
        var session = new Session();
        var application = new AggregateApplication<LetterCounts, Session>();
        var counts = new LetterCounts();
        
        var copy = await application.ApplyByDataAsync(counts, new DEvent(), session);
        copy.DCount.ShouldBe(1);
        copy.Session.ShouldBeSameAs(session);
    }

    [Fact]
    public async Task using_should_delete()
    {
        var session = new Session();
        var application = new AggregateApplication<LetterCounts, Session>();
        var counts = new LetterCounts();
        
        (await application.ApplyByDataAsync(counts, new FullStop(false), session)).ShouldBeSameAs(counts);
        (await application.ApplyByDataAsync(counts, new FullStop(true), session)).ShouldBeNull();
    }
    
}

public class Session
{
    
}

public class LetterCountsProjection
{
    public LetterCounts Create(AEvent e) => new LetterCounts { ACount = 1 };
    public static LetterCounts Create(BEvent e) => new LetterCounts { BCount = 1 };
    public LetterCounts Create(IEvent<CEvent> e) => new LetterCounts { CCount = 1 };
    public static LetterCounts Create(IEvent<DEvent> e) => new LetterCounts { DCount = 1 };
}

public record Assigned(string UserName);

public record AssignedToUser(User User);

public class LetterCounts
{
    public LetterCounts()
    {
    }

    public LetterCounts(StartLetters letters)
    {
        ACount = letters.A;
        BCount = letters.B;
    }
    
    public User? User { get; set; }

    public void Apply(AssignedToUser e) => User = e.User;

    public void Apply(AEvent e) => ACount++;

    public static LetterCounts Apply(BEvent e, LetterCounts snapshot)
    {
        var copy = new LetterCounts
        {
            ACount = snapshot.ACount,
            BCount = snapshot.BCount,
            CCount = snapshot.CCount,
            DCount = snapshot.DCount
        };

        copy.BCount++;

        return copy;
    }

    public void Apply(IEvent<CEvent> e, Session session)
    {
        Session = session;
        CCount++;
    }
    
    public static ValueTask<LetterCounts> Apply(IEvent<DEvent> e, LetterCounts snapshot, Session session)
    {
        var copy = new LetterCounts
        {
            ACount = snapshot.ACount,
            BCount = snapshot.BCount,
            CCount = snapshot.CCount,
            DCount = snapshot.DCount,
            Session = session
        };

        copy.DCount++;

        return new ValueTask<LetterCounts>(copy);
    }

    public Session? Session { get; set; }

    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }

    public bool ShouldDelete(FullStop stop) => stop.ShouldStop;
}

public record FullStop(bool ShouldStop);

public class LetterCounts2
{
    public LetterCounts2()
    {
    }

    public LetterCounts2(IEvent<StartLetters> letters)
    {
        ACount = letters.Data.A;
        BCount = letters.Data.B;
    }

    public void Apply(AEvent e) => ACount++;
    
    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }
}

public class LetterCounts3
{
    public LetterCounts3()
    {
    }

    public static LetterCounts3 Create(StartLetters letters)
    {
        return new LetterCounts3
        {
            ACount = letters.A,
            BCount = letters.B
        };
    }

    public void Apply(AEvent e) => ACount++;
    
    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }
}

public class LetterCounts4
{
    public LetterCounts4()
    {
    }

    public static LetterCounts4 Create(IEvent<StartLetters> letters)
    {
        return new LetterCounts4
        {
            ACount = letters.Data.A,
            BCount = letters.Data.B
        };
    }

    public void Apply(AEvent e) => ACount++;
    
    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }
}

public record StartLetters(int A, int B);