using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;

namespace EventTests.Daemon;

public class AsyncOptionsTests
{
    private readonly IEventDatabase theDatabase = Substitute.For<IEventDatabase>();
    private readonly ShardName theName = new ShardName("Fake", "All");
    private readonly CancellationToken theToken = CancellationToken.None;

    [Fact]
    public async Task determine_starting_position_if_rebuild()
    {
        var options = new AsyncOptions();
        (await options.DetermineStartingPositionAsync(2000L, theName, ShardExecutionMode.Rebuild, theDatabase, theToken))
            .ShouldBe(new Position(0, true));

    }

    [Fact]
    public async Task determine_starting_position_if_continuous_and_no_other_constraints()
    {
        theDatabase.ProjectionProgressFor(theName, theToken).Returns(111L);

        var options = new AsyncOptions();
        (await options.DetermineStartingPositionAsync(2000L, theName, ShardExecutionMode.Continuous, theDatabase, theToken))
            .ShouldBe(new Position(111L, false));
    }

    [Fact]
    public async Task subscribe_from_present()
    {
        var options = new AsyncOptions();
        options.SubscribeFromPresent();

        (await options.DetermineStartingPositionAsync(2000L, theName, ShardExecutionMode.Continuous, theDatabase, theToken))
            .ShouldBe(new Position(2000L, true));
    }

    [Fact]
    public async Task do_not_match_on_database_name()
    {
        theDatabase.ProjectionProgressFor(theName, theToken).Returns(111L);
        theDatabase.Identifier.Returns("One");

        var options = new AsyncOptions();
        options.SubscribeFromPresent("Two");

        (await options.DetermineStartingPositionAsync(2000L, theName, ShardExecutionMode.Continuous, theDatabase, theToken))
            .ShouldBe(new Position(111L, false));
    }

    [Fact]
    public async Task do_match_on_database_name()
    {
        theDatabase.ProjectionProgressFor(theName, theToken).Returns(111L);
        theDatabase.Identifier.Returns("One");

        var options = new AsyncOptions();
        options.SubscribeFromPresent("One");

        (await options.DetermineStartingPositionAsync(2000L, theName, ShardExecutionMode.Continuous, theDatabase, theToken))
            .ShouldBe(new Position(2000L, true));
    }

    [Fact]
    public async Task subscribe_from_time_hit_with_no_prior()
    {
        theDatabase.ProjectionProgressFor(theName, theToken).Returns(0);
        theDatabase.Identifier.Returns("One");

        var subscriptionTime = (DateTimeOffset)DateTime.Today;

        theDatabase.FindEventStoreFloorAtTimeAsync(subscriptionTime, theToken).Returns(222L);

        var options = new AsyncOptions();
        options.SubscribeFromTime(subscriptionTime);

        (await options.DetermineStartingPositionAsync(2000L, theName, ShardExecutionMode.Continuous, theDatabase, theToken))
            .ShouldBe(new Position(222L, true));
    }

    [Fact]
    public async Task subscribe_from_time_miss_with_no_prior()
    {
        theDatabase.ProjectionProgressFor(theName, theToken).Returns(0);
        theDatabase.Identifier.Returns("One");

        var subscriptionTime = (DateTimeOffset)DateTime.Today;

        theDatabase.FindEventStoreFloorAtTimeAsync(subscriptionTime, theToken).Returns((long?)null);

        var options = new AsyncOptions();
        options.SubscribeFromTime(subscriptionTime);

        (await options.DetermineStartingPositionAsync(2000L, theName, ShardExecutionMode.Continuous, theDatabase, theToken))
            .ShouldBe(new Position(0, false));
    }

    [Fact]
    public async Task subscribe_from_time_hit_with_prior_lower_than_threshold()
    {
        theDatabase.ProjectionProgressFor(theName, theToken).Returns(200L);
        theDatabase.Identifier.Returns("One");

        var subscriptionTime = (DateTimeOffset)DateTime.Today;

        theDatabase.FindEventStoreFloorAtTimeAsync(subscriptionTime, theToken).Returns(222L);

        var options = new AsyncOptions();
        options.SubscribeFromTime(subscriptionTime);

        (await options.DetermineStartingPositionAsync(2000L, theName, ShardExecutionMode.Continuous, theDatabase, theToken))
            .ShouldBe(new Position(222L, true));
    }

    [Fact]
    public async Task subscribe_from_time_hit_with_prior_higher_than_threshold()
    {
        theDatabase.ProjectionProgressFor(theName, theToken).Returns(500L);
        theDatabase.Identifier.Returns("One");

        var subscriptionTime = (DateTimeOffset)DateTime.Today;

        theDatabase.FindEventStoreFloorAtTimeAsync(subscriptionTime, theToken).Returns(222L);

        var options = new AsyncOptions();
        options.SubscribeFromTime(subscriptionTime);

        (await options.DetermineStartingPositionAsync(2000L, theName, ShardExecutionMode.Continuous, theDatabase, theToken))
            .ShouldBe(new Position(500L, false));
    }

