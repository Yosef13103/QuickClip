using QuickClip.Recording;
using QuickClip.Recording.Companion;

namespace QuickClip.Tests;

public sealed class DiscordMirrorSelectionTests
{
    private static WindowOwner Window(int id) => new((IntPtr)id, (uint)id, 100, 123);

    [Fact]
    public void Picks_only_verified_main_window_not_a_larger_popup()
    {
        var popup = Window(1);
        var main = Window(2);
        var result = DiscordMainWindow.Select([popup, main], null,
            candidate => candidate == main,
            candidate => candidate == popup ? 4_000_000 : 1_500_000);
        Assert.Equal(main, result);
    }

    [Fact]
    public void Keeps_pinned_main_even_if_window_order_or_size_changes()
    {
        var main = Window(10);
        var popup = Window(11);
        var result = DiscordMainWindow.Select([popup, main], main,
            _ => true, candidate => candidate == popup ? 9_000_000 : 1_000_000);
        Assert.Equal(main, result);
    }

    [Fact]
    public void Does_not_promote_a_popup_when_main_disappears()
    {
        var main = Window(20);
        var popup = Window(21);
        Assert.Null(DiscordMainWindow.Select([popup], main,
            _ => false, _ => 2_000_000));
        Assert.Null(DiscordMainWindow.Select([popup], null,
            _ => false, _ => 2_000_000));
    }

    [Fact]
    public void Allows_a_new_explicitly_identified_main_after_restart()
    {
        var old = Window(30);
        var current = Window(31);
        Assert.Equal(current, DiscordMainWindow.Select([current], old,
            _ => true, _ => 1_000_000));
    }
}
