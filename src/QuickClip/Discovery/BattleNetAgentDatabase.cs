using System.Text;

namespace QuickClip.Discovery;

/// <summary>Reads the product ID and directory fields of the Battle.net Agent's protobuf inventory.</summary>
internal static class BattleNetAgentDatabase
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static IEnumerable<LauncherInstall?> Read(byte[] bytes)
    {
        var message = new WireFields(bytes);
        while (!message.Done)
        {
            if (!message.Next(out int id, out var field))
            {
                yield return new(Issue: CatalogIssueKind.InvalidManifest);
                yield break;
            }
            yield return id == 1 ? ReadProduct(field) : null;
        }
    }

    private static LauncherInstall? ReadProduct(ReadOnlyMemory<byte> bytes)
    {
        var product = new WireFields(bytes);
        string? code = null;
        string? directory = null;
        try
        {
            while (!product.Done)
            {
                if (!product.Next(out int number, out var data)) return new(Issue: CatalogIssueKind.InvalidManifest);
                if (number == 1 && data.Length <= 128) code = Utf8.GetString(data.Span);
                if (number != 3) continue;
                var settings = new WireFields(data);
                while (!settings.Done)
                {
                    if (!settings.Next(out int key, out var value)) return new(Issue: CatalogIssueKind.InvalidManifest);
                    if (key == 1 && value.Length <= 32_768) directory = Utf8.GetString(value.Span);
                }
            }
            return code == null ? new(Issue: CatalogIssueKind.InvalidManifest) :
                LocalLaunchers.BattleNetProduct(code.ToLowerInvariant(), null, directory);
        }
        catch (DecoderFallbackException) { return new(Issue: CatalogIssueKind.InvalidManifest); }
    }

    private struct WireFields(ReadOnlyMemory<byte> bytes)
    {
        private ReadOnlyMemory<byte> _remaining = bytes;
        public readonly bool Done => _remaining.IsEmpty;

        public bool Next(out int number, out ReadOnlyMemory<byte> data)
        {
            number = 0;
            data = default;
            if (!Varint(out ulong tag) || tag < 8 || tag > uint.MaxValue) return false;
            switch (tag & 7)
            {
                case 0: return Varint(out _);
                case 1: return Consume(8, out _);
                case 2:
                    number = (int)(tag >> 3);
                    return Varint(out ulong length) && length <= int.MaxValue && Consume((int)length, out data);
                case 5: return Consume(4, out _);
                default: return false;
            }
        }

        private bool Consume(int count, out ReadOnlyMemory<byte> data)
        {
            data = default;
            if (count > _remaining.Length) return false;
            data = _remaining[..count];
            _remaining = _remaining[count..];
            return true;
        }

        private bool Varint(out ulong value)
        {
            value = 0;
            for (int shift = 0; shift < 64 && !_remaining.IsEmpty; shift += 7)
            {
                byte next = _remaining.Span[0];
                _remaining = _remaining[1..];
                if (shift == 63 && next > 1) return false;
                value |= (ulong)(next & 127) << shift;
                if (next < 128) return true;
            }
            return false;
        }
    }
}
