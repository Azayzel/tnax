using BackgroundTasks;
using Foundation;

namespace Tnax.App;

internal static class IosBackgroundWork
{
    private const string Identifier = "com.azayzel.tnax.photos";

    public static void Register() => BGTaskScheduler.Shared.Register(Identifier, null, task =>
    {
        var cancellation = new CancellationTokenSource();
        task.ExpirationHandler = cancellation.Cancel;
        _ = RunAsync(task, cancellation);
    });

    public static void Schedule()
    {
        BGTaskScheduler.Shared.Cancel(Identifier);
        if (!Preferences.Default.Get("automatic-photos", false))
            return;
        using var request = new BGProcessingTaskRequest(Identifier)
        {
            RequiresNetworkConnectivity = true, RequiresExternalPower = true,
            EarliestBeginDate = NSDate.FromTimeIntervalSinceNow(15 * 60)
        };
        BGTaskScheduler.Shared.Submit(request, out _);
    }

    private static async Task RunAsync(BGTask task, CancellationTokenSource cancellation)
    {
        try
        {
            var service = IPlatformApplication.Current!.Services.GetRequiredService<BackupService>();
            await service.InitializeAsync();
            if (service.Connection is null || !service.NetworkAllowed())
            {
                task.SetTaskCompleted(false);
                return;
            }
            await service.StartAsync(cancellation.Token);
            await IosPhotos.ImportAsync(service, false, cancellation.Token);
            await service.StartAsync(cancellation.Token);
            task.SetTaskCompleted(true);
        }
        catch (Exception) { task.SetTaskCompleted(false); }
        finally { cancellation.Dispose(); Schedule(); }
    }
}
