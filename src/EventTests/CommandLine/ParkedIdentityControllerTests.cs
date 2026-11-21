using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.CommandLine;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;

namespace EventTests.CommandLine;

/// <summary>
/// marten#6003: <c>projections parked</c> over the IEventDatabase parked-identity seam.
/// </summary>
public class ParkedIdentityControllerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly List<ParkedIdentity> theRows = new();
    private readonly List<ParkedActionRequest> theRequests = new();
    private readonly IEventDatabase theDatabase = Substitute.For<IEventDatabase>();
    private readonly IEventStore theStore = Substitute.For<IEventStore>();
    private readonly RecordingParkedView theView = new();
    private readonly ParkedIdentityController theController;
    private ProjectionProgressRow? theProgression;

    public ParkedIdentityControllerTests()
    {
        var subject = new Uri("marten://main");

        var usage = new EventStoreUsage
        {
            SubjectUri = subject,
            Database = new DatabaseUsage
            {
                Cardinality = DatabaseCardinality.Single,
                MainDatabase = new DatabaseDescriptor { Identifier = "main" }
            }
        };

        usage.Subscriptions.Add(new SubscriptionDescriptor(SubscriptionType.SingleStreamProjection)
        {
            Name = "Trips",
            Lifecycle = ProjectionLifecycle.Async,
            ShardNames = [new ShardName("Trips")]
        });

        theStore.Subject.Returns(subject);
        theStore.TryCreateUsage(Arg.Any<CancellationToken>()).Returns(usage);
        theStore.AllDatabases().Returns(new ValueTask<IReadOnlyList<IEventDatabase>>([theDatabase]));

        theDatabase.Identifier.Returns("main");
        theDatabase.FetchParkedIdentitiesAsync(Arg.Any<ParkedIdentityQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<ParkedIdentity>)theRows
                .Where(x => x.ProjectionName == call.ArgAt<ParkedIdentityQuery>(0).ProjectionName).ToList());

        theDatabase.RequestParkedActionAsync(Arg.Any<ParkedActionRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.ArgAt<ParkedActionRequest>(0);
                theRequests.Add(request);
                return theRows.Count(x => request.IdentityText == null || x.IdentityText == request.IdentityText);
            });

        theDatabase.ReadProjectionProgressAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<ProjectionProgressRow?>(theProgression));

        theController = new ParkedIdentityController([theStore], theView);
    }

    private ParkedIdentity aParkedRow(string identity) => new()
    {
        ProjectionName = "Trips",
        ShardKey = "All",
        TenantId = "*DEFAULT*",
        Identity = identity,
        IdentityText = identity,
        IdentityType = "string",
        FirstFailingSequence = 12,
        Category = ShardFailureCategory.ApplyEvent,
        ExceptionMessage = "boom",
        ParkedAt = DateTimeOffset.UtcNow
    };

    private Task<bool> execute(ProjectionInput input)
    {
        input.Action = ProjectionAction.parked;
        return theController.ExecuteAsync(input, Token);
    }

    [Fact]
    public async Task lists_the_parked_identities()
    {
        theRows.Add(aParkedRow("trip-1"));

        (await execute(new ProjectionInput())).ShouldBeTrue();

        theView.Parked.ShouldHaveSingleItem().Parked.IdentityText.ShouldBe("trip-1");
    }

    [Fact]
    public async Task lists_the_natural_key_the_store_filled_in()
    {
        theRows.Add(aParkedRow("trip-1") with { NaturalKey = "TRIP-1001" });
        theRows.Add(aParkedRow("trip-2"));

        (await execute(new ProjectionInput())).ShouldBeTrue();

        theView.Parked.Select(x => x.Parked.NaturalKey).ShouldBe(["TRIP-1001", null]);
    }

    [Fact]
    public async Task a_replay_by_identity_still_names_the_stream_id_when_there_is_a_natural_key()
    {
        theRows.Add(aParkedRow("trip-1") with { NaturalKey = "TRIP-1001" });

        (await execute(new ProjectionInput { ProjectionFlag = "Trips", ReplayFlag = true, IdentityFlag = "trip-1" }))
            .ShouldBeTrue();

        theRequests.ShouldHaveSingleItem().IdentityText.ShouldBe("trip-1");
    }

    [Fact]
    public async Task says_when_nothing_is_parked()
    {
        (await execute(new ProjectionInput())).ShouldBeTrue();
        theView.Messages.ShouldContain("Nothing is parked.");
    }

    [Fact]
    public async Task a_replay_of_one_identity_is_a_durable_request()
    {
        theRows.Add(aParkedRow("trip-1"));
        theProgression = new ProjectionProgressRow("Trips", null, 20, "Running", DateTimeOffset.UtcNow)
        {
            LastUpdated = DateTimeOffset.UtcNow
        };

        (await execute(new ProjectionInput { ProjectionFlag = "Trips", ReplayFlag = true, IdentityFlag = "trip-1" }))
            .ShouldBeTrue();

        var request = theRequests.ShouldHaveSingleItem();
        request.Action.ShouldBe(ParkedAction.Replay);
        request.IdentityText.ShouldBe("trip-1");
        theView.Messages.ShouldNotContain(x => x.Contains("No node is running"));
    }

    [Fact]
    public async Task says_when_no_node_runs_the_shard()
    {
        theRows.Add(aParkedRow("trip-1"));
        theProgression = new ProjectionProgressRow("Trips", null, 20, "Stopped", DateTimeOffset.UtcNow.AddHours(-1));

        (await execute(new ProjectionInput { ProjectionFlag = "Trips", ReplayFlag = true, AllFlag = true }))
            .ShouldBeTrue();

        theRequests.ShouldHaveSingleItem().IdentityText.ShouldBeNull();
        theView.Messages.ShouldContain(x => x.Contains("The request runs when the shard next starts"));
    }

    [Fact]
    public async Task replay_needs_exactly_one_choice()
    {
        (await execute(new ProjectionInput { ProjectionFlag = "Trips", ReplayFlag = true })).ShouldBeFalse();
        (await execute(new ProjectionInput
        {
            ProjectionFlag = "Trips", ReplayFlag = true, AllFlag = true, OldestFlag = 2
        })).ShouldBeFalse();

        theRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_discard_names_one_identity_and_asks_first()
    {
        theRows.Add(aParkedRow("trip-1"));

        (await execute(new ProjectionInput { ProjectionFlag = "Trips", DiscardFlag = true })).ShouldBeFalse();

        theView.Answer = false;
        (await execute(new ProjectionInput { ProjectionFlag = "Trips", DiscardFlag = true, IdentityFlag = "trip-1" }))
            .ShouldBeFalse();
        theRequests.ShouldBeEmpty();

        (await execute(new ProjectionInput
        {
            ProjectionFlag = "Trips", DiscardFlag = true, IdentityFlag = "trip-1", YesFlag = true
        })).ShouldBeTrue();

        theRequests.ShouldHaveSingleItem().Action.ShouldBe(ParkedAction.Discard);
    }

    [Fact]
    public async Task a_request_that_matches_nothing_fails()
    {
        (await execute(new ProjectionInput { ProjectionFlag = "Trips", ReplayFlag = true, IdentityFlag = "nope" }))
            .ShouldBeFalse();
    }

    private class RecordingParkedView : IParkedIdentityView
    {
        public List<ParkedIdentityRow> Parked { get; } = new();
        public List<string> Messages { get; } = new();
        public List<string> Errors { get; } = new();
        public bool Answer { get; set; } = true;

        public void WriteParked(IReadOnlyList<ParkedIdentityRow> rows) => Parked.AddRange(rows);
        public void WriteMessage(string message) => Messages.Add(message);
        public void WriteError(string message) => Errors.Add(message);
        public bool Confirm(string question) => Answer;
    }
}
