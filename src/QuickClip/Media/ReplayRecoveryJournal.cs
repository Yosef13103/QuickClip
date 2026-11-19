using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using QuickClip.Gallery;
using QuickClip.Library;

namespace QuickClip.Media;

internal enum ReplayOriginalState { Original, Escrowed, Conflict }

internal sealed record ReplayRecoveryItem(MediaSource Source, string Escrow);

/// <summary>Durable intent for identity-preserving same-volume moves. No deletion or automatic recovery.
/// The caller must reserve output, all original and escrow paths exclusively through MediaOwnership.
/// A restart can inspect and restore a partially applied batch without trusting a progress flag.</summary>
internal sealed class ReplayRecoveryJournal
{
    private const int MaxJournalBytes = 256 * 1024;
    public sealed record Intent(Guid Id, MediaSource Output, ReplayRecoveryItem[] Items);
    private readonly Intent _intent;
    private readonly string _directory;
    internal Guid Id => _intent.Id;
    internal MediaSource Output => _intent.Output;
    internal IReadOnlyList<ReplayRecoveryItem> Items => Array.AsReadOnly(_intent.Items);

    private ReplayRecoveryJournal(string directory, Intent intent)
    {
        Validate(intent);
        _intent = intent;
        _directory = Path.GetFullPath(directory);
    }

    internal static ReplayRecoveryJournal Prepare(string directory, MediaSource output, IReadOnlyList<MediaSource> originals)
    {
        if (originals.Count is < 2 or > ReplayCombine.MaxSources)
            throw new ExportException("Choose a valid replay replacement group.");
        var id = Guid.NewGuid();
        var intent = new Intent(id, output, originals.Select((source, i) => new ReplayRecoveryItem(source,
            EscrowPath(source.Path, id, i))).ToArray());
        var journal = new ReplayRecoveryJournal(directory, intent);
        using var outputHandle = output.OpenVerified();
        foreach (var item in journal.Items)
        {
            item.Source.Verify();
            if (Path.Exists(item.Escrow)) throw new ExportException("A recovery destination already exists.");
        }

        // No source mutation may occur before this durable write succeeds.
        MediaProcessing.RequireSafeDirectory(directory);
        Directory.CreateDirectory(directory);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(intent);
        if (bytes.Length > MaxJournalBytes) throw new ExportException("Replay recovery record is too large.");
        string path = JournalPath(directory, id);
        string pending = path + ".pending";
        using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(pending, path);
        return journal;
    }

