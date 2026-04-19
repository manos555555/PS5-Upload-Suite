#nullable enable
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using PS5Upload;

namespace PS5UploadMobile.Pages;

public partial class ScreenshotsPage : ContentPage
{
    private readonly ObservableCollection<PS5Screenshot> _shots = new();

    public ScreenshotsPage()
    {
        InitializeComponent();
        ShotsListView.ItemsSource = _shots;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_shots.Count == 0) await LoadShotsAsync();
    }

    private async void OnRefreshClicked(object sender, EventArgs e) => await LoadShotsAsync();

    private async void OnRefreshing(object sender, EventArgs e)
    {
        await LoadShotsAsync();
        ShotsRefresh.IsRefreshing = false;
    }

    private async Task LoadShotsAsync()
    {
        var proto = await PageHelper.EnsureConnectedAsync(this);
        if (proto == null) return;

        StatusLabel.Text = "Loading screenshots…";
        try
        {
            var list = await proto.ListScreenshotsAsync();
            _shots.Clear();
            foreach (var s in list) _shots.Add(s);
            StatusLabel.Text = $"Loaded {_shots.Count} screenshots";

            // Load thumbnails in background
            _ = Task.Run(() => LoadThumbnailsAsync(list));
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Error: {ex.Message}";
        }
    }

    private async Task LoadThumbnailsAsync(System.Collections.Generic.List<PS5Screenshot> shots)
    {
        string cacheDir = PageHelper.ThumbnailCacheDir;
        foreach (var shot in shots)
        {
            try
            {
                // Cache key
                string hash;
                using (var md5 = MD5.Create())
                {
                    var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(shot.FullPath));
                    hash = BitConverter.ToString(bytes).Replace("-", "").ToLower();
                }
                string cacheFile = Path.Combine(cacheDir, hash + ".jpg");

                if (!File.Exists(cacheFile))
                {
                    // Need its own protocol - use shared connection
                    var proto = PageHelper.Conn.Protocol;
                    if (proto == null || !proto.IsConnected) return;
                    bool ok = await proto.DownloadFileAsync(shot.FullPath, cacheFile);
                    if (!ok) continue;
                }

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    shot.Thumbnail = ImageSource.FromFile(cacheFile);
                });
            }
            catch { }
        }
    }

    private async void OnShotSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection == null || e.CurrentSelection.Count == 0) return;
        if (e.CurrentSelection[0] is not PS5Screenshot shot) return;
        ShotsListView.SelectedItem = null;

        string action = await DisplayActionSheet(shot.FileName, "Cancel", null,
            "👁️ View",
            "⬇️ Download",
            "🗑️ Delete",
            "📋 Copy Path");

        switch (action)
        {
            case "👁️ View":
                await ViewShotAsync(shot);
                break;
            case "⬇️ Download":
                await DownloadShotAsync(shot);
                break;
            case "🗑️ Delete":
                await DeleteShotAsync(shot);
                break;
            case "📋 Copy Path":
                await Clipboard.SetTextAsync(shot.FullPath);
                await DisplayAlert("Copied", shot.FullPath, "OK");
                break;
        }
    }

    private async Task ViewShotAsync(PS5Screenshot shot)
    {
        var proto = await PageHelper.EnsureConnectedAsync(this);
        if (proto == null) return;
        StatusLabel.Text = "Downloading for preview…";
        string tmp = Path.Combine(PageHelper.ThumbnailCacheDir, "preview_" + Path.GetFileName(shot.FileName));
        bool ok = await proto.DownloadFileAsync(shot.FullPath, tmp);
        StatusLabel.Text = $"Loaded {_shots.Count} screenshots";
        if (!ok) { await DisplayAlert("Error", "Download failed", "OK"); return; }
        try
        {
            await Microsoft.Maui.ApplicationModel.Launcher.OpenAsync(
                new Microsoft.Maui.ApplicationModel.OpenFileRequest { File = new ReadOnlyFile(tmp) });
        }
        catch (Exception ex) { await DisplayAlert("Open failed", ex.Message, "OK"); }
    }

    private async Task DownloadShotAsync(PS5Screenshot shot)
    {
        var proto = await PageHelper.EnsureConnectedAsync(this);
        if (proto == null) return;
        string dir = PageHelper.DownloadsDir;
        string local = Path.Combine(dir, shot.FileName);
        StatusLabel.Text = $"Downloading {shot.FileName}…";
        bool ok = await proto.DownloadFileAsync(shot.FullPath, local);
        StatusLabel.Text = $"Loaded {_shots.Count} screenshots";
        await DisplayAlert(ok ? "Downloaded" : "Error",
            ok ? $"Saved to:\n{local}" : "Download failed", "OK");
    }

    private async Task DeleteShotAsync(PS5Screenshot shot)
    {
        var proto = await PageHelper.EnsureConnectedAsync(this);
        if (proto == null) return;
        bool confirm = await DisplayAlert("Delete Screenshot",
            $"Delete {shot.FileName}? This also removes the thumbnail from PS5 Media Gallery.",
            "Delete", "Cancel");
        if (!confirm) return;

        var (ok, msg) = await proto.DeleteScreenshotAsync(shot.FullPath);
        if (ok)
        {
            _shots.Remove(shot);
            StatusLabel.Text = $"Deleted. {_shots.Count} screenshots remaining.";
        }
        else
        {
            await DisplayAlert("Delete failed", msg, "OK");
        }
    }
}
