#if NET11_0_OR_GREATER
using System.Runtime.CompilerServices;
using EventTests.Projections;

namespace EventTests.Unions;

// jasperfx#1053 fixtures. Declared at namespace level so the event type names are the plain
// snake_case of the case names (a nested type's name carries its declaring type).

public record UnionDeposited(decimal Amount);
public record UnionWithdrawn(decimal Amount, string Reason);
public record UnionFrozen(string By);
public record UnionOpened(string Owner);

public union AccountEvent(UnionDeposited, UnionWithdrawn, UnionFrozen);

// A union whose cases include another union.
public union AccountLifecycle(UnionOpened, AccountEvent);

// A hand-written union: no keyword, just [Union] + IUnion and one public constructor per case. STJ and
// JasperFx treat it exactly like a keyword union.
[Union]
public readonly struct LedgerEvent : IUnion
{
    public LedgerEvent(UnionDeposited value) => Value = value;
    public LedgerEvent(UnionWithdrawn value) => Value = value;

    public object? Value { get; }
}

public partial class UnionAccount
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }
    public string? FrozenBy { get; set; }
    public List<string> Handled { get; } = new();

    public void Apply(AccountEvent e)
    {
        switch (e)
        {
            case UnionDeposited d:
                Balance += d.Amount;
                Handled.Add("union:deposited");
                break;
            case UnionWithdrawn w:
                Balance -= w.Amount;
                Handled.Add("union:withdrawn");
                break;
            case UnionFrozen f:
                FrozenBy = f.By;
                Handled.Add("union:frozen");
                break;
        }
    }
}

// Apply(union) beside an Apply for one of its cases: the explicit case handler has to win.
public partial class PrecedenceAccount
{
    public Guid Id { get; set; }
    public List<string> Handled { get; } = new();

    public void Apply(AccountEvent e) => Handled.Add("union:" + e.Value!.GetType().Name);

    public void Apply(UnionFrozen e) => Handled.Add("explicit:" + e.By);
}

public partial class LedgerAccount
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }

    public void Apply(LedgerEvent e)
    {
        Balance += e.Value switch
        {
            UnionDeposited d => d.Amount,
            UnionWithdrawn w => -w.Amount,
            _ => 0
        };
    }
}

public partial class LifecycleAccount
{
    public Guid Id { get; set; }
    public string? Owner { get; set; }
    public decimal Balance { get; set; }

    public void Apply(AccountLifecycle e)
    {
        switch (e.Value)
        {
            case UnionOpened o:
                Owner = o.Owner;
                break;
            case AccountEvent inner when inner.Value is UnionDeposited d:
                Balance += d.Amount;
                break;
            case AccountEvent inner when inner.Value is UnionWithdrawn w:
                Balance -= w.Amount;
                break;
        }
    }
}

// Apply(union) declared on a projection class rather than on the aggregate.
public class ProjectedAccount
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }
}

public partial class ProjectedAccountProjection : SingleStreamProjection<ProjectedAccount, Guid>
{
    public void Apply(ProjectedAccount account, AccountEvent e)
    {
        if (e.Value is UnionDeposited d) account.Balance += d.Amount;
        if (e.Value is UnionWithdrawn w) account.Balance -= w.Amount;
    }
}

// Unsupported shapes: the projection would listen for the cases and dispatch none of them, so
// AssembleAndAssertValidity has to refuse these.
public class CreateFromUnionProjection : SingleStreamProjection<ProjectedAccount, Guid>
{
    public ProjectedAccount Create(AccountEvent e) => new();

    public void Apply(ProjectedAccount account, UnionDeposited e) => account.Balance += e.Amount;
}

public class WrappedUnionApplyProjection : SingleStreamProjection<ProjectedAccount, Guid>
{
    public void Apply(ProjectedAccount account, JasperFx.Events.IEvent<AccountEvent> e)
    {
    }
}
#endif
