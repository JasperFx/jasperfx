using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using JasperFx.Linq;
using Shouldly;

namespace CoreTests.Linq;

// TimeZoneInfo.Local is process-wide, so the tests that replace it run on their own, after every
// parallel collection, rather than beside a test that reads the real one.
[CollectionDefinition(nameof(LocalTimeZoneCollection), DisableParallelization = true)]
public class LocalTimeZoneCollection;

/// <summary>
/// Date text with no offset is read as UTC whatever the host's zone is. CI runs in UTC, where text read in
/// local time comes out right too, so these pin the host to UTC+2 for their duration.
/// </summary>
[Collection(nameof(LocalTimeZoneCollection))]
public class DynamicQueryLocalTimeZoneTests : IDisposable
{
    public class Shipment
    {
        public DateTime ShippedOn { get; set; }
        public DateTime? DeliveredOn { get; set; }
        public DateTimeOffset PlacedAt { get; set; }
    }

    private static readonly TimeSpan HostOffset = TimeSpan.FromHours(2);
    private static readonly TimeZoneInfo HostZone =
        TimeZoneInfo.CreateCustomTimeZone("JasperFx tests UTC+02", HostOffset, "UTC+02", "UTC+02");
    private static readonly DateTime NewYear = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public DynamicQueryLocalTimeZoneTests() => useLocalTimeZone(HostZone);

    public void Dispose() => TimeZoneInfo.ClearCachedData();

    [Fact]
    public void the_host_is_pinned_east_of_utc()
    {
        // Without this the cases below could pass on a UTC host whether the dates are read as UTC or not.
        TimeZoneInfo.Local.Id.ShouldBe(HostZone.Id);
        DateTimeOffset.Parse("2024-01-01", CultureInfo.InvariantCulture).Offset.ShouldBe(HostOffset);
    }

    [Theory]
    [InlineData("ShippedOn > \"2024-01-01\"")]
    [InlineData("\"2024-01-01\" < ShippedOn")]
    [InlineData("DeliveredOn > \"2024-01-01\"")]
    public void a_date_literal_compared_with_a_datetime_is_read_as_utc(string where)
    {
        var bound = boundDate(where);

        bound.ShouldBe(NewYear);
        bound.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Theory]
    [InlineData("ShippedOn > @0")]
    [InlineData("DeliveredOn > @0")]
    public void a_date_string_argument_compared_with_a_datetime_is_read_as_utc(string where)
        => boundDate(where, "2024-01-01").ShouldBe(NewYear);

    [Theory]
    [InlineData("2024-01-01T02:00:00+02:00")]
    [InlineData("2024-01-01T00:00:00Z")]
    public void date_text_that_carries_its_own_offset_keeps_it(string text)
        => boundDate("ShippedOn > @0", text).ShouldBe(NewYear);

    [Fact]
    public void a_date_literal_compared_with_a_datetimeoffset_is_read_as_utc()
        => bound<DateTimeOffset>("PlacedAt > \"2024-01-01\"").ShouldBe(new DateTimeOffset(NewYear));

    private static DateTime boundDate(string where, params object?[] args) => bound<DateTime>(where, args);

    // The value Dynamic LINQ bound for the date in the text: the one date constant in the composed tree.
    private static T bound<T>(string where, params object?[] args)
    {
        var query = DynamicQuery.Apply(Array.Empty<Shipment>().AsQueryable(), new DynamicQueryText(where, null, args));
        return ConstantValues.In(query.Expression).OfType<T>().ShouldHaveSingleItem();
    }

    // TimeZoneInfo.Local has no setter and Windows ignores TZ, so the runtime's cached local zone is replaced
    // and TimeZoneInfo.ClearCachedData() puts the real one back.
    private static void useLocalTimeZone(TimeZoneInfo zone)
    {
        TimeZoneInfo.ClearCachedData();

        var cachedData = typeof(TimeZoneInfo).GetField("s_cachedData", BindingFlags.NonPublic | BindingFlags.Static)
                             ?.GetValue(null)
                         ?? throw new InvalidOperationException("TimeZoneInfo.s_cachedData is gone; pin the zone another way.");
        var local = cachedData.GetType().GetField("_localTimeZone", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new InvalidOperationException("TimeZoneInfo.CachedData._localTimeZone is gone; pin the zone another way.");

        local.SetValue(cachedData, zone);
    }

    private sealed class ConstantValues : ExpressionVisitor
    {
        private readonly List<object?> _values = [];

        public static List<object?> In(Expression expression)
        {
            var visitor = new ConstantValues();
            visitor.Visit(expression);
            return visitor._values;
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            _values.Add(node.Value);
            return node;
        }
    }
}
