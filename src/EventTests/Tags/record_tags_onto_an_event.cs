using System.Text.Json;
using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Tags;
using Shouldly;

namespace EventTests.Tags;

public readonly record struct LedgerTag(Guid Value);

public record RegionTag(int Value);

public record LedgerPosted(int Amount);

// jasperfx#1254: WithRecordMetadata puts a record's tags back onto the event a store builds from it, so a projection
// grouped by tag groups a stepped event. The key is what EventRecord.Tags documents: the tag type's CLR name, or its
// table suffix, resolved by FindByTagName.
public class record_tags_onto_an_event
{
    private static readonly ITagTypeRegistration[] Registered =
    [
        TagTypeRegistration.FromUnwrapper<LedgerTag, Guid>(x => x.Value, "ledger"),
        TagTypeRegistration.FromUnwrapper<RegionTag, int>(x => x.Value, "region"),
        TagTypeRegistration.FromUnwrapper<StudentId, string>(x => x.Value, "student")
    ];

    private static EventRecord recordWith(IReadOnlyDictionary<string, string?> tags)
        => new(Guid.NewGuid(), 1, 1, Guid.NewGuid().ToString(), "ledger_posted",
            JsonSerializer.SerializeToElement(new LedgerPosted(5)), null, DateTimeOffset.UtcNow, null,
            tags.ToDictionary(x => x.Key, x => x.Value!));

    private static IEvent stamp(EventRecord record)
        => new Event<LedgerPosted>(new LedgerPosted(5)).WithRecordMetadata(record, StreamIdentity.AsGuid, Registered);

    [Fact]
    public void a_tag_named_by_its_clr_name_comes_back_as_the_tag_type()
    {
        var ledger = Guid.NewGuid();

        var @event = stamp(recordWith(new Dictionary<string, string?>
        {
            ["LedgerTag"] = ledger.ToString(), ["RegionTag"] = "42", ["StudentId"] = "STU-1"
        }));

        @event.Tags.ShouldBe([
            new EventTag(typeof(LedgerTag), new LedgerTag(ledger)),
            new EventTag(typeof(RegionTag), new RegionTag(42)),
            new EventTag(typeof(StudentId), new StudentId("STU-1"))
        ], ignoreOrder: true);
    }

    [Fact]
    public void a_tag_named_by_its_table_suffix_comes_back_as_the_tag_type()
    {
        var ledger = Guid.NewGuid();

        var @event = stamp(recordWith(new Dictionary<string, string?>
        {
            ["ledger"] = ledger.ToString(), ["REGION"] = "7"
        }));

        @event.Tags.ShouldBe([
            new EventTag(typeof(LedgerTag), new LedgerTag(ledger)),
            new EventTag(typeof(RegionTag), new RegionTag(7))
        ], ignoreOrder: true);
    }

    [Fact]
    public void the_rest_of_the_record_is_still_stamped()
    {
        var record = recordWith(new Dictionary<string, string?> { ["region"] = "1" });

        var @event = stamp(record);

        @event.Id.ShouldBe(record.EventId);
        @event.StreamId.ToString().ShouldBe(record.StreamId);
        @event.EventTypeName.ShouldBe(record.EventTypeName);
    }

    [Fact]
    public void a_name_no_tag_type_is_registered_under_is_left_off()
    {
        var @event = stamp(recordWith(new Dictionary<string, string?>
        {
            ["no_such_tag"] = "anything", ["region"] = "3"
        }));

        @event.Tags.ShouldBe([new EventTag(typeof(RegionTag), new RegionTag(3))]);
    }

    [Fact]
    public void a_null_value_is_no_tag()
    {
        var @event = stamp(recordWith(new Dictionary<string, string?> { ["region"] = null }));

        @event.Tags.ShouldBeNull();
    }

    [Fact]
    public void a_value_that_will_not_parse_throws_naming_the_tag()
    {
        var record = recordWith(new Dictionary<string, string?> { ["ledger"] = "not-a-guid" });

        var e = Should.Throw<ArgumentException>(() => stamp(record));

        e.ParamName.ShouldBe("record");
        e.Message.ShouldContain("'ledger'");
        e.Message.ShouldContain("not-a-guid");
        e.Message.ShouldContain(nameof(LedgerTag));
    }

    [Fact]
    public void a_number_out_of_range_throws_naming_the_tag()
    {
        var record = recordWith(new Dictionary<string, string?> { ["region"] = "99999999999" });

        Should.Throw<ArgumentException>(() => stamp(record)).Message.ShouldContain("'region'");
    }

    [Fact]
    public void a_tag_the_event_already_carries_is_not_added_twice()
    {
        var ledger = Guid.NewGuid();
        var @event = new Event<LedgerPosted>(new LedgerPosted(5));
        @event.AddTag(new LedgerTag(ledger));

        @event.WithRecordMetadata(recordWith(new Dictionary<string, string?> { ["ledger"] = ledger.ToString() }),
            StreamIdentity.AsGuid, Registered);

        @event.Tags.ShouldBe([new EventTag(typeof(LedgerTag), new LedgerTag(ledger))]);
    }

    [Fact]
    public void a_store_with_no_tag_types_leaves_the_tags_off()
    {
        var @event = new Event<LedgerPosted>(new LedgerPosted(5)).WithRecordMetadata(
            recordWith(new Dictionary<string, string?> { ["ledger"] = Guid.NewGuid().ToString() }),
            StreamIdentity.AsGuid, []);

        @event.Tags.ShouldBeNull();
    }

    [Fact]
    public void the_overload_without_registrations_still_leaves_the_tags_off()
    {
        var @event = new Event<LedgerPosted>(new LedgerPosted(5)).WithRecordMetadata(
            recordWith(new Dictionary<string, string?> { ["ledger"] = Guid.NewGuid().ToString() }),
            StreamIdentity.AsGuid);

        @event.Tags.ShouldBeNull();
    }
}
