using System;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Sessions;
using Xunit;

namespace MinorShift.Emuera.Tests;

public sealed class ControllerTests
{
    private static readonly ControlIdentity Agent = ControlIdentity.Agent("agent-1");
    private static readonly ControlIdentity OtherAgent = ControlIdentity.Agent("agent-2");

    [Fact]
    public void Transfer_table_idle_agent_user_steal_agent_rejected_release_idle()
    {
        using var controller = new Controller(TimeSpan.FromMinutes(1));

        Assert.Equal(Controller.StateIdle, controller.State);
        Assert.Equal(ControlAcquireStatus.Acquired, controller.Acquire(Agent).Status);
        Assert.Equal(Controller.StateHeld, controller.State);

        var stolen = controller.Acquire(ControlIdentity.User);
        Assert.Equal(ControlAcquireStatus.Acquired, stolen.Status);
        Assert.Equal("stolen", stolen.Event!.Type);
        Assert.Equal(ControlAcquireStatus.HeldByUser, controller.Acquire(Agent).Status);

        Assert.Equal(ControlReleaseStatus.NotOwner, controller.Release(Agent).Status);
        Assert.Equal(ControlReleaseStatus.Released, controller.Release(ControlIdentity.User).Status);
        Assert.Equal(Controller.StateIdle, controller.State);
    }

    [Fact]
    public void Agent_self_acquire_is_idempotent_and_refreshes_lease()
    {
        using var controller = new Controller(TimeSpan.FromMinutes(1));
        controller.Acquire(Agent);
        var firstExpiry = controller.CurrentInfo!.LeaseExpiresAt;
        Assert.NotNull(firstExpiry);

        Thread.Sleep(30);
        Assert.Equal(ControlAcquireStatus.Acquired, controller.Acquire(Agent).Status);
        var secondExpiry = controller.CurrentInfo!.LeaseExpiresAt;
        Assert.NotNull(secondExpiry);
        Assert.True(secondExpiry > firstExpiry);
    }

    [Fact]
    public void Second_agent_cannot_steal_from_first_agent()
    {
        using var controller = new Controller(TimeSpan.FromMinutes(1));
        controller.Acquire(Agent);
        Assert.Equal(ControlAcquireStatus.HeldByAgent, controller.Acquire(OtherAgent).Status);
        Assert.True(controller.IsCurrent(Agent));
    }

    [Theory]
    [InlineData("idle", "user", false, "Allowed", "")]
    [InlineData("idle", "agent", false, "Allowed", "")]
    [InlineData("user", "user", false, "Allowed", "")]
    [InlineData("user", "agent", false, "NotController", "CONTROL_HELD_BY_USER")]
    [InlineData("agent", "user", false, "NotController", "CONTROL_HELD_BY_AGENT")]
    [InlineData("agent", "agent", false, "Allowed", "")]
    [InlineData("agent", "other", false, "NotController", "CONTROL_HELD_BY_AGENT")]
    [InlineData("agent", "user", true, "Allowed", "")]
    [InlineData("user", "agent", true, "Allowed", "")]
    public void Input_gate_follows_controller_table(
        string holder,
        string caller,
        bool sessionEnded,
        string expected,
        string reason)
    {
        using var controller = new Controller(TimeSpan.FromMinutes(1));
        ApplyHolder(controller, holder);

        var result = controller.CheckInput(ToIdentity(caller), sessionEnded);

        Assert.Equal(expected, result.Status.ToString());
        Assert.Equal(reason, result.Reason);
    }

    [Theory]
    [InlineData("idle", "user", false, "Allowed", "")]
    [InlineData("idle", "agent", false, "Allowed", "")]
    [InlineData("user", "user", false, "Allowed", "")]
    [InlineData("user", "agent", false, "NotController", "CONTROL_HELD_BY_USER")]
    [InlineData("agent", "user", false, "NotController", "CONTROL_HELD_BY_AGENT")]
    [InlineData("agent", "agent", false, "Allowed", "")]
    [InlineData("agent", "other", false, "NotController", "CONTROL_HELD_BY_AGENT")]
    [InlineData("agent", "user", true, "Allowed", "")]
    [InlineData("user", "agent", true, "Allowed", "")]
    public void Lifecycle_gate_follows_controller_table(
        string holder,
        string caller,
        bool sessionEnded,
        string expected,
        string reason)
    {
        using var controller = new Controller(TimeSpan.FromMinutes(1));
        ApplyHolder(controller, holder);

        var result = controller.CheckLifecycle(ToIdentity(caller), sessionEnded);

        Assert.Equal(expected, result.Status.ToString());
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public async Task Lease_expiry_releases_agent_and_notifies_waiter()
    {
        using var controller = new Controller(TimeSpan.FromMilliseconds(40));
        controller.Acquire(Agent);
        Assert.NotNull(controller.CurrentInfo!.LeaseExpiresAt);

        var controlEvent = await controller.WaitForEventAsync(1000, CancellationToken.None);

        Assert.NotNull(controlEvent);
        Assert.Equal("lease_expired", controlEvent!.Type);
        Assert.Equal(Controller.StateIdle, controller.State);
        Assert.Null(controller.CurrentInfo);
    }

    [Fact]
    public void User_control_has_no_lease()
    {
        using var controller = new Controller(TimeSpan.FromMilliseconds(40));
        controller.Acquire(ControlIdentity.User);

        Assert.Equal(Controller.StateHeld, controller.State);
        Assert.Null(controller.CurrentInfo!.LeaseExpiresAt);
        Thread.Sleep(80);
        Assert.Equal(Controller.StateHeld, controller.State);
        Assert.True(controller.IsCurrent(ControlIdentity.User));
    }

    [Fact]
    public async Task Owner_change_completes_in_flight_signal()
    {
        using var controller = new Controller(TimeSpan.FromMinutes(1));
        var ownerChanged = controller.OwnerChangedTask;
        controller.Acquire(ControlIdentity.User);

        await ownerChanged.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(Controller.StateHeld, controller.State);
    }

    private static void ApplyHolder(Controller controller, string holder)
    {
        switch (holder)
        {
            case "idle":
                return;
            case "user":
                controller.Acquire(ControlIdentity.User);
                return;
            case "agent":
                controller.Acquire(Agent);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(holder), holder, null);
        }
    }

    private static ControlIdentity ToIdentity(string caller) => caller switch
    {
        "user" => ControlIdentity.User,
        "agent" => Agent,
        "other" => OtherAgent,
        _ => throw new ArgumentOutOfRangeException(nameof(caller), caller, null),
    };
}
