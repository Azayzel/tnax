using System.Security.Cryptography;
using System.Text.Json;
using Tnax.Core;

namespace Tnax.App;

public sealed record SavedConnection(string Address, string Username, string Password, NasPairing? Pairing = null);

public sealed partial class BackupService
{
    private const long StagingLimit = 2L * 1024 * 1024 * 1024;
    private readonly string staging = Path.Combine(FileSystem.AppDataDirectory, "staging");
    private readonly SemaphoreSlim stagingGate = new(1, 1);
    private readonly object initializationLock = new();
    private Task? initialization;
    private CancellationTokenSource? transferCancellation;
    public BackupJournal Journal { get; } = new(Path.Combine(FileSystem.AppDataDirectory, "backup.db"));
    public SavedConnection? Connection { get; private set; }
    public NasPairing? Pairing { get; private set; }
    public string DeviceId { get; }
    public bool IsRunning { get; private set; }
    public bool FolderVerified { get; private set; }
    public event EventHandler? Changed;
    public string ProfileId => Connection is null ? "" : BackupItem.Identity(Connection.Address, Connection.Username);
    public bool WifiOnly
    {
        get => Preferences.Default.Get("wifi-only", true);
        set => Preferences.Default.Set("wifi-only", value);
    }

    public BackupService()
    {
        Directory.CreateDirectory(staging);
        DeviceId = Preferences.Default.Get("device-id", Guid.NewGuid().ToString("N"));
        Preferences.Default.Set("device-id", DeviceId);
        Connectivity.Current.ConnectivityChanged += (_, _) => { if (!NetworkAllowed()) Pause(); };
    }

    public Task InitializeAsync()
    {
        lock (initializationLock)
            return initialization ??= InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        var stored = await SecureStorage.Default.GetAsync("nas-connection");
        if (stored is not null)
            Connection = JsonSerializer.Deserialize<SavedConnection>(stored);
        var pairingData = await SecureStorage.Default.GetAsync("nas-pairing");
        Pairing = Connection?.Pairing ?? (pairingData is null ? null : NasPairing.Parse(System.Text.Encoding.UTF8.GetBytes(pairingData)));
        await Journal.RecoverForegroundAsync();
        foreach (var file in Directory.EnumerateFiles(staging, "*.partial"))
            File.Delete(file);
    #if IOS
        await IosTransfers.Shared.AttachAsync(this);
    #endif
    }

    public async Task ConnectAsync(string address, string username, string password, CancellationToken cancellationToken = default)
    {
        EnsureConnectionEditable();
        var endpoint = NasEndpoint.Parse(address);
        password = ResolvePassword(endpoint, username.Trim(), password);
        using var client = CreateClient(endpoint, username.Trim(), password);
        await client.ProbeAsync(cancellationToken);
        var connection = new SavedConnection(endpoint.BaseUri.AbsoluteUri, username.Trim(), password, Pairing);
        await SecureStorage.Default.SetAsync("nas-connection", JsonSerializer.Serialize(connection));
        Connection = connection;
        FolderVerified = true;
        NotifyChanged();
    }

    public async Task SetPairingAsync(NasPairing? pairing)
    {
        EnsureConnectionEditable();
        pairing?.Validate();
        if (pairing is null)
            SecureStorage.Default.Remove("nas-pairing");
        else
            await SecureStorage.Default.SetAsync("nas-pairing", System.Text.Encoding.UTF8.GetString(pairing.Export()));
        SecureStorage.Default.Remove("nas-connection");
        Connection = null;
        Pairing = pairing;
        FolderVerified = false;
        NotifyChanged();
    }

    public async Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(string address, string username, string password, CancellationToken cancellationToken)
    {
        EnsureConnectionEditable();
        var endpoint = NasEndpoint.Parse(address);
        using var client = CreateClient(endpoint, username.Trim(), ResolvePassword(endpoint, username.Trim(), password));
        return await client.ListFoldersAsync(cancellationToken);
    }

    private string ResolvePassword(NasEndpoint endpoint, string username, string password)
    {
        if (string.IsNullOrEmpty(password) && Connection is not null && Connection.Username == username &&
            new Uri(Connection.Address).GetLeftPart(UriPartial.Authority) == endpoint.BaseUri.GetLeftPart(UriPartial.Authority))
            return Connection.Password;
        return password;
    }

