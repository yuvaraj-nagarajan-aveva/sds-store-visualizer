using System.Collections.ObjectModel;

namespace Aveva.Sds.StoreVisualizer.Models;

/// <summary>Identifies the physical store format inferred from a file name.</summary>
public enum StoreKind
{
    Unknown,
    Name,
    Header,
    Index,
    Data,
    Object,
    TransactionLog,
}

/// <summary>Identifies the storage layout inferred from the selected files.</summary>
public enum StorageLayoutHint
{
    Unknown,
    Qi,
    StreamGrouping,
}

/// <summary>Describes a request to inspect a local folder.</summary>
public sealed record OpenSessionRequest
{
    /// <summary>Gets the folder path to inspect.</summary>
    public required string RootPath { get; init; }
}

/// <summary>Describes a diagnostic produced while inspecting a store.</summary>
public sealed record DiagnosticResponse(
    string Severity,
    string Message,
    long? Offset = null);

/// <summary>Describes the geometry and scan results for one physical store file.</summary>
public sealed record StoreFileSummary(
    string FileId,
    string RelativePath,
    string FileName,
    long SizeBytes,
    StoreKind Kind,
    StorageLayoutHint LayoutHint,
    int PageSize,
    int? SlotSize,
    int? ItemSize,
    long PageCount,
    int ValidBlockCount,
    int DeletedBlockCount,
    int TerminatorCount,
    long PayloadBytes,
    long AllocatedBytes,
    string DecodeState);

/// <summary>Describes an inspection session and its discovered files.</summary>
public sealed record StoreSessionResponse(
    string SessionId,
    string RootPath,
    DateTimeOffset ScannedAt,
    bool LiveConsistencyWarning,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<StoreFileSummary> Files,
    IReadOnlyList<DiagnosticResponse> Diagnostics);

/// <summary>Describes a decoded name-store slot.</summary>
public sealed record NameEntryResponse(
    uint Token,
    string? Value,
    bool IsCommitted,
    bool IsDeleted,
    string RawHex);

/// <summary>Describes one interpreted header-store slot.</summary>
public sealed record HeaderEntryResponse(
    uint Token,
    string Interpretation,
    int ActualSize,
    IReadOnlyDictionary<string, object?> Fields,
    string RawHex);

/// <summary>Describes one variable-length store block.</summary>
public sealed record BlockResponse(
    int Sequence,
    long Offset,
    int PayloadSize,
    bool IsDeleted,
    long NextOffset,
    string PayloadPreviewHex,
    string? ObjectKey,
    string? JsonPreview);

/// <summary>Describes a bounded raw byte range.</summary>
public sealed record RawRangeResponse(
    long Offset,
    int Length,
    string Hex,
    string Ascii);

/// <summary>Describes the decoded portions of one store file.</summary>
public sealed record StoreFileDetailResponse(
    StoreFileSummary Summary,
    IReadOnlyList<NameEntryResponse> NameEntries,
    IReadOnlyList<HeaderEntryResponse> Headers,
    IReadOnlyList<BlockResponse> Blocks,
    RawRangeResponse RawPreview,
    IReadOnlyList<DiagnosticResponse> Diagnostics);

/// <summary>Provides a stable internal representation of a discovered file.</summary>
public sealed record StoreFileHandle(
    string FileId,
    string FullPath,
    string RelativePath,
    StoreFileSummary Summary,
    StoreFileDetailResponse Detail);

/// <summary>Provides a stable internal representation of an inspection session.</summary>
public sealed class StoreInspectionSession
{
    /// <summary>Initializes a new inspection session.</summary>
    public StoreInspectionSession(string sessionId, string rootPath, StoreSessionResponse response, IReadOnlyDictionary<string, StoreFileHandle> files)
    {
        SessionId = sessionId;
        RootPath = rootPath;
        Response = response;
        Files = new ReadOnlyDictionary<string, StoreFileHandle>(new Dictionary<string, StoreFileHandle>(files, StringComparer.Ordinal));
    }

    /// <summary>Gets the session identifier.</summary>
    public string SessionId { get; }

    /// <summary>Gets the canonical root path.</summary>
    public string RootPath { get; }

    /// <summary>Gets the latest session response.</summary>
    public StoreSessionResponse Response { get; }

    /// <summary>Gets the files discovered in the session.</summary>
    public IReadOnlyDictionary<string, StoreFileHandle> Files { get; }
}
