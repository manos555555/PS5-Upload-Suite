#nullable enable
using PS5UploadMobile.Services;
using System.Collections.ObjectModel;

namespace PS5UploadMobile;

public partial class MainPage : ContentPage
{
    private PS5UploadService _uploadService;
    private ObservableCollection<FileItem> _files;
    private string _currentPath = "/data/";
    private FileItem? _selectedFile;
    
    // New features
    private List<string> _favorites = new List<string>();
    private List<string> _debugLog = new List<string>();
    private List<string> _transferHistory = new List<string>();
    private const string FavoritesKey = "ps5_favorites";
    
    // PS5 Profiles
    private Dictionary<string, string> _ps5Profiles = new Dictionary<string, string>(); // name -> ip:port
    private const string ProfilesKey = "ps5_profiles";
    private string _currentProfileName = "";

    public MainPage()
    {
        InitializeComponent();
        
        _uploadService = new PS5UploadService();
        _files = new ObservableCollection<FileItem>();
        FileListView.ItemsSource = _files;

        _uploadService.LogMessage += OnLogMessage;
        _uploadService.ProgressChanged += OnProgressChanged;
        
        LoadFavorites();
        LoadProfiles();
    }

    // Menu button handler
    private async void OnMenuClicked(object sender, EventArgs e)
    {
        string action = await DisplayActionSheet("Menu", "Cancel", null,
            "🎮 PS5 Profiles",
            "📋 Debug Log",
            "📊 Transfer History", 
            "⭐ Favorites",
            "🗑️ Clear Logs");

        switch (action)
        {
            case "🎮 PS5 Profiles":
                await ShowProfiles();
                break;
            case "📋 Debug Log":
                await ShowDebugLog();
                break;
            case "📊 Transfer History":
                await ShowTransferHistory();
                break;
            case "⭐ Favorites":
                await ShowFavorites();
                break;
            case "🗑️ Clear Logs":
                _debugLog.Clear();
                _transferHistory.Clear();
                await DisplayAlert("Cleared", "Logs cleared", "OK");
                break;
        }
    }

    private async Task ShowDebugLog()
    {
        string log = _debugLog.Count > 0 
            ? string.Join("\n", _debugLog.TakeLast(50)) 
            : "No log entries yet";
        
        string action = await DisplayActionSheet("Debug Log", "Close", null, "📋 Copy Log", "👁️ View Log");
        
        if (action == "📋 Copy Log")
        {
            await Clipboard.SetTextAsync(log);
            await DisplayAlert("Copied", "Log copied to clipboard", "OK");
        }
        else if (action == "👁️ View Log")
        {
            await DisplayAlert("Debug Log", log, "OK");
        }
    }

    private async Task ShowTransferHistory()
    {
        string history = _transferHistory.Count > 0 
            ? string.Join("\n", _transferHistory.TakeLast(20)) 
            : "No transfers yet";
        await DisplayAlert("Transfer History", history, "OK");
    }

    private async Task ShowFavorites()
    {
        if (_favorites.Count == 0)
        {
            await DisplayAlert("Favorites", "No favorites saved.\nTap ⭐ to add current path.", "OK");
            return;
        }

        // Add options for each favorite plus a delete option
        var options = new List<string>();
        foreach (var fav in _favorites)
        {
            options.Add($"📁 {fav}");
        }
        options.Add("🗑️ Delete a favorite");

        string? selected = await DisplayActionSheet("Favorites", "Cancel", null, options.ToArray());
        
        if (selected == null || selected == "Cancel")
            return;
            
        if (selected == "🗑️ Delete a favorite")
        {
            await DeleteFavorite();
        }
        else if (selected.StartsWith("📁 "))
        {
            string path = selected.Substring(3);
            if (_favorites.Contains(path))
            {
                _currentPath = path;
                PathEntry.Text = _currentPath;
                await RefreshFileList();
            }
        }
    }

    private async Task DeleteFavorite()
    {
        if (_favorites.Count == 0) return;
        
        string? toDelete = await DisplayActionSheet("Delete which favorite?", "Cancel", null, _favorites.ToArray());
        if (toDelete != null && toDelete != "Cancel" && _favorites.Contains(toDelete))
        {
            _favorites.Remove(toDelete);
            SaveFavorites();
            await DisplayAlert("Deleted", $"'{toDelete}' removed from favorites", "OK");
        }
    }

