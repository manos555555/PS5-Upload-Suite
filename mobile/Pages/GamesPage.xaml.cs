#nullable enable
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using PS5Upload;

namespace PS5UploadMobile.Pages;

public partial class GamesPage : ContentPage
{
    private readonly ObservableCollection<PS5MountedGame> _games = new();

    public GamesPage()
    {
        InitializeComponent();
        GamesListView.ItemsSource = _games;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_games.Count == 0) await LoadGamesAsync();
    }

    private async void OnRefreshClicked(object sender, EventArgs e) => await LoadGamesAsync();

    private async void OnRefreshing(object sender, EventArgs e)
    {
        await LoadGamesAsync();
        GamesRefresh.IsRefreshing = false;
    }

    private async Task LoadGamesAsync()
    {
        var proto = await PageHelper.EnsureConnectedAsync(this);
        if (proto == null) return;

        StatusLabel.Text = "Loading games…";
        try
        {
            var list = await proto.GetGameListAsync();
            _games.Clear();
            foreach (var g in list) _games.Add(g);
            StatusLabel.Text = $"Loaded {_games.Count} games";

            // Load icons in background
            _ = Task.Run(async () =>
            {
                foreach (var game in list)
                {
                    try
                    {
                        byte[]? iconBytes = await proto.GetGameIconAsync(game.TitleId);
                        if (iconBytes != null && iconBytes.Length > 0)
                        {
                            var ms = new MemoryStream(iconBytes);
                            await MainThread.InvokeOnMainThreadAsync(() =>
                            {
                                game.Icon = ImageSource.FromStream(() => new MemoryStream(iconBytes));
                            });
                        }
                    }
                    catch { }
                }
            });
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Error: {ex.Message}";
        }
    }

    private async void OnGameSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection == null || e.CurrentSelection.Count == 0) return;
        if (e.CurrentSelection[0] is not PS5MountedGame game) return;
        GamesListView.SelectedItem = null;

        string action = await DisplayActionSheet($"{game.Name}", "Cancel", null,
            "▶️ Launch Game",
            "📖 Game Details",
            "⏏️ Unmount Game",
            "📋 Copy Title ID");

        switch (action)
        {
            case "▶️ Launch Game":
                await LaunchGameAsync(game);
                break;
            case "📖 Game Details":
                await ShowGameDetailsAsync(game);
                break;
            case "⏏️ Unmount Game":
                await UnmountGameAsync(game);
                break;
            case "📋 Copy Title ID":
                await Clipboard.SetTextAsync(game.TitleId);
                await DisplayAlert("Copied", $"Copied {game.TitleId}", "OK");
                break;
        }
    }

    private async Task LaunchGameAsync(PS5MountedGame game)
    {
        var proto = await PageHelper.EnsureConnectedAsync(this);
        if (proto == null) return;
        bool confirm = await DisplayAlert("Launch Game", $"Launch '{game.Name}' on your PS5?", "Launch", "Cancel");
        if (!confirm) return;
        var (ok, msg) = await proto.LaunchGameAsync(game.TitleId);
        await DisplayAlert(ok ? "Launching" : "Error", msg, "OK");
    }

    private async Task UnmountGameAsync(PS5MountedGame game)
    {
        var proto = await PageHelper.EnsureConnectedAsync(this);
        if (proto == null) return;
        bool confirm = await DisplayAlert("Unmount Game", $"Unmount '{game.Name}' from PS5?", "Unmount", "Cancel");
        if (!confirm) return;
        var (ok, msg) = await proto.UnmountGameAsync(game.TitleId);
        await DisplayAlert(ok ? "Unmounted" : "Error", msg, "OK");
        if (ok) await LoadGamesAsync();
    }

    private async Task ShowGameDetailsAsync(PS5MountedGame game)
    {
        var proto = await PageHelper.EnsureConnectedAsync(this);
        if (proto == null) return;
        StatusLabel.Text = "Loading game details…";
        var details = await proto.GetGameDetailsAsync(game.TitleId);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Title: {game.Name}");
        sb.AppendLine($"Title ID: {game.TitleId}");
        sb.AppendLine($"Region: {game.Region}");
        sb.AppendLine($"Size: {game.SizeDisplay}");
        sb.AppendLine($"Path: {game.Path}");
        sb.AppendLine($"Status: {game.StatusDisplay}");
        if (details != null)
        {
            sb.AppendLine();
            sb.AppendLine("—— param.json ——");
            foreach (var kv in details) sb.AppendLine($"{kv.Key}: {kv.Value}");
        }
        StatusLabel.Text = $"Loaded {_games.Count} games";
        await DisplayAlert("Game Details", sb.ToString(), "Close");
    }

    private async void OnMountAllClicked(object sender, EventArgs e)
    {
        var proto = await PageHelper.EnsureConnectedAsync(this);
        if (proto == null) return;
        bool confirm = await DisplayAlert("Mount Games",
            "This will scan all known locations (internal, USB, M.2) and mount unmounted games. Continue?",
            "Mount", "Cancel");
        if (!confirm) return;

        StatusLabel.Text = "Mounting games (this can take a while)…";
        var (ok, summary) = await proto.MountGamesAsync(msg => MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = msg));
        StatusLabel.Text = ok ? "Mount complete" : "Mount failed";
        await DisplayAlert(ok ? "Mount Games - Done" : "Mount Games - Error", summary, "OK");
        if (ok) await LoadGamesAsync();
    }
}
