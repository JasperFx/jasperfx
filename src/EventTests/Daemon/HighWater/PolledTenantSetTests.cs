using JasperFx.Events.Daemon.HighWater;
using Shouldly;

namespace EventTests.Daemon.HighWater;

// The polled set is reconciled wholesale from the agents registered on a node, so anything that is
// merely *starting* has to be held in it explicitly. These pin the two halves of that.
public class PolledTenantSetTests
{
    [Fact]
    public void set_tenants_keeps_a_pinned_tenant()
    {
        var set = new PolledTenantSet();

        // t1's agent start is in flight, so it is not in the assignment snapshot below yet
        set.Pin("t1");
        set.SetTenants(["t2"]);

        set.IsPolled("t1").ShouldBeTrue();
        set.IsPolled("t2").ShouldBeTrue();
    }

    [Fact]
    public void pins_are_reference_counted_per_tenant()
    {
        var set = new PolledTenantSet();

        // Two shards of the same tenant starting at once
        set.Pin("t1");
        set.Pin("t1");

        set.Unpin("t1");
        set.SetTenants([]);

        // The second start is still running, so the tenant must still be polled for it
        set.IsPolled("t1").ShouldBeTrue();

        set.Unpin("t1");
        set.SetTenants([]);

        set.IsPolled("t1").ShouldBeFalse();
    }

    [Fact]
    public void unpinning_does_not_itself_remove_the_tenant()
    {
        var set = new PolledTenantSet();

        set.Pin("t1");
        set.Unpin("t1");

        // Whether it belongs in the set is the next reconciliation's call, not the pin's — a start that
        // succeeded has an agent by now, and dropping it here would stop polling a live tenant.
        set.IsPolled("t1").ShouldBeTrue();
    }

    [Fact]
    public void unpinning_a_tenant_that_was_never_pinned_is_a_no_op()
    {
        var set = new PolledTenantSet();

        // jasperfx#710: the pin count must not go negative, or a later Pin/Unpin pair would leave the
        // tenant pinned forever.
        set.Pin("t1");
        set.Unpin("t1");
        set.Unpin("t1");
        set.Unpin("t1");

        set.Pin("t1");
        set.Unpin("t1");
        set.SetTenants([]);

        set.IsPolled("t1").ShouldBeFalse();
    }

    [Fact]
    public void deactivate_does_not_remove_a_pinned_tenant()
    {
        var set = new PolledTenantSet();

        set.Activate("t1");
        set.Pin("t1");

        // jasperfx#710: Pin used to guard only against SetTenants, so the obvious-looking Deactivate
        // silently defeated it and put back the field failure jasperfx#702 fixed.
        set.Deactivate("t1").ShouldBeFalse();
        set.IsPolled("t1").ShouldBeTrue();
    }

    [Fact]
    public void deactivate_works_again_once_the_last_pin_is_released()
    {
        var set = new PolledTenantSet();

        set.Activate("t1");
        set.Pin("t1");
        set.Pin("t1");

        set.Unpin("t1");
        set.Deactivate("t1").ShouldBeFalse();
        set.IsPolled("t1").ShouldBeTrue();

        set.Unpin("t1");
        set.Deactivate("t1").ShouldBeTrue();
        set.IsPolled("t1").ShouldBeFalse();
    }
}
