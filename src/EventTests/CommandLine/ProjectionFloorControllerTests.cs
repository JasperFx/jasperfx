using JasperFx.CommandLine;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.CommandLine;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;

namespace EventTests.CommandLine;

/// <summary>
/// jasperfx#1205: <c>projections floor</c> over the IEventDatabase floor seam.
/// </summary>
public class ProjectionFloorControllerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly Dictionary<(string Projection, string? Tenant), long> theFloors = new();
    private readonly IEventDatabase theDatabase = Substitute.For<IEventDatabase>();
    private readonly IEventStore theStore = Substitute.For<IEventStore>();
    private readonly RecordingFloorView theView = new();
    private readonly ProjectionFloorController theController;

    public ProjectionFloorControllerTests()
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

        usage.Subscriptions.Add(descriptor("Trips", ProjectionLifecycle.Async));
        usage.Subscriptions.Add(descriptor("Orders", ProjectionLifecycle.Inline));
        usage.Subscriptions.Add(descriptor("LiveTotals", ProjectionLifecycle.Live));

        theStore.Subject.Returns(subject);
        theStore.TryCreateUsage(Arg.Any<CancellationToken>()).Returns(usage);
        theStore.AllDatabases().Returns(new ValueTask<IReadOnlyList<IEventDatabase>>([theDatabase]));

        theDatabase.Identifier.Returns("main");

        theDatabase.FetchProjectionFloorAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => theFloors.TryGetValue((call.ArgAt<string>(0), call.ArgAt<string?>(1)), out var floor)
                ? floor
                : (long?)null);

        theDatabase.SetProjectionFloorAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<long?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var key = (call.ArgAt<string>(0), call.ArgAt<string?>(1));
                if (call.ArgAt<long?>(2) is { } floor)
                {
                    theFloors[key] = floor;
                }
                else
                {
                    theFloors.Remove(key);
                }

                return Task.CompletedTask;
            });

        theDatabase.FetchHighestEventSequenceNumber(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string?>(0) == null ? 500L : 77L);

        theController = new ProjectionFloorController([theStore], theView);
    }

    private static SubscriptionDescriptor descriptor(string name, ProjectionLifecycle lifecycle)
        => new(SubscriptionType.SingleStreamProjection)
        {
            Name = name,
            Lifecycle = lifecycle,
            ShardNames = [new ShardName(name)]
        };

    private Task<bool> execute(ProjectionInput input)
    {
        input.Action = ProjectionAction.floor;
        return theController.ExecuteAsync(input, Token);
    }

    [Fact]
    public async Task show_is_the_default_and_lists_every_projection_with_stored_state()
    {
        theFloors[("Trips", null)] = 30;

        (await execute(new ProjectionInput())).ShouldBeTrue();

        theView.Rows.Select(x => (x.Projection, x.Floor)).ShouldBe([("Trips", 30L), ("Orders", (long?)null)],
            ignoreOrder: true);

        // A live projection has no stored state, so no floor
        theView.Rows.ShouldNotContain(x => x.Projection == "LiveTotals");
    }

    [Fact]
    public async Task show_for_one_projection_and_tenant()
    {
        theFloors[("Trips", null)] = 30;
        theFloors[("Trips", "acme")] = 12;

        (await execute(new ProjectionInput { ShowFlag = true, ProjectionFlag = "Trips", TenantFlag = "acme" }))
            .ShouldBeTrue();

        var row = theView.Rows.ShouldHaveSingleItem();
        row.Floor.ShouldBe(12);
        row.TenantId.ShouldBe("acme");
        row.Database.ShouldBe("main");
    }

    [Fact]
    public async Task show_says_so_when_the_store_has_no_floor_seam()
    {
        theDatabase.FetchProjectionFloorAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<long?>>(_ => throw new NotSupportedException());

        (await execute(new ProjectionInput { ProjectionFlag = "Trips" })).ShouldBeTrue();

        theView.Rows.ShouldHaveSingleItem().Unsupported.ShouldBeTrue();
    }

    [Fact]
    public async Task pin_current_sets_the_floor_to_the_high_water_mark()
    {
        theView.Answer = true;

        (await execute(new ProjectionInput { PinCurrentFlag = true, ProjectionFlag = "Trips" })).ShouldBeTrue();

        theFloors[("Trips", null)].ShouldBe(500);
        theView.Questions.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task pin_current_for_a_tenant_uses_that_tenants_high_water_mark()
    {
        (await execute(new ProjectionInput
            {
                PinCurrentFlag = true, ProjectionFlag = "Trips", TenantFlag = "acme", YesFlag = true
            }))
            .ShouldBeTrue();

        theFloors[("Trips", "acme")].ShouldBe(77);
        theFloors.ContainsKey(("Trips", null)).ShouldBeFalse();
    }

    [Fact]
    public async Task set_asks_first_and_changes_nothing_when_refused()
    {
        theFloors[("Trips", null)] = 30;
        theView.Answer = false;

        (await execute(new ProjectionInput { SetFlag = 42, ProjectionFlag = "Trips" })).ShouldBeFalse();

        theFloors[("Trips", null)].ShouldBe(30);
        theView.Questions.ShouldHaveSingleItem().ShouldContain("42");
    }

    [Fact]
    public async Task set_writes_the_floor_once_confirmed()
    {
        theView.Answer = true;

        (await execute(new ProjectionInput { SetFlag = 42, ProjectionFlag = "trips" })).ShouldBeTrue();

        theFloors[("Trips", null)].ShouldBe(42);
    }

    [Fact]
    public async Task yes_skips_the_question()
    {
        (await execute(new ProjectionInput { SetFlag = 42, ProjectionFlag = "Trips", YesFlag = true }))
            .ShouldBeTrue();

        theFloors[("Trips", null)].ShouldBe(42);
        theView.Questions.ShouldBeEmpty();
    }

    [Fact]
    public async Task clear_removes_the_floor()
    {
        theFloors[("Trips", null)] = 30;
        theView.Answer = true;

        (await execute(new ProjectionInput { ClearFlag = true, ProjectionFlag = "Trips" })).ShouldBeTrue();

        theFloors.ContainsKey(("Trips", null)).ShouldBeFalse();
        theView.Questions.ShouldHaveSingleItem().ShouldContain("replays from 0");
    }

    [Fact]
    public async Task a_write_needs_a_projection()
    {
        (await execute(new ProjectionInput { SetFlag = 42, YesFlag = true })).ShouldBeFalse();

        theFloors.ShouldBeEmpty();
        theView.Errors.ShouldHaveSingleItem().ShouldContain("--projection");
    }

    [Fact]
    public async Task a_write_to_an_unknown_projection_changes_nothing()
    {
        (await execute(new ProjectionInput { SetFlag = 42, ProjectionFlag = "Nope", YesFlag = true }))
            .ShouldBeFalse();

        theFloors.ShouldBeEmpty();
    }

    [Fact]
    public async Task only_one_mode_at_a_time()
    {
        (await execute(new ProjectionInput { SetFlag = 42, ClearFlag = true, ProjectionFlag = "Trips" }))
            .ShouldBeFalse();

        theView.Errors.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task a_negative_floor_is_refused()
    {
        (await execute(new ProjectionInput { SetFlag = -1, ProjectionFlag = "Trips", YesFlag = true }))
            .ShouldBeFalse();

        theFloors.ShouldBeEmpty();
    }

    private static ProjectionInput parse(params string[] args)
        => (ProjectionInput)new ProjectionsCommand().Usages.BuildInput(new Queue<string>(args),
            new ActivatorCommandCreator());

    [Fact]
    public void the_floor_options_parse_without_colliding_with_store()
    {
        var input = parse("floor", "--projection", "Trips", "--set", "42", "--store", "marten://main",
            "--tenant", "acme", "--yes");

        input.Action.ShouldBe(ProjectionAction.floor);
        input.SetFlag.ShouldBe(42);
        input.StoreFlag.ShouldBe("marten://main");
        input.TenantFlag.ShouldBe("acme");
        input.YesFlag.ShouldBeTrue();

        parse("floor", "--show").ShowFlag.ShouldBeTrue();
        parse("floor", "--pin-current", "--projection", "Trips").PinCurrentFlag.ShouldBeTrue();
        parse("floor", "--clear", "--projection", "Trips").ClearFlag.ShouldBeTrue();
    }

    private class RecordingFloorView : IProjectionFloorView
    {
        public List<ProjectionFloorRow> Rows { get; } = new();
        public List<string> Messages { get; } = new();
        public List<string> Errors { get; } = new();
        public List<string> Questions { get; } = new();
        public bool Answer { get; set; }

        public void WriteFloors(IReadOnlyList<ProjectionFloorRow> rows) => Rows.AddRange(rows);
        public void WriteMessage(string message) => Messages.Add(message);
        public void WriteError(string message) => Errors.Add(message);

        public bool Confirm(string question)
        {
            Questions.Add(question);
            return Answer;
        }
    }
}
