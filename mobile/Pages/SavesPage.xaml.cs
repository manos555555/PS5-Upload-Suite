#nullable enable
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using PS5Upload;

namespace PS5UploadMobile.Pages;

public partial class SavesPage : ContentPage
{
    private readonly ObservableCollection<PS5SaveGame> _saves = new();
    private readonly System.Collections.Generic.Dictionary<string, string> _gameNameCache = new();

    public SavesPage()
    {
        InitializeComponent();
        SavesListView.ItemsSource = _saves;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_saves.Count == 0) await LoadSavesAsync();
    }

    private async void OnRefreshClicked(object sender, EventArgs e) => await LoadSavesAsync();

    private async void OnRefreshing(object sender, EventArgs e)
    {
        await LoadSavesAsync();
        SavesRefresh.IsRefreshing = false;
    }

    private async Task LoadSavesAsync()
    {
        var proto = await PageHelper.EnsureConnectedAsync(this);
        if (proto == null) return;

        StatusLabel.Text = "Loading saves…";
        try
        {
            var list = await proto.ListSavesAsync();
            _saves.Clear();
            foreach (var s in list)
            {
                s.GameName = _gameNameCache.TryGetValue(s.TitleId, out var nm) ? nm : s.TitleId;
                _saves.Add(s);
            }
            StatusLabel.Text = $"Loaded {_saves.Count} saves";

            // Background: fetch game names from GetGameList + icons
            _ = Task.Run(async () =>
            {
                try
                {
                    var games = await proto.GetGameListAsync();
                    var gameDict = new System.Collections.Generic.Dictionary<string, string>();
                    foreach (var g in games) gameDict[g.TitleId] = g.Name;

                    foreach (var save in list)
                    {
                        if (gameDict.TryGetValue(save.TitleId, out var nm))
                        {
                            _gameNameCache[save.TitleId] = nm;
                            await MainThread.InvokeOnMainThreadAsync(() => save.GameName = nm);
                        }
                        byte[]? iconBytes = await proto.GetGameIconAsync(save.TitleId);
                        if (iconBytes != null && iconBytes.Length > 0)
                        {
                            await MainThread.InvokeOnMainThreadAsync(() =>
                            {
                                save.Icon = ImageSource.FromStream(() => new MemoryStream(iconBytes));
                            });
                        }
                    }
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Error: {ex.Message}";
        }
    }

    private async void OnSaveSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection == null || e.CurrentSelection.Count == 0) return;
        if (e.CurrentSelection[0] is not PS5SaveGame save) return;
        SavesListView.SelectedItem = null;

        string action = await DisplayActionSheet($"{save.GameName} ({save.TitleId})", "Cancel", null,
            "📋 Copy Save Path",
            "ℹ️ Save Info");

        if (action == "📋 Copy Save Path")
        {
            await Clipboard.SetTextAsync(save.SavePath);
            await DisplayAlert("Copied", $"Copied: {save.SavePath}", "OK");
        }
        else if (action == "ℹ️ Save Info")
        {
            string info =
                $"Game: {save.GameName}\n" +
                $"Title ID: {save.TitleId}\n" +
                $"User ID: {save.UserId}\n" +
                $"Size: {save.SizeDisplay}\n" +
                $"Modified: {save.ModifiedDisplay}\n" +
                $"Path: {save.SavePath}";
            await DisplayAlert("Save Details", info, "Close");
        }
    }
}
