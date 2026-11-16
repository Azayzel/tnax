using Microsoft.Maui.Layouts;
using Tnax.Core;

namespace Tnax.App;

public sealed class MainPage : ContentPage
{
    private static readonly Color Ink = Color.FromArgb("#192D26");
    private static readonly Color Green = Color.FromArgb("#17694D");
    private static readonly Color Muted = Color.FromArgb("#66736D");
    private static readonly string Typeface = DeviceInfo.Platform == DevicePlatform.WinUI ? "Bahnschrift" : "AvenirNext-Regular";
    private readonly BackupService service;
    private readonly Entry address = Field("https://nas-host:port/backup-folder/");
    private readonly Entry username = Field("NAS username");
    private readonly Entry password = Field("Password", true);
    private readonly Label feedback = Text("Not connected", 14, Muted);
    private readonly Label verified = Text("0", 34, Ink);
    private readonly Label pending = Text("0", 34, Ink);
    private readonly Label failed = Text("0", 34, Ink);
    private readonly Label queueTitle = Text("Queue", 19, Ink);
    private readonly Label connectionStatus = Text("NOT CONNECTED", 12, Muted);
    private readonly ProgressBar progress = new() { ProgressColor = Green, BackgroundColor = Color.FromArgb("#DCE6DF"), HeightRequest = 4 };
    private readonly VerticalStackLayout rows = new() { Spacing = 0 };
    private readonly VerticalStackLayout backupPanel = new() { Spacing = 22 };
    private readonly VerticalStackLayout connectionPanel = new() { Spacing = 16, IsVisible = false };
    private readonly VerticalStackLayout advancedPanel = new() { Spacing = 14, IsVisible = false };
    private readonly VerticalStackLayout folderPanel = new() { Spacing = 12, IsVisible = false };
    private readonly Label nasName = Text("Home NAS", 24, Ink, true);
    private readonly Label location = Text("No backup folder selected", 13, Muted);
    private readonly Label folderLocation = Text("", 14, Ink);
    private readonly Picker folderPicker = new() { Title = "Choose a folder", FontFamily = Typeface, FontSize = 15,
        TextColor = Ink, ItemDisplayBinding = new Binding(nameof(RemoteFolder.Name)) };
    private readonly Button importSetup;
    private readonly Button shareSetup;
    private readonly Button removeSetup;
    private readonly Button browseFolders;
    private readonly Button useFolder;
    private readonly Button openFolder;
    private readonly Button parentFolder;
    private readonly Button cancelSetup;
    private Uri? browsingAddress;
    private readonly Button connect;
    private readonly Button import;
    private readonly Button start;
    private readonly Button retry;
    private readonly Button pause;
    private readonly Switch wifi = new() { OnColor = Green };
    private readonly ActivityIndicator activity = new() { Color = Green, WidthRequest = 24, HeightRequest = 24 };
    private CancellationTokenSource? actionCancellation;
    private bool loading;

