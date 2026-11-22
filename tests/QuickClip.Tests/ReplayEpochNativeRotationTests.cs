using System.Runtime.InteropServices;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class ReplayEpochNativeRotationTests
{
    [DllImport("QuickClipCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int qc_test_epoch_rotation_buffer();

    [Fact]
    public void Video_format_rotation_retains_old_encoded_keyframe_and_drops_new_audio()
    {
        CaptureEngine.Initialize();
        Assert.Equal(1, qc_test_epoch_rotation_buffer());
    }
}
