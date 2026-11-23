namespace JasperFx.Events;

/// <summary>
///     What <see cref="IEventDatabase.FetchAggregateStreamStatsAsync" /> should report (jasperfx#1282).
/// </summary>
/// <remarks>
///     The defaults are a reasonable first look for a monitoring view: stream counts above 100, 1,000 and
///     10,000 events, and the ten longest streams of each aggregate type.
/// </remarks>
public sealed record AggregateStreamStatsRequest
{
    /// <summary>
    ///     The thresholds the default request reports length buckets for.
    /// </summary>
    public static readonly IReadOnlyList<long> DefaultLengthThresholds = [100, 1_000, 10_000];

    /// <summary>
    ///     The number of longest streams the default request reports per aggregate type.
    /// </summary>
    public const int DefaultLongestStreams = 10;

    private readonly IReadOnlyList<long> _lengthThresholds = DefaultLengthThresholds;
    private readonly int _longestStreams = DefaultLongestStreams;

    /// <summary>
    ///     Tenant to scope the counts to. Null counts every stream in the database. A non-null tenant narrows
    ///     the counts only where streams carry a tenant, that is under conjoined tenancy, by the same rule as
    ///     <see cref="IEventDatabase.FetchFirstEventTimestampsAfterAsync" />.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    ///     Count archived streams too. False by default, because every event read leaves archived streams out
    ///     unless the caller opts in. A store that does not archive streams ignores this.
    /// </summary>
    public bool IncludeArchived { get; init; }

    /// <summary>
    ///     The stream versions to report <see cref="AggregateStreamStats.LengthBuckets" /> for: one bucket per
    ///     threshold, counting the streams whose version is at least that threshold. Kept sorted ascending and
    ///     without duplicates, whatever order they are given in. Empty asks for no buckets.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A threshold is zero or negative.</exception>
    public IReadOnlyList<long> LengthThresholds
    {
        get => _lengthThresholds;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            foreach (var threshold in value)
            {
                if (threshold <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(LengthThresholds), threshold,
                        "A stream length threshold must be greater than zero.");
                }
            }

            _lengthThresholds = value.Distinct().Order().ToArray();
        }
    }

    /// <summary>
    ///     How many of the longest streams to report per aggregate type, in
    ///     <see cref="AggregateStreamStats.LongestStreams" />. Zero asks for none, which saves the store the
    ///     ranking.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int LongestStreams
    {
        get => _longestStreams;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(LongestStreams));
            _longestStreams = value;
        }
    }
}

/// <summary>
///     The streams of one aggregate type in one database, from
///     <see cref="IEventDatabase.FetchAggregateStreamStatsAsync" /> (jasperfx#1282). What a monitoring tool
///     reports event-stream size from, and what stream compaction advice starts from.
/// </summary>
/// <param name="AggregateTypeName">
///     The aggregate type alias the streams are tagged with in storage. Null for the streams that were started
///     without an aggregate type.
/// </param>
/// <param name="AggregateType">
///     The type <paramref name="AggregateTypeName" /> resolves to in this deployment. Null for untagged
///     streams, and for an alias this deployment does not know, such as one written by another service or by
///     an older version of this one. Those streams are still counted.
/// </param>
/// <param name="StreamCount">How many streams carry this aggregate type.</param>
/// <param name="TotalEvents">
///     The sum of those streams' versions. A stream's version counts every event ever appended to it, so this
///     is not reduced by compaction, archiving events or moving them to cold storage.
/// </param>
/// <param name="MaxVersion">The highest version among those streams.</param>
/// <param name="LengthBuckets">
///     One bucket per <see cref="AggregateStreamStatsRequest.LengthThresholds" />, in the same ascending order,
///     including the thresholds no stream reaches.
/// </param>
/// <param name="LongestStreams">
///     At most <see cref="AggregateStreamStatsRequest.LongestStreams" /> streams, the longest first. Which of
///     several streams of equal version make the cut is up to the store.
/// </param>
public sealed record AggregateStreamStats(
    string? AggregateTypeName,
    Type? AggregateType,
    long StreamCount,
    long TotalEvents,
    long MaxVersion,
    IReadOnlyList<StreamLengthBucket> LengthBuckets,
    IReadOnlyList<AggregateStreamLength> LongestStreams)
{
    /// <summary>
    ///     The mean stream version, or zero when there are no streams.
    /// </summary>
    public double AverageVersion => StreamCount == 0 ? 0 : (double)TotalEvents / StreamCount;
}

/// <summary>
///     How many streams of an aggregate type have a version of at least <paramref name="AtLeastVersion" />.
///     Buckets are cumulative: a stream of version 5,000 counts toward the 100 and the 1,000 buckets.
/// </summary>
/// <param name="AtLeastVersion">The threshold, from <see cref="AggregateStreamStatsRequest.LengthThresholds" />.</param>
/// <param name="StreamCount">How many streams reach it.</param>
public sealed record StreamLengthBucket(long AtLeastVersion, long StreamCount);

/// <summary>
///     One of the longest streams of an aggregate type.
/// </summary>
/// <param name="Id">The stream id under Guid stream identity, <see cref="Guid.Empty" /> otherwise.</param>
/// <param name="Key">The stream key under string stream identity, null otherwise.</param>
/// <param name="TenantId">
///     The stream's tenant, as <see cref="StreamState.TenantId" /> reports it. Under conjoined tenancy the same
///     id in two tenants is two streams, and this tells them apart.
/// </param>
/// <param name="Version">The stream's version.</param>
public sealed record AggregateStreamLength(Guid Id, string? Key, string? TenantId, long Version);
