using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PS5Upload
{
    public partial class MainWindow : Window
    {
        private PS5Protocol _protocol = new PS5Protocol();
        private ObservableCollection<LocalFileItem> _localFiles = new ObservableCollection<LocalFileItem>();
        private ObservableCollection<PS5FileItem> _ps5Files = new ObservableCollection<PS5FileItem>();
        private ObservableCollection<PS5FileItem> _ps5FilesFiltered = new ObservableCollection<PS5FileItem>();
        private string _currentPS5Path = "/data";
        private CancellationTokenSource? _uploadCancellation;
        private string _ps5IpAddress = "";
        private string _searchQuery = "";
        
        // Multi-PS5 Support
        private Dictionary<string, string> _ps5Profiles = new Dictionary<string, string>();
        private const string ProfilesFileName = "ps5_profiles.json";
        
        // Favorites/Bookmarks
        private List<string> _favoritePaths = new List<string>();
        private const string FavoritesFileName = "ps5_favorites.json";
        
        // Transfer History
        private ObservableCollection<TransferHistoryItem> _transferHistory = new ObservableCollection<TransferHistoryItem>();
        private ObservableCollection<TransferHistoryItem> _completedTransfers = new ObservableCollection<TransferHistoryItem>();
        private ObservableCollection<TransferHistoryItem> _failedTransfers = new ObservableCollection<TransferHistoryItem>();
        private const string HistoryFileName = "ps5_transfer_history.json";
        private const string SettingsFileName = "ps5_upload_settings.json";
        private bool _autoClearHistoryOnStartup = false;
        
        // Parallel upload settings
        private const int MaxParallelUploads = 16; // 16 parallel uploads for maximum speed (~105-108 MB/s)
        
        // Total upload tracking
        private int _totalFilesToUpload = 0;
        private long _totalBytesToUpload = 0;
        private long _totalBytesUploaded = 0;
        private int _completedFiles = 0;
        private DateTime _uploadStartTime;
        private readonly object _progressLock = new object();
        
        // Current file progress tracking (for largest active file)
        private string _currentFileName = "";
        private long _currentFileBytes = 0;
        private long _currentFileTotalBytes = 0;
        
        // Real-time UI update timer
        private DispatcherTimer _uiUpdateTimer;
        private DispatcherTimer _storageUpdateTimer;
        private int _activeTaskCount = 0;
        private bool _isProtocolBusy = false; // Prevents storage updates during other operations
        
        // Log throttling to prevent UI freeze
        private int _logCounter = 0;
        private const int MaxLogLines = 1000;
        
        // Duplicate file handling
        private enum DuplicateAction { Ask, Replace, Skip, ReplaceAll, SkipAll }
        private DuplicateAction _duplicateAction = DuplicateAction.Ask;
        
        // Helper to normalize PS5 paths and prevent double slashes
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "/";
            // Replace multiple slashes with single slash, but preserve leading slash
            while (path.Contains("//"))
            {
                path = path.Replace("//", "/");
            }
            return path;
        }
        
        private static string CombinePath(string basePath, string name)
        {
            return NormalizePath(basePath.TrimEnd('/') + "/" + name);
        }

        public MainWindow()
        {
            InitializeComponent();
            LocalFilesListBox.ItemsSource = _localFiles;
            PS5FilesListBox.ItemsSource = _ps5FilesFiltered;
            CompletedTransfersListBox.ItemsSource = _completedTransfers;
            FailedTransfersListBox.ItemsSource = _failedTransfers;
            
            // Initialize real-time UI update timer (500ms interval)
            _uiUpdateTimer = new DispatcherTimer();
            _uiUpdateTimer.Interval = TimeSpan.FromMilliseconds(500);
            _uiUpdateTimer.Tick += UiUpdateTimer_Tick;
            
            // Initialize storage update timer - updates every 5 seconds when not uploading
            _storageUpdateTimer = new DispatcherTimer();
            _storageUpdateTimer.Interval = TimeSpan.FromSeconds(5);
            _storageUpdateTimer.Tick += StorageUpdateTimer_Tick;
            
            // Subscribe to progress messages from protocol
            _protocol.OnProgressMessage += (message) =>
            {
                Dispatcher.Invoke(() => Log(message));
            };
            
            Log("Application started");
            
            // Load saved PS5 profiles
            LoadProfiles();
            
            // Load saved favorite paths
            LoadFavorites();
            
            // Load settings (including auto-clear preference)
            LoadSettings();
            
            // Load transfer history (will be cleared if auto-clear is enabled)
            LoadTransferHistory();
        }

        private void UiUpdateTimer_Tick(object? sender, EventArgs e)
        {
            // Update UI stats in real-time (called every 500ms)
            int completed;
            lock (_progressLock)
            {
                completed = _completedFiles;
            }
            UpdateUploadStats(completed, _activeTaskCount);
        }

        private async void StorageUpdateTimer_Tick(object? sender, EventArgs e)
        {
            // Update PS5 storage info in real-time (called every 3 seconds)
            await UpdateStorageInfoAsync();
        }

        private async Task UpdateStorageInfoAsync()
        {
            try
            {
                // Skip if protocol is busy with another operation (upload, delete, etc.)
                if (_isProtocolBusy || !_protocol.IsConnected)
                    return;

                var storageList = await _protocol.ListStorageAsync();
                if (storageList.Length == 0)
                    return;

                // Try to find the best storage to display
                // Priority: /data (main PS5 storage), then any available
                StorageInfo? targetStorage = null;
                
                // First try /data
                targetStorage = storageList.FirstOrDefault(s => s.Path == "/data");
                
                // If /data not found, try other common paths
                if (targetStorage == null || targetStorage.TotalBytes == 0)
                {
                    targetStorage = storageList.FirstOrDefault(s => s.Path.StartsWith("/user") && s.TotalBytes > 0);
                }
                
                // If still not found, use the first one with valid data
                if (targetStorage == null || targetStorage.TotalBytes == 0)
                {
                    targetStorage = storageList.FirstOrDefault(s => s.TotalBytes > 0);
                }
                
                if (targetStorage == null)
                    return;

                long totalBytes = targetStorage.TotalBytes;
                long freeBytes = targetStorage.FreeBytes;
                long usedBytes = totalBytes - freeBytes;

                // Update UI
                StorageFreeText.Text = FormatFileSize(freeBytes);
                StorageUsedText.Text = FormatFileSize(usedBytes);
                StorageTotalText.Text = FormatFileSize(totalBytes);

                // Update progress bar (percentage used)
                double usedPercentage = totalBytes > 0 ? (double)usedBytes / totalBytes * 100 : 0;
                StorageProgressBar.Value = usedPercentage;
            }
            catch
            {
                // Ignore errors during storage update
            }
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Clear();
            Log("Log cleared");
        }

        private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            _searchQuery = SearchTextBox.Text.Trim();
            ApplySearchFilter();
        }

        private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
        {
            SearchTextBox.Text = "";
            _searchQuery = "";
            ApplySearchFilter();
        }

        private void ApplySearchFilter()
        {
            _ps5FilesFiltered.Clear();
            
            if (string.IsNullOrWhiteSpace(_searchQuery))
            {
                // No search - show all files
                foreach (var file in _ps5Files)
                {
                    _ps5FilesFiltered.Add(file);
                }
            }
            else
            {
                // Filter by search query (case-insensitive)
                string query = _searchQuery.ToLower();
                foreach (var file in _ps5Files)
                {
                    if (file.Name.ToLower().Contains(query))
                    {
                        _ps5FilesFiltered.Add(file);
                    }
                }
            }
        }

        private async void CopyLogButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Copy log text asynchronously without selecting it in the UI
                // This prevents the UI freeze that happens with Ctrl+A
                string logContent = "";
                
                await Dispatcher.InvokeAsync(() =>
                {
                    logContent = LogTextBox.Text;
                });
                
                // Copy to clipboard with retry logic (clipboard may be locked by another app)
                bool copied = false;
                for (int retry = 0; retry < 3 && !copied; retry++)
                {
                    try
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            System.Windows.Clipboard.SetText(logContent);
                        });
                        copied = true;
                    }
                    catch
                    {
                        await Task.Delay(100); // Wait and retry
                    }
                }
                
                if (copied)
                    Log("📋 Log copied to clipboard!");
                else
                    Log("⚠️ Clipboard busy - try again");
            }
            catch (Exception ex)
            {
                Log($"⚠️ Clipboard error: {ex.Message}");
            }
        }

        private void UpdateUploadStats(int completedFiles, int activeTaskCount)
        {
            // CRITICAL FIX: Use InvokeAsync instead of Invoke to prevent UI freezing
            // When uploading thousands of small files, Invoke blocks the UI thread
            Dispatcher.InvokeAsync(() =>
            {
                // Update files remaining counter
                int remainingFiles = _totalFilesToUpload - completedFiles;
                if (completedFiles > 0)
                {
                    FilesRemainingText.Text = $"Files: {completedFiles} / {_totalFilesToUpload} ({remainingFiles} remaining)";
                }
                else
                {
                    FilesRemainingText.Text = $"Files: 0 / {_totalFilesToUpload} ({_totalFilesToUpload} remaining)";
                }
                
                // Update total progress
                double totalPercent = _totalBytesToUpload > 0 ? (double)_totalBytesUploaded / _totalBytesToUpload * 100 : 0;
                TotalProgressBar.Value = totalPercent;
                
                if (completedFiles > 0)
                {
                    TotalProgressText.Text = $"Total: {completedFiles} / {_totalFilesToUpload} files ({FormatFileSize(_totalBytesUploaded)} / {FormatFileSize(_totalBytesToUpload)})";
                }
                else
                {
                    // Real-time update without completed file count
                    TotalProgressText.Text = $"Total: {FormatFileSize(_totalBytesUploaded)} / {FormatFileSize(_totalBytesToUpload)} ({totalPercent:F1}%)";
                }
                
                // Calculate speed and ETA in real-time
                var elapsed = DateTime.Now - _uploadStartTime;
                double speed = elapsed.TotalSeconds > 0 ? _totalBytesUploaded / elapsed.TotalSeconds : 0;
                long remaining = _totalBytesToUpload - _totalBytesUploaded;
                TimeSpan eta = speed > 0 ? TimeSpan.FromSeconds(remaining / speed) : TimeSpan.Zero;
                
                // Update speed and ETA
                UploadSpeedText.Text = $"Speed: {FormatFileSize((long)speed)}/s | {activeTaskCount} active";
                UploadETAText.Text = $"ETA: {eta:hh\\:mm\\:ss} | Elapsed: {elapsed:hh\\:mm\\:ss}";
                
                if (activeTaskCount > 0)
                {
                    UploadFileNameText.Text = $"Uploading {activeTaskCount} files in parallel...";
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void Log(string message)
        {
            // CRITICAL FIX: Use InvokeAsync and throttle logging to prevent UI freeze
            // Only log important events (file completions, errors, status changes)
            // Skip verbose progress updates that flood the log
            
            // Always log to file for crash debugging
            // Log all file completions and errors
            bool isImportant = message.Contains("❌") || message.Contains("⚠️") || 
                               message.Contains("Exception") || message.Contains("Error") ||
                               message.Contains("File") && message.Contains("completed") ||
                               message.Contains("Starting parallel") || message.Contains("🚀") ||
                               message.Contains("Upload complete") || message.Contains("finished");
            
            // Also log every 100th file for progress tracking
            if (message.Contains("File") && message.Contains("/"))
            {
                _logCounter++;
                if (_logCounter % 100 == 0)
                    isImportant = true;
            }
            
            if (isImportant)
            {
                App.LogToFile(message);
            }
            
            // Skip verbose upload progress messages for UI
            if (message.Contains("📊") || message.Contains("⬆️ Uploading:") || 
                message.Contains("⏳ Waiting") || message.Contains("✅ Task completed") ||
                message.Contains("✅ Task awaited") || message.Contains("🔍 Task index") ||
                message.Contains("🧹 Cleaning up") || message.Contains("📤 Starting upload") ||
                message.Contains("✅ Connection") && message.Contains("established"))
            {
                _logCounter++;
                // Only log every 50th verbose message to show activity
                if (_logCounter % 50 != 0)
                    return;
            }
            
            Dispatcher.InvokeAsync(() =>
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                
                // Limit log size to prevent memory issues and UI slowdown
                int lineCount = LogTextBox.LineCount;
                if (lineCount > MaxLogLines)
                {
                    // Remove first 200 lines when limit is reached
                    int firstLineLength = LogTextBox.GetLineLength(0);
                    int linesToRemove = 200;
                    int charsToRemove = 0;
                    for (int i = 0; i < linesToRemove && i < lineCount; i++)
                    {
                        charsToRemove += LogTextBox.GetLineLength(i);
                    }
                    LogTextBox.Text = LogTextBox.Text.Substring(charsToRemove);
                }
                
                LogTextBox.AppendText($"[{timestamp}] {message}\n");
                LogTextBox.ScrollToEnd();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            ConnectButton.IsEnabled = false;
            string ipAddress = IpAddressTextBox.Text.Trim();
            
            if (string.IsNullOrEmpty(ipAddress))
            {
                MessageBox.Show("Please enter PS5 IP address", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                ConnectButton.IsEnabled = true;
                return;
            }

            _ps5IpAddress = ipAddress;
            Log($"Connecting to PS5 at {ipAddress}...");

            // Check if we should disconnect
            if (_protocol.IsConnected)
            {
                Log("🔌 Disconnecting from PS5...");
                _storageUpdateTimer.Stop(); // Stop storage updates
                _protocol.Disconnect();
                
                Dispatcher.Invoke(() =>
                {
                    ConnectButton.Content = "🔴 Disconnected";
                    ConnectButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.DarkRed);
                    UploadButton.IsEnabled = false;
                    _ps5Files.Clear();
                });
                
                Log("✅ Disconnected from PS5");
                
                // Wait a moment then change back to Connect
                await Task.Delay(1000);
                Dispatcher.Invoke(() =>
                {
                    ConnectButton.Content = "🔵 Connect";
                    ConnectButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 122, 204));
                });
            }
            else
            {
                // Connect
                if (await _protocol.ConnectAsync(ipAddress))
                {
                    Log("✅ Connected to PS5 successfully");
                    
                    // Update UI to show connected status
                    Dispatcher.Invoke(() =>
                    {
                        ConnectButton.Content = "🟢 Disconnect";
                        ConnectButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.DarkGreen);
                        UploadButton.IsEnabled = true;
                    });
                    
                    // Update storage info on connect and start real-time updates
                    await UpdateStorageInfoAsync();
                    _storageUpdateTimer.Start();
                    
                    await LoadPS5DirectoryAsync(_currentPS5Path);
                }
                else
                {
                    Log("❌ Failed to connect to PS5");
                    MessageBox.Show("Failed to connect to PS5. Make sure the payload is running.", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            ConnectButton.IsEnabled = true;
        }

        private async Task LoadPS5DirectoryAsync(string path)
        {
            try
            {
                // Don't try to load directory if upload is in progress
                if (_uploadCancellation != null && !_uploadCancellation.Token.IsCancellationRequested)
                {
                    Log("⚠️ Skipping directory load - upload in progress");
                    return;
                }
                
                // Ensure connection is active before loading directory
                if (!_protocol.IsConnected)
                {
                    Log("⚠️ Connection lost, reconnecting...");
                    if (!await _protocol.ConnectAsync(_ps5IpAddress))
                    {
                        MessageBox.Show("Failed to reconnect to PS5", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    Log("✅ Reconnected successfully");
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                
                _currentPS5Path = path;
                CurrentPathTextBox.Text = path;

                var t1 = sw.ElapsedMilliseconds;
                var entries = await _protocol.ListDirAsync(path);
                var t2 = sw.ElapsedMilliseconds;
                
                // Build list first to avoid multiple UI updates
                var items = new List<PS5FileItem>();
                
                if (path != "/")
                {
                    string? parentPath = Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(parentPath))
                        parentPath = "/";
                    else
                        parentPath = parentPath.Replace("\\", "/");
                    
                    items.Add(new PS5FileItem
                    {
                        Name = "..",
                        FullPath = parentPath,
                        Icon = "📁",
                        IsDirectory = true,
                        Size = 0
                    });
                }

                foreach (var entry in entries)
                {
                    items.Add(new PS5FileItem
                    {
                        Name = entry.Name,
                        IsDirectory = entry.IsDirectory,
                        Size = entry.Size,
                        FullPath = $"{path}/{entry.Name}".Replace("//", "/"),
                        Icon = entry.IsDirectory ? "📁" : "📄"
                    });
                }

                // Sort: directories first, then files, both alphabetically
                var sortedItems = items.OrderBy(i => i.IsDirectory ? 0 : 1).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

                var t3 = sw.ElapsedMilliseconds;
                
                // Single UI update
                _ps5Files.Clear();
                foreach (var item in sortedItems)
                {
                    _ps5Files.Add(item);
                }

                var t4 = sw.ElapsedMilliseconds;
                int entryCount = entries.Count();
                Log("📂 Loaded " + entryCount.ToString() + " items (Total: " + t4.ToString() + "ms)");
                
                // Apply search filter
                ApplySearchFilter();
            }
            catch (Exception ex)
            {
                // Only show error if we're actually connected (not during disconnect)
                if (_protocol.IsConnected)
                {
                    Log($"❌ Failed to load directory: {ex.Message}");
                    MessageBox.Show($"Failed to load directory: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BrowseFilesButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Multiselect = true,
                Title = "Select files to upload"
            };

            if (dialog.ShowDialog() == true)
            {
                foreach (string file in dialog.FileNames)
                {
                    FileInfo info = new FileInfo(file);
                    _localFiles.Add(new LocalFileItem
                    {
                        Name = info.Name,
                        FullPath = file,
                        Icon = "📄",
                        IsDirectory = false,
                        Size = info.Length
                    });
                }
            }
        }

        private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select folder to upload"
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                DirectoryInfo dirInfo = new DirectoryInfo(dialog.SelectedPath);
                _localFiles.Add(new LocalFileItem
                {
                    Name = dirInfo.Name,
                    FullPath = dialog.SelectedPath,
                    Icon = "📁",
                    IsDirectory = true,
                    Size = 0
                });
            }
        }

        private void RemoveLocalFile_Click(object sender, RoutedEventArgs e)
        {
            if (LocalFilesListBox.SelectedItem is LocalFileItem selectedItem)
            {
                _localFiles.Remove(selectedItem);
            }
        }

        private void ClearLocalFiles_Click(object sender, RoutedEventArgs e)
        {
            _localFiles.Clear();
        }

        private void LocalFilesListBox_RightClick(object sender, MouseButtonEventArgs e)
        {
            // Context menu is handled by XAML
        }

        private void LocalFilesListBox_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
        }

        private void LocalFilesListBox_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (string path in files)
                {
                    if (File.Exists(path))
                    {
                        FileInfo info = new FileInfo(path);
                        _localFiles.Add(new LocalFileItem
                        {
                            Name = info.Name,
                            FullPath = path,
                            Icon = "📄",
                            IsDirectory = false,
                            Size = info.Length
                        });
                    }
                    else if (Directory.Exists(path))
                    {
                        DirectoryInfo dirInfo = new DirectoryInfo(path);
                        _localFiles.Add(new LocalFileItem
                        {
                            Name = dirInfo.Name,
                            FullPath = path,
                            Icon = "📁",
                            IsDirectory = true,
                            Size = 0
                        });
                    }
                }
            }
        }

        private async void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_localFiles.Count == 0)
            {
                MessageBox.Show("No files selected for upload", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!_protocol.IsConnected)
            {
                MessageBox.Show("Not connected to PS5", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            UploadButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Visible;
            _uploadCancellation = new CancellationTokenSource();

            Log("========== UPLOAD STARTED ==========");
            Log("Collecting files...");

            // Collect all files to upload
            var allFiles = new List<(string localPath, string remotePath)>();
            foreach (var item in _localFiles)
            {
                if (item.IsDirectory)
                {
                    CollectFilesFromDirectory(item.FullPath, CombinePath(_currentPS5Path, item.Name), allFiles);
                }
                else
                {
                    allFiles.Add((item.FullPath, CombinePath(_currentPS5Path, item.Name)));
                }
            }
            
            // Check for duplicates and filter files
            Log("Checking for existing files...");
            var filesToUpload = await FilterDuplicateFilesAsync(allFiles);
            if (filesToUpload.Count == 0)
            {
                Log("⚠️ No files to upload (all skipped)");
                MessageBox.Show("No files to upload. All files were skipped.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                UploadButton.IsEnabled = true;
                CancelButton.IsEnabled = false;
                ProgressPanel.Visibility = Visibility.Collapsed;
                return;
            }
            
            allFiles = filesToUpload;

            _totalFilesToUpload = allFiles.Count;
            _totalBytesToUpload = allFiles.Sum(f => new FileInfo(f.localPath).Length);
            _totalBytesUploaded = 0;
            _completedFiles = 0;
            _uploadStartTime = DateTime.Now;
            Log($"📊 Total: {_totalFilesToUpload} files, {FormatFileSize(_totalBytesToUpload)}");

            TotalProgressText.Text = $"Total: 0 / {_totalFilesToUpload} files ({FormatFileSize(0)} / {FormatFileSize(_totalBytesToUpload)})";
            TotalProgressBar.Value = 0;

            try
            {
                // First, create all necessary directories using main connection
                Log("Creating directories...");
                var directories = allFiles
                    .Select(f => Path.GetDirectoryName(f.remotePath)?.Replace("\\", "/"))
                    .Where(d => !string.IsNullOrEmpty(d) && d != _currentPS5Path)
                    .Distinct()
                    .OrderBy(d => d?.Length ?? 0)
                    .ToList();

                // Ensure connection is active before creating directories
                if (!_protocol.IsConnected)
                {
                    Log("⚠️ Connection lost before directory creation, reconnecting...");
                    if (!await _protocol.ConnectAsync(_ps5IpAddress))
                    {
                        throw new Exception("Failed to reconnect to PS5 before directory creation");
                    }
                    Log("✅ Reconnected successfully");
                }
                
                foreach (var dir in directories)
                {
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Log($"📁 Creating dir: {dir}");
                        try
                        {
                            await _protocol.CreateDirAsync(dir);
                        }
                        catch (Exception ex)
                        {
                            Log($"⚠️ Failed to create dir {dir}: {ex.Message}, retrying...");
                            // Try to reconnect and retry
                            _protocol.Disconnect();
                            await Task.Delay(500);
                            if (await _protocol.ConnectAsync(_ps5IpAddress))
                            {
                                await _protocol.CreateDirAsync(dir);
                            }
                            else
                            {
                                throw new Exception($"Failed to create directory {dir} after reconnection");
                            }
                        }
                    }
                }
                Log($"✅ Created {directories.Count} directories");
                
                // Ensure connection is still active after directory creation
                if (!_protocol.IsConnected)
                {
                    Log("⚠️ Connection lost after directory creation, reconnecting...");
                    if (!await _protocol.ConnectAsync(_ps5IpAddress))
                    {
                        throw new Exception("Failed to reconnect to PS5 after directory creation");
                    }
                    Log("✅ Reconnected successfully");
                }
                
                // Start real-time UI updates
                _uiUpdateTimer.Start();
                
                // Mark protocol as busy during upload
                _isProtocolBusy = true;

                // PARALLEL UPLOAD: Process files in batches using multiple connections
                Log($"🚀 Starting parallel upload with {MaxParallelUploads} connections");
                var fileQueue = new Queue<(string localPath, string remotePath)>(allFiles);
                var activeTasks = new List<Task>();
                var taskToConnection = new Dictionary<Task, PS5Protocol>(); // FIX: Map tasks to connections directly
                var taskToFilePath = new Dictionary<Task, string>(); // Map tasks to file paths
                var taskToRemotePath = new Dictionary<Task, string>(); // Map tasks to remote paths for retry
                var fileChunkCounts = new Dictionary<string, int>(); // Track how many chunks per file
                var fileChunksCompleted = new Dictionary<string, int>(); // Track completed chunks per file
                var completedFiles = new HashSet<string>(); // Track which files are fully complete
                var failedFiles = new Dictionary<string, int>(); // Track retry count per file
                var permanentlyFailedFiles = new List<string>(); // Track files that exceeded max retries
                const int MAX_RETRIES = 3; // Maximum retry attempts per file

                UploadFileNameText.Text = $"Parallel upload: {MaxParallelUploads} connections";

                while (fileQueue.Count > 0 || activeTasks.Count > 0)
                {
                    if (_uploadCancellation.Token.IsCancellationRequested)
                    {
                        Log("⚠️ Upload cancelled by user");
                        break;
                    }

                    // Start new tasks up to MaxParallelUploads
                    while (fileQueue.Count > 0 && activeTasks.Count < MaxParallelUploads)
                    {
                        var (localPath, remotePath) = fileQueue.Dequeue();
                        FileInfo fileInfo = new FileInfo(localPath);
                        
                        // CRITICAL: Large files (>100 MB) - limit parallel uploads to prevent PS5 memory exhaustion
                        // 8 parallel large files for maximum aggregate throughput
                        // This threshold MUST match CHUNK_THRESHOLD in UploadFileParallelAsync
                        const long LARGE_FILE_THRESHOLD = 100 * 1024 * 1024; // 100 MB - same as chunking threshold
                        const int MAX_PARALLEL_LARGE_FILES = 8; // 8 large files at a time - optimal balance
                        bool isLargeFile = fileInfo.Length > LARGE_FILE_THRESHOLD;
                        
                        // Count how many large files are currently uploading
                        int currentLargeFileCount = 0;
                        foreach (var kvp in taskToFilePath)
                        {
                            if (File.Exists(kvp.Value))
                            {
                                var fi = new FileInfo(kvp.Value);
                                if (fi.Length > LARGE_FILE_THRESHOLD)
                                    currentLargeFileCount++;
                            }
                        }
                        
                        if (isLargeFile && currentLargeFileCount >= MAX_PARALLEL_LARGE_FILES)
                        {
                            // Too many large files already uploading
                            // Put it back in queue and wait
                            fileQueue.Enqueue((localPath, remotePath));
                            Log($"⏸️ Large file queued, waiting for slot ({currentLargeFileCount}/{MAX_PARALLEL_LARGE_FILES} large files active): {Path.GetFileName(localPath)} ({FormatFileSize(fileInfo.Length)})");
                            break;
                        }
                        
                        // Single-connection upload per file (no parallel chunking)
                        // 6 parallel single-connection uploads = full gigabit speed + zero errors
                        Log($"📤 Starting upload: {Path.GetFileName(localPath)} (Queue: {fileQueue.Count}, Active: {activeTasks.Count})");
                        var connection = new PS5Protocol();
                        
                        if (await connection.ConnectAsync(_ps5IpAddress))
                        {
                            Log($"✅ Connection {activeTasks.Count + 1} established");
                            var task = UploadFileParallelAsync(connection, localPath, remotePath, _uploadCancellation.Token);
                            activeTasks.Add(task);
                            taskToConnection[task] = connection; // FIX: Map task to connection directly
                            taskToFilePath[task] = localPath; // Map task to file
                            taskToRemotePath[task] = remotePath; // Map task to remote path for retry
                            fileChunkCounts[localPath] = 1; // Single chunk for small files
                            fileChunksCompleted[localPath] = 0;
                        }
                        else
                        {
                            Log($"❌ Connection failed, requeueing {Path.GetFileName(localPath)}");
                            // Connection failed, put file back in queue and wait
                            fileQueue.Enqueue((localPath, remotePath));
                            // Wait before retrying to let PS5 recover
                            await Task.Delay(2000);
                            break;
                        }
                    }

                    if (activeTasks.Count > 0)
                    {
                        Log($"⏳ Waiting for task completion (Active: {activeTasks.Count})");
                        // Wait for any task to complete
                        var completedTask = await Task.WhenAny(activeTasks);
                        Log($"✅ Task completed");
                        
                        // Await the task to catch any exceptions
                        bool taskSucceeded = false;
                        string? failedFilePath = null;
                        string? failedRemotePath = null;
                        
                        try
                        {
                            await completedTask;
                            Log($"✅ Task awaited successfully");
                            taskSucceeded = true;
                        }
                        catch (Exception ex)
                        {
                            Log($"❌ Task exception: {ex.Message}");
                            taskSucceeded = false;
                            
                            // Get file paths for retry
                            if (taskToFilePath.TryGetValue(completedTask, out failedFilePath) &&
                                taskToRemotePath.TryGetValue(completedTask, out failedRemotePath))
                            {
                                // Check retry count
                                if (!failedFiles.ContainsKey(failedFilePath))
                                    failedFiles[failedFilePath] = 0;
                                
                                failedFiles[failedFilePath]++;
                                
                                if (failedFiles[failedFilePath] <= MAX_RETRIES)
                                {
                                    Log($"🔄 Requeueing failed file for retry ({failedFiles[failedFilePath]}/{MAX_RETRIES}): {Path.GetFileName(failedFilePath)}");
                                    
                                    // FIX: Delete corrupted/partial file on PS5 before retry
                                    // This prevents data corruption when chunked uploads fail mid-way
                                    try
                                    {
                                        Log($"🗑️ Deleting partial file before retry: {failedRemotePath}");
                                        await _protocol.DeleteFileAsync(failedRemotePath);
                                        Log($"✅ Partial file deleted");
                                    }
                                    catch (Exception delEx)
                                    {
                                        Log($"⚠️ Could not delete partial file (may not exist): {delEx.Message}");
                                    }
                                    
                                    fileQueue.Enqueue((failedFilePath, failedRemotePath));
                                    // Reset chunk tracking for retry
                                    fileChunksCompleted[failedFilePath] = 0;
                                    // Wait before retrying to let PS5 recover from connection errors
                                    await Task.Delay(3000);
                                }
                                else
                                {
                                    Log($"❌ Max retries exceeded, skipping: {Path.GetFileName(failedFilePath)}");
                                    permanentlyFailedFiles.Add(failedFilePath);
                                }
                            }
                            
                            Dispatcher.Invoke(() =>
                            {
                                UploadFileNameText.Text = $"Upload error: {ex.Message}";
                            });
                        }
                        
                        // FIX: Use Dictionary-based connection lookup instead of index-based
                        // Get the file path for this task
                        string? filePath = null;
                        if (taskToFilePath.TryGetValue(completedTask, out filePath))
                        {
                            // Only count as completed if task succeeded
                            if (taskSucceeded)
                            {
                                // Increment completed chunks for this file
                                lock (_progressLock)
                                {
                                    fileChunksCompleted[filePath]++;
                                    
                                    // Check if all chunks for this file are complete
                                    if (fileChunksCompleted[filePath] >= fileChunkCounts[filePath] && !completedFiles.Contains(filePath))
                                    {
                                        completedFiles.Add(filePath);
                                        _completedFiles++;
                                        Log($"✅ File {_completedFiles}/{_totalFilesToUpload} completed");
                                    }
                                }
                            }
                            taskToFilePath.Remove(completedTask);
                            taskToRemotePath.Remove(completedTask);
                        }
                        
                        // Remove task from list
                        activeTasks.Remove(completedTask);
                        
                        // FIX: Cleanup connection using Dictionary lookup (no more index mismatch!)
                        if (taskToConnection.TryGetValue(completedTask, out var completedConn))
                        {
                            Log($"🧹 Cleaning up connection for completed task");
                            taskToConnection.Remove(completedTask);
                            
                            // Aggressively dispose connection immediately
                            try
                            {
                                completedConn.Disconnect();
                                completedConn.Dispose();
                            }
                            catch { }
                        }
                        
                        // Update active task count for real-time UI updates
                        _activeTaskCount = activeTasks.Count;
                        
                        // Update UI (will be updated in real-time by timer, but also update on completion)
                        UpdateUploadStats(_completedFiles, activeTasks.Count);
                    }
                    else if (fileQueue.Count > 0)
                    {
                        Log($"⚠️ No active tasks but {fileQueue.Count} files remain - retrying...");
                        // No active tasks but files remain - wait a bit and retry connections
                        await Task.Delay(500);
                    }
                }
                Log("🔄 Upload loop finished");
                
                // Stop real-time UI updates
                _uiUpdateTimer.Stop();

                // Cleanup any remaining connections (but NOT the main protocol connection)
                Log($"🧹 Cleaning up {taskToConnection.Count} remaining connections");
                foreach (var conn in taskToConnection.Values.ToList())
                {
                    try
                    {
                        conn.Disconnect();
                        conn.Dispose();
                    }
                    catch { }
                }
                taskToConnection.Clear();
                Log("✅ Cleanup complete");

                if (!_uploadCancellation.Token.IsCancellationRequested)
                {
                    Log($"🎉 Upload completed! {_totalFilesToUpload} files, {FormatFileSize(_totalBytesUploaded)}");
                    _localFiles.Clear();
                    
                    // FIX: Keep main protocol connection alive - no disconnect/reconnect needed!
                    // The main _protocol connection was never used for upload (separate connections were created)
                    // So it should still be alive and ready for browsing
                    Log("✅ Main connection still active, refreshing directory...");
                    _isProtocolBusy = false;
                    
                    // Check if connection is still alive, reconnect only if needed
                    if (!_protocol.IsConnected)
                    {
                        Log("⚠️ Main connection lost, reconnecting...");
                        if (await _protocol.ConnectAsync(_ps5IpAddress))
                        {
                            Log("✅ Reconnected to PS5");
                        }
                        else
                        {
                            Log("❌ Reconnection failed");
                            MessageBox.Show("Failed to reconnect to PS5. Please reconnect manually.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                    }
                    
                    await UpdateStorageInfoAsync();
                    
                    // Wait a bit to ensure PS5 filesystem has committed all files
                    await Task.Delay(500);
                    
                    // Clear upload cancellation token so LoadPS5DirectoryAsync won't skip
                    _uploadCancellation?.Dispose();
                    _uploadCancellation = null;
                    
                    Log($"🔄 Refreshing directory: {_currentPS5Path}");
                    await LoadPS5DirectoryAsync(_currentPS5Path);
                    Log("✅ Directory refreshed - uploaded files should now be visible");
                    
                    // Show success message AFTER refresh so user sees updated file list immediately
                    // Include failed files information if any
                    int successfulFiles = _totalFilesToUpload - permanentlyFailedFiles.Count;
                    string message = $"Upload completed!\n\n✅ Successful: {successfulFiles} files\nTotal: {FormatFileSize(_totalBytesUploaded)}";
                    
                    if (permanentlyFailedFiles.Count > 0)
                    {
                        message += $"\n\n❌ Failed: {permanentlyFailedFiles.Count} files";
                        message += "\n\nFailed files:";
                        foreach (var failedFile in permanentlyFailedFiles.Take(10))
                        {
                            message += $"\n• {Path.GetFileName(failedFile)}";
                        }
                        if (permanentlyFailedFiles.Count > 10)
                        {
                            message += $"\n... and {permanentlyFailedFiles.Count - 10} more";
                        }
                        message += "\n\nYou can retry these files by uploading them again.";
                        
                        MessageBox.Show(message, "Upload Completed with Errors", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        MessageBox.Show(message, "Upload Completed", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                else
                {
                    Log("⚠️ Upload cancelled");
                    MessageBox.Show("Upload cancelled by user", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Warning);
                    
                    // FIX: Keep connection alive, only reconnect if needed
                    Log("✅ Checking main connection status...");
                    _isProtocolBusy = false;
                    
                    if (!_protocol.IsConnected)
                    {
                        Log("⚠️ Main connection lost, reconnecting...");
                        try
                        {
                            if (await _protocol.ConnectAsync(_ps5IpAddress))
                            {
                                Log("✅ Reconnected to PS5");
                                await UpdateStorageInfoAsync();
                                await LoadPS5DirectoryAsync(_currentPS5Path);
                            }
                            else
                            {
                                Log("❌ Failed to reconnect to PS5");
                            }
                        }
                        catch (Exception reconnectEx)
                        {
                            Log($"❌ Reconnection failed: {reconnectEx.Message}");
                        }
                    }
                    else
                    {
                        Log("✅ Main connection still active");
                        await UpdateStorageInfoAsync();
                        await LoadPS5DirectoryAsync(_currentPS5Path);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log("❌ Upload cancelled (OperationCanceledException)");
                MessageBox.Show("Upload cancelled by user", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Warning);
                
                // FIX: Keep connection alive, only reconnect if needed
                _isProtocolBusy = false;
                
                if (!_protocol.IsConnected)
                {
                    Log("⚠️ Main connection lost, reconnecting...");
                    try
                    {
                        if (await _protocol.ConnectAsync(_ps5IpAddress))
                        {
                            Log("✅ Reconnected to PS5");
                            await UpdateStorageInfoAsync();
                            await LoadPS5DirectoryAsync(_currentPS5Path);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"❌ Reconnection error: {ex.Message}");
                    }
                }
                else
                {
                    Log("✅ Main connection still active");
                    await UpdateStorageInfoAsync();
                    await LoadPS5DirectoryAsync(_currentPS5Path);
                }
            }
            catch (Exception ex)
            {
                Log($"❌ Upload failed: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"Upload failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                _isProtocolBusy = false;
            }
            finally
            {
                Log("========== UPLOAD FINISHED ==========");
                _uiUpdateTimer.Stop();
                ProgressPanel.Visibility = Visibility.Collapsed;
                UploadButton.IsEnabled = true;
                CancelButton.IsEnabled = false;
                
                // Only dispose if not already disposed (may have been cleared during refresh)
                if (_uploadCancellation != null)
                {
                    _uploadCancellation.Dispose();
                    _uploadCancellation = null;
                }
            }
        }
        
        private async Task UploadFileChunkAsync(PS5Protocol connection, string localPath, string remotePath, long offset, long size, CancellationToken cancellationToken)
        {
            try
            {
                string fileName = Path.GetFileName(localPath);
                Log($"⬆️ Uploading chunk {offset / (1024 * 1024 * 1024) + 1}: {fileName} @ {FormatFileSize(offset)}");
                
                bool success = await connection.UploadFileAsync(localPath, remotePath, null, cancellationToken, offset, size);
                
                if (success)
                {
                    Log($"✅ Chunk {offset / (1024 * 1024 * 1024) + 1} complete: {fileName}");
                    lock (_progressLock)
                    {
                        _totalBytesUploaded += size;
                    }
                }
                else
                {
                    Log($"❌ Chunk {offset / (1024 * 1024 * 1024) + 1} failed: {fileName}");
                }
            }
            catch (Exception ex)
            {
                Log($"❌ Exception uploading chunk of {Path.GetFileName(localPath)}: {ex.Message}");
            }
        }
        
        private void CollectFilesFromDirectory(string localDir, string remoteDir, List<(string localPath, string remotePath)> files)
        {
            foreach (string file in Directory.GetFiles(localDir))
            {
                FileInfo info = new FileInfo(file);
                files.Add((file, CombinePath(remoteDir, info.Name)));
            }
            
            foreach (string dir in Directory.GetDirectories(localDir))
            {
                DirectoryInfo info = new DirectoryInfo(dir);
                CollectFilesFromDirectory(dir, CombinePath(remoteDir, info.Name), files);
            }
        }

        private async Task UploadFileParallelAsync(PS5Protocol connection, string localPath, string remotePath, CancellationToken cancellationToken)
        {
            try
            {
                string fileName = Path.GetFileName(localPath);
                FileInfo fileInfo = new FileInfo(localPath);
                
                // CRITICAL: Use chunking for large files (>100MB) to prevent connection timeouts
                const long CHUNK_THRESHOLD = 100 * 1024 * 1024; // 100 MB
                const long CHUNK_SIZE = 500 * 1024 * 1024; // 500 MB chunks
                
                if (fileInfo.Length > CHUNK_THRESHOLD)
                {
                    Log($"⬆️ Uploading (chunked): {fileName} ({FormatFileSize(fileInfo.Length)})");
                    
                    long totalChunks = (fileInfo.Length + CHUNK_SIZE - 1) / CHUNK_SIZE;
                    
                    // OPTIMIZATION: Upload chunks in parallel for maximum speed
                    // Use up to 4 parallel connections for chunked uploads
                    const int MAX_PARALLEL_CHUNKS = 4;
                    var chunkTasks = new List<Task<(bool success, long chunkIndex, long size)>>();
                    var semaphore = new SemaphoreSlim(MAX_PARALLEL_CHUNKS);
                    bool allChunksSuccess = true;
                    
                    // Track progress per chunk to calculate total progress correctly
                    var chunkProgress = new Dictionary<long, long>(); // chunkIndex -> bytes uploaded
                    var progressLock = new object();
                    
                    // CRITICAL: First chunk (offset=0) must create the file before other chunks can write
                    // Signal when first chunk has started and file is created
                    var firstChunkStarted = new TaskCompletionSource<bool>();
                    
                    for (long chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                    {
                        long offset = chunkIndex * CHUNK_SIZE;
                        long size = Math.Min(CHUNK_SIZE, fileInfo.Length - offset);
                        long currentChunkIndex = chunkIndex; // Capture for closure
                        
                        lock (progressLock)
                        {
                            chunkProgress[currentChunkIndex] = 0;
                        }
                        
                        Log($"📦 Uploading chunk {chunkIndex + 1}/{totalChunks}: {fileName} @ {FormatFileSize(offset)}");
                        
                        // Capture offset and size for closure - loop variables may change before task executes
                        long chunkOffset = offset;
                        long chunkSize = size;
                        
                        // Create task immediately - semaphore wait happens inside the task
                        var chunkTask = Task.Run(async () =>
                        {
                            // CRITICAL: Non-first chunks must wait for first chunk to create the file
                            // Otherwise they will fail trying to open a non-existent file
                            if (currentChunkIndex > 0)
                            {
                                await firstChunkStarted.Task;
                                // Small delay to ensure file is fully created and pre-allocated
                                await Task.Delay(100, cancellationToken);
                            }
                            
                            // Wait for semaphore slot
                            await semaphore.WaitAsync(cancellationToken);
                            
                            try
                            {
                                // Create new connection for this chunk
                                var chunkConnection = new PS5Protocol();
                                if (!await chunkConnection.ConnectAsync(_ps5IpAddress, 9113))
                                {
                                    if (currentChunkIndex == 0) firstChunkStarted.TrySetResult(false);
                                    return (false, currentChunkIndex, chunkSize);
                                }
                                
                                // Signal that first chunk has connected and will create the file
                                if (currentChunkIndex == 0)
                                {
                                    firstChunkStarted.TrySetResult(true);
                                }
                                
                                // Progress reporting for this chunk - aggregate all chunks' progress
                                // p.BytesSent is relative to THIS chunk (0 to chunk_size)
                                // We need to track actual bytes uploaded for this chunk only
                                var progress = new Progress<UploadProgress>(p =>
                                {
                                    lock (progressLock)
                                    {
                                        // Store only the bytes uploaded for THIS chunk (not offset+bytes)
                                        long previousBytes = chunkProgress.ContainsKey(currentChunkIndex) ? chunkProgress[currentChunkIndex] : 0;
                                        chunkProgress[currentChunkIndex] = p.BytesSent;
                                        long bytesIncrease = p.BytesSent - previousBytes;
                                        
                                        // Calculate total progress from all chunks
                                        long totalUploaded = 0;
                                        foreach (var kvp in chunkProgress)
                                        {
                                            totalUploaded += kvp.Value;
                                        }
                                        
                                        double filePercent = fileInfo.Length > 0 ? (double)totalUploaded / fileInfo.Length * 100 : 0;
                                        
                                        lock (_progressLock)
                                        {
                                            _currentFileName = fileName;
                                            _currentFileBytes = totalUploaded;
                                            _currentFileTotalBytes = fileInfo.Length;
                                            
                                            // Update total bytes in real-time for accurate speed display
                                            // Add the bytes increase from this progress update
                                            _totalBytesUploaded += bytesIncrease;
                                        }
                                        
                                        // Calculate real-time speed and ETA
                                        var elapsed = DateTime.Now - _uploadStartTime;
                                        double speed = elapsed.TotalSeconds > 0 ? _totalBytesUploaded / elapsed.TotalSeconds : 0;
                                        long remaining = _totalBytesToUpload - _totalBytesUploaded;
                                        TimeSpan eta = speed > 0 ? TimeSpan.FromSeconds(remaining / speed) : TimeSpan.Zero;
                                        
                                        Dispatcher.InvokeAsync(() =>
                                        {
                                            UploadProgressBar.Value = Math.Min(100, filePercent); // Cap at 100%
                                            UploadProgressText.Text = $"{FormatFileSize(totalUploaded)} / {FormatFileSize(fileInfo.Length)} ({filePercent:F1}%)";
                                            
                                            // Update speed and ETA in real-time
                                            UploadSpeedText.Text = $"Speed: {FormatFileSize((long)speed)}/s | {_activeTaskCount} active";
                                            UploadETAText.Text = $"ETA: {eta:hh\\:mm\\:ss} | Elapsed: {elapsed:hh\\:mm\\:ss}";
                                        });
                                    }
                                });
                                
                                bool success = await chunkConnection.UploadFileAsync(localPath, remotePath, progress, cancellationToken, chunkOffset, chunkSize);
                                
                                chunkConnection.Disconnect();
                                
                                return (success, currentChunkIndex, chunkSize);
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        }, cancellationToken);
                        
                        chunkTasks.Add(chunkTask);
                    }
                    
                    // Wait for all chunks to complete
                    var results = await Task.WhenAll(chunkTasks);
                    
                    // Check results
                    // Note: _totalBytesUploaded is already updated in real-time during progress callbacks
                    foreach (var (success, chunkIndex, size) in results.OrderBy(r => r.chunkIndex))
                    {
                        if (success)
                        {
                            Log($"✅ Chunk {chunkIndex + 1}/{totalChunks} complete: {fileName}");
                            // Bytes already counted in real-time progress callback
                        }
                        else
                        {
                            Log($"❌ Chunk {chunkIndex + 1}/{totalChunks} failed: {fileName}");
                            allChunksSuccess = false;
                        }
                    }
                    
                    if (allChunksSuccess)
                    {
                        double avgSpeed = fileInfo.Length / (DateTime.Now - _uploadStartTime).TotalSeconds;
                        Log($"✅ Upload complete (chunked): {fileName} @ {FormatFileSize((long)avgSpeed)}/s");
                        
                        // Add to transfer history
                        double avgSpeedMBps = avgSpeed / (1024 * 1024);
                        AddToHistory("Upload", fileName, localPath, remotePath, fileInfo.Length, avgSpeedMBps, "Success");
                    }
                    else
                    {
                        Log($"❌ Upload failed (chunked): {fileName}");
                        
                        // Add to transfer history as failed
                        AddToHistory("Upload", fileName, localPath, remotePath, fileInfo.Length, 0, "Failed", "Chunked upload failed");
                        
                        throw new Exception($"Chunked upload failed for {fileName}");
                    }
                }
                else
                {
                    // Small file - upload normally without chunking
                    Log($"⬆️ Uploading: {fileName}");
                    
                    // Progress reporting for per-file progress bar
                    var progress = new Progress<UploadProgress>(p =>
                    {
                        double filePercent = p.TotalBytes > 0 ? (double)p.BytesSent / p.TotalBytes * 100 : 0;
                        
                        lock (_progressLock)
                        {
                            _currentFileName = fileName;
                            _currentFileBytes = p.BytesSent;
                            _currentFileTotalBytes = p.TotalBytes;
                        }
                        
                        Dispatcher.InvokeAsync(() =>
                        {
                            UploadProgressBar.Value = filePercent;
                            UploadProgressText.Text = $"{FormatFileSize(p.BytesSent)} / {FormatFileSize(p.TotalBytes)} ({filePercent:F1}%)";
                        });
                        
                        // Log progress periodically (every 16MB for better visibility)
                        if (p.BytesSent % (16 * 1024 * 1024) < 8 * 1024 * 1024 || p.BytesSent == p.TotalBytes)
                        {
                            Log($"📊 {fileName}: {FormatFileSize(p.BytesSent)}/{FormatFileSize(p.TotalBytes)} ({filePercent:F1}%) @ {FormatFileSize((long)p.SpeedBytesPerSecond)}/s");
                        }
                    });

                    bool success = await connection.UploadFileAsync(localPath, remotePath, progress, cancellationToken);
                    
                    if (success)
                    {
                        double avgSpeed = fileInfo.Length / (DateTime.Now - _uploadStartTime).TotalSeconds;
                        Log($"✅ Upload complete: {fileName} @ {FormatFileSize((long)avgSpeed)}/s");
                        lock (_progressLock)
                        {
                            _totalBytesUploaded += fileInfo.Length;
                        }
                        
                        // Add to transfer history
                        double avgSpeedMBps = avgSpeed / (1024 * 1024);
                        AddToHistory("Upload", fileName, localPath, remotePath, fileInfo.Length, avgSpeedMBps, "Success");
                    }
                    else
                    {
                        Log($"❌ Upload failed: {fileName}");
                        
                        // Add to transfer history as failed
                        AddToHistory("Upload", fileName, localPath, remotePath, fileInfo.Length, 0, "Failed", "Upload failed");
                        
                        throw new Exception($"Upload failed for {fileName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"❌ Exception uploading {Path.GetFileName(localPath)}: {ex.Message}");
                throw; // Re-throw so retry logic can catch it
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _uploadCancellation?.Cancel();
            CancelButton.IsEnabled = false;
        }

        private void GoButton_Click(object sender, RoutedEventArgs e)
        {
            string path = CurrentPathTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(path))
            {
                _ = LoadPS5DirectoryAsync(path);
            }
        }

        private void PS5FilesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (PS5FilesListBox.SelectedItem is PS5FileItem item && item.IsDirectory)
            {
                _ = LoadPS5DirectoryAsync(item.FullPath);
            }
        }

        private async void RenameMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (PS5FilesListBox.SelectedItem is PS5FileItem item)
            {
                var dialog = new Window
                {
                    Title = "Rename",
                    Width = 400,
                    Height = 180,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30))
                };

                var grid = new Grid { Margin = new Thickness(20) };
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = "New name:",
                    Foreground = System.Windows.Media.Brushes.White,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                Grid.SetRow(label, 0);

                var textBox = new TextBox
                {
                    Text = item.Name,
                    Margin = new Thickness(0, 0, 0, 15)
                };
                Grid.SetRow(textBox, 1);

                var buttonPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetRow(buttonPanel, 2);

                var okButton = new Button
                {
                    Content = "OK",
                    Width = 80,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                okButton.Click += (s, args) => { dialog.DialogResult = true; dialog.Close(); };

                var cancelButton = new Button
                {
                    Content = "Cancel",
                    Width = 80
                };
                cancelButton.Click += (s, args) => { dialog.DialogResult = false; dialog.Close(); };

                buttonPanel.Children.Add(okButton);
                buttonPanel.Children.Add(cancelButton);

                grid.Children.Add(label);
                grid.Children.Add(textBox);
                grid.Children.Add(buttonPanel);
                dialog.Content = grid;

                if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(textBox.Text))
                {
                    try
                    {
                        string newPath = Path.Combine(Path.GetDirectoryName(item.FullPath)?.Replace("\\", "/") ?? "", textBox.Text).Replace("\\", "/");
                        await _protocol.RenameAsync(item.FullPath, newPath);
                        Log($"✅ Renamed {item.Name} to {textBox.Text}");
                        await LoadPS5DirectoryAsync(_currentPS5Path);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Rename failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        Log($"❌ Rename failed: {ex.Message}");
                    }
                }
            }
        }

        private async void CopyMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (PS5FilesListBox.SelectedItem is PS5FileItem item)
            {
                var dialog = new Window
                {
                    Title = "Copy To",
                    Width = 500,
                    Height = 180,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30))
                };

                var grid = new Grid { Margin = new Thickness(20) };
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = "Destination path:",
                    Foreground = System.Windows.Media.Brushes.White,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                Grid.SetRow(label, 0);

                var pathPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, 15)
                };
                Grid.SetRow(pathPanel, 1);

                var textBox = new TextBox
                {
                    Text = CombinePath(_currentPS5Path, item.Name),
                    Width = 350
                };

                var browseButton = new Button
                {
                    Content = "Browse...",
                    Width = 80,
                    Margin = new Thickness(10, 0, 0, 0)
                };
                browseButton.Click += async (s, args) =>
                {
                    var pathDialog = new Window
                    {
                        Title = "Select PS5 Folder",
                        Width = 500,
                        Height = 450,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Owner = dialog,
                        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30))
                    };

                    var pathGrid = new Grid { Margin = new Thickness(10) };
                    pathGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    pathGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    pathGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    pathGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var pathLabel = new TextBlock
                    {
                        Text = "Current path:",
                        Foreground = System.Windows.Media.Brushes.White,
                        Margin = new Thickness(0, 0, 0, 5)
                    };
                    Grid.SetRow(pathLabel, 0);

                    var pathTextBox = new TextBox
                    {
                        Text = _currentPS5Path,
                        IsReadOnly = true,
                        Margin = new Thickness(0, 0, 0, 10)
                    };
                    Grid.SetRow(pathTextBox, 1);

                    var listBox = new ListBox { Margin = new Thickness(0, 0, 0, 10) };
                    Grid.SetRow(listBox, 2);

                    var buttonPanel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right
                    };
                    Grid.SetRow(buttonPanel, 3);

                    var selectCurrentButton = new Button { Content = "Select Current", Width = 100, Margin = new Thickness(0, 0, 10, 0) };
                    var selectButton = new Button { Content = "Select Folder", Width = 100, Margin = new Thickness(0, 0, 10, 0) };
                    var cancelButton = new Button { Content = "Cancel", Width = 80 };

                    buttonPanel.Children.Add(selectCurrentButton);
                    buttonPanel.Children.Add(selectButton);
                    buttonPanel.Children.Add(cancelButton);

                    pathGrid.Children.Add(pathLabel);
                    pathGrid.Children.Add(pathTextBox);
                    pathGrid.Children.Add(listBox);
                    pathGrid.Children.Add(buttonPanel);
                    pathDialog.Content = pathGrid;

                    string currentBrowsePath = _currentPS5Path;

                    async Task LoadFolders(string path)
                    {
                        listBox.Items.Clear();
                        pathTextBox.Text = path;
                        currentBrowsePath = path;

                        try
                        {
                            // Add parent directory option if not at root
                            if (path != "/" && path.Contains("/"))
                            {
                                listBox.Items.Add("..");
                            }

                            var dirs = await _protocol.ListDirAsync(path);
                            foreach (var dir in dirs.Where(d => d.IsDirectory))
                            {
                                listBox.Items.Add(dir.Name);
                            }
                        }
                        catch
                        {
                            MessageBox.Show("Failed to load folders from: " + path, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }

                    listBox.MouseDoubleClick += async (ss, ee) =>
                    {
                        if (listBox.SelectedItem != null)
                        {
                            string selected = listBox.SelectedItem.ToString()!;
                            if (selected == "..")
                            {
                                // Go to parent directory
                                int lastSlash = currentBrowsePath.TrimEnd('/').LastIndexOf('/');
                                if (lastSlash > 0)
                                {
                                    await LoadFolders(currentBrowsePath.Substring(0, lastSlash));
                                }
                                else if (lastSlash == 0)
                                {
                                    await LoadFolders("/");
                                }
                            }
                            else
                            {
                                // Navigate into folder
                                string newPath = currentBrowsePath.TrimEnd('/') + "/" + selected;
                                await LoadFolders(newPath);
                            }
                        }
                    };

                    selectCurrentButton.Click += (ss, aa) =>
                    {
                        textBox.Text = currentBrowsePath.TrimEnd('/') + "/" + item.Name;
                        pathDialog.Close();
                    };

                    selectButton.Click += (ss, aa) =>
                    {
                        if (listBox.SelectedItem != null && listBox.SelectedItem.ToString() != "..")
                        {
                            string selected = listBox.SelectedItem.ToString()!;
                            textBox.Text = currentBrowsePath.TrimEnd('/') + "/" + selected + "/" + item.Name;
                            pathDialog.Close();
                        }
                    };

                    cancelButton.Click += (ss, aa) => { pathDialog.Close(); };

                    // Load initial folders
                    await LoadFolders(_currentPS5Path);

                    pathDialog.ShowDialog();
                };

                pathPanel.Children.Add(textBox);
                pathPanel.Children.Add(browseButton);

                var buttonPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetRow(buttonPanel, 2);

                var okButton = new Button
                {
                    Content = "Copy",
                    Width = 80,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                okButton.Click += (s, args) => { dialog.DialogResult = true; dialog.Close(); };

                var cancelButton = new Button
                {
                    Content = "Cancel",
                    Width = 80
                };
                cancelButton.Click += (s, args) => { dialog.DialogResult = false; dialog.Close(); };

                buttonPanel.Children.Add(okButton);
                buttonPanel.Children.Add(cancelButton);

                grid.Children.Add(label);
                grid.Children.Add(pathPanel);
                grid.Children.Add(buttonPanel);
                dialog.Content = grid;

                if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(textBox.Text))
                {
                    try
                    {
                        await _protocol.CopyFileAsync(item.FullPath, textBox.Text);
                        Log($"✅ Copied {item.Name} to {textBox.Text}");
                        await LoadPS5DirectoryAsync(_currentPS5Path);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Copy failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        Log($"❌ Copy failed: {ex.Message}");
                    }
                }
            }
        }

        private async void MoveMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (PS5FilesListBox.SelectedItem is PS5FileItem item)
            {
                var dialog = new Window
                {
                    Title = "Move To",
                    Width = 500,
                    Height = 180,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30))
                };

                var grid = new Grid { Margin = new Thickness(20) };
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = "Destination path:",
                    Foreground = System.Windows.Media.Brushes.White,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                Grid.SetRow(label, 0);

                var pathPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, 15)
                };
                Grid.SetRow(pathPanel, 1);

                var textBox = new TextBox
                {
                    Text = CombinePath(_currentPS5Path, item.Name),
                    Width = 350
                };

                var browseButton = new Button
                {
                    Content = "Browse...",
                    Width = 80,
                    Margin = new Thickness(10, 0, 0, 0)
                };
                browseButton.Click += async (s, args) =>
                {
                    var pathDialog = new Window
                    {
                        Title = "Select PS5 Folder",
                        Width = 500,
                        Height = 450,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Owner = dialog,
                        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30))
                    };

                    var pathGrid = new Grid { Margin = new Thickness(10) };
                    pathGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    pathGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    pathGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    pathGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var pathLabel = new TextBlock
                    {
                        Text = "Current path:",
                        Foreground = System.Windows.Media.Brushes.White,
                        Margin = new Thickness(0, 0, 0, 5)
                    };
                    Grid.SetRow(pathLabel, 0);

                    var pathTextBox = new TextBox
                    {
                        Text = _currentPS5Path,
                        IsReadOnly = true,
                        Margin = new Thickness(0, 0, 0, 10)
                    };
                    Grid.SetRow(pathTextBox, 1);

                    var listBox = new ListBox { Margin = new Thickness(0, 0, 0, 10) };
                    Grid.SetRow(listBox, 2);

                    var buttonPanel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right
                    };
                    Grid.SetRow(buttonPanel, 3);

                    var selectCurrentButton = new Button { Content = "Select Current", Width = 100, Margin = new Thickness(0, 0, 10, 0) };
                    var selectButton = new Button { Content = "Select Folder", Width = 100, Margin = new Thickness(0, 0, 10, 0) };
                    var cancelButton = new Button { Content = "Cancel", Width = 80 };

                    buttonPanel.Children.Add(selectCurrentButton);
                    buttonPanel.Children.Add(selectButton);
                    buttonPanel.Children.Add(cancelButton);

                    pathGrid.Children.Add(pathLabel);
                    pathGrid.Children.Add(pathTextBox);
                    pathGrid.Children.Add(listBox);
                    pathGrid.Children.Add(buttonPanel);
                    pathDialog.Content = pathGrid;

                    string currentBrowsePath = _currentPS5Path;

                    async Task LoadFolders(string path)
                    {
                        listBox.Items.Clear();
                        pathTextBox.Text = path;
                        currentBrowsePath = path;

                        try
                        {
                            // Add parent directory option if not at root
                            if (path != "/" && path.Contains("/"))
                            {
                                listBox.Items.Add("..");
                            }

                            var dirs = await _protocol.ListDirAsync(path);
                            foreach (var dir in dirs.Where(d => d.IsDirectory))
                            {
                                listBox.Items.Add(dir.Name);
                            }
                        }
                        catch
                        {
                            MessageBox.Show("Failed to load folders from: " + path, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }

                    listBox.MouseDoubleClick += async (ss, ee) =>
                    {
                        if (listBox.SelectedItem != null)
                        {
                            string selected = listBox.SelectedItem.ToString()!;
                            if (selected == "..")
                            {
                                // Go to parent directory
                                int lastSlash = currentBrowsePath.TrimEnd('/').LastIndexOf('/');
                                if (lastSlash > 0)
                                {
                                    await LoadFolders(currentBrowsePath.Substring(0, lastSlash));
                                }
                                else if (lastSlash == 0)
                                {
                                    await LoadFolders("/");
                                }
                            }
                            else
                            {
                                // Navigate into folder
                                string newPath = currentBrowsePath.TrimEnd('/') + "/" + selected;
                                await LoadFolders(newPath);
                            }
                        }
                    };

                    selectCurrentButton.Click += (ss, aa) =>
                    {
                        textBox.Text = currentBrowsePath.TrimEnd('/') + "/" + item.Name;
                        pathDialog.Close();
                    };

                    selectButton.Click += (ss, aa) =>
                    {
                        if (listBox.SelectedItem != null && listBox.SelectedItem.ToString() != "..")
                        {
                            string selected = listBox.SelectedItem.ToString()!;
                            textBox.Text = currentBrowsePath.TrimEnd('/') + "/" + selected + "/" + item.Name;
                            pathDialog.Close();
                        }
                    };

                    cancelButton.Click += (ss, aa) => { pathDialog.Close(); };

                    // Load initial folders
                    await LoadFolders(_currentPS5Path);

                    pathDialog.ShowDialog();
                };

                pathPanel.Children.Add(textBox);
                pathPanel.Children.Add(browseButton);

                var buttonPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetRow(buttonPanel, 2);

                var okButton = new Button
                {
                    Content = "Move",
                    Width = 80,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                okButton.Click += (s, args) => { dialog.DialogResult = true; dialog.Close(); };

                var cancelButton = new Button
                {
                    Content = "Cancel",
                    Width = 80
                };
                cancelButton.Click += (s, args) => { dialog.DialogResult = false; dialog.Close(); };

                buttonPanel.Children.Add(okButton);
                buttonPanel.Children.Add(cancelButton);

                grid.Children.Add(label);
                grid.Children.Add(pathPanel);
                grid.Children.Add(buttonPanel);
                dialog.Content = grid;

                if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(textBox.Text))
                {
                    try
                    {
                        await _protocol.RenameAsync(item.FullPath, textBox.Text);
                        Log($"✅ Moved {item.Name} to {textBox.Text}");
                        await LoadPS5DirectoryAsync(_currentPS5Path);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Move failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        Log($"❌ Move failed: {ex.Message}");
                    }
                }
            }
        }

        private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (PS5FilesListBox.SelectedItem is PS5FileItem item)
            {
                var result = MessageBox.Show($"Delete {item.Name}?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        if (item.IsDirectory)
                        {
                            Log($"🗑️ Deleting folder: {item.Name}");
                            await _protocol.DeleteDirAsync(item.FullPath);
                            Log($"✅ Folder deletion complete: {item.Name}");
                            
                            // Wait a moment before reloading to ensure server is ready
                            await Task.Delay(500);
                        }
                        else
                        {
                            await _protocol.DeleteFileAsync(item.FullPath);
                        }
                        await LoadPS5DirectoryAsync(_currentPS5Path);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Delete failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        Log($"❌ Delete failed: {ex.Message}");
                    }
                }
            }
        }

        private async void DownloadMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (PS5FilesListBox.SelectedItem is PS5FileItem item)
            {
                if (item.IsDirectory)
                {
                    MessageBox.Show("Folder download not yet implemented. Please select a file.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                // Show save file dialog
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = item.Name,
                    Title = "Save Downloaded File"
                };
                
                if (dialog.ShowDialog() == true)
                {
                    try
                    {
                        Log($"⬇️ Downloading: {item.Name} ({FormatFileSize(item.Size)})");
                        
                        DateTime downloadStart = DateTime.Now;
                        double totalSpeedMBps = 0;
                        int speedSamples = 0;
                        
                        var progress = new Progress<UploadProgress>(p =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                double percent = p.TotalBytes > 0 ? (double)p.BytesSent / p.TotalBytes * 100 : 0;
                                Log($"📊 Download progress: {FormatFileSize(p.BytesSent)}/{FormatFileSize(p.TotalBytes)} ({percent:F1}%) @ {FormatFileSize((long)p.SpeedBytesPerSecond)}/s");
                                
                                // Track average speed
                                totalSpeedMBps += p.SpeedBytesPerSecond / (1024 * 1024);
                                speedSamples++;
                            });
                        });
                        
                        bool success = await _protocol.DownloadFileAsync(item.FullPath, dialog.FileName, progress);
                        
                        if (success)
                        {
                            Log($"✅ Downloaded: {item.Name} → {dialog.FileName}");
                            
                            // Calculate average speed
                            double avgSpeed = speedSamples > 0 ? totalSpeedMBps / speedSamples : item.Size / (DateTime.Now - downloadStart).TotalSeconds / (1024 * 1024);
                            
                            // Add to transfer history
                            AddToHistory("Download", item.Name, item.FullPath, dialog.FileName, item.Size, avgSpeed, "Success");
                            
                            MessageBox.Show($"File downloaded successfully!\n\nSaved to: {dialog.FileName}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        else
                        {
                            Log($"❌ Download failed: {item.Name}");
                            
                            // Add to transfer history as failed
                            AddToHistory("Download", item.Name, item.FullPath, dialog.FileName, item.Size, 0, "Failed", "Download failed");
                            
                            MessageBox.Show("Download failed", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"❌ Download error: {ex.Message}");
                        
                        // Add to transfer history as failed
                        AddToHistory("Download", item.Name, item.FullPath, dialog.FileName ?? "", item.Size, 0, "Failed", ex.Message);
                        
                        MessageBox.Show($"Download error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private async void DeleteSelectedMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = PS5FilesListBox.SelectedItems.Cast<PS5FileItem>().ToList();
            
            if (selectedItems.Count == 0)
            {
                MessageBox.Show("No items selected.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            // Count folders and files
            int folderCount = selectedItems.Count(i => i.IsDirectory);
            int fileCount = selectedItems.Count(i => !i.IsDirectory);
            
            string message = $"Delete {selectedItems.Count} items?\n\n";
            if (folderCount > 0) message += $"📁 {folderCount} folders\n";
            if (fileCount > 0) message += $"📄 {fileCount} files";
            
            var result = MessageBox.Show(message, "Confirm Bulk Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;
            
            Log($"🗑️ Starting bulk delete of {selectedItems.Count} items...");
            
            // Mark protocol as busy
            _isProtocolBusy = true;
            
            int successCount = 0;
            int failCount = 0;
            
            foreach (var item in selectedItems)
            {
                try
                {
                    if (item.IsDirectory)
                    {
                        Log($"🗑️ Deleting folder: {item.Name}");
                        await _protocol.DeleteDirAsync(item.FullPath);
                        Log($"✅ Deleted: {item.Name}");
                    }
                    else
                    {
                        Log($"🗑️ Deleting file: {item.Name}");
                        await _protocol.DeleteFileAsync(item.FullPath);
                        Log($"✅ Deleted: {item.Name}");
                    }
                    successCount++;
                    
                    // Give PS5 time to process before next delete
                    await Task.Delay(300);
                }
                catch (Exception ex)
                {
                    Log($"❌ Failed to delete {item.Name}: {ex.Message}");
                    failCount++;
                }
            }
            
            Log($"🗑️ Bulk delete complete: {successCount} succeeded, {failCount} failed");
            
            // Mark protocol as not busy
            _isProtocolBusy = false;
            
            // Wait a moment before reloading
            await Task.Delay(500);
            await LoadPS5DirectoryAsync(_currentPS5Path);
            
            MessageBox.Show($"Bulk delete complete!\n\n✅ {successCount} deleted\n❌ {failCount} failed", 
                "Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private async Task<List<(string localPath, string remotePath)>> FilterDuplicateFilesAsync(List<(string localPath, string remotePath)> allFiles)
        {
            var filesToUpload = new List<(string localPath, string remotePath)>();
            _duplicateAction = DuplicateAction.Ask; // Reset for each upload session
            
            // Group files by directory to minimize ListDir calls
            var filesByDir = allFiles.GroupBy(f => Path.GetDirectoryName(f.remotePath)?.Replace("\\", "/") ?? "").ToList();
            Log($"🔍 Checking {filesByDir.Count} directories for duplicates...");
            
            int dirIndex = 0;
            foreach (var dirGroup in filesByDir)
            {
                dirIndex++;
                string remoteDir = dirGroup.Key;
                
                // Get existing files in this directory
                Dictionary<string, long> existingFiles;
                try
                {
                    Log($"📂 Checking dir ({dirIndex}/{filesByDir.Count}): {remoteDir}");
                    var entries = await _protocol.ListDirAsync(remoteDir);
                    existingFiles = entries
                        .Where(e => !e.IsDirectory)
                        .ToDictionary(e => e.Name, e => e.Size);
                    Log($"   Found {existingFiles.Count} existing files");
                }
                catch
                {
                    // Directory doesn't exist or error - all files are new
                    Log($"   Directory doesn't exist - {dirGroup.Count()} new files");
                    filesToUpload.AddRange(dirGroup);
                    continue;
                }
                
                // Check each file in this directory
                foreach (var file in dirGroup)
                {
                    string? fileName = Path.GetFileName(file.remotePath);
                    
                    if (existingFiles.ContainsKey(fileName!))
                    {
                        // File exists - check what to do
                        if (_duplicateAction == DuplicateAction.ReplaceAll)
                        {
                            // Delete existing file first to free disk space
                            try
                            {
                                await _protocol.DeleteFileAsync(file.remotePath);
                                Log($"🗑️ Deleted existing file: {fileName}");
                            }
                            catch { /* Ignore delete errors */ }
                            filesToUpload.Add(file);
                            continue;
                        }
                        else if (_duplicateAction == DuplicateAction.SkipAll)
                        {
                            Log($"⏭️ Skipping existing file: {fileName}");
                            continue;
                        }
                        else if (_duplicateAction == DuplicateAction.Ask)
                        {
                            // Show dialog
                            long localSize = new FileInfo(file.localPath).Length;
                            long remoteSize = existingFiles[fileName];
                            
                            var dialog = new DuplicateFileDialog(fileName, localSize, remoteSize);
                            dialog.Owner = this;
                            
                            bool? result = dialog.ShowDialog();
                            if (result == true)
                            {
                                switch (dialog.UserAction)
                                {
                                    case DuplicateFileDialog.FileAction.Replace:
                                        // Delete existing file first to free disk space
                                        try
                                        {
                                            await _protocol.DeleteFileAsync(file.remotePath);
                                            Log($"🗑️ Deleted existing file: {fileName}");
                                        }
                                        catch { /* Ignore delete errors */ }
                                        filesToUpload.Add(file);
                                        break;
                                    case DuplicateFileDialog.FileAction.Skip:
                                        Log($"⏭️ Skipping: {fileName}");
                                        break;
                                    case DuplicateFileDialog.FileAction.ReplaceAll:
                                        _duplicateAction = DuplicateAction.ReplaceAll;
                                        // Delete existing file first to free disk space
                                        try
                                        {
                                            await _protocol.DeleteFileAsync(file.remotePath);
                                            Log($"🗑️ Deleted existing file: {fileName}");
                                        }
                                        catch { /* Ignore delete errors */ }
                                        filesToUpload.Add(file);
                                        break;
                                    case DuplicateFileDialog.FileAction.SkipAll:
                                        _duplicateAction = DuplicateAction.SkipAll;
                                        Log($"⏭️ Skipping all existing files");
                                        break;
                                }
                            }
                            else
                            {
                                // Dialog cancelled - skip this file
                                Log($"⏭️ Skipping: {fileName}");
                            }
                        }
                    }
                    else
                    {
                        // File doesn't exist - add to upload list
                        filesToUpload.Add(file);
                    }
                }
            }
            
            return filesToUpload;
        }

        // Multi-PS5 Support Methods
        private void LoadProfiles()
        {
            try
            {
                if (File.Exists(ProfilesFileName))
                {
                    string json = File.ReadAllText(ProfilesFileName);
                    _ps5Profiles = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                    
                    PS5ProfileComboBox.Items.Clear();
                    foreach (var profile in _ps5Profiles)
                    {
                        PS5ProfileComboBox.Items.Add(profile.Key);
                    }
                    
                    if (PS5ProfileComboBox.Items.Count > 0)
                    {
                        PS5ProfileComboBox.SelectedIndex = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ Failed to load profiles: {ex.Message}");
            }
        }

        private void SaveProfiles()
        {
            try
            {
                string json = System.Text.Json.JsonSerializer.Serialize(_ps5Profiles, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ProfilesFileName, json);
            }
            catch (Exception ex)
            {
                Log($"❌ Failed to save profiles: {ex.Message}");
            }
        }

        private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
        {
            string ipAddress = IpAddressTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                MessageBox.Show("Please enter a PS5 IP address first", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Ask for profile name
            var dialog = new Window
            {
                Title = "Save PS5 Profile",
                Width = 400,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30))
            };

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = "Profile Name:",
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(label, 0);

            var textBox = new TextBox
            {
                Text = "My PS5",
                Margin = new Thickness(0, 0, 0, 15)
            };
            Grid.SetRow(textBox, 1);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetRow(buttonPanel, 2);

            var okButton = new Button
            {
                Content = "Save",
                Width = 80,
                Margin = new Thickness(0, 0, 10, 0)
            };
            okButton.Click += (s, args) => { dialog.DialogResult = true; dialog.Close(); };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 80
            };
            cancelButton.Click += (s, args) => { dialog.DialogResult = false; dialog.Close(); };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);

            grid.Children.Add(label);
            grid.Children.Add(textBox);
            grid.Children.Add(buttonPanel);
            dialog.Content = grid;

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(textBox.Text))
            {
                string profileName = textBox.Text.Trim();
                _ps5Profiles[profileName] = ipAddress;
                SaveProfiles();
                
                // Refresh combo box
                PS5ProfileComboBox.Items.Clear();
                foreach (var profile in _ps5Profiles)
                {
                    PS5ProfileComboBox.Items.Add(profile.Key);
                }
                PS5ProfileComboBox.SelectedItem = profileName;
                
                Log($"💾 Saved profile: {profileName} ({ipAddress})");
                MessageBox.Show($"Profile '{profileName}' saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (PS5ProfileComboBox.SelectedItem is string profileName)
            {
                var result = MessageBox.Show($"Delete profile '{profileName}'?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _ps5Profiles.Remove(profileName);
                    SaveProfiles();
                    
                    PS5ProfileComboBox.Items.Remove(profileName);
                    if (PS5ProfileComboBox.Items.Count > 0)
                    {
                        PS5ProfileComboBox.SelectedIndex = 0;
                    }
                    
                    Log($"🗑️ Deleted profile: {profileName}");
                }
            }
            else
            {
                MessageBox.Show("No profile selected", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void PS5ProfileComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (PS5ProfileComboBox.SelectedItem is string profileName && _ps5Profiles.ContainsKey(profileName))
            {
                IpAddressTextBox.Text = _ps5Profiles[profileName];
                Log($"📋 Loaded profile: {profileName}");
            }
        }

        // Favorites/Bookmarks Methods
        private void LoadFavorites()
        {
            try
            {
                if (File.Exists(FavoritesFileName))
                {
                    string json = File.ReadAllText(FavoritesFileName);
                    _favoritePaths = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                    
                    FavoritesComboBox.Items.Clear();
                    foreach (var path in _favoritePaths)
                    {
                        FavoritesComboBox.Items.Add(path);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ Failed to load favorites: {ex.Message}");
            }
        }

        private void SaveFavorites()
        {
            try
            {
                string json = System.Text.Json.JsonSerializer.Serialize(_favoritePaths, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FavoritesFileName, json);
            }
            catch (Exception ex)
            {
                Log($"❌ Failed to save favorites: {ex.Message}");
            }
        }

        private void AddFavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            string currentPath = CurrentPathTextBox.Text.Trim();
            
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                MessageBox.Show("No path to add to favorites", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_favoritePaths.Contains(currentPath))
            {
                MessageBox.Show("This path is already in favorites", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _favoritePaths.Add(currentPath);
            SaveFavorites();
            
            FavoritesComboBox.Items.Add(currentPath);
            FavoritesComboBox.SelectedItem = currentPath;
            
            Log($"⭐ Added to favorites: {currentPath}");
            MessageBox.Show($"Added to favorites:\n{currentPath}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void RemoveFavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            if (FavoritesComboBox.SelectedItem is string selectedPath)
            {
                var result = MessageBox.Show($"Remove from favorites?\n{selectedPath}", "Confirm Remove", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _favoritePaths.Remove(selectedPath);
                    SaveFavorites();
                    
                    FavoritesComboBox.Items.Remove(selectedPath);
                    if (FavoritesComboBox.Items.Count > 0)
                    {
                        FavoritesComboBox.SelectedIndex = 0;
                    }
                    
                    Log($"🗑️ Removed from favorites: {selectedPath}");
                }
            }
            else
            {
                MessageBox.Show("No favorite selected", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async void FavoritesComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (FavoritesComboBox.SelectedItem is string favoritePath && !string.IsNullOrWhiteSpace(favoritePath))
            {
                Log($"⭐ Navigating to favorite: {favoritePath}");
                CurrentPathTextBox.Text = favoritePath;
                await LoadPS5DirectoryAsync(favoritePath);
            }
        }

        // Transfer History Methods
        private void LoadTransferHistory()
        {
            try
            {
                // If auto-clear is enabled, clear history on startup
                if (_autoClearHistoryOnStartup)
                {
                    _transferHistory.Clear();
                    _completedTransfers.Clear();
                    _failedTransfers.Clear();
                    SaveTransferHistory();
                    Log("🔄 Transfer history auto-cleared on startup");
                    return;
                }
                
                if (File.Exists(HistoryFileName))
                {
                    string json = File.ReadAllText(HistoryFileName);
                    var history = System.Text.Json.JsonSerializer.Deserialize<List<TransferHistoryItem>>(json);
                    if (history != null)
                    {
                        _transferHistory.Clear();
                        _completedTransfers.Clear();
                        _failedTransfers.Clear();
                        
                        foreach (var item in history.OrderByDescending(h => h.Timestamp))
                        {
                            _transferHistory.Add(item);
                            
                            // Separate into completed and failed collections
                            if (item.Status == "Success")
                            {
                                _completedTransfers.Add(item);
                            }
                            else
                            {
                                _failedTransfers.Add(item);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ Failed to load transfer history: {ex.Message}");
            }
        }

        private void SaveTransferHistory()
        {
            try
            {
                var historyList = _transferHistory.ToList();
                string json = System.Text.Json.JsonSerializer.Serialize(historyList, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(HistoryFileName, json);
            }
            catch (Exception ex)
            {
                Log($"❌ Failed to save transfer history: {ex.Message}");
            }
        }

        private void AddToHistory(string type, string fileName, string sourcePath, string destinationPath, long size, double speedMBps, string status, string errorMessage = "")
        {
            var historyItem = new TransferHistoryItem
            {
                Timestamp = DateTime.Now,
                Type = type,
                FileName = fileName,
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                Size = size,
                SpeedMBps = speedMBps,
                Status = status,
                ErrorMessage = errorMessage
            };

            Dispatcher.Invoke(() =>
            {
                _transferHistory.Insert(0, historyItem); // Add to top
                
                // Add to appropriate collection
                if (status == "Success")
                {
                    _completedTransfers.Insert(0, historyItem);
                }
                else
                {
                    _failedTransfers.Insert(0, historyItem);
                }
                
                SaveTransferHistory();
            });
        }

        private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Clear all transfer history?", "Confirm Clear", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _transferHistory.Clear();
                _completedTransfers.Clear();
                _failedTransfers.Clear();
                SaveTransferHistory();
                Log("🗑️ Transfer history cleared");
            }
        }

        private void AutoClearHistoryCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            _autoClearHistoryOnStartup = AutoClearHistoryCheckBox.IsChecked == true;
            SaveSettings();
            
            if (_autoClearHistoryOnStartup)
            {
                Log("✅ Auto-clear history on startup enabled");
            }
            else
            {
                Log("⚪ Auto-clear history on startup disabled");
            }
        }

        // Settings Methods
        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFileName))
                {
                    string json = File.ReadAllText(SettingsFileName);
                    var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
                    if (settings != null && settings.ContainsKey("AutoClearHistoryOnStartup"))
                    {
                        _autoClearHistoryOnStartup = settings["AutoClearHistoryOnStartup"];
                        AutoClearHistoryCheckBox.IsChecked = _autoClearHistoryOnStartup;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ Failed to load settings: {ex.Message}");
            }
        }

        private void SaveSettings()
        {
            try
            {
                var settings = new Dictionary<string, bool>
                {
                    { "AutoClearHistoryOnStartup", _autoClearHistoryOnStartup }
                };
                string json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFileName, json);
            }
            catch (Exception ex)
            {
                Log($"❌ Failed to save settings: {ex.Message}");
            }
        }

        private void ExportHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var saveDialog = new SaveFileDialog
                {
                    Filter = "CSV files (*.csv)|*.csv|JSON files (*.json)|*.json",
                    FileName = $"PS5_Transfer_History_{DateTime.Now:yyyyMMdd_HHmmss}"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    string extension = Path.GetExtension(saveDialog.FileName).ToLower();
                    
                    if (extension == ".csv")
                    {
                        // Export to CSV
                        var csv = new System.Text.StringBuilder();
                        csv.AppendLine("Timestamp,Type,FileName,SourcePath,DestinationPath,Size,Speed (MB/s),Status,ErrorMessage");
                        
                        foreach (var item in _transferHistory)
                        {
                            csv.AppendLine($"\"{item.TimestampFormatted}\",\"{item.Type}\",\"{item.FileName}\",\"{item.SourcePath}\",\"{item.DestinationPath}\",{item.Size},{item.SpeedMBps:F2},\"{item.Status}\",\"{item.ErrorMessage}\"");
                        }
                        
                        File.WriteAllText(saveDialog.FileName, csv.ToString());
                    }
                    else
                    {
                        // Export to JSON
                        string json = System.Text.Json.JsonSerializer.Serialize(_transferHistory.ToList(), new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(saveDialog.FileName, json);
                    }
                    
                    Log($"📊 Transfer history exported to: {saveDialog.FileName}");
                    MessageBox.Show($"Transfer history exported successfully!\n\n{saveDialog.FileName}", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Log($"❌ Failed to export history: {ex.Message}");
                MessageBox.Show($"Failed to export history:\n{ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void FailedTransfersListBox_RightClick(object sender, MouseButtonEventArgs e)
        {
            // Context menu will show automatically
        }

        private async void RetryFailedTransfers_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = FailedTransfersListBox.SelectedItems.Cast<TransferHistoryItem>().ToList();
            
            if (selectedItems.Count == 0)
            {
                MessageBox.Show("No failed transfers selected.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!_protocol.IsConnected)
            {
                MessageBox.Show("Not connected to PS5. Please connect first.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var result = MessageBox.Show($"Retry {selectedItems.Count} failed transfer(s)?", "Confirm Retry", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;

            Log($"🔄 Retrying {selectedItems.Count} failed transfer(s)...");

            foreach (var item in selectedItems)
            {
                try
                {
                    if (item.Type == "Upload")
                    {
                        // Retry upload
                        if (File.Exists(item.SourcePath))
                        {
                            Log($"🔄 Retrying upload: {item.FileName}");
                            
                            var fileInfo = new FileInfo(item.SourcePath);
                            var progress = new Progress<UploadProgress>(p =>
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    double percent = p.TotalBytes > 0 ? (double)p.BytesSent / p.TotalBytes * 100 : 0;
                                    Log($"📊 Retry progress: {FormatFileSize(p.BytesSent)}/{FormatFileSize(p.TotalBytes)} ({percent:F1}%) @ {FormatFileSize((long)p.SpeedBytesPerSecond)}/s");
                                });
                            });

                            bool success = await _protocol.UploadFileAsync(item.SourcePath, item.DestinationPath, progress, CancellationToken.None);
                            
                            if (success)
                            {
                                Log($"✅ Retry successful: {item.FileName}");
                                
                                // Remove from failed, add to completed
                                _failedTransfers.Remove(item);
                                _transferHistory.Remove(item);
                                
                                double avgSpeed = fileInfo.Length / 10.0 / (1024 * 1024); // Rough estimate
                                AddToHistory("Upload", item.FileName, item.SourcePath, item.DestinationPath, fileInfo.Length, avgSpeed, "Success");
                            }
                            else
                            {
                                Log($"❌ Retry failed: {item.FileName}");
                            }
                        }
                        else
                        {
                            Log($"⚠️ Source file not found: {item.SourcePath}");
                            MessageBox.Show($"Source file not found:\n{item.SourcePath}", "File Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                    else if (item.Type == "Download")
                    {
                        // Retry download
                        Log($"🔄 Retrying download: {item.FileName}");
                        
                        var saveDialog = new SaveFileDialog
                        {
                            FileName = item.FileName,
                            Title = "Save Downloaded File (Retry)"
                        };
                        
                        if (saveDialog.ShowDialog() == true)
                        {
                            var progress = new Progress<UploadProgress>(p =>
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    double percent = p.TotalBytes > 0 ? (double)p.BytesSent / p.TotalBytes * 100 : 0;
                                    Log($"📊 Retry download progress: {FormatFileSize(p.BytesSent)}/{FormatFileSize(p.TotalBytes)} ({percent:F1}%) @ {FormatFileSize((long)p.SpeedBytesPerSecond)}/s");
                                });
                            });

                            bool success = await _protocol.DownloadFileAsync(item.SourcePath, saveDialog.FileName, progress);
                            
                            if (success)
                            {
                                Log($"✅ Retry download successful: {item.FileName}");
                                
                                // Remove from failed, add to completed
                                _failedTransfers.Remove(item);
                                _transferHistory.Remove(item);
                                
                                double avgSpeed = item.Size / 10.0 / (1024 * 1024); // Rough estimate
                                AddToHistory("Download", item.FileName, item.SourcePath, saveDialog.FileName, item.Size, avgSpeed, "Success");
                            }
                            else
                            {
                                Log($"❌ Retry download failed: {item.FileName}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"❌ Retry error for {item.FileName}: {ex.Message}");
                    MessageBox.Show($"Retry error for {item.FileName}:\n{ex.Message}", "Retry Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            Log($"✅ Retry operation completed");
            MessageBox.Show("Retry operation completed. Check the log for details.", "Retry Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void RemoveFailedTransfers_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = FailedTransfersListBox.SelectedItems.Cast<TransferHistoryItem>().ToList();
            
            if (selectedItems.Count == 0)
            {
                MessageBox.Show("No failed transfers selected.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show($"Remove {selectedItems.Count} failed transfer(s) from history?", "Confirm Remove", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                foreach (var item in selectedItems)
                {
                    _failedTransfers.Remove(item);
                    _transferHistory.Remove(item);
                }
                
                SaveTransferHistory();
                Log($"🗑️ Removed {selectedItems.Count} failed transfer(s) from history");
            }
        }
    }

    public class LocalFileItem
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string Icon { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public string SizeFormatted => FormatSize(Size);

        private string FormatSize(long bytes)
        {
            if (IsDirectory) return "";
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }

    public class PS5FileItem
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string Icon { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public string SizeFormatted => FormatSize(Size);

        private string FormatSize(long bytes)
        {
            if (IsDirectory) return "";
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }

    public class TransferHistoryItem
    {
        public DateTime Timestamp { get; set; }
        public string Type { get; set; } = ""; // "Upload" or "Download"
        public string FileName { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string DestinationPath { get; set; } = "";
        public long Size { get; set; }
        public double SpeedMBps { get; set; }
        public string Status { get; set; } = ""; // "Success" or "Failed"
        public string ErrorMessage { get; set; } = "";
        
        public string TimestampFormatted => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
        public string SizeFormatted => FormatSize(Size);
        public string SpeedFormatted => $"{SpeedMBps:F2} MB/s";
        public string StatusIcon => Status == "Success" ? "✅" : "❌";

        private string FormatSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }
}
