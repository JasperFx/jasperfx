using System.Text.Json;
using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.InMemory;
using JasperFx.Events.InMemory.Projections;
using JasperFx.Events.Projections;
using Shouldly;

namespace InMemoryEventStoreTests;

public readonly record struct AccountTag(Guid Value);

public record AccountPosted(int Amount);

public record AccountBatchPosted(int[] Amounts);

public class TaggedAccount
{
    public Guid Id { get; set; }
    public int Balance { get; set; }
    public int Postings { get; set; }
}

/// <summary>Grouped only by tag, with a fan-out, so a stepped event reaches an identity only through its tags.</summary>
public partial class TaggedAccountProjection : MultiStreamProjection<TaggedAccount, Guid>
{
    public TaggedAccountProjection()
    {
        Name = "TaggedAccount";
        IdentityFromTag<AccountTag>();
        IncludeType<AccountPosted>();
        IncludeType<AccountBatchPosted>();
        FanOut<AccountBatchPosted, AccountPosted>(x => x.Amounts.Select(a => new AccountPosted(a)));
    }

    public void Apply(AccountPosted e, TaggedAccount account)
    {
        account.Balance += e.Amount;
        account.Postings++;
    }
}

/// <summary>
/// jasperfx#1254: stepping an <c>IdentityFromTag</c> projection from wire records, through the in-memory store's own
/// projection graph and event registry. The <c>toEvent</c> here is the one a store's step-through hands
/// <see cref="ProjectionGraph{TProjection,TOperations,TQuerySession}.StepThrough" />, and before the fix
/// <c>WithRecordMetadata</c> left the record's tags behind, so the slicer grouped nothing.
/// </summary>
public class stepping_a_tag_grouped_projection_from_records
{
    private static readonly ProjectionStateSerializer States = new(
        state => JsonSerializer.SerializeToElement(state, state.GetType()),
        (json, type) => json.Deserialize(type));

    private readonly InMemoryDocumentStore theStore = new();
    private readonly ProjectionStepThrough<IInMemoryQuerySession> theStepThrough;
    private long theSequence;

    public stepping_a_tag_grouped_projection_from_records()
    {
        theStore.Events.AddEventType(typeof(AccountPosted));
        theStore.Events.AddEventType(typeof(AccountBatchPosted));
        theStore.Events.RegisterTagType<AccountTag, Guid>(x => x.Value, "account");
        theStore.Projections.Add(new TaggedAccountProjection(), ProjectionLifecycle.Async);

        theStepThrough = theStore.Projections.StepThrough(toEvent, States);
    }

    // A store's toEvent: its own event type lookup and deserialization, then WithRecordMetadata with its tag types
    private IEvent? toEvent(EventRecord record)
    {
        var type = new[] { typeof(AccountPosted), typeof(AccountBatchPosted) }
            .SingleOrDefault(x => theStore.Events.EventMappingFor(x).EventTypeName == record.EventTypeName);

        return type == null
            ? null
            : theStore.Events.BuildEvent(record.Data.Deserialize(type)!)
                .WithRecordMetadata(record, theStore.Events.StreamIdentity, theStore.Events.TagTypes);
    }

    private EventRecord recordOf(object body, IReadOnlyDictionary<string, string> tags)
    {
        var sequence = ++theSequence;
        return new EventRecord(Guid.NewGuid(), sequence, 1, Guid.NewGuid().ToString(),
            theStore.Events.EventMappingFor(body.GetType()).EventTypeName,
            JsonSerializer.SerializeToElement(body, body.GetType()), null, DateTimeOffset.UtcNow, null, tags);
    }

    private static int balance(JsonElement? state) => state!.Value.GetProperty(nameof(TaggedAccount.Balance)).GetInt32();

    [Fact]
    public async Task stepping_one_identity_from_records_groups_by_the_record_tags()
    {
        var account = Guid.NewGuid();
        var other = Guid.NewGuid();

        // The issue's repro, keyed by the registered table suffix
        var first = recordOf(new AccountPosted(10), new Dictionary<string, string> { ["account"] = account.ToString() });
        var theirs = recordOf(new AccountPosted(100), new Dictionary<string, string> { ["account"] = other.ToString() });

        // Keyed by the tag type's CLR name, which is what a store writes
        var second = recordOf(new AccountPosted(5), new Dictionary<string, string> { ["AccountTag"] = account.ToString() });

        var timeline = await theStepThrough.RunMultiStreamProjectionForIdentityAsync("TaggedAccount", account,
            [first, theirs, second], null, theStore.QuerySession(), CancellationToken.None);

        timeline.Steps.Select(x => x.Event).ShouldBe([first, second]);
        balance(timeline.Steps[0].After).ShouldBe(10);
        balance(timeline.FinalState).ShouldBe(15);
        timeline.FinalState!.Value.GetProperty(nameof(TaggedAccount.Postings)).GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task stepping_every_identity_from_records_finds_each_tag_as_an_identity()
    {
        var account = Guid.NewGuid();
        var other = Guid.NewGuid();

        var result = await theStepThrough.RunMultiStreamProjectionAsync("TaggedAccount",
        [
            recordOf(new AccountPosted(10), new Dictionary<string, string> { ["account"] = account.ToString() }),
            recordOf(new AccountPosted(100), new Dictionary<string, string> { ["account"] = other.ToString() }),
            recordOf(new AccountPosted(5), new Dictionary<string, string> { ["account"] = account.ToString() })
        ], theStore.QuerySession(), CancellationToken.None);

        result.AggregatesByIdentity.Keys.ShouldBe([account.ToString(), other.ToString()], ignoreOrder: true);
        balance(result.AggregatesByIdentity[account.ToString()].FinalState).ShouldBe(15);
        balance(result.AggregatesByIdentity[other.ToString()].FinalState).ShouldBe(100);
    }

    // jasperfx#1257 alongside: a fan-out event is grouped by the typed tags its source got from the record, and its
    // step reports the source record's own tag text
    [Fact]
    public async Task a_fan_out_from_a_tagged_record_steps_under_the_tag_identity_with_the_record_tags()
    {
        var account = Guid.NewGuid();
        var tags = new Dictionary<string, string> { ["account"] = account.ToString() };
        var batch = recordOf(new AccountBatchPosted([1, 2, 3]), tags);

        var timeline = await theStepThrough.RunMultiStreamProjectionForIdentityAsync("TaggedAccount", account,
            [batch], null, theStore.QuerySession(), CancellationToken.None);

        timeline.Steps.Count.ShouldBe(4);
        timeline.Steps.Select(x => x.Event.Tags).ShouldAllBe(x => x == tags);
        balance(timeline.FinalState).ShouldBe(6);
    }

    [Fact]
    public async Task a_record_tag_that_will_not_parse_fails_the_step_naming_the_tag()
    {
        var record = recordOf(new AccountPosted(1), new Dictionary<string, string> { ["account"] = "not-a-guid" });

        var e = await Should.ThrowAsync<ArgumentException>(() => theStepThrough.RunMultiStreamProjectionAsync(
            "TaggedAccount", [record], theStore.QuerySession(), CancellationToken.None));

        e.Message.ShouldContain("'account'");
    }
}