    private void EnsureConnectionEditable()
    {
        if (IsRunning)
            throw new InvalidOperationException("Pause backup before changing connections.");
#if IOS
        if (IosTransfers.Shared.HasActiveTasks)
            throw new InvalidOperationException("Pause background uploads before changing connections.");
#endif
    }

    public WebDavClient CreateClient()
    {
        var connection = Connection ?? throw new InvalidOperationException("Connect to your WebDAV folder first.");
        return CreateClient(NasEndpoint.Parse(connection.Address), connection.Username, connection.Password);
    }

    private WebDavClient CreateClient(NasEndpoint endpoint, string username, string password)
    {
        if (Pairing is not null && !Pairing.Allows(endpoint.BaseUri))
            throw new InvalidOperationException("This setup belongs to another NAS. Import its setup file or remove the current pairing.");
#if IOS
        var configuration = Foundation.NSUrlSessionConfiguration.DefaultSessionConfiguration;
        configuration.AllowsCellularAccess = !WifiOnly;
        configuration.AllowsExpensiveNetworkAccess = !WifiOnly;
        HttpMessageHandler handler = Pairing is null
            ? new NSUrlSessionHandler(configuration) { AllowAutoRedirect = false }
            : new IosPairedHandler(Pairing, configuration);
        return new WebDavClient(endpoint, username, password, handler);
#else
        return new WebDavClient(endpoint, username, password, Pairing?.CreateHttpHandler(endpoint));
#endif
    }

    public async Task<int> ImportFilesAsync(IEnumerable<FileResult> files, CancellationToken cancellationToken = default)
    {
        if (Connection is null)
            throw new InvalidOperationException("Connect to your WebDAV folder first.");
        var added = 0;
        foreach (var file in files)
        {
            await using var stream = await file.OpenReadAsync();
            if (await StageAsync(stream, BackupItem.Identity("file", file.FileName), file.FileName, DateTime.UtcNow, cancellationToken) is not null)
                added++;
        }
        NotifyChanged();
        return added;
    }

