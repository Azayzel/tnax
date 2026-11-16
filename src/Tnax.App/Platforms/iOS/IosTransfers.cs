using Foundation;
using System.Text;
using Tnax.Core;

namespace Tnax.App;

internal sealed class IosTransfers : NSUrlSessionTaskDelegate
{
    public static IosTransfers Shared { get; } = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<Task> completions = [];
    private NSUrlSession? session;
    private BackupService? service;
    private CancellationTokenSource? setupCancellation;
    private bool wifiOnly;
    public bool HasActiveTasks { get; private set; }
    public Action? CompletionHandler { get; set; }

    public override void DidReceiveChallenge(NSUrlSession nativeSession, NSUrlAuthenticationChallenge challenge,
        Action<NSUrlSessionAuthChallengeDisposition, NSUrlCredential> completionHandler) =>
        IosNasTrust.Handle(service?.Pairing, challenge, (disposition, credential) => completionHandler(disposition, credential!));

    public override void DidReceiveChallenge(NSUrlSession nativeSession, NSUrlSessionTask task, NSUrlAuthenticationChallenge challenge,
        Action<NSUrlSessionAuthChallengeDisposition, NSUrlCredential> completionHandler) =>
        IosNasTrust.Handle(service?.Pairing, challenge, (disposition, credential) => completionHandler(disposition, credential!));

    public async Task AttachAsync(BackupService owner)
    {
        service = owner;
        EnsureSession();
        var tasks = await session!.GetAllTasksAsync();
        HasActiveTasks = tasks.Length != 0;
        var ids = tasks.Select(task => task.TaskDescription).ToHashSet();
        foreach (var item in await owner.Journal.ListAsync(owner.ProfileId))
        {
            if (item.BackgroundTaskId >= 0 && !ids.Contains(item.Id))
            {
                item.BackgroundTaskId = -1;
                item.State = BackupState.Pending;
                await owner.Journal.UpdateAsync(item);
            }
        }
        owner.NotifyChanged();
    }

    private void EnsureSession()
    {
        if (session is not null && wifiOnly == service!.WifiOnly)
            return;
        if (session is not null)
        {
            if (HasActiveTasks)
                throw new InvalidOperationException("Pause all background uploads before changing network policy.");
            session.FinishTasksAndInvalidate();
        }
        wifiOnly = service!.WifiOnly;
        var configuration = NSUrlSessionConfiguration.CreateBackgroundSessionConfiguration("com.azayzel.tnax.uploads");
        configuration.AllowsCellularAccess = !wifiOnly;
        configuration.AllowsExpensiveNetworkAccess = !wifiOnly;
        configuration.AllowsConstrainedNetworkAccess = false;
        configuration.Discretionary = true;
        configuration.SessionSendsLaunchEvents = true;
        configuration.WaitsForConnectivity = true;
        configuration.HttpMaximumConnectionsPerHost = 2;
        session = NSUrlSession.FromConfiguration(configuration, this, new NSOperationQueue { MaxConcurrentOperationCount = 1 });
    }

