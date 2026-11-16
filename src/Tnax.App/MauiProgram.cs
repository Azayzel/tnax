namespace Tnax.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<App>();
        builder.Services.AddSingleton<BackupService>();
        builder.Services.AddSingleton<MainPage>();
        return builder.Build();
    }
}
