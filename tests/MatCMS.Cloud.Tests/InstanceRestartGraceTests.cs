using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Xunit;

namespace MatCMS.Cloud.Tests;

/// <summary>A site whose container the cloud restarted itself (an update) is "restarting", not offline, until it beats again or the
/// grace runs out; so is every site right after the cloud's own restart. Every update used to report the whole fleet offline.</summary>
[Collection("InstanceClock")]
public class InstanceRestartGraceTests
{
    private static Instance Silent(TimeSpan since, DateTime? restartUntil = null) =>
        new() { LastHeartbeatUtc = DateTime.UtcNow - since, RestartExpectedUntil = restartUntil };

    [Fact]
    public void A_site_the_cloud_restarted_is_restarting_until_the_grace_ends_then_offline()
    {
        InstanceService.StartedUtc = DateTime.UtcNow - TimeSpan.FromHours(1);
        var i = Silent(TimeSpan.FromMinutes(4), DateTime.UtcNow + TimeSpan.FromMinutes(6));
        Assert.True(InstanceService.IsRestarting(i));
        Assert.True(InstanceService.IsOnline(i));                                   // no offline alert, no "offline" badge
        Assert.False(AttentionService.IsOffline(i));

        i.RestartExpectedUntil = DateTime.UtcNow - TimeSpan.FromSeconds(1);        // grace over, still silent: a real outage
        Assert.False(InstanceService.IsOnline(i));
        Assert.True(AttentionService.IsOffline(i));

        Assert.False(InstanceService.IsOnline(Silent(TimeSpan.FromMinutes(4))));     // silent without a restart of ours: offline as before
        Assert.False(InstanceService.IsRestarting(Silent(TimeSpan.FromSeconds(30), DateTime.UtcNow.AddMinutes(5)))); // beating: just online
    }

    [Fact]
    public void Right_after_the_clouds_own_restart_sites_that_beat_before_get_one_window()
    {
        InstanceService.StartedUtc = DateTime.UtcNow - TimeSpan.FromSeconds(30);
        Assert.True(InstanceService.IsOnline(Silent(TimeSpan.FromMinutes(5))));      // the cloud was down for its update
        Assert.False(InstanceService.IsOnline(Silent(TimeSpan.FromHours(2))));       // was already gone before the cloud stopped

        InstanceService.StartedUtc = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        Assert.False(InstanceService.IsOnline(Silent(TimeSpan.FromMinutes(5))));     // the window is over
    }
}
