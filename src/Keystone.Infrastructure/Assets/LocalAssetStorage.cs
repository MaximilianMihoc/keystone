using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Keystone.Infrastructure.Assets;

/// <summary>
/// Files in a folder, read through links that expire.
/// </summary>
/// <remarks>
/// Development and any deployment without blob storage. Deliberately not a no-op or a plain
/// static folder: it signs and expires links exactly as the blob implementation does, so a
/// difference between the two shows up here rather than in production.
/// </remarks>
public sealed class LocalAssetStorage : IAssetStorage
{
    private readonly string _root;
    private readonly byte[] _signingKey;
    private readonly TimeProvider _clock;

    public LocalAssetStorage(IOptions<AssetOptions> options, TimeProvider clock)
    {
        var settings = options.Value;

        _root = Path.GetFullPath(settings.LocalPath);
        _clock = clock;

        _signingKey = string.IsNullOrWhiteSpace(settings.SigningKey)
            ? RandomNumberGenerator.GetBytes(32)
            : Encoding.UTF8.GetBytes(settings.SigningKey);

        Directory.CreateDirectory(_root);
    }

    public string Description => $"local folder {_root}";

    public async Task<StoredFile> SaveAsync(
        string key,
        Stream content,
        string contentType,
        CancellationToken ct)
    {
        var path = PathFor(key);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using (var file = File.Create(path))
        {
            await content.CopyToAsync(file, ct);
        }

        return new StoredFile(key, new FileInfo(path).Length, contentType);
    }

    /// <summary>
    /// A link that works for <paramref name="lifetime"/> and then does not.
    /// </summary>
    /// <remarks>
    /// The key goes in unescaped. It is read back by a catch-all route, which takes the
    /// separators literally and does not decode <c>%2F</c> — escaping it produced a link whose
    /// signature was computed over one string and checked against another, so every image on
    /// the site 404'd while every test passed.
    /// </remarks>
    public Task<string> GetReadUrlAsync(string key, TimeSpan lifetime, CancellationToken ct)
    {
        var expires = _clock.GetUtcNow().Add(lifetime).ToUnixTimeSeconds();

        return Task.FromResult($"/assets/{key}?expires={expires}&signature={Sign(key, expires)}");
    }

    public Task<Stream?> OpenAsync(string key, CancellationToken ct)
    {
        var path = PathFor(key);

        return Task.FromResult<Stream?>(
            File.Exists(path) ? File.OpenRead(path) : null);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = PathFor(key);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <summary>Whether a link is genuine and still current.</summary>
    public bool IsValid(string key, long expires, string signature)
    {
        if (expires < _clock.GetUtcNow().ToUnixTimeSeconds())
        {
            return false;
        }

        // Fixed-time comparison: a signature check that returns early leaks how much of the
        // signature was right, one byte at a time.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Sign(key, expires)),
            Encoding.UTF8.GetBytes(signature));
    }

    private string Sign(string key, long expires) =>
        Convert.ToHexString(HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes($"{key}:{expires}")));

    /// <summary>
    /// The file for a key, refusing anything that would climb out of the folder.
    /// </summary>
    /// <remarks>
    /// Keys are generated here and never typed by anybody, but this path is reachable from a
    /// URL. A traversal check on a value that "cannot" be hostile costs one comparison.
    /// </remarks>
    private string PathFor(string key)
    {
        var path = Path.GetFullPath(Path.Combine(_root, key));

        if (!path.StartsWith(_root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("That asset key is not inside the store.");
        }

        return path;
    }
}
