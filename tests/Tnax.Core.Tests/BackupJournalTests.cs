using Tnax.Core;

namespace Tnax.Core.Tests;

public sealed class BackupJournalTests
{
    [Fact]
    public async Task QueueSurvivesRestartAndDoesNotDuplicateJobs()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "queue.db");
            await using (var journal = new BackupJournal(path))
            {
                Assert.True(await journal.AddAsync(new BackupItem { Id = "photo", ProfileId = "person-a", State = BackupState.Uploading }));
                Assert.False(await journal.AddAsync(new BackupItem { Id = "photo", ProfileId = "person-a" }));
                await journal.AddAsync(new BackupItem { Id = "background", ProfileId = "person-a", State = BackupState.Uploading, BackgroundTaskId = 7 });
                await journal.AddAsync(new BackupItem { Id = "other", ProfileId = "person-b" });
            }
            await using (var journal = new BackupJournal(path))
            {
                await journal.RecoverForegroundAsync();
                Assert.Equal(2, (await journal.ListAsync("person-a")).Count);
                Assert.Equal(BackupState.Pending, (await journal.FindAsync("photo"))!.State);
                Assert.Equal(BackupState.Uploading, (await journal.FindAsync("background"))!.State);
                Assert.Single(await journal.ListAsync("person-b"));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AssetIsNotImportedUntilEveryResourceIsAccountedFor()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var journal = new BackupJournal(Path.Combine(directory, "queue.db"));
            await journal.AddAsync(new BackupItem { Id = "still", ProfileId = "person", AssetId = "live-photo", State = BackupState.Verified });
            await journal.MarkAssetImportedAsync("person", "live-photo", 2);
            Assert.False(await journal.IsAssetImportedAsync("person", "live-photo"));
            await journal.AddAsync(new BackupItem { Id = "video", ProfileId = "person", AssetId = "live-photo", State = BackupState.Pending, LocalPath = Path.Combine(directory, "missing.mov") });
            Assert.False(await journal.IsAssetImportedAsync("person", "live-photo"));
            var video = (await journal.FindAsync("video"))!;
            video.State = BackupState.Verified;
            await journal.UpdateAsync(video);
            Assert.True(await journal.IsAssetImportedAsync("person", "live-photo"));
            Assert.False(await journal.IsAssetImportedAsync("another-person", "live-photo"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ProfileIdentityCannotConfuseDelimiterBoundaries()
    {
        Assert.NotEqual(BackupItem.Identity("ab", "c"), BackupItem.Identity("a", "bc"));
        Assert.Equal(BackupItem.Identity("same", "account"), BackupItem.Identity("same", "account"));
    }
}
