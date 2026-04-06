using Azure.Storage.Blobs;

namespace Shortlister.API.Services;

public sealed class AzureBlobClientAccessor
{
    public BlobServiceClient? Client { get; init; }
}
