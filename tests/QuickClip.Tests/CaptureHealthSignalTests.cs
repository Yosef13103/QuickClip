using QuickClip.Shell;

namespace QuickClip.Tests;

public sealed class CaptureHealthSignalTests
{
    [Fact]
    public void One_or_two_isolated_missing_frames_do_not_flash()
    {
        var signal = new CaptureHealthSignal();
        signal.Reset(1, 1000);
        Assert.False(signal.Observe(1, 2000, true, 60, 60, 0));
        Assert.False(signal.Observe(1, 3000, true, 59, 60, 2));
        Assert.False(signal.Observe(1, 4000, true, 60, 60, 2));
        Assert.Equal(CaptureHealthState.Healthy, signal.State);
    }

    [Fact]
    public void Sustained_missing_frames_turn_red_and_three_good_samples_restore_normal()
    {
        var signal = new CaptureHealthSignal();
        signal.Reset(7, 1000);
        signal.Observe(7, 2000, true, 60, 60, 0);
        signal.Observe(7, 3000, true, 47, 60, 9);
        Assert.True(signal.Observe(7, 4000, true, 49, 60, 19));
        Assert.Equal(CaptureHealthState.MissingFrames, signal.State);
        Assert.False(signal.Observe(7, 5000, true, 60, 60, 19));
        Assert.False(signal.Observe(7, 6000, true, 60, 60, 19));
        Assert.True(signal.Observe(7, 7000, true, 60, 60, 19));
        Assert.Equal(CaptureHealthState.Healthy, signal.State);
    }

    [Fact]
    public void Severe_loss_is_immediate_but_only_after_startup_and_first_sample()
    {
        var signal = new CaptureHealthSignal();
        signal.Reset(2, 1000);
        signal.Observe(2, 2000, true, 59, 60, 0);
        Assert.False(signal.Observe(2, 2500, true, 30, 60, 20));
        Assert.True(signal.Observe(2, 4000, true, 30, 60, 50));
        Assert.Equal(CaptureHealthState.MissingFrames, signal.State);
    }

    [Fact]
    public void Low_actual_fps_warns_even_without_dxgi_drop_counter()
    {
        var signal = new CaptureHealthSignal();
        signal.Reset(3, 1000);
        signal.Observe(3, 2000, true, 60, 60, 0);
        signal.Observe(3, 3000, true, 40, 60, 0);
        Assert.True(signal.Observe(3, 4000, true, 41, 60, 0));
        Assert.Equal(CaptureHealthState.MissingFrames, signal.State);
    }

    [Fact]
    public void Epoch_counter_reset_and_interrupted_sampling_do_not_leak_old_warning()
    {
        var signal = new CaptureHealthSignal();
        signal.Reset(4, 1000);
        signal.Observe(4, 2000, true, 60, 60, 0);
        signal.Observe(4, 3000, true, 20, 60, 15);
        signal.Observe(4, 4000, true, 20, 60, 40);
        Assert.Equal(CaptureHealthState.MissingFrames, signal.State);
        signal.Reset(5, 5000);
        Assert.Equal(CaptureHealthState.Healthy, signal.State);
        Assert.False(signal.Observe(5, 6000, true, 60, 60, 0));
        Assert.False(signal.Observe(5, 7000, false, 0, 60, 0));
        Assert.Equal(CaptureHealthState.Healthy, signal.State);
    }
}