    public Task<BackupItem?> StageAsync(Stream source, string assetId, string name, DateTime createdUtc, CancellationToken cancellationToken) =>
        StageProducedAsync(async (output, budget, token) =>
        {
            var buffer = new byte[128 * 1024];
            long length = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token)) != 0)
            {
                length += read;
                if (length > budget)
                    throw new InvalidOperationException("The 2 GB staging limit is full. Finish queued backups before adding more files.");
                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
        }, assetId, name, createdUtc, cancellationToken);

    public async Task<BackupItem?> StageProducedAsync(Func<Stream, long, CancellationToken, Task> producer,
        string assetId, string name, DateTime createdUtc, CancellationToken cancellationToken)
    {
        if (Connection is null)
            throw new InvalidOperationException("Connect to your WebDAV folder first.");
        await stagingGate.WaitAsync(cancellationToken);
        var temporary = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            var used = new DirectoryInfo(staging).EnumerateFiles().Sum(file => file.Length);
            long length;
            string digest;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, true))
            {
                await producer(output, StagingLimit - used, cancellationToken);
                output.Flush(true);
                length = output.Length;
                output.Position = 0;
                digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(output, cancellationToken));
            }
            var id = BackupItem.Identity(ProfileId, assetId, name, digest);
            var existing = await Journal.FindAsync(id);
            if (existing is not null && (existing.State == BackupState.Verified || File.Exists(existing.LocalPath)))
                return null;
            var localPath = Path.Combine(staging, id + ".original");
            File.Move(temporary, localPath, true);
            var safeName = new string(Path.GetFileName(name).Select(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_').Take(120).ToArray());
            if (string.IsNullOrWhiteSpace(safeName) || safeName is "." or "..")
                safeName = "original";
            string[] destination = ["TNAX", DeviceId, createdUtc.ToString("yyyy"), createdUtc.ToString("MM"), digest, safeName];
            var item = new BackupItem
            {
                Id = id, ProfileId = ProfileId, AssetId = assetId, Name = name, LocalPath = localPath,
                Sha256 = digest, Length = length, DestinationJson = JsonSerializer.Serialize(destination)
            };
            if (existing is null)
                await Journal.AddAsync(item);
            else
                await Journal.UpdateAsync(item);
            return item;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { stagingGate.Release(); }
        }
    }

    public bool NetworkAllowed() => !WifiOnly || Connectivity.Current.ConnectionProfiles.Any(profile =>
        profile is ConnectionProfile.WiFi or ConnectionProfile.Ethernet);

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
    #if IOS
            return IosTransfers.Shared.StartAsync(this, cancellationToken);
    #else
            return RunForegroundAsync(cancellationToken);
    #endif
        }

        private async Task RunForegroundAsync(CancellationToken cancellationToken)
    {
        if (IsRunning)
            return;
        if (!NetworkAllowed())
            throw new InvalidOperationException("Waiting for Wi-Fi or Ethernet.");
        transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsRunning = true;
        NotifyChanged();
        try
        {
            using var client = CreateClient();
            await client.ProbeAsync(transferCancellation.Token);
            FolderVerified = true;
            var items = (await Journal.ListAsync(ProfileId)).OrderBy(item => item.CreatedUtc).Where(item =>
                item.State == BackupState.Pending || item.State == BackupState.Failed && item.Attempts < 3 && item.RetryAfterUtc <= DateTime.UtcNow);
            foreach (var item in items)
            {
                transferCancellation.Token.ThrowIfCancellationRequested();
                if (!NetworkAllowed())
                    throw new InvalidOperationException("Backup paused. Waiting for Wi-Fi or Ethernet.");
                item.Attempts++;
                item.State = BackupState.Uploading;
                item.Error = "";
                await Journal.UpdateAsync(item);
                NotifyChanged();
                try
                {
                    var destination = item.Destination();
                    await client.EnsureFoldersAsync(destination[..^1], transferCancellation.Token);
                    await client.UploadFileAsync(item.LocalPath, destination, async stage =>
                    {
                        item.State = stage == TransferStage.Verifying ? BackupState.Verifying : BackupState.Uploading;
                        await Journal.UpdateAsync(item);
                        NotifyChanged();
                    }, transferCancellation.Token, item.Sha256);
                    await CompleteVerifiedAsync(item);
                }
                catch (OperationCanceledException) when (transferCancellation.IsCancellationRequested)
                {
                    item.State = BackupState.Pending;
                    await Journal.UpdateAsync(item);
                    throw;
                }
                catch (Exception exception)
                {
                    item.State = BackupState.Failed;
                    item.Error = UserMessage(exception);
                    item.RetryAfterUtc = DateTime.UtcNow.AddSeconds(Math.Min(300, 15 * Math.Pow(2, item.Attempts)));
                    await Journal.UpdateAsync(item);
                    FolderVerified = false;
                    throw;
                }
                finally { NotifyChanged(); }
            }
        }
        finally
        {
            IsRunning = false;
            transferCancellation.Dispose();
            transferCancellation = null;
            NotifyChanged();
        }
    }

    public async Task CompleteVerifiedAsync(BackupItem item)
    {
        item.State = BackupState.Verified;
        item.BackgroundTaskId = -1;
        item.Error = "";
        await Journal.UpdateAsync(item);
        try { File.Delete(item.LocalPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            item.Error = "Verified; local staging cleanup deferred";
            await Journal.UpdateAsync(item);
        }
        NotifyChanged();
    }

    public void Pause()
    {
        transferCancellation?.Cancel();
#if IOS
        IosTransfers.Shared.Pause();
#endif
    }
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
    public static string UserMessage(Exception exception) => exception switch
    {
        System.Security.Cryptography.CryptographicException or FormatException or JsonException => "The NAS setup or public CA certificate is invalid.",
        NasException or ArgumentException or InvalidOperationException => exception.Message,
        OperationCanceledException => "The operation was cancelled or timed out. Queued originals are retained.",
        IOException => "A local file could not be read or saved. Check free space and try again.",
        _ => "The operation could not finish. Your original files have not been removed."
    };
}
