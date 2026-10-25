using System.Globalization;
using System.Text.Json;

namespace Vellum.Modules.Integrations.SharePoint;

/// <summary>
/// "Local folder (development)" connection: a stand-in for SharePoint used for development, demos and tests. Files are
/// written to <c>{root}/{host}/{library URL}/{folder}/{file}</c> with a <c>{file}.metadata.json</c> sidecar holding the
/// mapped columns. Overwritten files keep their previous content under <c>.versions</c>, like library version history.
/// A library's simulated columns stand in for its list schema; the profile's "simulate outage" switch makes every call
/// fail as if SharePoint were unreachable.
/// </summary>
public sealed class LocalFolderSharePointClient(SharePointProfile profile, string root) : ISharePointClient
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public const string SidecarSuffix = ".metadata.json";
    public const string VersionsFolder = ".versions";

    private string HostRoot => Path.Combine(Path.GetFullPath(root), SharePointNaming.SanitizeName(profile.Host));

    /// <summary>Absolute directory of a library.</summary>
    public string LibraryRoot(SharePointLibrary lib)
    {
        var segments = SharePointNaming.NormalizeRelativeUrl(lib.RelativeUrl).Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SharePointNaming.SanitizeName);
        return Safe(Path.Combine([HostRoot, .. segments]), HostRoot);
    }

    private static string Safe(string path, string within)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(within).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!(full + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new SharePointException(SpErrors.Config, "The path leaves the library folder.", false);
        return full;
    }

    private string PathOf(SharePointLibrary lib, string relative)
    {
        var libRoot = LibraryRoot(lib);
        var parts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Safe(Path.Combine([libRoot, .. parts]), libRoot);
    }

    private void Online()
    {
        if (profile.SimulateOutage)
            throw new SharePointException(SpErrors.Unavailable, "SharePoint is unavailable (simulated outage on this local-folder profile).", true);
    }

    private static T Io<T>(Func<T> action)
    {
        try { return action(); }
        catch (SharePointException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SharePointException(SpErrors.Unavailable, "The local folder couldn't be written: " + ex.Message, true, inner: ex);
        }
    }

    public Task<SpTestResult> TestAsync(IReadOnlyList<SharePointLibrary> libraries, CancellationToken ct = default)
    {
        Online();
        var checks = new List<SpLibraryCheck>();
        foreach (var lib in libraries)
        {
            try
            {
                var dir = Io(() => Directory.CreateDirectory(LibraryRoot(lib)).FullName);
                var probe = Path.Combine(dir, $".probe-{Guid.NewGuid():N}");
                Io(() => { File.WriteAllText(probe, "ok"); File.Delete(probe); return true; });
                var cols = Columns(lib);
                checks.Add(new SpLibraryCheck(lib.Key, lib.Name, true,
                    cols == null ? "Folder is writable. No simulated columns: any column is accepted." : $"Folder is writable. {cols.Count} simulated column(s).",
                    cols?.Count, dir));
            }
            catch (SharePointException ex)
            {
                checks.Add(new SpLibraryCheck(lib.Key, lib.Name, false, ex.Message));
            }
        }
        var ok = checks.All(c => c.Ok);
        return Task.FromResult(new SpTestResult(ok, ok ? $"Local folder {HostRoot} is ready ({checks.Count} librar{(checks.Count == 1 ? "y" : "ies")})." : "Some libraries failed.", checks));
    }

    public Task<IReadOnlyList<SpColumn>?> GetColumnsAsync(SharePointLibrary library, CancellationToken ct = default)
    {
        Online();
        return Task.FromResult(Columns(library));
    }

    /// <summary>Simulated columns: one per line or comma-separated, optionally "Name:type" (text, note, number, dateTime, boolean…).</summary>
    public static IReadOnlyList<SpColumn>? Columns(SharePointLibrary lib)
    {
        if (string.IsNullOrWhiteSpace(lib.SimulatedColumns)) return null;
        return lib.SimulatedColumns.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(p => p[0].Length > 0)
            .Select(p => new SpColumn(p[0], p[0], p.Length > 1 && p[1].Length > 0 ? p[1] : "text", SharePointMetadata.IsSystemColumn(p[0])))
            .DistinctBy(c => c.Name, StringComparer.Ordinal).ToList();
    }

    public Task EnsureFolderAsync(SharePointLibrary library, string folderPath, CancellationToken ct = default)
    {
        Online();
        Io(() => Directory.CreateDirectory(PathOf(library, folderPath)));
        return Task.CompletedTask;
    }

    public async Task<SpItem> UploadAsync(SharePointLibrary library, string folderPath, string fileName, byte[] content, string contentType, CancellationToken ct = default)
    {
        Online();
        var relative = SharePointNaming.Join(folderPath, fileName);
        var target = PathOf(library, relative);
        Io(() => Directory.CreateDirectory(Path.GetDirectoryName(target)!));
        if (File.Exists(target))
        {
            // Library version history: keep the previous content.
            var versions = PathOf(library, SharePointNaming.Join(VersionsFolder, relative));
            Io(() =>
            {
                Directory.CreateDirectory(versions);
                File.Copy(target, Path.Combine(versions, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{fileName}"), overwrite: true);
                return true;
            });
        }
        try { await File.WriteAllBytesAsync(target, content, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SharePointException(SpErrors.Unavailable, "The local folder couldn't be written: " + ex.Message, true, inner: ex);
        }
        return new SpItem(relative, null, relative);
    }

    public async Task SetFieldsAsync(SharePointLibrary library, SpItem item, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
    {
        Online();
        var cols = Columns(library);
        if (cols != null)
        {
            var unknown = fields.Keys.Where(k => cols.All(c => !string.Equals(c.Name, k, StringComparison.Ordinal))).ToList();
            if (unknown.Count > 0)
                throw new SharePointException(SpErrors.Column, $"Column{(unknown.Count > 1 ? "s" : "")} {string.Join(", ", unknown.Select(u => $"\"{u}\""))} {(unknown.Count > 1 ? "don't" : "doesn't")} exist in {library.Name}.", false);
        }
        var file = PathOf(library, item.Id);
        if (!File.Exists(file)) throw new SharePointException(SpErrors.NotFound, $"{item.Path} doesn't exist.", false);
        var sidecar = new
        {
            file = Path.GetFileName(file),
            path = item.Path,
            updatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            fields,
        };
        try { await File.WriteAllTextAsync(file + SidecarSuffix, JsonSerializer.Serialize(sidecar, Json), ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SharePointException(SpErrors.Unavailable, "The metadata sidecar couldn't be written: " + ex.Message, true, inner: ex);
        }
    }

    public Task<bool> DeleteAsync(SharePointLibrary library, string itemId, CancellationToken ct = default)
    {
        Online();
        var file = PathOf(library, itemId);
        if (!File.Exists(file)) return Task.FromResult(false);
        Io(() =>
        {
            File.Delete(file);
            if (File.Exists(file + SidecarSuffix)) File.Delete(file + SidecarSuffix);
            // Deleting a SharePoint file removes its version history too.
            var versions = PathOf(library, SharePointNaming.Join(VersionsFolder, itemId));
            if (Directory.Exists(versions)) Directory.Delete(versions, recursive: true);
            return true;
        });
        return Task.FromResult(true);
    }

    public Task<SpItem?> MoveAsync(SharePointLibrary library, string itemId, string folderPath, string newName, CancellationToken ct = default)
    {
        Online();
        var file = PathOf(library, itemId);
        if (!File.Exists(file)) return Task.FromResult<SpItem?>(null);
        var relative = SharePointNaming.Join(folderPath, newName);
        var target = PathOf(library, relative);
        Io(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, overwrite: true);
            if (File.Exists(file + SidecarSuffix)) File.Move(file + SidecarSuffix, target + SidecarSuffix, overwrite: true);
            return true;
        });
        return Task.FromResult<SpItem?>(new SpItem(relative, null, relative));
    }
}
