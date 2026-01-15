using JasperFx;
using JasperFx.Events;

namespace EventTests;

/// <summary>
/// Basically an ObjectMother for the A/B/C/D/Event types
/// </summary>
public static class LetterEvents
{
    public static IEnumerable<IEvent> ToLetterEventsWithWrapper(this string text)
    {
        foreach (var character in text.ToLowerInvariant())
        {
            switch (character)
            {
                case 'a':
                    yield return Event.For(new AEvent());
                    break;

                case 'b':
                    yield return Event.For(new BEvent());
                    break;

                case 'c':
                    yield return Event.For(new CEvent());
                    break;

                case 'd':
                    yield return Event.For(new DEvent());
                    break;

                case 'e':
                    yield return Event.For(new EEvent());
                    break;
            }
        }
    }
    
    public static IEnumerable<object> ToLetterEvents(this string text)
    {
        foreach (var character in text.ToLowerInvariant())
        {
            switch (character)
            {
                case 'a':
                    yield return new AEvent();
                    break;

                case 'b':
                    yield return new BEvent();
                    break;

                case 'c':
                    yield return new CEvent();
                    break;

                case 'd':
                    yield return new DEvent();
                    break;

                case 'e':
                    yield return new EEvent();
                    break;
            }
        }
    }
}

public class MyAggregate
{
    public Guid Id { get; set; }

    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }
    public int ECount { get; set; }

    public string Created { get; set; }
    public string UpdatedBy { get; set; }
    public Guid EventId { get; set; }

    protected bool Equals(MyAggregate other)
    {
        return Id.Equals(other.Id) && ACount == other.ACount && BCount == other.BCount && CCount == other.CCount &&
               DCount == other.DCount && ECount == other.ECount;
    }

    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != this.GetType())
        {
            return false;
        }

        return Equals((MyAggregate)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Id, ACount, BCount, CCount, DCount, ECount);
    }

    public override string ToString()
    {
        return
            $"{nameof(Id)}: {Id}, {nameof(ACount)}: {ACount}, {nameof(BCount)}: {BCount}, {nameof(CCount)}: {CCount}, {nameof(DCount)}: {DCount}, {nameof(ECount)}: {ECount}";
    }
}

public interface ITabulator
{
    void Apply(MyAggregate aggregate);
}

public class AEvent: ITabulator
{
    // Necessary for a couple tests. Let it go.
    public Guid Id { get; set; }

    public void Apply(MyAggregate aggregate)
    {
        aggregate.ACount++;
    }

    public Guid Tracker { get; } = Guid.NewGuid();
}

public class BEvent: ITabulator
{
    public void Apply(MyAggregate aggregate)
    {
        aggregate.BCount++;
    }
}

public class CEvent: ITabulator
{
    public void Apply(MyAggregate aggregate)
    {
        aggregate.CCount++;
    }
}

public class DEvent: ITabulator
{
    public void Apply(MyAggregate aggregate)
    {
        aggregate.DCount++;
    }
}

public class EEvent
{
}

public class CreateEvent
{
    public int A { get; }
    public int B { get; }
    public int C { get; }
    public int D { get; }

    public CreateEvent(int a, int b, int c, int d)
    {
        A = a;
        B = b;
        C = c;
        D = d;
    }
}

public class SimpleAggregate : IRevisioned
{
    // This will be the aggregate version
    public int Version { get; set; }

    public Guid Id { get;
        set; }

    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }
    public int ECount { get; set; }

    public void Apply(AEvent _)
    {
        ACount++;
    }

    public void Apply(BEvent _)
    {
        BCount++;
    }

    public void Apply(CEvent _)
    {
        CCount++;
    }

    public void Apply(DEvent _)
    {
        DCount++;
    }

    public void Apply(EEvent _)
    {
        ECount++;
    }

}



