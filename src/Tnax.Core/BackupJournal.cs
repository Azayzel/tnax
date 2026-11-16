using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SQLite;

namespace Tnax.Core;

public enum BackupState { Pending, Uploading, Verifying, Verified, Failed }

public sealed class ImportedAsset
{
    [PrimaryKey]
    public string Id { get; set; } = "";
    public int ResourceCount { get; set; }
}

public sealed class BackupItem
{
    [PrimaryKey]
    public string Id { get; set; } = "";
    [Indexed]
    public string ProfileId { get; set; } = "";
    [Indexed]
    public string AssetId { get; set; } = "";
    public string Name { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string DestinationJson { get; set; } = "[]";
    public string Sha256 { get; set; } = "";
    public long Length { get; set; }
    public BackupState State { get; set; }
    public string Error { get; set; } = "";
    public int Attempts { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime RetryAfterUtc { get; set; } = DateTime.MinValue;
    public long BackgroundTaskId { get; set; } = -1;

    public string[] Destination() => JsonSerializer.Deserialize<string[]>(DestinationJson) ?? [];

    public static string Identity(params string[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
}

public sealed class BackupJournal : IAsyncDisposable
{
    private readonly SQLiteAsyncConnection database;
    private readonly Task ready;

    public BackupJournal(string path)
    {
        SQLitePCL.Batteries_V2.Init();
        database = new SQLiteAsyncConnection(path, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex);
        ready = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await database.CreateTableAsync<BackupItem>();
        await database.CreateTableAsync<ImportedAsset>();
    }

    public async Task<List<BackupItem>> AssetItemsAsync(string profileId, string assetId)
    {
        await ready;
        return await database.Table<BackupItem>().Where(item => item.ProfileId == profileId && item.AssetId == assetId).ToListAsync();
    }

    public async Task<bool> IsAssetImportedAsync(string profileId, string assetId)
    {
        await ready;
        var imported = await database.FindAsync<ImportedAsset>(BackupItem.Identity(profileId, assetId));
        if (imported is null)
            return false;
        var items = await AssetItemsAsync(profileId, assetId);
        return items.Count == imported.ResourceCount && items.All(item => item.State == BackupState.Verified || File.Exists(item.LocalPath));
    }

    public async Task MarkAssetImportedAsync(string profileId, string assetId, int resourceCount)
    {
        await ready;
        await database.InsertOrReplaceAsync(new ImportedAsset { Id = BackupItem.Identity(profileId, assetId), ResourceCount = resourceCount });
    }

    public async Task<bool> AddAsync(BackupItem item)
    {
        await ready;
        return await database.InsertAsync(item, "OR IGNORE") == 1;
    }

    public async Task UpdateAsync(BackupItem item)
    {
        await ready;
        await database.UpdateAsync(item);
    }

    public async Task<BackupItem?> FindAsync(string id)
    {
        await ready;
        return await database.FindAsync<BackupItem>(id);
    }

    public async Task<List<BackupItem>> ListAsync(string profileId)
    {
        await ready;
        return await database.Table<BackupItem>().Where(item => item.ProfileId == profileId)
            .OrderByDescending(item => item.CreatedUtc).ToListAsync();
    }

    public async Task RecoverForegroundAsync()
    {
        await ready;
        await database.ExecuteAsync("UPDATE BackupItem SET State = ? WHERE BackgroundTaskId = -1 AND State IN (?, ?)",
            BackupState.Pending, BackupState.Uploading, BackupState.Verifying);
    }

    public async Task RetryFailedAsync(string profileId)
    {
        await ready;
        await database.ExecuteAsync("UPDATE BackupItem SET State = ?, Attempts = 0, Error = '', RetryAfterUtc = ? WHERE ProfileId = ? AND State = ?",
            BackupState.Pending, DateTime.MinValue, profileId, BackupState.Failed);
    }

    public async ValueTask DisposeAsync()
    {
        await ready;
        await database.CloseAsync();
    }
}
