using Foundation;
using UIKit;

namespace Tnax.App;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        IosBackgroundWork.Register();
        return base.FinishedLaunching(application, launchOptions);
    }

    public override void DidEnterBackground(UIApplication application)
    {
        IosBackgroundWork.Schedule();
        base.DidEnterBackground(application);
    }

    [Export("application:handleEventsForBackgroundURLSession:completionHandler:")]
    public void HandleEventsForBackgroundUrl(UIApplication application, string sessionIdentifier, Action completionHandler)
    {
        IosTransfers.Shared.CompletionHandler = completionHandler;
        var service = IPlatformApplication.Current!.Services.GetRequiredService<BackupService>();
        _ = service.InitializeAsync();
    }
}
