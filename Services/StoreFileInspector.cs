using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Aveva.Sds.StoreVisualizer.Models;

namespace Aveva.Sds.StoreVisualizer.Services;

/// <summary>Reads SDS store files without opening them through the SDS mutation pipeline.</summary>
public sealed class StoreFileInspector
{
    private const int NamePageSize = 16_384;
    private const int NameSlotSize = 512;
    private const int HeaderPageSize = 65_536;
    private const int HeaderSlotSize = 64;
    private const int IndexPageSize = 4_096;
    private const int DataPageSize = 16_384;
    private const int BlockAlignment = 128;
    private const int RawPreviewLength = 256;
    private const int PayloadPreviewLength = 64;
    private const int MaxDecodedEntries = 10_000;
    private const int MaxDecodedBlocks = 512;

    /// <summary>Inspects one physical store file and returns bounded decoded content.</summary>
    /// <param name="fileId">The stable identifier assigned to the file for the session.</param>
    /// <param name="fullPath">The full path to the file.</param>
    /// <param name="relativePath">The path relative to the inspection root.</param>
    /// <param name="cancellationToken">The cancellation token for the read.</param>
    /// <returns>A decoded file detail response.</returns>
    public async Task<StoreFileDetailResponse> InspectAsync(
        string fileId,
        string fullPath,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(fullPath);
        var kind = Classify(relativePath);
        var layoutHint = InferLayout(relativePath);
        var geometry = GetGeometry(kind);
        var diagnostics = new List<DiagnosticResponse>();
        var names = new List<NameEntryResponse>();
        var headers = new List<HeaderEntryResponse>();
        var blocks = new List<BlockResponse>();
        var scan = new ScanState();

        await using (var stream = OpenRead(fullPath))
        {
            if (kind == StoreKind.Name)
            {
                await DecodeNamesAsync(stream, fileInfo.Length, names, diagnostics, cancellationToken).ConfigureAwait(false);
            }
            else if (kind == StoreKind.Header)
            {
                await DecodeHeadersAsync(stream, fileInfo.Length, headers, diagnostics, cancellationToken).ConfigureAwait(false);
            }
            else if (kind is StoreKind.Data or StoreKind.Object or StoreKind.Unknown)
            {
                await ScanBlocksAsync(stream, fileInfo.Length, kind, blocks, diagnostics, scan, cancellationToken).ConfigureAwait(false);
            }
        }

        var rawPreview = await ReadRawAsync(fullPath, 0, RawPreviewLength, cancellationToken).ConfigureAwait(false);
        var summary = new StoreFileSummary(
            fileId,
            relativePath,
            fileInfo.Name,
            fileInfo.Length,
            kind,
            layoutHint,
            geometry.PageSize,
            geometry.SlotSize,
            geometry.ItemSize,
            GetPageCount(fileInfo.Length, geometry.PageSize),
            scan.ValidBlockCount,
            scan.DeletedBlockCount,
            scan.TerminatorCount,
            scan.PayloadBytes,
            scan.AllocatedBytes,
            GetDecodeState(kind, diagnostics));

        return new StoreFileDetailResponse(summary, names, headers, blocks, rawPreview, diagnostics);
    }