    internal static ReplayRecoveryJournal Open(string directory, Guid id)
    {
        MediaProcessing.RequireSafeDirectory(directory);
        string path = JournalPath(directory, id);
        if (LibraryFiles.InspectPath(path, directory: false) != ClipAvailability.Available)
            throw new ExportException("Replay recovery record is unavailable.");
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxJournalBytes) throw new ExportException("Replay recovery record is too large.");
        var intent = JsonSerializer.Deserialize<Intent>(stream);
        if (intent == null || intent.Id != id) throw new ExportException("Invalid replay recovery record.");
        return new(directory, intent);
    }

    // Derive state from both names and the exact immutable file identity.
    // Unknown, inaccessible, replaced, or missing-both files always fail closed.
    internal ReplayOriginalState Inspect(ReplayRecoveryItem item)
    {
        if (!_intent.Items.Contains(item)) throw new ExportException("Unrecognized replay recovery item.");
        var original = LibraryFiles.InspectPath(item.Source.Path, directory: false);
        var escrow = LibraryFiles.InspectPath(item.Escrow, directory: false);
        if (original == ClipAvailability.Available && escrow == ClipAvailability.Missing && Matches(item.Source))
            return ReplayOriginalState.Original;
        if (original == ClipAvailability.Missing && escrow == ClipAvailability.Available &&
            Matches(item.Source with { Path = item.Escrow }))
            return ReplayOriginalState.Escrowed;
        return ReplayOriginalState.Conflict;
    }

    internal void Stage(CancellationToken token)
    {
        using var journal = OpenVerifiedIntent();
        using var output = Output.OpenVerified();
        foreach (var item in Items)
        {
            token.ThrowIfCancellationRequested();
            switch (Inspect(item))
            {
                case ReplayOriginalState.Original:
                    RenameVerified(item.Source, item.Escrow);
                    break;
                case ReplayOriginalState.Escrowed:
                    break; // idempotent, including interruption between move and return
                default:
                    throw new ExportException("A replay original changed. Keep all files and recover the batch.");
            }
        }
    }

    // Restore does not require the combined output to be present. Never overwrite a
    // newly created occupant, and never remove the combined result in this primitive.
    internal void Restore(CancellationToken token)
    {
        using var journal = OpenVerifiedIntent();
        foreach (var item in Items)
        {
            token.ThrowIfCancellationRequested();
            switch (Inspect(item))
            {
                case ReplayOriginalState.Escrowed:
                    RenameVerified(item.Source with { Path = item.Escrow }, item.Source.Path);
                    break;
                case ReplayOriginalState.Original:
                    break;
                default:
                    throw new ExportException("A replay recovery path conflicts. No files were overwritten.");
            }
        }
    }

    private SafeFileHandle OpenVerifiedIntent()
    {
        string path = JournalPath(_directory, Id);
        if (LibraryFiles.InspectPath(path, false) != ClipAvailability.Available)
            throw new ExportException("The durable replay recovery record is missing.");
        var handle = LibraryFiles.OpenMedia(path);
        try
        {
            // Hold a handle denying mutation/deletion throughout the entire operation.
            long length = RandomAccess.GetLength(handle);
            if (length <= 0 || length > MaxJournalBytes)
                throw new ExportException("Invalid replay recovery record.");
            byte[] bytes = new byte[(int)length];
            int read = 0;
            while (read < bytes.Length)
            {
                int n = RandomAccess.Read(handle, bytes.AsSpan(read), read);
                if (n == 0) throw new ExportException("Incomplete replay recovery record.");
                read += n;
            }
            var stored = JsonSerializer.Deserialize<Intent>(bytes);
            if (stored == null || stored.Id != Id || stored.Output != Output ||
                stored.Items == null || !stored.Items.SequenceEqual(Items))
                throw new ExportException("The replay recovery record changed.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static bool Matches(MediaSource item)
    {
        try { item.Verify(); return true; }
        catch (Exception ex) when (ex is ExportException || LibraryFiles.IsAccessError(ex)) { return false; }
    }

    private static string JournalPath(string directory, Guid id) =>
        Path.Combine(Path.GetFullPath(directory), id.ToString("N") + ".json");

    private static string EscrowPath(string source, Guid id, int index) =>
        Path.Combine(Path.GetDirectoryName(source)!, $".quickclip-tmp-replay-{id:N}-{index}.mp4");

    private static void Validate(Intent intent)
    {
        if (intent.Id == Guid.Empty || intent.Output == null || intent.Items is not { Length: >= 2 and <= ReplayCombine.MaxSources })
            throw new ExportException("Invalid replay recovery record.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddSource(MediaSource source)
        {
            if (source == null || !Path.IsPathFullyQualified(source.Path) ||
                !string.Equals(Path.GetFullPath(source.Path), source.Path, StringComparison.OrdinalIgnoreCase) ||
                !ClipLibrary.IsClipFile(source.Path) || ClipLibrary.IsMomentsPath(source.Path) ||
                !Path.GetExtension(source.Path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                source.Stamp.Identity is not { Length: > 0 } identity ||
                !names.Add(source.Path) || !identities.Add(identity))
                throw new ExportException("Replay recovery requires distinct, identified MP4 files.");
        }
        AddSource(intent.Output);
        for (int i = 0; i < intent.Items.Length; i++)
        {
            var item = intent.Items[i] ?? throw new ExportException("Missing replay recovery item.");
            AddSource(item.Source);
            if (!string.Equals(item.Escrow, EscrowPath(item.Source.Path, intent.Id, i), StringComparison.OrdinalIgnoreCase) ||
                !names.Add(item.Escrow))
                throw new ExportException("Invalid replay recovery destination.");
        }
    }

    // Rename the open, verified file itself, not a path that can be exchanged
    // between verification and a separate File.Move. Never replace an occupant.
    private static void RenameVerified(MediaSource source, string destination)
    {
        string directory = Path.GetDirectoryName(source.Path)!;
        if (!string.Equals(directory, Path.GetDirectoryName(destination), StringComparison.OrdinalIgnoreCase) ||
            ClipLibrary.IsMomentsPath(source.Path) || LibraryFiles.InspectPath(source.Path, false) != ClipAvailability.Available)
            throw new ExportException("Unsafe replay recovery path.");
        MediaProcessing.RequireSafeDirectory(directory);
        using var handle = CreateFileW(source.Path, 0x80010000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid || LibraryFiles.IsUnavailable(File.GetAttributes(handle)) || LibraryFiles.ReadStamp(handle) != source.Stamp)
            throw new ExportException("The replay is changed or in use.");
        if (!GetFileInformationByHandleEx(handle, 1, out var standard, (uint)Marshal.SizeOf<FileStandardInfo>()) ||
            standard.NumberOfLinks != 1 || standard.Directory)
            throw new ExportException("Linked or unavailable recordings cannot be replaced.");

        byte[] name = Encoding.Unicode.GetBytes(destination);
        int lengthOffset = Marshal.OffsetOf<FileRenameInfo>(nameof(FileRenameInfo.Length)).ToInt32();
        int nameOffset = Marshal.OffsetOf<FileRenameInfo>(nameof(FileRenameInfo.Name)).ToInt32();
        int bytes = checked(nameOffset + name.Length + sizeof(char));
        IntPtr buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            Marshal.Copy(new byte[bytes], 0, buffer, bytes);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            if (!SetFileInformationByHandle(handle, 3, buffer, (uint)bytes))
                throw new ExportException($"The verified replay could not be moved without overwriting another file (Windows error {Marshal.GetLastWin32Error()}).");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileRenameInfo
    {
        public uint Flags;
        public IntPtr RootDirectory;
        public uint Length;
        public ushort Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        public long AllocationSize, EndOfFile;
        public uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)] public bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] public bool Directory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int info, out FileStandardInfo value, uint size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int info, IntPtr buffer, uint size);
}