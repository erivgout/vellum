namespace Vellum.Modules.Integrations.SharePoint;

/// <summary>
/// The SharePoint operations publishing needs (INT-304/305/308). Implemented over Microsoft Graph
/// (<see cref="GraphSharePointClient"/>) and over a local folder for development, demos and tests
/// (<see cref="LocalFolderSharePointClient"/>). Every failure is a <see cref="SharePointException"/> with a stable code.
/// </summary>
public interface ISharePointClient
{
    /// <summary>Check credentials and that every library resolves; reports the columns found.</summary>
    Task<SpTestResult> TestAsync(IReadOnlyList<SharePointLibrary> libraries, CancellationToken ct = default);

    /// <summary>The library's columns, or null when the schema is unknown (any column is accepted).</summary>
    Task<IReadOnlyList<SpColumn>?> GetColumnsAsync(SharePointLibrary library, CancellationToken ct = default);

    /// <summary>Create the folder (and its parents) when missing (DOC-084). Empty path = the library root.</summary>
    Task EnsureFolderAsync(SharePointLibrary library, string folderPath, CancellationToken ct = default);

    /// <summary>Upload (create or overwrite) a file; large files use a resumable upload session.</summary>
    Task<SpItem> UploadAsync(SharePointLibrary library, string folderPath, string fileName, byte[] content, string contentType, CancellationToken ct = default);

    /// <summary>Set the item's list fields (column internal name → value).</summary>
    Task SetFieldsAsync(SharePointLibrary library, SpItem item, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);

    /// <summary>Delete an item. Returns false when it no longer exists.</summary>
    Task<bool> DeleteAsync(SharePointLibrary library, string itemId, CancellationToken ct = default);

    /// <summary>Move and/or rename an item. Returns null when it no longer exists.</summary>
    Task<SpItem?> MoveAsync(SharePointLibrary library, string itemId, string folderPath, string newName, CancellationToken ct = default);
}

/// <param name="Id">Graph drive item ID, or the library-relative path for the local folder.</param>
public sealed record SpItem(string Id, string? WebUrl, string Path);

/// <param name="Type">text | note | number | currency | dateTime | boolean | choice | person | lookup | other</param>
public sealed record SpColumn(string Name, string? DisplayName, string Type, bool ReadOnly);

public sealed record SpLibraryCheck(string Key, string Name, bool Ok, string Message, int? ColumnCount = null, string? ResolvedId = null);

public sealed record SpTestResult(bool Ok, string Summary, IReadOnlyList<SpLibraryCheck> Libraries);

/// <summary>A publishing error with a specific code (INT-306) and whether retrying can help (NFR-025).</summary>
public sealed class SharePointException(string code, string message, bool retryable, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Stable error codes shown in logs, notifications and on the record.</summary>
public static class SpErrors
{
    public const string Auth = "SP-AUTH";
    public const string Access = "SP-ACCESS";
    public const string NotFound = "SP-NOTFOUND";
    public const string Column = "SP-COLUMN";
    public const string Throttled = "SP-THROTTLED";
    public const string Unavailable = "SP-UNAVAILABLE";
    public const string Network = "SP-NETWORK";
    public const string Conflict = "SP-CONFLICT";
    public const string NeverOverwrite = "SP-INV163";
    public const string RenditionPending = "SP-RENDITION";
    public const string Secret = "SP-SECRET";
    public const string Config = "SP-CONFIG";
    public const string File = "SP-FILE";
    public const string Unknown = "SP-ERROR";

    public static string Describe(string? code) => code switch
    {
        Auth => "Authentication failed: check the tenant ID, client ID and secret or certificate.",
        Access => "Access denied: grant the app registration access to this site (Sites.Selected).",
        NotFound => "The site, library or item doesn't exist.",
        Column => "A mapped column doesn't exist in the library or can't be written.",
        Throttled => "SharePoint throttled the request; it will be retried.",
        Unavailable => "SharePoint is unavailable; it will be retried.",
        Network => "SharePoint couldn't be reached; it will be retried.",
        Conflict => "Another record's file is already published at this location.",
        NeverOverwrite => "Blocked: a completed revision's published file is never overwritten by a pending revision (INV-163).",
        RenditionPending => "Waiting for the PDF rendition of the attachment.",
        Secret => "The stored credential can't be decrypted; enter it again on the profile.",
        Config => "The publishing configuration is incomplete or invalid.",
        File => "The attachment's stored content is missing or failed its checksum.",
        _ => "Unexpected publishing error.",
    };
}
