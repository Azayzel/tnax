namespace Tnax.App;

public sealed class App(MainPage page) : Application
{
    protected override Window CreateWindow(IActivationState? activationState) => new(page)
    {
        Title = "TNAX", Width = 1000, Height = 820, MinimumWidth = 390, MinimumHeight = 620
    };
}