    [Fact]
    public async Task subscribe_from_time_hit_with_prior_higher_than_threshold_and_rebuild()
    {
        theDatabase.ProjectionProgressFor(theName, theToken).Returns(500L);
        theDatabase.Identifier.Returns("One");

        var subscriptionTime = (DateTimeOffset)DateTime.Today;

        theDatabase.FindEventStoreFloorAtTimeAsync(subscriptionTime, theToken).Returns(222L);

        var options = new AsyncOptions();
        options.SubscribeFromTime(subscriptionTime);

        (await options.DetermineStartingPositionAsync(2000L, theName, ShardExecutionMode.Rebuild, theDatabase, theToken))
            .ShouldBe(new Position(222L, true));
    }

    [Fact]
    public async Task subscribe_from_sequence_hit_with_no_prior()
    {
        theDatabase.ProjectionProgressFor(theName, theToken).Returns(100);
        theDatabase.Identifier.Returns("One");

        var options = new AsyncOptions();
        options.SubscribeFromSequence(222L);

        (await options.DetermineStartingPositionAsync(2000L, theName, ShardExecutionMode.Continuous, theDatabase, theToken))
            .ShouldBe(new Position(222L, true));
    }

    [Fact]
    public async Task subscribe_from_sequence_miss_with_no_prior()
    {
        theDatabase.ProjectionProgressFor(theName, theToken).Returns(0);
        theDatabase.Identifier.Returns("One");

        var options = new AsyncOptions();
        options.SubscribeFromSequence(222L);

        (await options.DetermineStartingPositionAsync(2000L, theName, ShardExecutionMode.Continuous, theDatabase, theToken))
            .ShouldBe(new Position(222L, true));
    }

    [Fact]
    public async Task subscribe_from_sequence_hit_with_prior_lower_than_threshold()
    {
        theDatabase.ProjectionProgressFor(theName, theToken).Returns(200L);
        theDatabase.Identifier.Returns("One");

        var options = new AsyncOptions();
        options.SubscribeFromSequence(222L);

        (await options.DetermineStartingPositionAsync(2000L, theName, ShardExecutionMode.Continuous, theDatabase, theToken))
            .ShouldBe(new Position(222L, true));
    }

    [Fact]
    public async Task subscribe_from_sequence_hit_with_prior_higher_than_threshold()
    {
        theDatabase.ProjectionProgressFor(theName, theToken).Returns(500L);
        theDatabase.Identifier.Returns("One");

        var options = new AsyncOptions();
        options.SubscribeFromSequence(222L);

        (await options.DetermineStartingPositionAsync(2000L, theName, ShardExecutionMode.Continuous, theDatabase, theToken))
            .ShouldBe(new Position(500L, false));
    }

    [Fact]
    public async Task subscribe_from_sequence_hit_with_prior_higher_than_threshold_and_rebuild()
    {
        theDatabase.ProjectionProgressFor(theName, theToken).Returns(500L);
        theDatabase.Identifier.Returns("One");

        var options = new AsyncOptions();
        options.SubscribeFromSequence(222L);

        (await options.DetermineStartingPositionAsync(2000L, theName, ShardExecutionMode.Rebuild, theDatabase, theToken))
            .ShouldBe(new Position(222L, true));
    }

    [Fact]
    public async Task transition_from_inline_to_async_no_initial_progress()
    {
        theDatabase.ProjectionProgressFor(theName, theToken).Returns(0);
        theDatabase.FetchHighestEventSequenceNumber(theToken).Returns(1234L);
        var options = new AsyncOptions();
        options.SubscribeAsInlineToAsync();

        (await options.DetermineStartingPositionAsync(1000L, theName, ShardExecutionMode.Continuous, theDatabase, theToken))
            .ShouldBe(new Position(1234L, true));
    }

    [Fact]
    public async Task transition_from_inline_to_async_but_there_is_initial_progress()
    {
        theDatabase.ProjectionProgressFor(theName, theToken).Returns(1000L);
        theDatabase.FetchHighestEventSequenceNumber(theToken).Returns(2005L);
        var options = new AsyncOptions();
        options.SubscribeAsInlineToAsync();

        (await options.DetermineStartingPositionAsync(2003L, theName, ShardExecutionMode.Continuous, theDatabase, theToken))
            .ShouldBe(new Position(1000L, false));
    }




}
