using System.Windows;

namespace QuickClip.Tests;

public sealed class TitleBarTests
{
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(-1920, 1080)]
    [InlineData(1920, -1080)]
    [InlineData(-1920, -1080)]
    public void Hit_test_preserves_negative_coordinates_on_secondary_monitors(int x, int y)
    {
        var packed = new IntPtr(unchecked((int)((uint)(ushort)y << 16 | (ushort)x)));
        Assert.Equal(new Point(x, y), MainWindow.DecodeScreenPoint(packed));
    }
}