    public async Task StartAsync(BackupService owner, CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken))
            return;
        service = owner;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        setupCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            EnsureSession();
            if (!owner.NetworkAllowed())
                throw new InvalidOperationException("Waiting for Wi-Fi.");
            using var client = owner.CreateClient();
            await client.ProbeAsync(token);
            var active = (await session!.GetAllTasksAsync()).Select(task => task.TaskDescription).ToHashSet();
            var jobs = (await owner.Journal.ListAsync(owner.ProfileId)).Where(item => !active.Contains(item.Id) &&
                (item.State is BackupState.Pending or BackupState.Verifying ||
                 item.State == BackupState.Failed && item.Attempts < 3 && item.RetryAfterUtc <= DateTime.UtcNow)).Take(48);
            foreach (var item in jobs)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                item.Attempts++;
                var segments = item.Destination();
                var destination = client.Endpoint.Resource(segments);
                if (await client.InspectAsync(destination, token) is not null)
                {
                    item.State = BackupState.Verifying;
                    await owner.Journal.UpdateAsync(item);
                    await client.VerifyAsync(destination, item.Length, item.Sha256, token);
                    await owner.CompleteVerifiedAsync(item);
                    continue;
                }
                if (!File.Exists(item.LocalPath))
                    throw new IOException("The staged original is missing. Import it again from Photos.");
                await using (var original = File.OpenRead(item.LocalPath))
                {
                    var digest = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(original, token));
                    if (digest != item.Sha256)
                        throw new NasException(NasError.Conflict, "The staged original changed. Import it again before backup.");
                }
                await client.EnsureFoldersAsync(segments[..^1], token);
                using var request = new NSMutableUrlRequest(new NSUrl(destination.AbsoluteUri)) { HttpMethod = "PUT" };
                using var headers = new NSMutableDictionary();
                headers[new NSString("Authorization")] = new NSString("Basic " + Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{owner.Connection!.Username}:{owner.Connection.Password}")));
                headers[new NSString("If-None-Match")] = new NSString("*");
                headers[new NSString("Content-Type")] = new NSString("application/octet-stream");
                request.Headers = headers;
                var task = session.CreateUploadTask(request, NSUrl.FromFilename(item.LocalPath));
                task.TaskDescription = item.Id;
                item.BackgroundTaskId = checked((long)task.TaskIdentifier);
                item.State = BackupState.Uploading;
                item.Error = "";
                await owner.Journal.UpdateAsync(item);
                HasActiveTasks = true;
                task.Resume();
                owner.NotifyChanged();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    item.State = BackupState.Pending;
                    await owner.Journal.UpdateAsync(item);
                    throw;
                }
                catch (Exception exception)
                {
                    item.State = BackupState.Failed;
                    item.Error = BackupService.UserMessage(exception);
                    item.RetryAfterUtc = DateTime.UtcNow.AddMinutes(Math.Min(30, Math.Pow(2, item.Attempts)));
                    await owner.Journal.UpdateAsync(item);
                    owner.NotifyChanged();
                    throw;
                }
            }
        }
        finally { setupCancellation = null; gate.Release(); }
    }

    public void Pause()
    {
        setupCancellation?.Cancel();
        if (session is null)
            return;
        session.GetAllTasks(tasks => { foreach (var task in tasks) task.Cancel(); });
    }

    public override void DidCompleteWithError(NSUrlSession nativeSession, NSUrlSessionTask task, NSError? error)
    {
        lock (completions)
        {
            completions.RemoveAll(completion => completion.IsCompleted);
            completions.Add(CompleteAsync(nativeSession, task, error));
        }
    }

    private async Task CompleteAsync(NSUrlSession nativeSession, NSUrlSessionTask task, NSError? error)
    {
        if (service is null || task.TaskDescription is null)
            return;
        try
        {
            var item = await service.Journal.FindAsync(task.TaskDescription);
            if (item is null)
                return;
            item.BackgroundTaskId = -1;
            var status = (task.Response as NSHttpUrlResponse)?.StatusCode;
            if (error is null && status is 201 or 412)
            {
                item.State = BackupState.Verifying;
                item.Error = "Awaiting content verification";
            }
            else if (error?.Code == -999)
            {
                item.State = BackupState.Pending;
                item.Error = "Paused";
            }
            else
            {
                item.State = BackupState.Failed;
                item.Error = error is null ? $"WebDAV upload returned {status}. Original retained." : "Background transfer interrupted. Original retained.";
                item.RetryAfterUtc = DateTime.UtcNow.AddMinutes(Math.Min(30, Math.Pow(2, item.Attempts)));
            }
            await service.Journal.UpdateAsync(item);
            HasActiveTasks = (await nativeSession.GetAllTasksAsync()).Any(other => other.TaskIdentifier != task.TaskIdentifier);
            service.NotifyChanged();
            IosBackgroundWork.Schedule();
        }
        catch (Exception)
        {
            service.NotifyChanged();
        }
    }

    public override async void DidFinishEventsForBackgroundSession(NSUrlSession nativeSession)
    {
        Task[] pending;
        lock (completions)
        {
            pending = completions.ToArray();
            completions.Clear();
        }
        await Task.WhenAll(pending);
        var completion = CompletionHandler;
        CompletionHandler = null;
        if (completion is not null)
            MainThread.BeginInvokeOnMainThread(completion);
    }
}
