namespace Keystone.Infrastructure.Assets;

/// <summary>A stored file, as the storage layer sees it.</summary>
/// <param name="Key">Where it lives. Opaque, and never shown to anybody.</param>
public sealed record StoredFile(string Key, long SizeBytes, string ContentType);

/// <summary>
/// Where uploaded files live.
/// </summary>
/// <remarks>
/// <para>
/// Two implementations on purpose. A single-implementation interface tends to leak whatever
/// the one implementation happens to do, and the whole point here is that development runs
/// against a folder while production runs against blob storage, with nothing above this line
/// able to tell the difference.
/// </para>
/// <para>
/// There are no permanent URLs. Everything is read through a link that expires, so a floor
/// plan that leaks out of an email is a link that stops working rather than a file that is
/// public for ever. It also means the container itself never needs to allow anonymous reads.
/// </para>
/// </remarks>
public interface IAssetStorage
{
    /// <summary>Human-readable name of where files are going, for the startup log.</summary>
    string Description { get; }

    Task<StoredFile> SaveAsync(
        string key,
        Stream content,
        string contentType,
        CancellationToken ct);

    /// <summary>A link that works for <paramref name="lifetime"/> and then does not.</summary>
    Task<string> GetReadUrlAsync(string key, TimeSpan lifetime, CancellationToken ct);

    Task<Stream?> OpenAsync(string key, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);
}

/// <summary>Configuration for wherever assets are kept.</summary>
public sealed class AssetOptions
{
    public const string Section = "Assets";

    /// <summary>Azure connection string. Empty means the local folder is used instead.</summary>
    public string? BlobConnectionString { get; set; }

    public string ContainerName { get; set; } = "keystone-assets";

    /// <summary>Where files go when there is no blob storage configured.</summary>
    public string LocalPath { get; set; } = "asset-store";

    /// <summary>
    /// Signs local read links.
    /// </summary>
    /// <remarks>
    /// Left empty in development, where a random key is generated per run: links then stop
    /// working when the application restarts, which is a fair trade for having no secret
    /// checked in and no shared default that could reach production by accident.
    /// </remarks>
    public string? SigningKey { get; set; }

    /// <summary>How long a read link lasts. Long enough to load a page, short enough to matter.</summary>
    public int LinkLifetimeMinutes { get; set; } = 30;

    /// <summary>Refused above this. Floor plans are drawings, not videos.</summary>
    public int MaxUploadMegabytes { get; set; } = 25;
}