    private async void OnAddFavoriteClicked(object sender, EventArgs e)
    {
        if (_favorites.Contains(_currentPath))
        {
            bool remove = await DisplayAlert("Favorite Exists", 
                $"Remove '{_currentPath}' from favorites?", "Remove", "Cancel");
            if (remove)
            {
                _favorites.Remove(_currentPath);
                SaveFavorites();
                await DisplayAlert("Removed", "Favorite removed", "OK");
            }
        }
        else
        {
            _favorites.Add(_currentPath);
            SaveFavorites();
            await DisplayAlert("Added", $"'{_currentPath}' added to favorites", "OK");
        }
    }

    private void LoadFavorites()
    {
        string? saved = Preferences.Get(FavoritesKey, "");
        if (!string.IsNullOrEmpty(saved))
        {
            _favorites = saved.Split('|').Where(s => !string.IsNullOrEmpty(s)).ToList();
        }
    }

    private void SaveFavorites()
    {
        Preferences.Set(FavoritesKey, string.Join("|", _favorites));
    }

    // PS5 Profile Management
    private void LoadProfiles()
    {
        string? saved = Preferences.Get(ProfilesKey, "");
        if (!string.IsNullOrEmpty(saved))
        {
            _ps5Profiles.Clear();
            var entries = saved.Split('|').Where(s => !string.IsNullOrEmpty(s));
            foreach (var entry in entries)
            {
                var parts = entry.Split('=');
                if (parts.Length == 2)
                {
                    _ps5Profiles[parts[0]] = parts[1];
                }
            }
        }
    }

    private void SaveProfiles()
    {
        var entries = _ps5Profiles.Select(p => $"{p.Key}={p.Value}");
        Preferences.Set(ProfilesKey, string.Join("|", entries));
    }

    private async Task ShowProfiles()
    {
        var options = new List<string>();
        
        foreach (var profile in _ps5Profiles)
        {
            string marker = profile.Key == _currentProfileName ? "✓ " : "";
            options.Add($"{marker}🎮 {profile.Key} ({profile.Value})");
        }
        options.Add("➕ Add new profile");
        if (_ps5Profiles.Count > 0)
        {
            options.Add("🗑️ Delete a profile");
        }

        string? selected = await DisplayActionSheet("PS5 Profiles", "Cancel", null, options.ToArray());
        
        if (selected == null || selected == "Cancel")
            return;
            
        if (selected == "➕ Add new profile")
        {
            await AddNewProfile();
        }
        else if (selected == "🗑️ Delete a profile")
        {
            await DeleteProfile();
        }
        else if (selected.Contains("🎮"))
        {
            // Extract profile name
            int start = selected.IndexOf("🎮 ") + 3;
            int end = selected.IndexOf(" (");
            if (start > 2 && end > start)
            {
                string profileName = selected.Substring(start, end - start);
                await SelectProfile(profileName);
            }
        }
    }

    private async Task AddNewProfile()
    {
        string? name = await DisplayPromptAsync("New Profile", "Enter profile name:", "Save", "Cancel", "My PS5");
        if (string.IsNullOrEmpty(name)) return;
        
        string? ip = await DisplayPromptAsync("PS5 IP", "Enter PS5 IP address:", "Save", "Cancel", "192.168.0.160");
        if (string.IsNullOrEmpty(ip)) return;
        
        string? port = await DisplayPromptAsync("Port", "Enter port (default 9113):", "Save", "Cancel", "9113", keyboard: Keyboard.Numeric);
        if (string.IsNullOrEmpty(port)) port = "9113";
        
        _ps5Profiles[name] = $"{ip}:{port}";
        SaveProfiles();
        
        await DisplayAlert("Saved", $"Profile '{name}' saved", "OK");
        await SelectProfile(name);
    }

    private async Task DeleteProfile()
    {
        if (_ps5Profiles.Count == 0) return;
        
        string? toDelete = await DisplayActionSheet("Delete which profile?", "Cancel", null, _ps5Profiles.Keys.ToArray());
        if (toDelete != null && toDelete != "Cancel" && _ps5Profiles.ContainsKey(toDelete))
        {
            _ps5Profiles.Remove(toDelete);
            SaveProfiles();
            if (_currentProfileName == toDelete)
            {
                _currentProfileName = "";
            }
            await DisplayAlert("Deleted", $"Profile '{toDelete}' deleted", "OK");
        }
    }

