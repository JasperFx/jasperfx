namespace JasperFx.Events.EventModeling;

/// <summary>
/// How an automation's schedule is expressed (jasperfx#1298).
/// </summary>
/// <remarks>
/// Goes over the wire as an integer under the STJ defaults, so append new members; never renumber.
/// </remarks>
public enum ScheduleKind
{
    /// <summary>A fixed interval: every so often.</summary>
    Interval = 0,

    /// <summary>A cron expression.</summary>
    Cron = 1,

    /// <summary>A named schedule the host defines, referred to by name only.</summary>
    Named = 2,
}

/// <summary>
/// The schedule that fires an automation slice (jasperfx#1298) — the cadence beside
/// <see cref="TriggerKind.JobScheduler"/>. See <see cref="EventModelSliceDescriptor.Schedule"/>.
/// </summary>
/// <remarks>
/// Declared with <c>Every(TimeSpan)</c>, <c>OnCron("…")</c> or <c>Scheduled("name")</c>; the derived rung
/// fills it from registered recurring schedules. Exactly one of <see cref="Every"/>, <see cref="Cron"/> and
/// <see cref="Name"/> is set, as <see cref="Kind"/> says.
/// </remarks>
/// <param name="Kind">Which form the schedule takes.</param>
/// <param name="Every">The interval, for <see cref="ScheduleKind.Interval"/>.</param>
/// <param name="Cron">The cron expression, for <see cref="ScheduleKind.Cron"/>.</param>
/// <param name="Name">The schedule's name, for <see cref="ScheduleKind.Named"/>.</param>
public sealed record ScheduleDescriptor(ScheduleKind Kind, TimeSpan? Every = null, string? Cron = null, string? Name = null)
{
    /// <summary>A schedule firing every <paramref name="interval"/>.</summary>
    public static ScheduleDescriptor EveryInterval(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "A schedule's interval has to be positive");
        }

        return new ScheduleDescriptor(ScheduleKind.Interval, Every: interval);
    }

    /// <summary>A schedule following <paramref name="expression"/>.</summary>
    public static ScheduleDescriptor OnCron(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new ArgumentException("A cron schedule needs an expression", nameof(expression));
        }

        return new ScheduleDescriptor(ScheduleKind.Cron, Cron: expression.Trim());
    }

    /// <summary>A schedule the host defines, named <paramref name="name"/>.</summary>
    public static ScheduleDescriptor Named(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A named schedule needs a name", nameof(name));
        }

        return new ScheduleDescriptor(ScheduleKind.Named, Name: name.Trim());
    }

    /// <summary>Display form: <c>every 00:05:00</c>, <c>cron 0 9 * * MON</c>, or the schedule's name.</summary>
    public override string ToString() => Kind switch
    {
        ScheduleKind.Interval => $"every {Every}",
        ScheduleKind.Cron => $"cron {Cron}",
        _ => Name ?? "scheduled",
    };
}
