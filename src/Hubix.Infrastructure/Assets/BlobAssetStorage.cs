using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;

namespace Hubix.Infrastructure.Assets;

/// <summary>
/// Files in Azure Blob Storage, read through SAS URLs.
/// </summary>
/// <remarks>
/// <para>
/// First run against a real storage account on 19 September 2026: upload, signed read and
/// admin preview all worked. The tests still cover only the local implementation. Previews
/// need a CORS rule on the account allowing <c>GET</c> from the portal's origin; without it
/// every tile shows "no preview".
/// </para>
/// <para>
/// The container is created private. Nothing here ever makes it public: every read goes
/// through a SAS URL that expires, which is the same contract the local implementation
/// honours, so switching between them changes where files live and nothing else.
/// </para>
/// </remarks>
public sealed class BlobAssetStorage : IAssetStorage
{
    private readonly BlobContainerClient _container;

    public BlobAssetStorage(IOptions<AssetOptions> options)
    {
        var settings = options.Value;

        if (string.IsNullOrWhiteSpace(settings.BlobConnectionString))
        {
            throw new InvalidOperationException(
                "Assets:BlobConnectionString is empty. Set it, or leave it unset to use the local folder.");
        }

        _container = new BlobContainerClient(settings.BlobConnectionString, settings.ContainerName);

        // Private. A container that allows anonymous reads makes every expiring link
        // pointless, and it is one checkbox away by default.
        _container.CreateIfNotExists(PublicAccessType.None);
    }

    public string Description => $"Azure container {_container.Name}";

    public async Task<StoredFile> SaveAsync(
        string key,
        Stream content,
        string contentType,
        CancellationToken ct)
    {
        var blob = _container.GetBlobClient(key);

        await blob.UploadAsync(
            content,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
            },
            ct);

        var properties = await blob.GetPropertiesAsync(cancellationToken: ct);

        return new StoredFile(key, properties.Value.ContentLength, contentType);
    }

    public Task<string> GetReadUrlAsync(string key, TimeSpan lifetime, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(key);

        if (!blob.CanGenerateSasUri)
        {
            // Happens when the client was built from a token credential rather than a key.
            // Failing here is better than handing back a URL that quietly does not work.
            throw new InvalidOperationException(
                "This storage account cannot sign URLs. A connection string with a key is needed.");
        }

        var permissions = new BlobSasBuilder
        {
            BlobContainerName = _container.Name,
            BlobName = key,
            Resource = "b",

            // A minute of slack, because a clock a few seconds fast on either side would
            // otherwise reject a link the instant it was issued.
            StartsOn = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresOn = DateTimeOffset.UtcNow.Add(lifetime)
        };

        permissions.SetPermissions(BlobSasPermissions.Read);

        return Task.FromResult(blob.GenerateSasUri(permissions).ToString());
    }

    public async Task<Stream?> OpenAsync(string key, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(key);

        if (!await blob.ExistsAsync(ct))
        {
            return null;
        }

        return await blob.OpenReadAsync(cancellationToken: ct);
    }

    public Task DeleteAsync(string key, CancellationToken ct) =>
        _container.GetBlobClient(key).DeleteIfExistsAsync(cancellationToken: ct);
}
