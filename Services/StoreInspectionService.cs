using System.Collections.Concurrent;
using Aveva.Sds.StoreVisualizer.Models;

namespace Aveva.Sds.StoreVisualizer.Services;

/// <summary>Manages short-lived read-only store inspection sessions.</summary>
public sealed class StoreInspectionService
{
    private readonly StoreFileInspector _fileInspector;
    private readonly ConcurrentDictionary<string, StoreInspectionSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>Initializes a new inspection service.</summary>
    /// <param name="fileInspector">The file decoder.</param>
    public StoreInspectionService(StoreFileInspector fileInspector)
    {
        _fileInspector = fileInspector;
    }

    /// <summary>Opens a folder and scans its files into a new inspection session.</summary>
    /// <param name="rootPath">The folder to inspect.</param>
    /// <param name="cancellationToken">The cancellation token for the scan.</param>
    /// <returns>The newly created session.</returns>
    public async Task<StoreSessionResponse> OpenAsync(string rootPath, CancellationToken cancellationToken)
    {
        var canonicalRoot = CanonicalizeRoot(rootPath);
        var sessionId = Guid.NewGuid().ToString("N");
        var session = await BuildSessionAsync(sessionId, canonicalRoot, cancellationToken).ConfigureAwait(false);
        _sessions[sessionId] = session;
        return session.Response;
    }

    /// <summary>Refreshes an existing inspection session.</summary>
    /// <param name="sessionId">The inspection session identifier.</param>
    /// <param name="cancellationToken">The cancellation token for the scan.</param>
    /// <returns>The refreshed session, or <see langword="null" /> when it does not exist.</returns>
    public async Task<StoreInspectionSession?> RefreshAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var existing))
        {
            return null;
        }

        var refreshed = await BuildSessionAsync(sessionId, existing.RootPath, cancellationToken).ConfigureAwait(false);
        _sessions[sessionId] = refreshed;
        return refreshed;
    }

    /// <summary>Gets a session by identifier.</summary>
    /// <param name="sessionId">The inspection session identifier.</param>
    /// <param name="session">When this method returns, contains the session if found.</param>
    /// <returns><see langword="true" /> when the session exists; otherwise, <see langword="false" />.</returns>
    public bool TryGetSession(string sessionId, out StoreInspectionSession session)
    {
        return _sessions.TryGetValue(sessionId, out session!);
    }

    /// <summary>Gets a file from a session by its stable identifier.</summary>
    /// <param name="sessionId">The inspection session identifier.</param>
    /// <param name="fileId">The file identifier.</param>
    /// <param name="file">When this method returns, contains the file if found.</param>
    /// <returns><see langword="true" /> when the file exists; otherwise, <see langword="false" />.</returns>
    public bool TryGetFile(string sessionId, string fileId, out StoreFileHandle file)
    {
        file = null!;
        return _sessions.TryGetValue(sessionId, out var session) && session.Files.TryGetValue(fileId, out file);
    }

    /// <summary>Reads a bounded byte range from a file in a session.</summary>
    /// <param name="sessionId">The inspection session identifier.</param>
    /// <param name="fileId">The file identifier.</param>
    /// <param name="offset">The byte offset.</param>
    /// <param name="length">The requested byte count.</param>
    /// <param name="cancellationToken">The cancellation token for the read.</param>
    /// <returns>The raw range, or <see langword="null" /> when the session or file does not exist.</returns>
    public async Task<RawRangeResponse?> ReadRawAsync(
        string sessionId,
        string fileId,
        long offset,
        int length,
        CancellationToken cancellationToken)
    {
        if (!TryGetFile(sessionId, fileId, out var file))
        {
            return null;
        }

        return await _fileInspector.ReadRawAsync(file.FullPath, offset, length, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a session from memory.</summary>
    /// <param name="sessionId">The inspection session identifier.</param>
    /// <returns><see langword="true" /> when the session was removed; otherwise, <see langword="false" />.</returns>
    public bool RemoveSession(string sessionId)
    {
        return _sessions.TryRemove(sessionId, out _);
    }

    private async Task<StoreInspectionSession> BuildSessionAsync(
        string sessionId,
        string rootPath,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, StoreFileHandle>(StringComparer.Ordinal);
        var diagnostics = new List<DiagnosticResponse>();
        var totalBytes = 0L;

        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new DiagnosticResponse("error", exception.Message));
            paths = Array.Empty<string>();
        }

        foreach (var fullPath in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string canonicalPath;
            try
            {
                canonicalPath = Path.GetFullPath(fullPath);
            }
            catch (IOException exception)
            {
                diagnostics.Add(new DiagnosticResponse("warning", exception.Message));
                continue;
            }

            if (!IsUnderRoot(rootPath, canonicalPath))
            {
                diagnostics.Add(new DiagnosticResponse("warning", $"Skipped a path outside the selected root: {fullPath}"));
                continue;
            }

            var relativePath = Path.GetRelativePath(rootPath, canonicalPath);
            var fileId = Guid.NewGuid().ToString("N");

            try
            {
                var detail = await _fileInspector.InspectAsync(fileId, canonicalPath, relativePath, cancellationToken).ConfigureAwait(false);
                files.Add(fileId, new StoreFileHandle(fileId, canonicalPath, relativePath, detail.Summary, detail));
                totalBytes += detail.Summary.SizeBytes;
            }
            catch (FileNotFoundException)
            {
                diagnostics.Add(new DiagnosticResponse("warning", $"The file disappeared while scanning: {relativePath}"));
            }
            catch (UnauthorizedAccessException)
            {
                diagnostics.Add(new DiagnosticResponse("warning", $"The file could not be read: {relativePath}"));
            }
            catch (IOException exception)
            {
                diagnostics.Add(new DiagnosticResponse("warning", $"Could not inspect {relativePath}: {exception.Message}"));
            }
        }

        var response = new StoreSessionResponse(
            sessionId,
            rootPath,
            DateTimeOffset.UtcNow,
            true,
            files.Count,
            totalBytes,
            files.Values.Select(file => file.Summary).OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            diagnostics);

        return new StoreInspectionSession(sessionId, rootPath, response, files);
    }

    private static string CanonicalizeRoot(string rootPath)
    {
        var canonicalRoot = Path.GetFullPath(rootPath.Trim());
        if (!Directory.Exists(canonicalRoot))
        {
            throw new DirectoryNotFoundException(canonicalRoot);
        }

        return canonicalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsUnderRoot(string rootPath, string candidatePath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootWithSeparator = rootPath.EndsWith(Path.DirectorySeparatorChar) ? rootPath : rootPath + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(rootWithSeparator, comparison) || string.Equals(candidatePath, rootPath, comparison);
    }
}