    private async Task SelectProfile(string profileName)
    {
        if (!_ps5Profiles.ContainsKey(profileName)) return;
        
        string ipPort = _ps5Profiles[profileName];
        var parts = ipPort.Split(':');
        if (parts.Length == 2)
        {
            IpEntry.Text = parts[0];
            PortEntry.Text = parts[1];
            _currentProfileName = profileName;
            AddLog($"Selected profile: {profileName} ({ipPort})");
            await DisplayAlert("Profile Selected", $"Using '{profileName}'\nIP: {parts[0]}\nPort: {parts[1]}", "OK");
        }
    }

    private async void OnConnectClicked(object sender, EventArgs e)
    {
        ConnectBtn.IsEnabled = false;
        StatusLabel.Text = "Connecting...";
        StatusLabel.TextColor = Colors.Yellow;

        _uploadService.PS5Ip = IpEntry.Text;
        _uploadService.Port = int.Parse(PortEntry.Text);

        AddLog($"Connecting to {IpEntry.Text}:{PortEntry.Text}...");
        bool connected = await _uploadService.ConnectAsync();

        if (connected)
        {
            StatusLabel.Text = "Connected ✓";
            StatusLabel.TextColor = Colors.Green;
            ConnectBtn.IsEnabled = false;
            DisconnectBtn.IsEnabled = true;
            AddLog("Connected successfully");
            
            await RefreshFileList();
        }
        else
        {
            StatusLabel.Text = "Connection failed";
            StatusLabel.TextColor = Colors.Red;
            ConnectBtn.IsEnabled = true;
            AddLog("Connection failed");
        }
    }

    private async void OnDisconnectClicked(object sender, EventArgs e)
    {
        await _uploadService.DisconnectAsync();
        
        StatusLabel.Text = "Disconnected";
        StatusLabel.TextColor = Colors.Orange;
        ConnectBtn.IsEnabled = true;
        DisconnectBtn.IsEnabled = false;
        
        _files.Clear();
        AddLog("Disconnected");
    }

    private async void OnRefreshClicked(object sender, EventArgs e)
    {
        await RefreshFileList();
    }

    private async void OnUpDirectoryClicked(object sender, EventArgs e)
    {
        if (_currentPath != "/")
        {
            int lastSlash = _currentPath.TrimEnd('/').LastIndexOf('/');
            _currentPath = _currentPath.Substring(0, lastSlash + 1);
            PathEntry.Text = _currentPath;
            await RefreshFileList();
        }
    }

