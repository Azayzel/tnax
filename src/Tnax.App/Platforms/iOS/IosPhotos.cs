using Foundation;
using Photos;
using System.Text.Json;
using Tnax.Core;

namespace Tnax.App;

internal static class IosPhotos
{
    private static readonly SemaphoreSlim ScanGate = new(1, 1);
    public static string PermissionLabel => PHPhotoLibrary.GetAuthorizationStatus(PHAccessLevel.ReadWrite) == PHAuthorizationStatus.Limited
        ? "Limited Photos access" : "Full Photos access";

    public static async Task EnsurePermissionAsync(bool prompt)
    {
        var status = PHPhotoLibrary.GetAuthorizationStatus(PHAccessLevel.ReadWrite);
        if (status == PHAuthorizationStatus.NotDetermined && prompt)
        {
            var completion = new TaskCompletionSource<PHAuthorizationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            MainThread.BeginInvokeOnMainThread(() => PHPhotoLibrary.RequestAuthorization(PHAccessLevel.ReadWrite, completion.SetResult));
            status = await completion.Task;
        }
        if (status is not (PHAuthorizationStatus.Authorized or PHAuthorizationStatus.Limited))
            throw new InvalidOperationException("Photos access is unavailable. Allow TNAX access in iPhone Settings.");
    }

    public static async Task<int> ImportAsync(BackupService service, bool prompt, CancellationToken cancellationToken)
    {
        await EnsurePermissionAsync(prompt);
        if (service.Connection is null)
            throw new InvalidOperationException("Connect to your WebDAV folder first.");
        if (!await ScanGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("A Photos scan is already running.");
        try
        {
            using var options = new PHFetchOptions { SortDescriptors = [new NSSortDescriptor("creationDate", true)] };
            using var assets = PHAsset.FetchAssets(options);
            var imported = 0;
            for (nint position = 0; position < assets.Count && imported < 24; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var asset = (PHAsset)assets.ObjectAt(position);
                var version = BackupItem.Identity(asset.LocalIdentifier, asset.ModificationDate?.SecondsSinceReferenceDate.ToString("R") ?? "original");
                if (await service.Journal.IsAssetImportedAsync(service.ProfileId, version))
                    continue;
                var resources = PHAssetResource.GetAssetResources(asset);
                if (resources.Length == 0)
                    continue;
                var created = asset.CreationDate is null ? DateTime.UnixEpoch : ((DateTime)asset.CreationDate).ToUniversalTime();
                foreach (var resource in resources)
                {
                    await service.StageProducedAsync((output, budget, token) => ExportAsync(resource, output, budget, service, token),
                        version, $"{(int)resource.ResourceType}_{resource.OriginalFilename}", created, cancellationToken);
                }
                var jobs = await service.Journal.AssetItemsAsync(service.ProfileId, version);
                var manifest = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Version = 1, Asset = version, CreatedUtc = created,
                    Resources = resources.Select(resource => new
                    {
                        Type = resource.ResourceType.ToString(), resource.OriginalFilename,
                        Files = jobs.Where(job => job.Name == $"{(int)resource.ResourceType}_{resource.OriginalFilename}")
                            .Select(job => new { job.Sha256, job.Length, Destination = job.Destination() })
                    })
                });
                await using var manifestStream = new MemoryStream(manifest);
                await service.StageAsync(manifestStream, version, "_tnax_asset.json", created, cancellationToken);
                var all = await service.Journal.AssetItemsAsync(service.ProfileId, version);
                await service.Journal.MarkAssetImportedAsync(service.ProfileId, version, all.Count);
                imported++;
                service.NotifyChanged();
            }
            return imported;
        }
        finally { ScanGate.Release(); }
    }

    private static async Task ExportAsync(PHAssetResource resource, Stream output, long budget,
        BackupService service, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = PHAssetResourceManager.DefaultManager;
        using var options = new PHAssetResourceRequestOptions { NetworkAccessAllowed = service.NetworkAllowed() };
        Exception? failure = null;
        long written = 0;
        var requestId = 0;
        requestId = manager.RequestData(resource, options, data =>
        {
            if (failure is not null)
                return;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!service.NetworkAllowed())
                    throw new InvalidOperationException("Photos export paused. Waiting for Wi-Fi.");
                written += checked((long)data.Length);
                if (written > budget)
                    throw new InvalidOperationException("The 2 GB staging limit is full. Finish queued backups before importing more Photos.");
                using var stream = data.AsStream();
                stream.CopyTo(output);
            }
            catch (Exception exception)
            {
                failure = exception;
                if (requestId != 0)
                    manager.CancelDataRequest(requestId);
            }
        }, error =>
        {
            if (failure is not null)
                completion.TrySetException(failure);
            else if (error is not null)
                completion.TrySetException(new IOException("Photos could not supply an original. Its iCloud download may be unavailable."));
            else
                completion.TrySetResult();
        });
        if (failure is not null)
            manager.CancelDataRequest(requestId);
        using var cancellation = cancellationToken.Register(() => manager.CancelDataRequest(requestId));
        await completion.Task;
    }
}
