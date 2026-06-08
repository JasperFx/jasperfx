using JasperFx.Events;
using Shouldly;

namespace EventTests;

public class IEventStoreInstrumentationTests
{
    // jasperfx#424: lock the storage-agnostic contract that Marten's EventGraph and Polecat's
    // options implement so CritterWatch can flip extended progression tracking generically.
    private class FakeInstrumentation : IEventStoreInstrumentation
    {
        public bool ExtendedProgressionEnabled { get; set; }
    }

    [Fact]
    public void extended_progression_defaults_to_false_and_is_settable()
    {
        IEventStoreInstrumentation instrumentation = new FakeInstrumentation();
        instrumentation.ExtendedProgressionEnabled.ShouldBeFalse();

        instrumentation.ExtendedProgressionEnabled = true;
        instrumentation.ExtendedProgressionEnabled.ShouldBeTrue();
    }
}
