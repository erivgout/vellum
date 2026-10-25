using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;

namespace Vellum.Infrastructure.Files;

public sealed class FileIntegrityException(string message) : Exception(message);

/// <summary>
/// Content-addressed file storage (PLT-803). Files are stored once per SHA-256 and verified on every read (INV-053):
/// a mismatch is never served silently.
/// </summary>
public sealed class FileStore(VellumDbContext db, IWebHostEnvironment env, IConfiguration config)
{
    private string Root => Path.GetFullPath(config["Vellum:FileRoot"] ?? Path.Combine(env.ContentRootPath, "App_Data", "blobs"));

    private string PathFor(string sha) => Path.Combine(Root, sha[..2], sha);

    public async Task<(string Sha256, long Size)> SaveAsync(Stream content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Root);
        var temp = Path.Combine(Root, $"upload-{Guid.NewGuid():N}.tmp");
        string sha;
        long size;
        await using (var fs = File.Create(temp))
        using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[81920];
            int read;
            size = 0;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                size += read;
            }
            sha = Convert.ToHexStringLower(hasher.GetHashAndReset());
        }
        var target = PathFor(sha);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target)) File.Delete(temp);
        else File.Move(temp, target);
        if (!await db.FileBlobs.AnyAsync(b => b.Sha256 == sha, ct) && db.FileBlobs.Local.All(b => b.Sha256 != sha))
            db.FileBlobs.Add(new FileBlob { Sha256 = sha, Size = size, StoredAt = DateTime.UtcNow });
        return (sha, size);
    }

    /// <summary>Read and verify. Throws <see cref="FileIntegrityException"/> when content doesn't match its checksum.</summary>
    public async Task<byte[]> ReadVerifiedAsync(string sha, CancellationToken ct = default)
    {
        var p = PathFor(sha);
        if (!File.Exists(p)) throw new FileIntegrityException($"Stored file {sha[..12]}… is missing.");
        var bytes = await File.ReadAllBytesAsync(p, ct);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha)
            throw new FileIntegrityException($"Stored file {sha[..12]}… failed its checksum and was not served.");
        return bytes;
    }

    public bool Verify(string sha)
    {
        var p = PathFor(sha);
        if (!File.Exists(p)) return false;
        using var fs = File.OpenRead(p);
        return Convert.ToHexStringLower(SHA256.HashData(fs)) == sha;
    }
}