    public MainPage(BackupService service)
    {
        this.service = service;
        Title = "TNAX";
        BackgroundColor = Color.FromArgb("#F5F7F5");
        Padding = new Thickness(0);
        connect = Command("Sign in", "\uE701", async () =>
        {
            feedback.Text = "Checking your NAS";
            await service.ConnectAsync(address.Text ?? "", username.Text ?? "", password.Text ?? "", actionCancellation!.Token);
            password.Text = "";
            feedback.Text = "Connected";
            ShowConnection(false);
        });
        importSetup = Command("Import NAS setup", "\uE8B5", ImportSetupAsync);
        shareSetup = Command("Share NAS setup", "\uE72D", ShareSetupAsync, false);
        removeSetup = Command("Remove pairing", "\uE74D", async () =>
        {
            if (!await DisplayAlertAsync("Remove pairing?", "The saved sign-in and app-local NAS trust will be removed. Your files and backup queue will stay.", "Remove", "Cancel"))
                return;
            await service.SetPairingAsync(null);
            username.Text = "";
            password.Text = "";
            folderPanel.IsVisible = false;
            advancedPanel.IsVisible = true;
            feedback.Text = "Pairing removed";
        }, false);
        browseFolders = Command("Choose backup folder", "\uE8B7", async () => await BrowseFoldersAsync(address.Text ?? ""), false);
        openFolder = Command("Open folder", "\uE838", async () =>
        {
            if (folderPicker.SelectedItem is RemoteFolder selected)
                await BrowseFoldersAsync(selected.Address.AbsoluteUri);
        }, false);
        parentFolder = Command("Up", "\uE74A", async () =>
        {
            if (browsingAddress is not null)
                await BrowseFoldersAsync(new Uri(browsingAddress, "../").AbsoluteUri);
        }, false);
        useFolder = Command("Use this folder", "\uE73E", async () =>
        {
            var selected = (folderPicker.SelectedItem as RemoteFolder)?.Address ?? browsingAddress;
            if (selected is null)
                return;
            await service.ConnectAsync(selected.AbsoluteUri, username.Text ?? "", password.Text ?? "", actionCancellation!.Token);
            address.Text = selected.AbsoluteUri;
            password.Text = "";
            folderPanel.IsVisible = false;
            feedback.Text = "Backup folder saved";
            ShowConnection(false);
        });
        cancelSetup = Command("Cancel", "\uE711", () => { actionCancellation?.Cancel(); return Task.CompletedTask; }, false, false);
        folderPicker.SelectedIndexChanged += (_, _) => openFolder.IsEnabled = !loading && folderPicker.SelectedItem is RemoteFolder;
        import = Command("Add files", "\uE8E5", async () =>
        {
#if IOS
            var count = await IosPhotos.ImportAsync(service, true, actionCancellation!.Token);
            feedback.Text = $"{count} photo assets scanned / {IosPhotos.PermissionLabel}";
#else
            var files = await FilePicker.Default.PickMultipleAsync(new PickOptions
            {
                PickerTitle = "Add originals to TNAX",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.WinUI] = [".jpg", ".jpeg", ".heic", ".heif", ".png", ".gif", ".webp", ".tif", ".tiff", ".dng", ".mov", ".mp4", ".m4v"],
                    [DevicePlatform.iOS] = ["public.image", "public.movie"]
                })
            });
            var count = await service.ImportFilesAsync(files.OfType<FileResult>(), actionCancellation!.Token);
            feedback.Text = $"{count} new resources queued";
        #endif
        });
        #if IOS
            import.Text = "Import Photos";
        #endif
        start = Command("Back up", "\uE898", async () =>
        {
            feedback.Text = "Backing up originals";
            await service.StartAsync(actionCancellation!.Token);
            feedback.Text = DeviceInfo.Platform == DevicePlatform.iOS ? "Uploads scheduled; verification remains pending" : "Backup run finished";
        });
        retry = Command("Retry", "\uE72C", async () =>
        {
            await service.Journal.RetryFailedAsync(service.ProfileId);
            await service.StartAsync(actionCancellation!.Token);
            feedback.Text = DeviceInfo.Platform == DevicePlatform.iOS ? "Retry scheduled; verification remains pending" : "Retry finished";
        }, false);
        pause = Command("Pause", "\uE769", () =>
        {
            actionCancellation?.Cancel();
            service.Pause();
            feedback.Text = "Pausing backup";
            return Task.CompletedTask;
        }, false, false);
        pause.IsEnabled = false;

        var brand = new VerticalStackLayout { Spacing = 4, Children = { Text("TNAX", 26, Ink, true), Text("Photo backup", 14, Muted) } };
        var settings = Command("Connection", "\uE713", () => { ShowConnection(!connectionPanel.IsVisible); return Task.CompletedTask; }, false, false);
        var header = new Grid { ColumnDefinitions = Columns("*,Auto"), Padding = new Thickness(24, 22), BackgroundColor = Colors.White };
        header.Add(brand, 0);
        header.Add(settings, 1);

        var metrics = new Grid { ColumnDefinitions = Columns("*,*,*"), ColumnSpacing = 12 };
        metrics.Add(Metric("Verified", verified), 0);
        metrics.Add(Metric("Waiting", pending), 1);
        metrics.Add(Metric("Needs attention", failed), 2);
        var actions = new FlexLayout { Direction = FlexDirection.Row, Wrap = FlexWrap.Wrap, JustifyContent = FlexJustify.Start, Children = { import, start, pause, retry } };
        foreach (var child in actions.Children.OfType<View>())
            child.Margin = new Thickness(0, 0, 8, 8);
        backupPanel.Children.Add(metrics);
        backupPanel.Children.Add(progress);
        backupPanel.Children.Add(actions);
        backupPanel.Children.Add(queueTitle);
        backupPanel.Children.Add(rows);

        connectionPanel.Children.Add(nasName);
        connectionPanel.Children.Add(location);
        connectionPanel.Children.Add(importSetup);
        connectionPanel.Children.Add(Labeled("Username", username));
        connectionPanel.Children.Add(Labeled("Password", password));
        connectionPanel.Children.Add(connect);
        connectionPanel.Children.Add(browseFolders);
        folderPanel.Children.Add(folderLocation);
        folderPanel.Children.Add(folderPicker);
        var navigation = new HorizontalStackLayout { Spacing = 8, Children = { parentFolder, openFolder } };
        folderPanel.Children.Add(navigation);
        folderPanel.Children.Add(useFolder);
        connectionPanel.Children.Add(folderPanel);
        var policy = new Grid { ColumnDefinitions = Columns("*,Auto"), Padding = new Thickness(0, 8) };
        policy.Add(Text("Wi-Fi only", 16, Ink), 0);
        policy.Add(wifi, 1);
        wifi.Toggled += (_, args) => service.WifiOnly = args.Value;
        connectionPanel.Children.Add(policy);
