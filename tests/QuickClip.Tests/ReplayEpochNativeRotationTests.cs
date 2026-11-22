using System.Runtime.InteropServices;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class ReplayEpochNativeRotationTests
{
    [DllImport("QuickClipCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int qc_test_epoch_rotation_buffer();

    [Fact]
    public void Native_epoch_handoff_and_save_exports_are_available()
    {
        CaptureEngine.Initialize();
        Assert.True(NativeLibrary.TryLoad("QuickClipCapture.dll",
            typeof(CaptureEngine).Assembly,
            DllImportSearchPath.AssemblyDirectory, out IntPtr handle));
        try
        {
            foreach (var name in new[]
            {
                "qc_seal_game_epoch_and_stop", "qc_epoch_rotation_pending",
                "qc_list_game_epochs", "qc_write_game_epoch",
                "qc_discard_epochs", "qc_retained_epoch_count"
            })
                Assert.True(NativeLibrary.TryGetExport(handle, name, out _),
                    "Missing epoch native ABI export: " + name);
        }
        finally { NativeLibrary.Free(handle); }
    }

    [Fact]
    public void Video_format_rotation_retains_old_encoded_keyframe_and_drops_new_audio()
    {
        CaptureEngine.Initialize();
        Assert.Equal(1, qc_test_epoch_rotation_buffer());
    }
}