    private async void OnFileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.Count > 0)
        {
            _selectedFile = e.CurrentSelection[0] as FileItem;
            
            if (_selectedFile?.IsDirectory == true)
            {
                _currentPath = _selectedFile.FullPath;
                PathEntry.Text = _currentPath;
                await RefreshFileList();
                FileListView.SelectedItem = null;
            }
        }
    }

    private async void OnUploadClicked(object sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.PickAsync();
            if (result != null)
            {
                ProgressLabel.Text = $"Uploading {result.FileName}...";
                ProgressBar.Progress = 0;
                ProgressPercentLabel.Text = "0%";

                // Copy file to temp location for reliable access on Android
                string tempPath = Path.Combine(FileSystem.CacheDirectory, result.FileName);
                using (var sourceStream = await result.OpenReadAsync())
                using (var destStream = File.Create(tempPath))
                {
                    await sourceStream.CopyToAsync(destStream);
                }

                string remotePath = _currentPath.TrimEnd('/') + "/" + result.FileName;
                AddLog($"Uploading: {result.FileName} -> {remotePath}");
                
                bool success = await _uploadService.UploadFileAsync(tempPath, remotePath);

                // Clean up temp file
                try { File.Delete(tempPath); } catch { }

                if (success)
                {
                    AddTransfer($"⬆️ {result.FileName}");
                    await DisplayAlert("Success", "File uploaded successfully!", "OK");
                    await RefreshFileList();
                }
                else
                {
                    await DisplayAlert("Error", "Upload failed", "OK");
                }
            }
        }
        catch (Exception ex)
        {
            AddLog($"Upload error: {ex.Message}");
            await DisplayAlert("Error", $"Upload failed: {ex.Message}", "OK");
        }
    }

    private async void OnDownloadClicked(object sender, EventArgs e)
    {
        if (_selectedFile == null || _selectedFile.IsDirectory)
        {
            await DisplayAlert("Error", "Please select a file to download", "OK");
            return;
        }

        try
        {
            // First download to cache, then let user save via Share
            string cachePath = Path.Combine(FileSystem.CacheDirectory, _selectedFile.Name);

            ProgressLabel.Text = $"Downloading {_selectedFile.Name}...";
            ProgressBar.Progress = 0;
            ProgressPercentLabel.Text = "0%";

            AddLog($"Downloading: {_selectedFile.FullPath} -> {cachePath}");
            bool success = await _uploadService.DownloadFileAsync(_selectedFile.FullPath, cachePath);

            if (success)
            {
                AddTransfer($"⬇️ {_selectedFile.Name}");
                
                // Use Share to let user choose where to save
                // This opens the Android share sheet where user can:
                // - Save to Files app (choose location)
                // - Save to Downloads
                // - Send via other apps
                string? action = await DisplayActionSheet(
                    $"'{_selectedFile.Name}' downloaded!",
                    "Close", null,
                    "💾 Save to location...",
                    "📂 Open file");
                
                if (action == "💾 Save to location...")
                {
                    // Share opens Android's save/share dialog where user can choose location
                    await Share.RequestAsync(new ShareFileRequest
                    {
                        Title = $"Save {_selectedFile.Name}",
                        File = new ShareFile(cachePath)
                    });
                }
                else if (action == "📂 Open file")
                {
                    try
                    {
                        await Launcher.OpenAsync(new OpenFileRequest
                        {
                            File = new ReadOnlyFile(cachePath)
                        });
                    }
                    catch
                    {
                        await DisplayAlert("Error", "No app found to open this file type", "OK");
                    }
                }
            }
            else
            {
                await DisplayAlert("Error", "Download failed", "OK");
            }
        }
        catch (Exception ex)
        {
            AddLog($"Download error: {ex.Message}");
            await DisplayAlert("Error", $"Download failed: {ex.Message}", "OK");
        }
    }

    private async void OnDeleteClicked(object sender, EventArgs e)
    {
        if (_selectedFile == null)
        {
            await DisplayAlert("Error", "Please select a file to delete", "OK");
            return;
        }

        bool confirm = await DisplayAlert("Confirm", 
            $"Delete {_selectedFile.Name}?", "Yes", "No");

        if (confirm)
        {
            AddLog($"Deleting: {_selectedFile.FullPath}");
            bool success = await _uploadService.DeleteFileAsync(_selectedFile.FullPath);
            if (success)
            {
                AddTransfer($"🗑️ {_selectedFile.Name}");
                await RefreshFileList();
            }
        }
    }

    private async Task RefreshFileList()
    {
        try
        {
            _files.Clear();
            AddLog($"Listing: {_currentPath}");
            
            var fileItems = await _uploadService.ListDirectoryAsync(_currentPath);
            
            foreach (var fileItem in fileItems)
            {
                _files.Add(fileItem);
            }
            
            AddLog($"Found {fileItems.Count} items");
        }
        catch (Exception ex)
        {
            AddLog($"List error: {ex.Message}");
            await DisplayAlert("Error", $"Failed to list directory: {ex.Message}", "OK");
        }
    }

    private void AddLog(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        _debugLog.Add($"[{timestamp}] {message}");
        if (_debugLog.Count > 100) _debugLog.RemoveAt(0);
    }

    private void AddTransfer(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        _transferHistory.Add($"[{timestamp}] {message}");
        if (_transferHistory.Count > 50) _transferHistory.RemoveAt(0);
    }

    private void OnLogMessage(object? sender, string message)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            AddLog(message);
        });
    }

    private void OnProgressChanged(object? sender, int progress)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ProgressBar.Progress = progress / 100.0;
            ProgressPercentLabel.Text = $"{progress}%";
        });
    }
}

