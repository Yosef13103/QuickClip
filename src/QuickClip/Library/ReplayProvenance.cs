using System.Buffers.Binary;
using System.Security.Cryptography;
using QuickClip.Media;
using QuickClip.Gallery;
using static QuickClip.Library.LibraryDatabase;

namespace QuickClip.Library;

internal sealed record ReplayProvenance(Guid Id, MediaSource Media, Guid Session, long NativeSession, long StartedUtcUs,
    long Epoch, long FirstUs, long EndUs, string StreamSignature, string ProofHash)
{
    internal long DurationUs => EndUs - FirstUs;
    internal (Guid, long, long, long, string) Compatibility => (Session, NativeSession, StartedUtcUs, Epoch, StreamSignature);
}

/// <summary>Immutable, local packet metadata. An absent or incomplete row never grants join eligibility.</summary>
internal sealed class ReplayProvenanceStore
{
    private readonly LibraryDatabase _database;
    internal ReplayProvenanceStore(string databasePath) => _database = new(databasePath);
    internal static ReplayProvenanceStore Current { get; } = new(Path.Combine(AppProfile.LocalDirectory, "library", "clips.sqlite3"));
    internal const int MaximumProofBytes = 160_000_000;
    private const ulong Magic = 0x3230464f52504351;
    private const int PacketBytes = 144;

