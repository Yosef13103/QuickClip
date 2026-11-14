using System.Threading;
using System.Windows;
using System.Windows.Controls;
using QuickClip.Controls;

namespace QuickClip.Tests;

public sealed class WaveformLaneTests
{
    [Fact]
    public void Seek_events_bubble_only_to_the_owning_editor_tree()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var editorA = new StackPanel();
                var editorB = new StackPanel();
                var laneA = new WaveformLane();
                var laneB = new WaveformLane();
                editorA.Children.Add(laneA);
                editorB.Children.Add(laneB);

                var receivedA = new List<(double Seconds, bool Final)>();
                var receivedB = new List<(double Seconds, bool Final)>();
                editorA.AddHandler(WaveformLane.SeekRequestedEvent, new RoutedEventHandler((_, e) =>
                {
                    var seek = Assert.IsType<WaveformSeekEventArgs>(e);
                    receivedA.Add((seek.Seconds, seek.Final));
                }));
                editorB.AddHandler(WaveformLane.SeekRequestedEvent, new RoutedEventHandler((_, e) =>
                {
                    var seek = Assert.IsType<WaveformSeekEventArgs>(e);
                    receivedB.Add((seek.Seconds, seek.Final));
                }));

                laneA.RaiseEvent(new WaveformSeekEventArgs(2.5, false));
                laneA.RaiseEvent(new WaveformSeekEventArgs(3.5, true));
                Assert.Equal([(2.5, false), (3.5, true)], receivedA);
                Assert.Empty(receivedB);

                laneB.RaiseEvent(new WaveformSeekEventArgs(9, true));
                Assert.Single(receivedB);
                Assert.Equal((9.0, true), receivedB[0]);
                Assert.Equal(2, receivedA.Count);
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA test thread did not complete");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
