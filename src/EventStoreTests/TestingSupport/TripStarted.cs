namespace EventStoreTests.TestingSupport;

public class TripStarted : IDayEvent
{
    public int Day { get; set; }
}