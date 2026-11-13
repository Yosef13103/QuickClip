using QuickClip.Discovery;

namespace QuickClip.Tests;

public sealed class ManualGameInputTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("quickclip-game-").FullName;

    private string FileAt(string relative)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    [Fact]
    public void Direct_files_are_validated_and_large_selections_have_no_count_cap()
    {
        var paths = Enumerable.Range(0, 150).Select(i => FileAt($"game{i}.exe")).ToArray();
        Assert.Equal(paths, paths.Select(path => ManualGameInput.Read(path).Executable));
        Assert.Throws<IOException>(() => ManualGameInput.Read(Path.Combine(_root, "missing.exe")));
        Assert.Throws<IOException>(() => ManualGameInput.Read(@"\\server\share\game.exe"));
    }

    [Fact]
    public void One_install_ranks_game_binaries_and_keeps_ambiguous_candidates_for_review()
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "Fixture")).FullName;
        string main = FileAt(@"Fixture\bin\x64\Fixture-Win64-Shipping.exe");
        string other = FileAt(@"Fixture\bin\x64\Alternate.exe");
        FileAt(@"Fixture\bin\x64\CrashReporter\7za.exe");
        FileAt(@"Fixture\_CommonRedist\arbitrary.exe");
        FileAt(@"Fixture\DiagnosticTool.exe");
        FileAt(@"Fixture\unins000.exe");
        FileAt(@"Unrelated\other-game.exe");
        Assert.Equal(new[] { main, other }, ManualGameInput.FindCandidates(root).Paths);
        Assert.Throws<IOException>(() => ManualGameInput.FindCandidates(Path.GetPathRoot(root)!));
    }

    [Fact]
    public async Task Shell_shortcut_with_arguments_maps_to_the_same_executable_and_missing_targets_fail()
    {
        string executable = FileAt(@"install\fixture.exe");
        string shortcut = Path.Combine(_root, "fixture.lnk");
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
                dynamic link = shell.CreateShortcut(shortcut);
                link.TargetPath = executable;
                link.Arguments = "--fullscreen --profile fixture";
                link.WorkingDirectory = _root;
                link.Save();
                Assert.Equal(executable, ManualGameInput.Read(shortcut).Executable);
                Assert.Single(new[] { executable, shortcut }.Select(path => ManualGameInput.Read(path).Executable)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                File.Delete(executable);
                Assert.Throws<IOException>(() => ManualGameInput.Read(shortcut));
                Assert.Throws<IOException>(() => ManualGameInput.Read(FileAt("corrupt.lnk")));
                result.SetResult();
            }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