#if IOS
        var automatic = new Switch { IsToggled = Preferences.Default.Get("automatic-photos", false), OnColor = Green };
        automatic.Toggled += async (_, args) => await RunAsync(async () =>
        {
            if (args.Value)
            {
                try { await IosPhotos.EnsurePermissionAsync(true); }
                catch
                {
                    automatic.IsToggled = false;
                    Preferences.Default.Set("automatic-photos", false);
                    IosBackgroundWork.Schedule();
                    throw;
                }
            }
            Preferences.Default.Set("automatic-photos", args.Value);
            IosBackgroundWork.Schedule();
        });
        var automaticRow = new Grid { ColumnDefinitions = Columns("*,Auto") };
        automaticRow.Add(Text("Automatic Photos backup", 16, Ink), 0);
        automaticRow.Add(automatic, 1);
        connectionPanel.Children.Add(automaticRow);
#endif
        connectionPanel.Children.Add(Command("Advanced", "\uE713", () =>
        {
            advancedPanel.IsVisible = !advancedPanel.IsVisible;
            return Task.CompletedTask;
        }, false, false));
        advancedPanel.Children.Add(Labeled("HTTPS WebDAV address", address));
        advancedPanel.Children.Add(shareSetup);
        advancedPanel.Children.Add(removeSetup);
        advancedPanel.Children.Add(Command("Open TOS", "\uE774", async () =>
            await Launcher.Default.OpenAsync("http://192.168.0.87:8181/"), false));
        connectionPanel.Children.Add(advancedPanel);
        connectionPanel.Children.Add(cancelSetup);
        var statusRow = new Grid { ColumnDefinitions = Columns("*,Auto") };
        statusRow.Add(connectionStatus, 0);
        statusRow.Add(activity, 1);
        var body = new VerticalStackLayout
        {
            Padding = new Thickness(24), Spacing = 20, MaximumWidthRequest = 960,
            HorizontalOptions = LayoutOptions.Fill,
            Children = { statusRow, feedback, connectionPanel, backupPanel }
        };
        var layout = new Grid { RowDefinitions = new RowDefinitionCollection(new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star)) };
        layout.Add(header, 0, 0);
        layout.Add(new ScrollView { Content = body }, 0, 1);
        Content = layout;
        service.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(async () => await RefreshAsync());
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RunAsync(async () =>
        {
            await service.InitializeAsync();
            address.Text = service.Connection?.Address ?? service.Pairing?.ServerAddress ?? "https://192.168.0.87:474/";
            username.Text = service.Connection?.Username ?? "";
            wifi.IsToggled = service.WifiOnly;
            ShowConnection(service.Connection is null);
            feedback.Text = service.Connection is null ? "Ready to sign in" : "Saved connection ready";
#if IOS
            if (service.Connection is not null && Preferences.Default.Get("automatic-photos", false))
            {
                await IosPhotos.ImportAsync(service, false, actionCancellation!.Token);
                await service.StartAsync(actionCancellation!.Token);
            }
#endif
        });
    }

    private async Task ImportSetupAsync()
    {
        var file = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Import NAS setup",
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.WinUI] = [".tnax"], [DevicePlatform.iOS] = ["public.data"]
            })
        });
        if (file is null)
            return;
        var pairing = NasPairing.Parse(await ReadSetupDataAsync(file));
        var fingerprint = string.Join(" ", Enumerable.Range(0, 8).Select(index => pairing.Fingerprint.Substring(index * 8, 8)));
        var approved = await DisplayAlertAsync("Pair with " + pairing.Name + "?",
            $"{new Uri(pairing.ServerAddress).GetLeftPart(UriPartial.Authority)}\n\nOnly accept a setup file received from your NAS owner. Trust applies only inside TNAX.\n\nCA fingerprint:\n{fingerprint}",
            "Pair NAS", "Cancel");
        if (!approved)
            return;
        await service.SetPairingAsync(pairing);
        address.Text = pairing.ServerAddress;
        username.Text = "";
        password.Text = "";
        advancedPanel.IsVisible = false;
        folderPanel.IsVisible = false;
        feedback.Text = "NAS paired. Sign in with your account.";
    }

    private async Task ShareSetupAsync()
    {
        NasPairing pairing;
        if (service.Pairing is not null)
            pairing = service.Pairing with { ServerAddress = service.Connection?.Address ?? service.Pairing.ServerAddress };
        else
        {
            var file = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Choose the public root CA certificate",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.WinUI] = [".pem", ".crt", ".cer"], [DevicePlatform.iOS] = ["public.data"]
                })
            });
            if (file is null)
                return;
            var name = await DisplayPromptAsync("NAS name", "", initialValue: "Family NAS", maxLength: 80);
            if (string.IsNullOrWhiteSpace(name))
                return;
            pairing = NasPairing.Create(name, service.Connection?.Address ?? address.Text ?? "", await ReadSetupDataAsync(file));
        }
        var path = Path.Combine(FileSystem.CacheDirectory, "NAS-setup.tnax");
        await File.WriteAllBytesAsync(path, pairing.Export(), actionCancellation!.Token);
        await Share.Default.RequestAsync(new ShareFileRequest { Title = "Share NAS setup", File = new ShareFile(path, "application/json") });
        feedback.Text = "Setup file shared without passwords or private keys";
    }

    private async Task<byte[]> ReadSetupDataAsync(FileResult file)
    {
        await using var stream = await file.OpenReadAsync();
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, actionCancellation!.Token)) != 0)
        {
            if (data.Length + read > NasPairing.MaximumFileBytes)
                throw new ArgumentException("The setup file is too large.");
            data.Write(buffer, 0, read);
        }
        return data.ToArray();
    }

    private async Task BrowseFoldersAsync(string folderAddress)
    {
        feedback.Text = "Loading NAS folders";
        var endpoint = NasEndpoint.Parse(folderAddress);
        var folders = await service.ListFoldersAsync(endpoint.BaseUri.AbsoluteUri, username.Text ?? "", password.Text ?? "", actionCancellation!.Token);
        browsingAddress = endpoint.BaseUri;
        folderLocation.Text = Uri.UnescapeDataString(endpoint.BaseUri.AbsolutePath);
        folderPicker.ItemsSource = folders.ToList();
        folderPicker.SelectedIndex = -1;
        folderPanel.IsVisible = true;
        feedback.Text = folders.Count == 0 ? "No subfolders" : $"{folders.Count} folders available";
    }

    private void ShowConnection(bool show)
    {
        connectionPanel.IsVisible = show;
        backupPanel.IsVisible = !show;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (loading)
            return;
        loading = true;
        actionCancellation = new CancellationTokenSource();
        await RefreshAsync();
        try { await action(); }
        catch (NasException exception) when (exception.Code == NasError.Certificate && service.Pairing is not null)
        {
            feedback.Text = "The NAS certificate does not match this setup or is no longer valid. Contact the NAS owner.";
        }
        catch (Exception exception) { feedback.Text = BackupService.UserMessage(exception); }
        finally
        {
            actionCancellation.Dispose();
            actionCancellation = null;
            loading = false;
            await RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var items = await service.Journal.ListAsync(service.ProfileId);
            var completed = items.Count(item => item.State == BackupState.Verified);
            var errors = items.Count(item => item.State == BackupState.Failed);
            verified.Text = completed.ToString();
            pending.Text = (items.Count - completed - errors).ToString();
            failed.Text = errors.ToString();
            progress.Progress = items.Count == 0 ? 0 : (double)completed / items.Count;
            queueTitle.Text = items.Count == 0 ? "Queue is empty" : $"Resources  /  {items.Count}";
            connectionStatus.Text = service.IsRunning ? "BACKUP IN PROGRESS" : service.FolderVerified ? "FOLDER VERIFIED" : service.Connection is null ? "NOT CONNECTED" : "NOT VERIFIED THIS SESSION";
            rows.Children.Clear();
            foreach (var item in items.Take(60))
            {
                var detail = new VerticalStackLayout { Spacing = 3 };
                detail.Children.Add(Text(item.Name, 15, Ink, true));
                detail.Children.Add(Text($"{FormatBytes(item.Length)}  /  {item.State}", 12, item.State == BackupState.Verified ? Green : Muted));
                if (!string.IsNullOrEmpty(item.Error))
                    detail.Children.Add(Text(item.Error, 12, Color.FromArgb("#B23D35")));
                var row = new Grid { ColumnDefinitions = Columns("52,*"), ColumnSpacing = 12, Padding = new Thickness(0, 14) };
                var video = new[] { ".mov", ".mp4", ".m4v" }.Contains(Path.GetExtension(item.Name), StringComparer.OrdinalIgnoreCase);
                row.Add(new Image { Source = video ? "video.png" : "photo.png", WidthRequest = 48, HeightRequest = 48,
                    BackgroundColor = Color.FromArgb("#E3EDE6"), Aspect = Aspect.Center, Opacity = 0.65 }, 0);
                row.Add(detail, 1);
                rows.Children.Add(row);
                rows.Children.Add(new BoxView { HeightRequest = 1, Color = Color.FromArgb("#DFE5DF") });
            }
            var idle = !loading && !service.IsRunning;
#if IOS
            idle = idle && !IosTransfers.Shared.HasActiveTasks;
#endif
            connect.IsEnabled = idle;
            nasName.Text = service.Pairing?.Name ?? "Home NAS";
            location.Text = service.Connection is null
                ? service.Pairing is null ? "Not paired" : "Paired NAS"
                : Uri.UnescapeDataString(new Uri(service.Connection.Address).AbsolutePath);
            password.Placeholder = service.Connection is null ? "Password" : "Saved securely";
            importSetup.IsEnabled = idle;
            shareSetup.IsEnabled = idle;
            removeSetup.IsEnabled = idle && service.Pairing is not null;
            browseFolders.IsEnabled = idle;
            folderPicker.IsEnabled = idle;
            useFolder.IsEnabled = idle && browsingAddress is not null;
            openFolder.IsEnabled = idle && folderPicker.SelectedItem is RemoteFolder;
            parentFolder.IsEnabled = idle && browsingAddress is not null && browsingAddress.AbsolutePath != "/";
            cancelSetup.IsVisible = loading;
            address.IsEnabled = idle;
            username.IsEnabled = idle;
            password.IsEnabled = idle;
            import.IsEnabled = idle && service.Connection is not null;
            start.IsEnabled = idle && service.Connection is not null && items.Any(item => item.State == BackupState.Pending);
            retry.IsEnabled = idle && errors > 0;
            pause.IsEnabled = loading || service.IsRunning;
            activity.IsRunning = loading || service.IsRunning;
            activity.IsVisible = activity.IsRunning;
#if IOS
            pause.IsEnabled = loading || IosTransfers.Shared.HasActiveTasks;
            wifi.IsEnabled = !IosTransfers.Shared.HasActiveTasks && idle;
            start.IsEnabled = idle && service.Connection is not null && items.Any(item => item.State is BackupState.Pending or BackupState.Verifying);
#endif
        }
        catch (Exception exception) { feedback.Text = BackupService.UserMessage(exception); }
    }

    private Button Command(string title, string glyph, Func<Task> action, bool primary = true, bool exclusive = true)
    {
        var button = new Button { Text = title, FontFamily = Typeface, FontSize = 14, HeightRequest = 44,
            CornerRadius = 6, Padding = new Thickness(16, 0), BackgroundColor = primary ? Green : Colors.White,
            TextColor = primary ? Colors.White : Ink, BorderColor = Color.FromArgb("#D5DED7"), BorderWidth = primary ? 0 : 1 };
        if (DeviceInfo.Platform == DevicePlatform.WinUI)
            button.ImageSource = new FontImageSource { FontFamily = "Segoe MDL2 Assets", Glyph = glyph, Size = 16, Color = primary ? Colors.White : Ink };
        ToolTipProperties.SetText(button, title);
        button.Clicked += async (_, _) => { if (exclusive) await RunAsync(action); else await action(); };
        return button;
    }

    private static Entry Field(string placeholder, bool secret = false) => new()
    {
        Placeholder = placeholder, IsPassword = secret, FontFamily = Typeface, FontSize = 15,
        TextColor = Ink, PlaceholderColor = Muted, BackgroundColor = Colors.White, HeightRequest = 48,
        IsTextPredictionEnabled = false, IsSpellCheckEnabled = false, ClearButtonVisibility = ClearButtonVisibility.WhileEditing
    };
    private static Label Text(string value, double size, Color color, bool bold = false) => new()
    {
        Text = value, FontFamily = Typeface, FontSize = size, TextColor = color,
        FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None, LineBreakMode = LineBreakMode.WordWrap
    };
    private static VerticalStackLayout Metric(string title, Label value) => new() { Spacing = 6, Children = { Text(title, 12, Muted), value } };
    private static VerticalStackLayout Labeled(string title, View field) => new() { Spacing = 8, Children = { Text(title, 13, Ink, true), field } };
    private static ColumnDefinitionCollection Columns(string values) => new(values.Split(',').Select(value =>
        new ColumnDefinition(value == "*" ? GridLength.Star : value == "Auto" ? GridLength.Auto : new GridLength(double.Parse(value)))).ToArray());
    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.0} GB" : $"{bytes / (1024d * 1024):0.0} MB";
}