    // The scheduler must own the media and pending proof reservations. Keep both
    // immutable through native validation and the SQLite commit.
    internal ReplayProvenance FinalizeAndAdd(MediaSource media, MediaSource pending, Guid session,
        long firstUs, long endUs, MediaJobContext? job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var mediaHandle = media.OpenVerified();
        using var pendingHandle = pending.OpenVerified();
        using var stage = MediaStaging.Reserve(Path.GetDirectoryName(pending.Path)!, ".proof");
        ReplayNative.FinalizeProof(media.Path, pending.Path, stage.Path, job, ct);
        if (new FileInfo(stage.Path).Length > MaximumProofBytes)
            throw new ExportException("Replay proof exceeds the metadata limit.");
        byte[] proof = File.ReadAllBytes(stage.Path);
        ct.ThrowIfCancellationRequested();
        return Add(media, session, firstUs, endUs, proof);
    }
    internal ReplayProvenance Add(MediaSource media, Guid session, long firstUs, long endUs, byte[] proof)
    {
        if (session == Guid.Empty || media.Stamp.Identity == null || firstUs < 0 || endUs <= firstUs)
            throw new ExportException("This replay does not have sufficient recording provenance.");
        var summary = Inspect(proof);
        if (summary.First != firstUs || summary.Last >= endUs)
            throw new ExportException("Replay coverage does not match its encoded packet provenance.");
        using var mediaHandle = media.OpenVerified();
        var row = new ReplayProvenance(Guid.NewGuid(), media, session, summary.NativeSession, summary.Utc,
            summary.Epoch, firstUs, endUs, summary.Signature, Convert.ToHexString(SHA256.HashData(proof)));
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "UPDATE replay_provenance SET invalidated=1 WHERE path_key=$path;",
            ("$path", LibraryFiles.PathKey(media.Path)));
        Execute(connection, transaction, """
            INSERT INTO replay_provenance(id,path_key,path,created,modified,bytes,identity,session,native_session,
                started_utc_us,epoch,first_us,end_us,stream_signature,proof_hash,proof)
            VALUES($id,$key,$path,$created,$modified,$bytes,$identity,$session,$native,$utc,$epoch,$first,$end,$signature,$hash,$proof);
            """, ("$id", row.Id.ToString("N")), ("$key", LibraryFiles.PathKey(media.Path)), ("$path", media.Path),
            ("$created", media.Stamp.Created), ("$modified", media.Stamp.Modified), ("$bytes", media.Stamp.Bytes),
            ("$identity", media.Stamp.Identity), ("$session", session.ToString("N")), ("$native", row.NativeSession),
            ("$utc", row.StartedUtcUs), ("$epoch", row.Epoch), ("$first", firstUs), ("$end", endUs),
            ("$signature", row.StreamSignature), ("$hash", row.ProofHash), ("$proof", proof));
        transaction.Commit();
        return row;
    }

    internal ReplayProvenance[] ReadCandidates(IReadOnlySet<string> availablePaths)
    {
        using var connection = _database.Open();
        using var command = Command(connection, null, """
            SELECT id,path,created,modified,bytes,identity,session,native_session,started_utc_us,
                epoch,first_us,end_us,stream_signature,proof_hash
            FROM replay_provenance WHERE invalidated=0 ORDER BY session,epoch,stream_signature,first_us,id;
            """);
        using var reader = command.ExecuteReader();
        var result = new List<ReplayProvenance>();
        while (reader.Read())
        {
            string path = reader.GetString(1);
            if (!availablePaths.Contains(LibraryFiles.PathKey(path)) || ClipLibrary.IsMomentsPath(path)) continue;
            var source = new MediaSource(path, new(reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5)));
            try { source.Verify(); }
            catch (Exception ex) when (ex is ExportException || LibraryFiles.IsAccessError(ex)) { continue; }
            result.Add(new(Guid.ParseExact(reader.GetString(0), "N"), source, Guid.ParseExact(reader.GetString(6), "N"),
                reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11),
                reader.GetString(12), reader.GetString(13)));
        }
        return result.ToArray();
    }

    internal byte[] ReadProof(ReplayProvenance snapshot)
    {
        using var mediaHandle = snapshot.Media.OpenVerified();
        using var connection = _database.Open();
        using var command = Command(connection, null,
            "SELECT proof,length(proof) FROM replay_provenance WHERE id=$id AND invalidated=0 AND proof_hash=$hash;",
            ("$id", snapshot.Id.ToString("N")), ("$hash", snapshot.ProofHash));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(1) > MaximumProofBytes)
            throw new ExportException("Replay provenance changed or is unavailable.");
        var proof = (byte[])reader.GetValue(0);
        if (Convert.ToHexString(SHA256.HashData(proof)) != snapshot.ProofHash)
            throw new ExportException("Replay provenance failed its integrity check.");
        return proof;
    }

    internal void Invalidate(string path)
    {
        using var connection = _database.Open();
        Execute(connection, null, "UPDATE replay_provenance SET invalidated=1 WHERE path_key=$key;", ("$key", LibraryFiles.PathKey(path)));
    }

    private static (long NativeSession, long Utc, long Epoch, long First, long Last, string Signature) Inspect(byte[] bytes)
    {
        if (bytes.Length < 36 || bytes.Length > MaximumProofBytes) throw new ExportException("Invalid replay proof size.");
        var data = bytes.AsSpan();
        long Long(int at) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at, 8));
        int Int(int at) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at, 4));
        if (BinaryPrimitives.ReadUInt64LittleEndian(data) != Magic || Int(8) != 1 || Long(12) <= 0 || Long(20) <= 0)
            throw new ExportException("Only validated new recording provenance is eligible.");
        int streams = Int(28), packets = Int(32);
        if (streams is < 1 or > 32 || packets is < 1 or > 1_000_000 ||
            36L + streams * 32L + packets * (long)PacketBytes != bytes.Length)
            throw new ExportException("Replay proof is incomplete.");
        long epoch = 0, first = long.MaxValue, last = -1;
        int begin = 36 + streams * 32;
        for (int n = 0; n < packets; ++n)
        {
            int at = begin + n * PacketBytes;
            if (Int(at) != 0) continue;
            long source = Long(at + 16), time = Long(at + 24);
            if (source <= 0 || (epoch != 0 && epoch != source))
                throw new ExportException("This save crosses recording epochs.");
            epoch = source; first = Math.Min(first, time); last = Math.Max(last, time);
        }
        if (epoch == 0 || first < 0 || last < first) throw new ExportException("Replay video provenance is missing.");
        return (Long(12), Long(20), epoch, first, last, Convert.ToHexString(SHA256.HashData(data.Slice(36, streams * 32))));
    }
}
