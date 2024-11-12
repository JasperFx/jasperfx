namespace EventStoreTests;


public class MyAggregate
{
    // This will be the aggregate version
    public int Version { get; set; }


    public Guid Id { get; set; }

    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }
    public int ECount { get; set; }

    public string Created { get; set; }
    public string UpdatedBy { get; set; }
    public Guid EventId { get; set; }
}


public interface ITabulator
{
    void Apply(MyAggregate aggregate);
}

public class AEvent : ITabulator
{
    // Necessary for a couple tests. Let it go.
    public Guid Id { get; set; } = Guid.NewGuid();

    public void Apply(MyAggregate aggregate)
    {
        aggregate.ACount++;
    }

    public Guid Tracker { get; } = Guid.NewGuid();
}

public class BEvent : ITabulator
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public void Apply(MyAggregate aggregate)
    {
        aggregate.BCount++;
    }
}

public class CEvent : ITabulator
{
    public void Apply(MyAggregate aggregate)
    {
        aggregate.CCount++;
    }
}

public class DEvent : ITabulator
{
    public void Apply(MyAggregate aggregate)
    {
        aggregate.DCount++;
    }
}
public class EEvent {}
