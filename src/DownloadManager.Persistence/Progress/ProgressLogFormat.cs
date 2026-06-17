using System.Buffers.Binary;
using DownloadManager.Persistence.Io;

namespace DownloadManager.Persistence.Progress;

/// <summary>
/// On-disk layout of the binary append-only progress log (spec §6b). Fixed-size records make
/// recovery a simple linear scan and make a torn tail trivially detectable (misaligned length or a
/// failing CRC).
///
/// <para>
/// Header (16 bytes): magic "DLLOG\0" (6) · format version u16 · reserved u64.<br/>
/// Record (24 bytes): segmentId i32 · durableOffset i64 · sequence i64 · crc32 u32, where the CRC
/// covers the first 20 bytes of the record.
/// </para>
/// </summary>
internal static class ProgressLogFormat
{
    public const int HeaderSize = 16;
    public const int RecordSize = 24;
    public const ushort Version = 1;

    private static ReadOnlySpan<byte> Magic => "DLLOG\0"u8;

    public static void WriteHeader(Span<byte> header)
    {
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], Version);
    }

    public static bool IsValidHeader(ReadOnlySpan<byte> header) =>
        header.Length >= HeaderSize
        && header[..6].SequenceEqual(Magic)
        && BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) == Version;

    public static void WriteRecord(Span<byte> record, int segmentId, long durableOffset, long sequence)
    {
        BinaryPrimitives.WriteInt32LittleEndian(record[..4], segmentId);
        BinaryPrimitives.WriteInt64LittleEndian(record[4..12], durableOffset);
        BinaryPrimitives.WriteInt64LittleEndian(record[12..20], sequence);
        var crc = Crc32.Compute(record[..20]);
        BinaryPrimitives.WriteUInt32LittleEndian(record[20..24], crc);
    }

    /// <summary>
    /// Builds a fresh, compacted log image: a header followed by exactly one record per segment at
    /// its current maximum durable offset, re-sequenced from 0. Used for both recovery-time torn-tail
    /// normalization and threshold compaction (spec §6b).
    /// </summary>
    public static byte[] BuildCompacted(IReadOnlyDictionary<int, long> maxima)
    {
        var segmentIds = maxima.Keys.ToArray();
        Array.Sort(segmentIds);

        var buffer = new byte[HeaderSize + (segmentIds.Length * RecordSize)];
        WriteHeader(buffer.AsSpan(0, HeaderSize));

        var pos = HeaderSize;
        long sequence = 0;
        foreach (var segmentId in segmentIds)
        {
            WriteRecord(buffer.AsSpan(pos, RecordSize), segmentId, maxima[segmentId], sequence++);
            pos += RecordSize;
        }

        return buffer;
    }

    /// <summary>Validates a record's CRC and, if good, decodes its fields.</summary>
    public static bool TryReadRecord(
        ReadOnlySpan<byte> record, out int segmentId, out long durableOffset, out long sequence)
    {
        segmentId = 0;
        durableOffset = 0;
        sequence = 0;

        if (record.Length < RecordSize)
        {
            return false;
        }

        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(record[20..24]);
        if (Crc32.Compute(record[..20]) != storedCrc)
        {
            return false;
        }

        segmentId = BinaryPrimitives.ReadInt32LittleEndian(record[..4]);
        durableOffset = BinaryPrimitives.ReadInt64LittleEndian(record[4..12]);
        sequence = BinaryPrimitives.ReadInt64LittleEndian(record[12..20]);
        return true;
    }
}