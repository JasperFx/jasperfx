namespace JasperFx.Events.Daemon;

/// <summary>
/// Opt-in with <see cref="DaemonSettings.TenantLoadBudget"/>: bounds how many events this store's per-tenant agents
/// may hold in memory on this node, loaded but not yet committed, and rotates which tenants may load when the
/// budget is tight or the process is under memory pressure.
///
/// <para>
/// An agent reserves a page's worth of the budget before it loads, trims the reservation to the events it actually
/// loaded, and gives it back as the page commits. While the budget has room and nobody is waiting, loads go straight
/// through. Otherwise the agent joins a FIFO line and is woken with a reservation for exactly one page once enough is
/// given back. After that page it goes to the back of the line, so every tenant gets a turn and none falls behind
/// indefinitely. A waiting agent stays running and heartbeating; it just stops loading.
/// </para>
///
/// <para>
/// When GC memory load reaches <see cref="DaemonSettings.TenantLoadMemoryThreshold"/>, no new reservation is handed
/// out until the in-flight events drain, which tightens the effective budget whatever it is set to. A reservation is
/// always granted when nothing at all is in flight, so the store never stops making progress.
/// </para>
/// </summary>
public sealed class TenantLoadGovernor
{
    private readonly object _lock = new();
    private readonly LinkedList<Waiter> _waiting = new();
    private readonly Dictionary<object, LinkedListNode<Waiter>> _waitingByKey = new();
    private readonly Func<double> _memoryLoad;
    private long _inFlight;

    /// <param name="budget">Events that may be loaded and uncommitted at once</param>
    /// <param name="memoryLoadThreshold">
    /// Fraction of the GC's high memory load threshold at which no new reservations are handed out while anything
    /// is in flight. Null = the event budget only.
    /// </param>
    /// <param name="memoryLoad">The current memory load as that fraction. Defaults to the GC's own figures.</param>
    public TenantLoadGovernor(int budget, double? memoryLoadThreshold = null, Func<double>? memoryLoad = null)
    {
        Budget = budget;
        MemoryLoadThreshold = memoryLoadThreshold;
        _memoryLoad = memoryLoad ?? gcMemoryLoad;
    }

    public int Budget { get; }

    public double? MemoryLoadThreshold { get; }

    /// <summary>
    /// Events reserved or held by loaded, uncommitted pages
    /// </summary>
    public long InFlight
    {
        get
        {
            lock (_lock) return _inFlight;
        }
    }

    /// <summary>
    /// Agents in line for their next page
    /// </summary>
    public int Waiting
    {
        get
        {
            lock (_lock) return _waiting.Count;
        }
    }

    /// <summary>
    /// Reserve <paramref name="cost"/> events if nobody is ahead in line and there is room. Otherwise put
    /// <paramref name="key"/> in line, once, and return false: <paramref name="onGranted"/> runs later, off the
    /// caller's thread, once the reservation has been taken for it. Whoever receives a reservation must give it back
    /// with <see cref="Release"/>.
    /// </summary>
    public bool TryReserve(object key, int cost, Action onGranted)
    {
        lock (_lock)
        {
            if (_waitingByKey.ContainsKey(key))
            {
                return false;
            }

            if (_waiting.Count == 0 && fits(cost))
            {
                _inFlight += cost;
                return true;
            }

            _waitingByKey[key] = _waiting.AddLast(new Waiter(key, cost, onGranted));
            return false;
        }
    }

    /// <summary>
    /// Wait in line for a reservation of <paramref name="cost"/> events. Returns false, holding nothing, if
    /// <paramref name="token"/> fires first.
    /// </summary>
    public async Task<bool> ReserveAsync(int cost, CancellationToken token)
    {
        var granted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = new object();

        if (TryReserve(key, cost, () => granted.TrySetResult(true)))
        {
            return true;
        }

        await using (token.Register(() =>
                     {
                         if (Withdraw(key)) granted.TrySetResult(false);
                     }))
        {
            return await granted.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Take <paramref name="key"/> out of line. Returns false if it was not waiting, including when its reservation
    /// was already granted, in which case the grant callback owns giving it back.
    /// </summary>
    public bool Withdraw(object key)
    {
        List<Waiter>? granted;
        lock (_lock)
        {
            if (!_waitingByKey.Remove(key, out var node))
            {
                return false;
            }

            _waiting.Remove(node);

            // Whoever was behind it may fit now
            granted = grantWaiting();
        }

        runGrants(granted);
        return true;
    }

    /// <summary>
    /// Give back events that were reserved or held, then hand the room to whoever is first in line
    /// </summary>
    public void Release(long events)
    {
        if (events <= 0) return;

        List<Waiter>? granted;
        lock (_lock)
        {
            _inFlight = Math.Max(0, _inFlight - events);
            granted = grantWaiting();
        }

        runGrants(granted);
    }

    // Under the lock
    private bool fits(int cost)
    {
        if (_inFlight == 0)
        {
            return true;
        }

        if (_inFlight + cost > Budget)
        {
            return false;
        }

        return MemoryLoadThreshold is not { } threshold || _memoryLoad() < threshold;
    }

    // Under the lock. Strictly FIFO: the head of the line is never passed by a cheaper waiter behind it.
    private List<Waiter>? grantWaiting()
    {
        List<Waiter>? granted = null;
        while (_waiting.First is { } head && fits(head.Value.Cost))
        {
            _waiting.RemoveFirst();
            _waitingByKey.Remove(head.Value.Key);
            _inFlight += head.Value.Cost;
            (granted ??= new()).Add(head.Value);
        }

        return granted;
    }

    private static void runGrants(List<Waiter>? granted)
    {
        if (granted == null) return;

        foreach (var waiter in granted)
        {
            // Off the releasing thread: that is an agent's command loop or a batch's commit, neither of which
            // should run another agent's continuation
            ThreadPool.UnsafeQueueUserWorkItem(static w => w.OnGranted(), waiter, preferLocal: false);
        }
    }

    private static double gcMemoryLoad()
    {
        var info = GC.GetGCMemoryInfo();
        return info.HighMemoryLoadThresholdBytes > 0
            ? (double)info.MemoryLoadBytes / info.HighMemoryLoadThresholdBytes
            : 0;
    }

    private sealed record Waiter(object Key, int Cost, Action OnGranted);
}
