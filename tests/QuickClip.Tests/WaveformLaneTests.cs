using System.Threading;
using System.Windows;
using System.Windows.Controls;
using QuickClip.Controls;

namespace QuickClip.Tests;

public sealed class WaveformLaneTests
{
    [Fact]
    public void Waveform_seek_bubbles_only_to_owning_editor_visual_tree()
    {
        Assert.False(typeof(WaveformLane).GetEvent(nameof(WaveformLane.SeekRequested))!
            .GetAddMethod()!.IsStatic);

        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var firstEditor = new Grid();
                var secondEditor = new Grid();
                var firstLane = new WaveformLane();
                var secondLane = new WaveformLane();
                firstEditor.Children.Add(firstLane);
                secondEditor.Children.Add(secondLane);

                int firstCalls = 0, secondCalls = 0;
                double requestedTime = -1;
                bool final = false;
                firstEditor.AddHandler(WaveformLane.SeekRequestedEvent, new RoutedEventHandler((_, args) =>
                {
                    firstCalls++;
                    var seek = Assert.IsType<WaveformLane.WaveformSeekEventArgs>(args);
                    requestedTime = seek.Time;
                    final = seek.Finished;
                }));
                secondEditor.AddHandler(WaveformLane.SeekRequestedEvent, new RoutedEventHandler((_, _) => secondCalls++));

                firstLane.RaiseEvent(new WaveformLane.WaveformSeekEventArgs(
                    WaveformLane.SeekRequestedEvent, firstLane, 12.5, true));

                Assert.Equal(1, firstCalls);
                Assert.Equal(0, secondCalls);
                Assert.Equal(12.5, requestedTime);
                Assert.True(final);
            }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Waveform seek STA test timed out.");
        if (error != null) throw new InvalidOperationException("Waveform routed-event test failed.", error);
    }
}