    /// <summary>Reads a bounded raw range from a store file.</summary>
    /// <param name="fullPath">The full path to the file.</param>
    /// <param name="offset">The byte offset at which to start reading.</param>
    /// <param name="length">The maximum number of bytes to read.</param>
    /// <param name="cancellationToken">The cancellation token for the read.</param>
    /// <returns>The requested raw range.</returns>
    public async Task<RawRangeResponse> ReadRawAsync(
        string fullPath,
        long offset,
        int length,
        CancellationToken cancellationToken)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (length is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var fileLength = new FileInfo(fullPath).Length;
        if (offset > fileLength)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "The offset is outside the file.");
        }

        var available = Math.Min(length, fileLength - offset);
        if (available > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var data = new byte[(int)available];
        await using var stream = OpenRead(fullPath);
        stream.Position = offset;
        await ReadExactlyAsync(stream, data, cancellationToken).ConfigureAwait(false);

        return new RawRangeResponse(offset, data.Length, ToHex(data), ToAscii(data));
    }

    private static FileStream OpenRead(string fullPath)
    {
        return new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static StoreKind Classify(string relativePath)
    {
        var value = relativePath.Replace('\\', '/').ToLowerInvariant();

        if (value.Contains("recordindexnamestore", StringComparison.Ordinal) || value.Contains("namestore", StringComparison.Ordinal))
        {
            return StoreKind.Name;
        }

        if (value.Contains("recordindexidstore", StringComparison.Ordinal) ||
            value.Contains("recordstateidstore", StringComparison.Ordinal) ||
            value.Contains("headerstore", StringComparison.Ordinal))
        {
            return StoreKind.Header;
        }

        if (value.Contains("transaction", StringComparison.Ordinal) || value.EndsWith(".log", StringComparison.Ordinal))
        {
            return StoreKind.TransactionLog;
        }

        if (value.Contains("object", StringComparison.Ordinal) || value.Contains("typestore", StringComparison.Ordinal))
        {
            return StoreKind.Object;
        }

        if (value.Contains("index", StringComparison.Ordinal) || value.Contains("/ri", StringComparison.Ordinal))
        {
            return StoreKind.Index;
        }

        if (value.Contains("data", StringComparison.Ordinal) || value.Contains("/ra", StringComparison.Ordinal))
        {
            return StoreKind.Data;
        }

        return StoreKind.Unknown;
    }

    private static StorageLayoutHint InferLayout(string relativePath)
    {
        var value = relativePath.Replace('\\', '/').ToLowerInvariant();

        if (value.Contains("streamgroup", StringComparison.Ordinal) || value.Contains("indexstore", StringComparison.Ordinal) || value.Contains("datastore", StringComparison.Ordinal))
        {
            return StorageLayoutHint.StreamGrouping;
        }

        if (value.Contains("qirecord", StringComparison.Ordinal))
        {
            return StorageLayoutHint.Qi;
        }

        return StorageLayoutHint.Unknown;
    }

    private static StoreGeometry GetGeometry(StoreKind kind)
    {
        return kind switch
        {
            StoreKind.Name => new StoreGeometry(NamePageSize, NameSlotSize, null),
            StoreKind.Header => new StoreGeometry(HeaderPageSize, HeaderSlotSize, null),
            StoreKind.Index => new StoreGeometry(IndexPageSize, null, null),
            StoreKind.Data or StoreKind.Object => new StoreGeometry(DataPageSize, null, null),
            _ => new StoreGeometry(0, null, null),
        };
    }

    private static long GetPageCount(long length, int pageSize)
    {
        return pageSize <= 0 || length == 0 ? 0 : (length + pageSize - 1) / pageSize;
    }

    private static async Task DecodeNamesAsync(
        FileStream stream,
        long fileLength,
        ICollection<NameEntryResponse> entries,
        ICollection<DiagnosticResponse> diagnostics,
        CancellationToken cancellationToken)
    {
        var slot = new byte[NameSlotSize];
        var count = Math.Min(fileLength / NameSlotSize, MaxDecodedEntries);

        for (uint token = 0; token < count; token++)
        {
            var offset = (long)token * NameSlotSize;
            var read = await ReadAtAsync(stream, offset, slot, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (read != slot.Length)
            {
                diagnostics.Add(new DiagnosticResponse("warning", "The name slot is truncated.", offset));
                break;
            }

            var length = BinaryPrimitives.ReadInt16LittleEndian(slot.AsSpan());
            if (length <= 0)
            {
                continue;
            }

            if (length > NameSlotSize - sizeof(short))
            {
                diagnostics.Add(new DiagnosticResponse("warning", "The name slot contains an invalid UTF-8 length.", offset));
                continue;
            }

            var value = Encoding.UTF8.GetString(slot, sizeof(short), length);
            entries.Add(new NameEntryResponse(token, value, value != "\\", value == "\\", ToHex(slot)));
        }

        if (fileLength / NameSlotSize > MaxDecodedEntries)
        {
            diagnostics.Add(new DiagnosticResponse("info", $"Only the first {MaxDecodedEntries.ToString(CultureInfo.InvariantCulture)} name slots were decoded."));
        }
    }

    private static async Task DecodeHeadersAsync(
        FileStream stream,
        long fileLength,
        ICollection<HeaderEntryResponse> entries,
        ICollection<DiagnosticResponse> diagnostics,
        CancellationToken cancellationToken)
    {
        var slot = new byte[HeaderSlotSize];
        var count = Math.Min(fileLength / HeaderSlotSize, MaxDecodedEntries);

        for (uint token = 0; token < count; token++)
        {
            var offset = (long)token * HeaderSlotSize;
            var read = await ReadAtAsync(stream, offset, slot, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (read != slot.Length)
            {
                diagnostics.Add(new DiagnosticResponse("warning", "The header slot is truncated.", offset));
                break;
            }

            if (IsAllZero(slot))
            {
                continue;
            }

            var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["NextIndexPosition"] = BinaryPrimitives.ReadInt64LittleEndian(slot.AsSpan(0, 8)),
                ["NextDataPosition"] = BinaryPrimitives.ReadInt64LittleEndian(slot.AsSpan(8, 8)),
                ["LastWriteIndex"] = BinaryPrimitives.ReadInt64LittleEndian(slot.AsSpan(16, 8)),
                ["FreeSpace"] = BinaryPrimitives.ReadInt64LittleEndian(slot.AsSpan(24, 8)),
                ["StreamStatus"] = BinaryPrimitives.ReadInt32LittleEndian(slot.AsSpan(32, 4)),
                ["IsLowIngress"] = slot[36] != 0,
                ["DataItemsTotal"] = BinaryPrimitives.ReadInt32LittleEndian(slot.AsSpan(37, 4)),
                ["DataItemsIntervalEnd"] = BinaryPrimitives.ReadInt64LittleEndian(slot.AsSpan(41, 8)),
                ["FirstIndexPosition"] = BinaryPrimitives.ReadInt64LittleEndian(slot.AsSpan(49, 8)),
                ["IndexEntriesTotal"] = BinaryPrimitives.ReadInt32LittleEndian(slot.AsSpan(57, 4)),
            };

            entries.Add(new HeaderEntryResponse(
                token,
                "SdsStreamHeader candidate; correlate with the name store for the exact header type",
                61,
                fields,
                ToHex(slot)));
        }

        if (fileLength / HeaderSlotSize > MaxDecodedEntries)
        {
            diagnostics.Add(new DiagnosticResponse("info", $"Only the first {MaxDecodedEntries.ToString(CultureInfo.InvariantCulture)} header slots were decoded."));
        }
    }

    private static async Task ScanBlocksAsync(
        FileStream stream,
        long fileLength,
        StoreKind kind,
        ICollection<BlockResponse> blocks,
        ICollection<DiagnosticResponse> diagnostics,
        ScanState scan,
        CancellationToken cancellationToken)
    {
        var position = 0L;
        var sequence = 0;

        while (position + sizeof(int) <= fileLength)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sizeBuffer = new byte[sizeof(int)];
            var sizeRead = await ReadAtAsync(stream, position, sizeBuffer, cancellationToken).ConfigureAwait(false);
            if (sizeRead != sizeof(int))
            {
                diagnostics.Add(new DiagnosticResponse("warning", "A block size prefix is truncated.", position));
                break;
            }

            var encodedSize = BinaryPrimitives.ReadInt32LittleEndian(sizeBuffer);
            if (encodedSize == 0)
            {
                scan.TerminatorCount++;
                break;
            }

            if (encodedSize == int.MinValue)
            {
                diagnostics.Add(new DiagnosticResponse("error", "A block uses the invalid Int32.MinValue size marker.", position));
                break;
            }

            var isDeleted = encodedSize < 0;
            var payloadSize = Math.Abs(encodedSize);
            var payloadOffset = position + sizeof(int);
            var nextOffset = Align(position + sizeof(int) + payloadSize, BlockAlignment);

            if (payloadSize > fileLength - payloadOffset)
            {
                diagnostics.Add(new DiagnosticResponse("warning", "A block payload extends beyond the end of the file.", position));
                break;
            }

            scan.PayloadBytes += payloadSize;
            scan.AllocatedBytes += Math.Min(nextOffset, fileLength) - position;
            if (isDeleted)
            {
                scan.DeletedBlockCount++;
            }
            else
            {
                scan.ValidBlockCount++;
            }

            if (blocks.Count < MaxDecodedBlocks)
            {
                var previewLength = Math.Min(payloadSize, PayloadPreviewLength);
                var preview = previewLength == 0
                    ? Array.Empty<byte>()
                    : await ReadBytesAtAsync(stream, payloadOffset, previewLength, cancellationToken).ConfigureAwait(false);
                string? objectKey = null;
                string? jsonPreview = null;

                if (!isDeleted && kind == StoreKind.Object && payloadSize > 0)
                {
                    var objectPayload = await ReadBytesAtAsync(stream, payloadOffset, Math.Min(payloadSize, 262_144), cancellationToken).ConfigureAwait(false);
                    TryDecodeObjectPayload(objectPayload, out objectKey, out jsonPreview);
                }

                blocks.Add(new BlockResponse(sequence, position, payloadSize, isDeleted, nextOffset, ToHex(preview), objectKey, jsonPreview));
            }

            sequence++;
            if (nextOffset <= position)
            {
                diagnostics.Add(new DiagnosticResponse("error", "The next block offset did not advance.", position));
                break;
            }

            if (nextOffset > fileLength)
            {
                diagnostics.Add(new DiagnosticResponse("warning", "A block's alignment padding extends beyond the file.", position));
                break;
            }

            position = nextOffset;
        }

        if (sequence > MaxDecodedBlocks)
        {
            diagnostics.Add(new DiagnosticResponse("info", $"Only the first {MaxDecodedBlocks.ToString(CultureInfo.InvariantCulture)} blocks were decoded."));
        }
    }

    private static void TryDecodeObjectPayload(byte[] payload, out string? key, out string? jsonPreview)
    {
        key = null;
        jsonPreview = null;

        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            key = reader.ReadString();
            var remaining = (int)(stream.Length - stream.Position);
            if (remaining > 0)
            {
                var json = Encoding.UTF8.GetString(reader.ReadBytes(remaining));
                jsonPreview = json.Length > 4_096 ? json[..4_096] : json;
            }
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or ArgumentException)
        {
            key = null;
            jsonPreview = null;
        }
    }

    private static async Task<int> ReadAtAsync(FileStream stream, long offset, byte[] buffer, CancellationToken cancellationToken)
    {
        stream.Position = offset;
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static async Task<byte[]> ReadBytesAtAsync(FileStream stream, long offset, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var read = await ReadAtAsync(stream, offset, buffer, cancellationToken).ConfigureAwait(false);
        return read == length ? buffer : buffer[..read];
    }

    private static async Task ReadExactlyAsync(FileStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = await ReadAtAsync(stream, stream.Position, buffer, cancellationToken).ConfigureAwait(false);
        if (read != buffer.Length)
        {
            Array.Resize(ref buffer, read);
        }
    }

    private static bool IsAllZero(byte[] bytes)
    {
        return bytes.All(value => value == 0);
    }

    private static long Align(long position, int alignment)
    {
        var remainder = position % alignment;
        return remainder == 0 ? position : position + alignment - remainder;
    }

    private static string GetDecodeState(StoreKind kind, ICollection<DiagnosticResponse> diagnostics)
    {
        if (diagnostics.Any(diagnostic => diagnostic.Severity == "error"))
        {
            return "partial";
        }

        return kind == StoreKind.Unknown ? "raw" : "decoded";
    }

    private static string ToHex(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(bytes).Chunk(2).Aggregate(string.Empty, (current, pair) => current + string.Concat(pair) + " ").TrimEnd();
    }

    private static string ToAscii(ReadOnlySpan<byte> bytes)
    {
        return string.Concat(bytes.ToArray().Select(value => value is >= 32 and <= 126 ? (char)value : '.'));
    }

    private sealed class ScanState
    {
        public int ValidBlockCount { get; set; }

        public int DeletedBlockCount { get; set; }

        public int TerminatorCount { get; set; }

        public long PayloadBytes { get; set; }

        public long AllocatedBytes { get; set; }
    }

    private readonly record struct StoreGeometry(int PageSize, int? SlotSize, int? ItemSize);
}
