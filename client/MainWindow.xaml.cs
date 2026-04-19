using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
        
        // Parallel upload settings
        private const int MaxParallelUploads = 24; // Increased for better throughput
        private const long SmallFileThresholdBytes = 5L * 1024 * 1024;
        private const long LargeFileThresholdBytes = 100L * 1024 * 1024;
        private const int MaxParallelLargeFiles = 6; // Increased from 4
        private const long HugeFileThresholdBytes = 20L * 1024 * 1024 * 1024;
        private const int MaxParallelHugeFiles = 3; // Increased from 2
        private const int MaxParallelChunksForLargeFile = 3; // Optimal: 3 lanes for ~47 MB/s
        private const int MaxParallelChunksForHugeFile = 4; // Optimal: 4 lanes for ~63 MB/s
        private const long ChunkLogIntervalBytes = 512L * 1024 * 1024; // Reduced log frequency
        private const long SmallFileLogIntervalBytes = 100L * 1024 * 1024;

        private const long ChunkThresholdBytes = LargeFileThresholdBytes;
        private const long DefaultChunkSizeBytes = 512L * 1024 * 1024; // Increased from 256MB for fewer round-trips
        private const long HugeFileChunkSizeBytes = 1536L * 1024 * 1024; // 1.5GB for fewer chunks and less overhead
        
        // Total upload tracking
        private int _totalFilesToUpload = 0;
        private long _totalBytesToUpload = 0;
        private long _totalBytesUploaded = 0;
        private int _completedFiles = 0;
        private DateTime _uploadStartTime;
        private readonly object _progressLock = new object();
        private ConcurrentDictionary<string, long> _fileProgressBytes = new ConcurrentDictionary<string, long>(); // Track bytes already counted per file
        private ConcurrentDictionary<string, ConcurrentDictionary<long, long>> _fileChunkProgressBytes = new ConcurrentDictionary<string, ConcurrentDictionary<long, long>>(); // Track per-chunk progress for concurrent uploads
        private ConcurrentDictionary<string, long> _chunkLogLastBytes = new ConcurrentDictionary<string, long>(); // Track last log emission per file
        private readonly object _speedLock = new object();
        private TimeSpan _smoothedETA = TimeSpan.Zero;
        private const double ETASmoothingFactor = 0.15; // Lower = smoother ETA (less jumpy)
        
        // Sliding window for real-time speed calculation
        private const int SpeedWindowSize = 10; // 10 samples at 500ms = 5 second window
        private long[] _speedWindowBytes = new long[SpeedWindowSize];
        private DateTime[] _speedWindowTimes = new DateTime[SpeedWindowSize];
        private int _speedWindowIndex = 0;
        private int _speedWindowCount = 0;
        private double _currentSpeed = 0; // Real-time speed in bytes/sec
        
        // Current file progress tracking (for largest active file)
        private string _currentFileName = "";
        private long _currentFileBytes = 0;
        private long _currentFileTotalBytes = 0;
        
        // Real-time UI update timer
        private DispatcherTimer _uiUpdateTimer;
        private int _activeTaskCount = 0;

        // Connection pooling for uploads
        private readonly ConcurrentQueue<PS5Protocol> _connectionPool = new ConcurrentQueue<PS5Protocol>();
        private int _currentPoolConnections = 0;
        private int _activeLargeUploads = 0;
        private int _activeHugeUploads = 0;

        // Small file logging batches
        private readonly object _smallFileLogLock = new object();
        private int _smallFileBatchRemainder = 0;
        private int _smallFileCompletedTotal = 0;
        private const int SmallFileLogBatchSize = 50; // Increased from 25 to reduce UI pressure with huge file counts
        private long _smallFileBatchBytes = 0;
        private long _smallFileTotalBytes = 0;

        // Log throttling to prevent UI freeze
        private int _logCounter = 0;
        private const int MaxLogLines = 1000;
        
        // Duplicate file handling
        private enum DuplicateAction { Ask, Replace, Skip, ReplaceAll, SkipAll }
        private DuplicateAction _duplicateAction = DuplicateAction.Ask;
        
        
        // Shell Terminal
        private bool _shellActive = false;
        private ObservableCollection<string> _shellOutput = new ObservableCollection<string>();
        private string _shellCurrentDir = "/data";
        
        // Auto-send payload configuration
        private bool _autoSendPayload = false;
        private string _payloadPath = "";
        private int _payloadPort = 9021;

        public MainWindow()
        {
            InitializeComponent();
            LocalFilesListBox.ItemsSource = _localFiles;
            PS5FilesListBox.ItemsSource = _ps5FilesFiltered;
            ShellOutputListBox.ItemsSource = _shellOutput;
            CompletedTransfersListBox.ItemsSource = _completedTransfers;
            FailedTransfersListBox.ItemsSource = _failedTransfers;
            
            // Initialize real-time UI update timer (500ms interval)
            _uiUpdateTimer = new DispatcherTimer();
            _uiUpdateTimer.Interval = TimeSpan.FromMilliseconds(500);
            _uiUpdateTimer.Tick += UiUpdateTimer_Tick;
            
            
            
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
            
            // Load auto-send payload settings
            LoadSettings();
            
            // Update UI with loaded settings
            if (AutoSendPayloadCheckBox != null)
            {
                AutoSendPayloadCheckBox.IsChecked = _autoSendPayload;
            }
            if (PayloadPathTextBox != null)
            {
                PayloadPathTextBox.Text = _payloadPath;
            }
            if (PayloadPortTextBox != null)
            {
                PayloadPortTextBox.Text = _payloadPort.ToString();
            }
            
            // Auto-send payload on startup if enabled
            if (_autoSendPayload && !string.IsNullOrEmpty(_payloadPath) && !string.IsNullOrEmpty(_ps5IpAddress))
            {
                _ = AutoSendPayloadOnStartup();
            }
        }

        private async Task<PS5StorageInfo?> FetchStorageInfoWithFallbackAsync()
        {
            bool uploadsIdle = (_uploadCancellation == null || _uploadCancellation.IsCancellationRequested) && _activeTaskCount == 0;
            if (_protocol.IsConnected && uploadsIdle)
            {
                return await _protocol.ListStorageAsync();
            }

            var tempProtocol = new PS5Protocol();
            try
            {
                if (!await tempProtocol.ConnectAsync(_ps5IpAddress))
                {
                    Log("❌ Storage refresh: failed to open temporary connection");
                    return null;
                }

                return await tempProtocol.ListStorageAsync();
            }
            finally
            {
                tempProtocol.Dispose();
            }
        }
        
        private async Task AutoSendPayloadOnStartup()
        {
            await Task.Delay(1000); // Wait 1 second for UI to load
            
            if (string.IsNullOrEmpty(_ps5IpAddress))
            {
                Log("⚠️ Auto-send payload: No PS5 IP address configured");
                return;
            }
            
            if (string.IsNullOrEmpty(_payloadPath) || !File.Exists(_payloadPath))
            {
                Log("⚠️ Auto-send payload: Payload file not found");
                return;
            }
            
            Log($"📤 Auto-sending payload to {_ps5IpAddress}:{_payloadPort}...");
            
            var progress = new Progress<long>(bytes =>
            {
                // Optional: Update progress in UI
            });
            
            bool success = await PS5Protocol.SendPayloadAsync(_ps5IpAddress, _payloadPath, _payloadPort, progress);
            
            if (success)
            {
                Log($"✅ Payload sent successfully ({new FileInfo(_payloadPath).Length} bytes)");
                await Task.Delay(2000); // Wait 2 seconds for payload to initialize
                
                // Auto-connect after payload is sent
                await ConnectToPS5Async();
            }
            else
            {
                Log("❌ Failed to send payload");
            }
        }
        
        private async Task ConnectToPS5Async()
        {
            if (await _protocol.ConnectAsync(_ps5IpAddress))
            {
                Log("✅ Connected to PS5 successfully");
                
                Dispatcher.Invoke(() =>
                {
                    ConnectButton.Content = "🟢 Disconnect";
                    ConnectButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
                    UploadButton.IsEnabled = true;
                    MountGamesButton.IsEnabled = true;
                });
                
                await LoadPS5DirectoryAsync(_currentPS5Path);
            }
            else
            {
                Log("❌ Failed to connect to PS5");
            }
        }
        
        private void LoadSettings()
        {
            try
            {
                const string settingsFile = "ps5upload_settings.json";
                if (File.Exists(settingsFile))
                {
                    string json = File.ReadAllText(settingsFile);
                    var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                    
                    if (settings != null)
                    {
                        if (settings.ContainsKey("AutoSendPayload"))
                            _autoSendPayload = settings["AutoSendPayload"].ToString() == "True";
                        
                        if (settings.ContainsKey("PayloadPath"))
                            _payloadPath = settings["PayloadPath"].ToString() ?? "";
                        
                        if (settings.ContainsKey("PayloadPort"))
                            int.TryParse(settings["PayloadPort"].ToString(), out _payloadPort);
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
                const string settingsFile = "ps5upload_settings.json";
                var settings = new Dictionary<string, object>
                {
                    ["AutoSendPayload"] = _autoSendPayload,
                    ["PayloadPath"] = _payloadPath,
                    ["PayloadPort"] = _payloadPort
                };
                
                string json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(settingsFile, json);
            }
            catch (Exception ex)
            {
                Log($"⚠️ Failed to save settings: {ex.Message}");
            }
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
            // Use Normal priority (not Background) so updates aren't starved during heavy uploads
            Dispatcher.InvokeAsync(() =>
            {
                // Update files remaining counter
                int remainingFiles = Math.Max(0, _totalFilesToUpload - completedFiles);
                FilesRemainingText.Text = $"Files: {completedFiles} / {_totalFilesToUpload} ({remainingFiles} remaining)";
                
                // Update total progress
                double totalPercent = _totalBytesToUpload > 0 ? (double)_totalBytesUploaded / _totalBytesToUpload * 100 : 0;
                TotalProgressBar.Value = totalPercent;
                TotalProgressText.Text = $"Total: {completedFiles} / {_totalFilesToUpload} files ({FormatFileSize(_totalBytesUploaded)} / {FormatFileSize(_totalBytesToUpload)}) [{totalPercent:F1}%]";
                
                var elapsed = DateTime.Now - _uploadStartTime;
                long currentBytes = Interlocked.Read(ref _totalBytesUploaded);
                
                // --- Sliding window real-time speed ---
                var now = DateTime.Now;
                _speedWindowBytes[_speedWindowIndex] = currentBytes;
                _speedWindowTimes[_speedWindowIndex] = now;
                _speedWindowIndex = (_speedWindowIndex + 1) % SpeedWindowSize;
                if (_speedWindowCount < SpeedWindowSize) _speedWindowCount++;
                
                double realtimeSpeed = 0;
                if (_speedWindowCount >= 2)
                {
                    int oldestIndex = (_speedWindowIndex - _speedWindowCount + SpeedWindowSize) % SpeedWindowSize;
                    long bytesDelta = currentBytes - _speedWindowBytes[oldestIndex];
                    double timeDelta = (now - _speedWindowTimes[oldestIndex]).TotalSeconds;
                    if (timeDelta > 0.1)
                    {
                        realtimeSpeed = bytesDelta / timeDelta;
                    }
                }
                
                // Smooth the real-time speed to avoid jitter
                if (_currentSpeed <= 0)
                {
                    _currentSpeed = realtimeSpeed;
                }
                else if (realtimeSpeed > 0)
                {
                    _currentSpeed = (_currentSpeed * 0.7) + (realtimeSpeed * 0.3);
                }
                
                // Average speed for comparison
                double avgSpeed = elapsed.TotalSeconds > 0 ? currentBytes / elapsed.TotalSeconds : 0;
                
                // --- ETA calculation using blended speed ---
                // Use 60% real-time + 40% average for stable but responsive ETA
                double etaSpeed = (_currentSpeed * 0.6) + (avgSpeed * 0.4);
                long remainingBytes = _totalBytesToUpload - currentBytes;
                TimeSpan rawETA = etaSpeed > 0 ? TimeSpan.FromSeconds(remainingBytes / etaSpeed) : TimeSpan.Zero;
                
                // Apply exponential smoothing to ETA
                if (_smoothedETA == TimeSpan.Zero)
                {
                    _smoothedETA = rawETA;
                }
                else if (rawETA.TotalSeconds > 0)
                {
                    double smoothedSeconds = (_smoothedETA.TotalSeconds * (1 - ETASmoothingFactor)) + (rawETA.TotalSeconds * ETASmoothingFactor);
                    _smoothedETA = TimeSpan.FromSeconds(Math.Max(0, smoothedSeconds));
                }
                TimeSpan eta = _smoothedETA;
                
                // --- Update UI ---
                UploadSpeedText.Text = $"Speed: {FormatFileSize((long)_currentSpeed)}/s (avg {FormatFileSize((long)avgSpeed)}/s) | {activeTaskCount} active";
                
                // Format ETA nicely
                string etaStr;
                if (eta.TotalHours >= 1)
                    etaStr = $"{(int)eta.TotalHours}h {eta.Minutes:D2}m {eta.Seconds:D2}s";
                else if (eta.TotalMinutes >= 1)
                    etaStr = $"{(int)eta.TotalMinutes}m {eta.Seconds:D2}s";
                else
                    etaStr = $"{eta.Seconds}s";
                
                UploadETAText.Text = $"ETA: {etaStr} | Elapsed: {elapsed:hh\\:mm\\:ss}";
                
                if (activeTaskCount > 0)
                {
                    UploadFileNameText.Text = $"Uploading {activeTaskCount} files in parallel...";
                }
            }, System.Windows.Threading.DispatcherPriority.Normal);
        }

        private void Log(string message)
        {
            // Don't log messages after cancellation (except final cleanup messages)
            if (_uploadCancellation != null && _uploadCancellation.Token.IsCancellationRequested)
            {
                // Only allow critical final messages
                if (!message.Contains("Upload cancelled") && 
                    !message.Contains("Cleanup complete") && 
                    !message.Contains("UPLOAD FINISHED") &&
                    !message.Contains("Checking main connection"))
                {
                    return; // Suppress all other messages after cancellation
                }
            }
            
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
                _protocol.Disconnect();
                
                // Reset shell state
                _shellActive = false;
                _shellCurrentDir = "/data";
                
                Dispatcher.Invoke(() =>
                {
                    ConnectButton.Content = "🔴 Disconnected";
                    ConnectButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.DarkRed);
                    UploadButton.IsEnabled = false;
                    MountGamesButton.IsEnabled = false;
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
                        MountGamesButton.IsEnabled = true;
                    });
                    
                    // Auto-connect Shell Terminal
                    await OpenShellAsync();
                    
                    await LoadPS5DirectoryAsync(_currentPS5Path);
                    
                    // Load storage info
                    await RefreshStorageInfoAsync();
                    
                    // NOTE: Hardware refresh disabled for stability
                    // User can manually refresh with the Refresh button in Hardware tab
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
                // Ensure connection is active before loading directory
                if (!_protocol.IsConnected)
                {
                    Log($"⚠️ Connection lost (LastError: {_protocol.LastError}), reconnecting...");
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
                
                // DEBUG: Log what we received
                Log($"🔍 DEBUG: ListDir returned {entries.Count()} entries for path: {path}");
                foreach (var entry in entries.Take(5))
                {
                    Log($"  - {(entry.IsDirectory ? "📁" : "📄")} {entry.Name} ({entry.Size} bytes)");
                }
                if (entries.Count() > 5)
                {
                    Log($"  ... and {entries.Count() - 5} more");
                }
                
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
            
            // Snapshot local files list for background processing
            var localFilesCopy = _localFiles.ToList();
            string currentPath = _currentPS5Path;
            
            // Show immediate feedback so user knows it's working
            UploadFileNameText.Text = "Preparing upload...";
            TotalProgressText.Text = "Collecting files...";

            // Move heavy file collection + byte calculation OFF the UI thread
            Log("Collecting files...");
            var allFiles = await Task.Run(() =>
            {
                var files = new List<(string localPath, string remotePath)>();
                foreach (var item in localFilesCopy)
                {
                    string targetBasePath = item.RemotePathOverride ?? (currentPath + "/" + item.Name);
                    if (item.IsDirectory)
                    {
                        CollectFilesFromDirectory(item.FullPath, targetBasePath, files);
                    }
                    else
                    {
                        files.Add((item.FullPath, targetBasePath));
                    }
                }
                return files;
            });
            
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
            
            // Calculate total bytes on background thread to avoid UI freeze on large game folders
            _totalBytesToUpload = await Task.Run(() =>
            {
                long total = 0;
                foreach (var file in allFiles)
                {
                    try { total += new FileInfo(file.localPath).Length; }
                    catch { }
                }
                return total;
            });
            _totalBytesUploaded = 0;
            _completedFiles = 0;
            _uploadStartTime = DateTime.Now;
            _fileProgressBytes.Clear(); // Clear progress tracking for new upload session
            _fileChunkProgressBytes.Clear();
            _chunkLogLastBytes.Clear();
            _smoothedETA = TimeSpan.Zero;
            _speedWindowIndex = 0;
            _speedWindowCount = 0;
            _currentSpeed = 0;
            _smallFileBatchRemainder = 0;
            _smallFileCompletedTotal = 0;
            _smallFileBatchBytes = 0;
            _smallFileTotalBytes = 0;
            _activeLargeUploads = 0;
            _activeHugeUploads = 0;
            DrainConnectionPool();
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
                
                int dirCreated = 0;
                int dirTotal = directories.Count;
                int dirLogInterval = Math.Max(1, dirTotal / 10); // Log every 10% progress
                
                foreach (var dir in directories)
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    
                    // Update UI progress without flooding the log
                    if (dirCreated % dirLogInterval == 0 || dirCreated == 0)
                    {
                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            UploadFileNameText.Text = $"Creating directories... ({dirCreated}/{dirTotal})";
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    }
                    
                    try
                    {
                        // Add timeout to prevent infinite hang on stuck connection
                        using var dirCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        var dirTask = _protocol.CreateDirAsync(dir);
                        var completedTask = await Task.WhenAny(dirTask, Task.Delay(-1, dirCts.Token));
                        if (completedTask != dirTask)
                        {
                            throw new TimeoutException($"Timeout creating directory: {dir}");
                        }
                        await dirTask; // Propagate any exception
                    }
                    catch (Exception ex)
                    {
                        Log($"⚠️ Failed to create dir {dir}: {ex.Message}, retrying...");
                        try
                        {
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
                        catch (Exception retryEx)
                        {
                            Log($"❌ Directory creation failed permanently: {dir} - {retryEx.Message}");
                            // Continue with remaining directories instead of crashing
                        }
                    }
                    dirCreated++;
                }
                Log($"✅ Created {dirCreated}/{dirTotal} directories");
                
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
                

                // Pre-warm connection pool to eliminate per-file connection setup delay
                int preWarmCount = Math.Min(MaxParallelUploads - 1, allFiles.Count); // -1 because main loop acquires on demand
                Log($"🚀 Starting parallel upload with {MaxParallelUploads} connections (pre-warming {preWarmCount})...");
                var preWarmTasks = new List<Task<PS5Protocol?>>();
                for (int i = 0; i < preWarmCount; i++)
                {
                    preWarmTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var conn = new PS5Protocol();
                            if (await conn.ConnectAsync(_ps5IpAddress))
                            {
                                Interlocked.Increment(ref _currentPoolConnections);
                                return conn;
                            }
                            conn.Dispose();
                        }
                        catch { }
                        return (PS5Protocol?)null;
                    }));
                }
                await Task.WhenAll(preWarmTasks);
                int pooled = 0;
                foreach (var t in preWarmTasks)
                {
                    if (t.Result != null)
                    {
                        _connectionPool.Enqueue(t.Result);
                        pooled++;
                    }
                }
                Log($"✅ {pooled} connections ready");

                // PARALLEL UPLOAD: Process files in batches using multiple connections
                var fileQueue = new Queue<(string localPath, string remotePath)>(allFiles);
                var activeTasks = new List<Task>();
                var taskToConnection = new Dictionary<Task, PS5Protocol>(); // FIX: Map tasks to connections directly
                var taskToFilePath = new Dictionary<Task, string>(); // Map tasks to file paths
                var taskToRemotePath = new Dictionary<Task, string>(); // Map tasks to remote paths for retry
                var fileChunkCounts = new Dictionary<string, int>(); // Track how many chunks per file
                var fileChunksCompleted = new Dictionary<string, int>(); // Track completed chunks per file
                var completedFiles = new HashSet<string>(); // Track which files are fully complete
                var failedFiles = new Dictionary<string, int>(); // Track retry count per file
                var taskIsLargeFile = new Dictionary<Task, bool>();
                var taskIsHugeFile = new Dictionary<Task, bool>();
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
                        long fileSize = new FileInfo(localPath).Length;
                        string fileName = Path.GetFileName(localPath);

                        bool isLargeFile = fileSize > LargeFileThresholdBytes;
                        bool isHugeFile = fileSize >= HugeFileThresholdBytes;

                        int currentLargeFileCount = Volatile.Read(ref _activeLargeUploads);
                        if (isLargeFile && currentLargeFileCount >= MaxParallelLargeFiles)
                        {
                            fileQueue.Enqueue((localPath, remotePath));
                            break; // Wait for a large file slot to free up
                        }

                        int currentHugeFileCount = Volatile.Read(ref _activeHugeUploads);
                        if (isHugeFile && currentHugeFileCount >= MaxParallelHugeFiles)
                        {
                            fileQueue.Enqueue((localPath, remotePath));
                            break; // Wait for a huge file slot to free up
                        }

                        PS5Protocol connection;
                        try
                        {
                            connection = await AcquireUploadConnectionAsync();
                        }
                        catch (Exception ex)
                        {
                            Log($"❌ Unable to acquire connection: {ex.Message}. Requeueing {fileName}");
                            fileQueue.Enqueue((localPath, remotePath));
                            await Task.Delay(1000);
                            break;
                        }

                        if (isLargeFile)
                        {
                            Interlocked.Increment(ref _activeLargeUploads);
                        }

                        if (isHugeFile)
                        {
                            Interlocked.Increment(ref _activeHugeUploads);
                        }

                        var task = UploadFileParallelAsync(connection, localPath, remotePath, _uploadCancellation.Token);
                        activeTasks.Add(task);
                        taskToConnection[task] = connection; // FIX: Map task to connection directly
                        taskToFilePath[task] = localPath; // Map task to file
                        taskToRemotePath[task] = remotePath; // Map task to remote path for retry
                        taskIsLargeFile[task] = isLargeFile;
                        taskIsHugeFile[task] = isHugeFile;

                        // Each file is ONE task (UploadFileParallelAsync handles chunks internally)
                        // So chunkCount must always be 1 for the completion counter to work
                        fileChunkCounts[localPath] = 1;
                        fileChunksCompleted[localPath] = 0;
                    }

                    if (activeTasks.Count > 0)
                    {
                        // Wait for any task to complete
                        var completedTask = await Task.WhenAny(activeTasks);
                        
                        // Await the task to catch any exceptions
                        bool taskSucceeded = false;
                        string? failedFilePath = null;
                        string? failedRemotePath = null;
                        
                        try
                        {
                            await completedTask;
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
                                }
                            }
                            
                            _ = Dispatcher.InvokeAsync(() =>
                            {
                                UploadFileNameText.Text = $"Upload error: {ex.Message}";
                            }, System.Windows.Threading.DispatcherPriority.Background);
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
                                        // FIX: Use Interlocked.Increment for thread-safe atomic increment
                                        // This prevents race conditions when 16 parallel threads update the counter
                                        int completed = Interlocked.Increment(ref _completedFiles);
                                        Log($"✅ File {completed}/{_totalFilesToUpload} completed");
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
                            taskToConnection.Remove(completedTask);

                            if (taskIsLargeFile.TryGetValue(completedTask, out var wasLarge) && wasLarge)
                            {
                                Interlocked.Decrement(ref _activeLargeUploads);
                                taskIsLargeFile.Remove(completedTask);
                            }

                            if (taskIsHugeFile.TryGetValue(completedTask, out var wasHuge) && wasHuge)
                            {
                                Interlocked.Decrement(ref _activeHugeUploads);
                                taskIsHugeFile.Remove(completedTask);
                            }

                            if (taskSucceeded && completedConn.IsConnected)
                            {
                                ReleaseUploadConnection(completedConn);
                            }
                            else
                            {
                                DestroyConnection(completedConn);
                            }
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
                FlushSmallFileBatch();
                
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
                DrainConnectionPool();
                Log("✅ Cleanup complete");

                if (!_uploadCancellation.Token.IsCancellationRequested)
                {
                    Log($"🎉 Upload completed! {_totalFilesToUpload} files, {FormatFileSize(_totalBytesUploaded)}");
                    
                    // Transfer history is now updated in real-time per file
                    
                    MessageBox.Show($"Upload completed!\n\n{_totalFilesToUpload} files uploaded\nTotal: {FormatFileSize(_totalBytesUploaded)}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    _localFiles.Clear();
                }
                else
                {
                    Log("⚠️ Upload cancelled");
                    MessageBox.Show("Upload cancelled by user", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                
                // FIX: Keep connection alive, only reconnect if needed
                Log("✅ Checking main connection status...");
                
                if (!_protocol.IsConnected)
                {
                    Log("⚠️ Main connection lost, reconnecting...");
                    try
                    {
                        if (await _protocol.ConnectAsync(_ps5IpAddress))
                        {
                            Log("✅ Reconnected to PS5");
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
                    await LoadPS5DirectoryAsync(_currentPS5Path);
                }
            }
            catch (OperationCanceledException)
            {
                Log("❌ Upload cancelled (OperationCanceledException)");
                MessageBox.Show("Upload cancelled by user", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Warning);
                
                // FIX: Keep connection alive, only reconnect if needed
                
                if (!_protocol.IsConnected)
                {
                    Log("⚠️ Main connection lost, reconnecting...");
                    try
                    {
                        if (await _protocol.ConnectAsync(_ps5IpAddress))
                        {
                            Log("✅ Reconnected to PS5");
                            await LoadPS5DirectoryAsync(_currentPS5Path);
                            await RefreshStorageInfoAsync(); // Add storage info refresh after successful connection
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
                    await LoadPS5DirectoryAsync(_currentPS5Path);
                }
            }
            catch (Exception ex)
            {
                Log($"❌ Upload failed: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"Upload failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Log("========== UPLOAD FINISHED ==========");
                _uiUpdateTimer.Stop();
                ProgressPanel.Visibility = Visibility.Collapsed;
                UploadButton.IsEnabled = true;
                CancelButton.IsEnabled = false;
                _uploadCancellation?.Dispose();
                _uploadCancellation = null;
                
                // Refresh PS5 directory to show uploaded files
                await LoadPS5DirectoryAsync(_currentPS5Path);
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
                files.Add((file, remoteDir + "/" + info.Name));
            }
            
            foreach (string dir in Directory.GetDirectories(localDir))
            {
                DirectoryInfo info = new DirectoryInfo(dir);
                CollectFilesFromDirectory(dir, remoteDir + "/" + info.Name, files);
            }
        }

        private async Task UploadFileParallelAsync(PS5Protocol connection, string localPath, string remotePath, CancellationToken cancellationToken)
        {
            try
            {
                string fileName = Path.GetFileName(localPath);
                FileInfo fileInfo = new FileInfo(localPath);

                // For small files, skip UI progress updates but keep byte tracking for speed calculation
                bool reportProgress = fileInfo.Length >= SmallFileThresholdBytes;

                if (fileInfo.Length > ChunkThresholdBytes)
                {
                    long chunkSize = fileInfo.Length >= HugeFileThresholdBytes ? HugeFileChunkSizeBytes : DefaultChunkSizeBytes;
                    long totalChunks = (fileInfo.Length + chunkSize - 1) / chunkSize;
                    int maxParallelChunks = fileInfo.Length >= HugeFileThresholdBytes ? MaxParallelChunksForHugeFile : MaxParallelChunksForLargeFile;
                    int workerCount = (int)Math.Min(totalChunks, Math.Max(1, maxParallelChunks));

                    Log($"⬆️ Uploading (chunked): {fileName} ({FormatFileSize(fileInfo.Length)}) using {FormatFileSize(chunkSize)} chunks with {workerCount} lanes");

                    // Initialize tracking for chunked upload
                    _fileProgressBytes[localPath] = 0;
                    _fileChunkProgressBytes[localPath] = new ConcurrentDictionary<long, long>();
                    _chunkLogLastBytes[localPath] = 0;

                    int nextChunkIndex = -1;
                    var workerTasks = new List<Task>(workerCount);
                    
                    // CRITICAL: Chunk 0 must complete START_UPLOAD (file creation + pre-allocation)
                    // before other chunks can open the file. Use a gate to synchronize.
                    var chunk0Ready = new SemaphoreSlim(0, 1);

                    for (int workerId = 0; workerId < workerCount; workerId++)
                    {
                        workerTasks.Add(RunChunkWorkerAsync(workerId));
                    }

                    await Task.WhenAll(workerTasks);

                    // Cleanup will happen after completion - no need here

                    async Task RunChunkWorkerAsync(int workerId)
                    {
                        PS5Protocol? workerConnection = null;
                        bool ownsConnection = workerId != 0;

                        try
                        {
                            workerConnection = ownsConnection ? await AcquireUploadConnectionAsync() : connection;

                            while (true)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                int chunkIndex = Interlocked.Increment(ref nextChunkIndex);
                                if (chunkIndex >= totalChunks)
                                {
                                    break;
                                }

                                // Non-zero chunks must wait for chunk 0's START_UPLOAD to complete
                                // (chunk 0 creates and pre-allocates the file on the PS5)
                                if (chunkIndex > 0)
                                {
                                    await chunk0Ready.WaitAsync(cancellationToken);
                                    chunk0Ready.Release(); // Re-release so other workers can also pass
                                }

                                // For chunk 0, pass a callback that signals the gate
                                // immediately after RESP_READY (file created on PS5)
                                Action? readyCallback = chunkIndex == 0 ? () => chunk0Ready.Release() : null;
                                await UploadChunkAsync(workerConnection!, chunkIndex, readyCallback);
                            }
                        }
                        finally
                        {
                            if (ownsConnection && workerConnection != null)
                            {
                                if (workerConnection.IsConnected)
                                {
                                    ReleaseUploadConnection(workerConnection);
                                }
                                else
                                {
                                    DestroyConnection(workerConnection);
                                }
                            }
                        }
                    }

                    async Task UploadChunkAsync(PS5Protocol workerConnection, int chunkIndex, Action? readyCallback = null)
                    {
                        long offset = chunkIndex * chunkSize;
                        long size = Math.Min(chunkSize, fileInfo.Length - offset);
                        long humanIndex = chunkIndex + 1;

                        Log($"⬆️ Uploading chunk {humanIndex}/{totalChunks}: {fileName} @ {FormatFileSize(offset)} ({FormatFileSize(size)})");

                        var progress = CreateChunkProgressReporter(offset, size, humanIndex, totalChunks);

                        bool success = await workerConnection.UploadFileAsync(localPath, remotePath, progress, cancellationToken, offset, size, readyCallback);

                        if (!success)
                        {
                            throw new Exception($"Chunk {humanIndex}/{totalChunks} failed for {fileName}");
                        }

                        // Update chunk progress atomically
                        if (_fileChunkProgressBytes.TryGetValue(localPath, out var chunkMap))
                        {
                            chunkMap[offset] = size;
                            long aggregateBytes = chunkMap.Values.Sum();
                            long previousTotal = _fileProgressBytes.GetOrAdd(localPath, 0);
                            long delta = aggregateBytes - previousTotal;
                            if (delta > 0)
                            {
                                Interlocked.Add(ref _totalBytesUploaded, delta);
                                _fileProgressBytes[localPath] = aggregateBytes;
                            }
                            _currentFileBytes = aggregateBytes;
                            _currentFileTotalBytes = fileInfo.Length;
                        }

                        Log($"✅ Chunk {humanIndex}/{totalChunks} complete: {fileName}");
                    }

                    IProgress<UploadProgress> CreateChunkProgressReporter(long chunkOffset, long chunkLength, long chunkNumber, long totalChunkCount)
                    {
                        int progressCallCount = 0;
                        return new Progress<UploadProgress>(p =>
                        {
                            progressCallCount++;
                            long chunkBytesSent = p.BytesSent - chunkOffset;
                            if (chunkBytesSent < 0) chunkBytesSent = 0;
                            if (chunkBytesSent > chunkLength) chunkBytesSent = chunkLength;

                            long aggregateBytes;
                            long previousLoggedBytes = 0;
                            bool shouldUpdateUI = progressCallCount % 10 == 0 || chunkBytesSent == chunkLength;

                            // Update progress atomically without locks
                            var chunkProgressMap = _fileChunkProgressBytes.GetOrAdd(localPath, _ => new ConcurrentDictionary<long, long>());
                            chunkProgressMap[chunkOffset] = chunkBytesSent;
                            aggregateBytes = chunkProgressMap.Values.Sum();

                            long previousTotal = _fileProgressBytes.GetOrAdd(localPath, 0);
                            long delta = aggregateBytes - previousTotal;
                            if (delta != 0)
                            {
                                Interlocked.Add(ref _totalBytesUploaded, delta);
                                _fileProgressBytes[localPath] = aggregateBytes;
                            }

                            _currentFileName = fileName;
                            _currentFileBytes = aggregateBytes;
                            _currentFileTotalBytes = fileInfo.Length;

                            _chunkLogLastBytes.TryGetValue(localPath, out previousLoggedBytes);

                            double filePercent = fileInfo.Length > 0 ? (double)aggregateBytes / fileInfo.Length * 100 : 0;

                            if (reportProgress && shouldUpdateUI)
                            {
                                Dispatcher.InvokeAsync(() =>
                                {
                                    UploadProgressBar.Value = filePercent;
                                    UploadProgressText.Text = $"{FormatFileSize(aggregateBytes)} / {FormatFileSize(fileInfo.Length)} ({filePercent:F1}%)";
                                }, System.Windows.Threading.DispatcherPriority.Render);

                                if (aggregateBytes == fileInfo.Length || aggregateBytes - previousLoggedBytes >= ChunkLogIntervalBytes)
                                {
                                    _chunkLogLastBytes[localPath] = aggregateBytes;
                                    Log($"📊 Chunk {chunkNumber}/{totalChunkCount}: {FormatFileSize(chunkBytesSent)}/{FormatFileSize(chunkLength)} ({filePercent:F1}%)");
                                }
                            }
                        });
                    }
                }
                else
                {
                    var progress = CreateSingleFileProgressReporter();
                    bool success = await connection.UploadFileAsync(localPath, remotePath, progress, cancellationToken);

                    if (!success)
                    {
                        throw new Exception($"Upload failed for {fileName}");
                    }

                    // Update final progress for small files
                    long previousBytes = _fileProgressBytes.GetOrAdd(localPath, 0);
                    long delta = fileInfo.Length - previousBytes;
                    if (delta > 0)
                    {
                        Interlocked.Add(ref _totalBytesUploaded, delta);
                        _fileProgressBytes[localPath] = fileInfo.Length;
                    }

                    IProgress<UploadProgress> CreateSingleFileProgressReporter()
                    {
                        int progressCallCount = 0;
                        return new Progress<UploadProgress>(p =>
                        {
                            progressCallCount++;
                            double filePercent = fileInfo.Length > 0 ? (double)p.BytesSent / fileInfo.Length * 100 : 0;
                            long previousLoggedBytes = 0;
                            bool shouldUpdateUI = progressCallCount % 10 == 0 || p.BytesSent == p.TotalBytes;

                            // Update progress atomically
                            _currentFileName = fileName;
                            _currentFileBytes = p.BytesSent;
                            _currentFileTotalBytes = fileInfo.Length;

                            long previousBytes = _fileProgressBytes.GetOrAdd(localPath, 0);
                            long bytesToAdd = p.BytesSent - previousBytes;
                            if (bytesToAdd > 0)
                            {
                                Interlocked.Add(ref _totalBytesUploaded, bytesToAdd);
                                _fileProgressBytes[localPath] = p.BytesSent;
                            }

                            _chunkLogLastBytes.TryGetValue(localPath, out previousLoggedBytes);

                            if (reportProgress && shouldUpdateUI)
                            {
                                Dispatcher.InvokeAsync(() =>
                                {
                                    UploadProgressBar.Value = filePercent;
                                    UploadProgressText.Text = $"{FormatFileSize(p.BytesSent)} / {FormatFileSize(fileInfo.Length)} ({filePercent:F1}%)";
                                }, System.Windows.Threading.DispatcherPriority.Render);

                                if (p.BytesSent == p.TotalBytes || p.BytesSent - previousLoggedBytes >= SmallFileLogIntervalBytes)
                                {
                                    _chunkLogLastBytes[localPath] = p.BytesSent;
                                    Log($"📊 {fileName}: {FormatFileSize(p.BytesSent)} / {FormatFileSize(fileInfo.Length)} ({filePercent:F1}%)");
                                }
                            }
                        });
                    }
                }

                // Clean up dictionaries to prevent memory overflow with huge file counts
                _fileProgressBytes.TryRemove(localPath, out _);
                _fileChunkProgressBytes.TryRemove(localPath, out _);
                _chunkLogLastBytes.TryRemove(localPath, out _);

                if (fileInfo.Length < 10 * 1024 * 1024)
                {
                    TrackSmallFileCompletion(fileName, fileInfo.Length);
                }
                else
                {
                    Log($"✅ Upload complete: {fileName}");
                }

                _ = Dispatcher.InvokeAsync(() =>
                {
                    _completedTransfers.Add(new TransferHistoryItem
                    {
                        FileName = fileName,
                        Status = "✅ Completed",
                        Size = FormatFileSize(fileInfo.Length),
                        Timestamp = DateTime.Now
                    });
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                string fileName = Path.GetFileName(localPath);
                Log($"❌ Exception uploading {fileName}: {ex.Message}");

                // Clean up on failure
                _fileProgressBytes.TryRemove(localPath, out _);
                _fileChunkProgressBytes.TryRemove(localPath, out _);
                _chunkLogLastBytes.TryRemove(localPath, out _);

                // Add to failed transfers
                _ = Dispatcher.InvokeAsync(() =>
                {
                    _failedTransfers.Add(new TransferHistoryItem
                    {
                        FileName = fileName,
                        Status = "❌ Failed (Exception)",
                        Size = FormatFileSize(new FileInfo(localPath).Length),
                        Timestamp = DateTime.Now,
                        LocalPath = localPath,
                        RemotePath = remotePath
                    });
                });

                throw; // Re-throw so retry logic can catch it
            }
        }

        private async Task<PS5Protocol> AcquireUploadConnectionAsync()
        {
            while (_connectionPool.TryDequeue(out var pooledConnection))
            {
                if (pooledConnection.IsConnected)
                {
                    return pooledConnection;
                }

                DestroyConnection(pooledConnection);
            }

            var connection = new PS5Protocol();
            const int retryAttempts = 3;

            for (int attempt = 0; attempt < retryAttempts; attempt++)
            {
                if (await connection.ConnectAsync(_ps5IpAddress))
                {
                    Interlocked.Increment(ref _currentPoolConnections);
                    return connection;
                }

                await Task.Delay(1000);
            }

            connection.Dispose();
            throw new InvalidOperationException("Unable to acquire upload connection");
        }

        private void ReleaseUploadConnection(PS5Protocol connection)
        {
            if (!connection.IsConnected)
            {
                DestroyConnection(connection);
                return;
            }

            _connectionPool.Enqueue(connection);
        }

        private void DestroyConnection(PS5Protocol connection)
        {
            try
            {
                connection.Disconnect();
                connection.Dispose();
            }
            catch
            {
                // Ignore cleanup exceptions
            }
            finally
            {
                Interlocked.Decrement(ref _currentPoolConnections);
            }
        }

        private void DrainConnectionPool()
        {
            while (_connectionPool.TryDequeue(out var connection))
            {
                DestroyConnection(connection);
            }
        }

        private void TrackSmallFileCompletion(string fileName, long fileSize)
        {
            lock (_smallFileLogLock)
            {
                _smallFileCompletedTotal++;
                _smallFileTotalBytes += fileSize;
                _smallFileBatchRemainder++;
                _smallFileBatchBytes += fileSize;

                if (_smallFileBatchRemainder >= SmallFileLogBatchSize)
                {
                    Log($"✅ {SmallFileLogBatchSize} small files completed (batch {FormatFileSize(_smallFileBatchBytes)}, total {FormatFileSize(_smallFileTotalBytes)})");
                    _smallFileBatchRemainder = 0;
                    _smallFileBatchBytes = 0;
                }
            }
        }

        private void FlushSmallFileBatch()
        {
            lock (_smallFileLogLock)
            {
                if (_smallFileBatchRemainder > 0)
                {
                    Log($"✅ {_smallFileBatchRemainder} small files completed (batch {FormatFileSize(_smallFileBatchBytes)}, total {FormatFileSize(_smallFileTotalBytes)})");
                    _smallFileBatchRemainder = 0;
                    _smallFileBatchBytes = 0;
                }
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

        private Task<string?> ShowPathPickerDialogAsync(string title, string actionLabel, string itemName, string defaultPath)
        {
            string? result = null;
            
            var dialog = new Window
            {
                Title = title,
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
                Text = defaultPath,
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

                var btnPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetRow(btnPanel, 3);

                var selectCurrentButton = new Button { Content = "Select Current", Width = 100, Margin = new Thickness(0, 0, 10, 0) };
                var selectButton = new Button { Content = "Select Folder", Width = 100, Margin = new Thickness(0, 0, 10, 0) };
                var cancelBtn = new Button { Content = "Cancel", Width = 80 };

                btnPanel.Children.Add(selectCurrentButton);
                btnPanel.Children.Add(selectButton);
                btnPanel.Children.Add(cancelBtn);

                pathGrid.Children.Add(pathLabel);
                pathGrid.Children.Add(pathTextBox);
                pathGrid.Children.Add(listBox);
                pathGrid.Children.Add(btnPanel);
                pathDialog.Content = pathGrid;

                string currentBrowsePath = _currentPS5Path;

                async Task LoadFolders(string path)
                {
                    listBox.Items.Clear();
                    pathTextBox.Text = path;
                    currentBrowsePath = path;

                    try
                    {
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
                            string newPath = currentBrowsePath.TrimEnd('/') + "/" + selected;
                            await LoadFolders(newPath);
                        }
                    }
                };

                selectCurrentButton.Click += (ss, aa) =>
                {
                    textBox.Text = currentBrowsePath.TrimEnd('/') + "/" + itemName;
                    pathDialog.Close();
                };

                selectButton.Click += (ss, aa) =>
                {
                    if (listBox.SelectedItem != null && listBox.SelectedItem.ToString() != "..")
                    {
                        string selected = listBox.SelectedItem.ToString()!;
                        textBox.Text = currentBrowsePath.TrimEnd('/') + "/" + selected + "/" + itemName;
                        pathDialog.Close();
                    }
                };

                cancelBtn.Click += (ss, aa) => { pathDialog.Close(); };

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
                Content = actionLabel,
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
                result = textBox.Text;
            }

            return Task.FromResult(result);
        }

        private async void CopyMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (PS5FilesListBox.SelectedItem is PS5FileItem item)
            {
                string? destPath = await ShowPathPickerDialogAsync("Copy To", "Copy", item.Name, _currentPS5Path + "/" + item.Name);
                if (destPath != null)
                {
                    try
                    {
                        await _protocol.CopyFileAsync(item.FullPath, destPath);
                        Log($"✅ Copied {item.Name} to {destPath}");
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
                string? destPath = await ShowPathPickerDialogAsync("Move To", "Move", item.Name, _currentPS5Path + "/" + item.Name);
                if (destPath != null)
                {
                    try
                    {
                        await _protocol.RenameAsync(item.FullPath, destPath);
                        Log($"✅ Moved {item.Name} to {destPath}");
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
                        }
                        else
                        {
                            Log($"🗑️ Deleting file: {item.Name}");
                            await _protocol.DeleteFileAsync(item.FullPath);
                            Log($"✅ File deletion complete: {item.Name}");
                        }
                        
                        // Wait for PS5 filesystem to update after delete
                        await Task.Delay(1500);
                        
                        // Refresh the directory
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
                    // Folder download - use FolderBrowserDialog
                    using var folderDialog = new System.Windows.Forms.FolderBrowserDialog
                    {
                        Description = $"Select destination for '{item.Name}' folder",
                        UseDescriptionForTitle = true
                    };
                    
                    if (folderDialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                        return;
                    
                    string localBasePath = Path.Combine(folderDialog.SelectedPath, item.Name);
                    
                    try
                    {
                        Log($"⬇️ Downloading folder: {item.FullPath} → {localBasePath}");
                        
                        _uploadCancellation = new CancellationTokenSource();
                        var ct = _uploadCancellation.Token;
                        
                        var progress = new Progress<DownloadFolderProgress>(p =>
                        {
                            Dispatcher.InvokeAsync(() =>
                            {
                                if (p.Phase == "Scanning")
                                {
                                    TotalProgressText.Text = $"Scanning: {p.FilesCompleted} files found... ({p.CurrentFile})";
                                }
                                else if (p.Phase == "Downloading")
                                {
                                    double percent = p.TotalBytes > 0 ? (double)p.BytesDownloaded / p.TotalBytes * 100 : 0;
                                    TotalProgressBar.Value = percent;
                                    TotalProgressText.Text = $"Downloading: {p.FilesCompleted}/{p.TotalFiles} files ({FormatFileSize(p.BytesDownloaded)}/{FormatFileSize(p.TotalBytes)}) [{percent:F1}%]";
                                    if (!string.IsNullOrEmpty(p.CurrentFile))
                                        UploadFileNameText.Text = $"⬇️ {p.CurrentFile}";
                                }
                                else if (p.Phase == "Complete")
                                {
                                    TotalProgressBar.Value = 100;
                                    TotalProgressText.Text = $"Complete: {p.FilesCompleted}/{p.TotalFiles} files ({FormatFileSize(p.BytesDownloaded)})";
                                }
                            }, System.Windows.Threading.DispatcherPriority.Normal);
                        });
                        
                        var (downloaded, failed, totalBytes) = await _protocol.DownloadFolderAsync(
                            item.FullPath, localBasePath, _ps5IpAddress, progress, ct);
                        
                        Log($"✅ Folder download complete: {downloaded} files ({FormatFileSize(totalBytes)}), {failed} failed");
                        
                        string msg = $"Folder downloaded!\n\n" +
                                     $"Files: {downloaded} downloaded, {failed} failed\n" +
                                     $"Size: {FormatFileSize(totalBytes)}\n" +
                                     $"Saved to: {localBasePath}";
                        MessageBox.Show(msg, "Download Complete", MessageBoxButton.OK, 
                            failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
                    }
                    catch (Exception ex)
                    {
                        Log($"❌ Folder download error: {ex.Message}");
                        MessageBox.Show($"Folder download error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    finally
                    {
                        _uploadCancellation = null;
                        Dispatcher.Invoke(() =>
                        {
                            TotalProgressBar.Value = 0;
                            TotalProgressText.Text = "";
                            UploadFileNameText.Text = "";
                        });
                    }
                    return;
                }
                
                // Single file download - use SaveFileDialog
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
                        
                        var progress = new Progress<UploadProgress>(p =>
                        {
                            Dispatcher.InvokeAsync(() =>
                            {
                                double percent = p.TotalBytes > 0 ? (double)p.BytesSent / p.TotalBytes * 100 : 0;
                                TotalProgressBar.Value = percent;
                                TotalProgressText.Text = $"Downloading: {FormatFileSize(p.BytesSent)}/{FormatFileSize(p.TotalBytes)} ({percent:F1}%) @ {FormatFileSize((long)p.SpeedBytesPerSecond)}/s";
                            }, System.Windows.Threading.DispatcherPriority.Normal);
                        });
                        
                        bool success = await _protocol.DownloadFileAsync(item.FullPath, dialog.FileName, progress);
                        
                        if (success)
                        {
                            Log($"✅ Downloaded: {item.Name} → {dialog.FileName}");
                            MessageBox.Show($"File downloaded successfully!\n\nSaved to: {dialog.FileName}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        else
                        {
                            Log($"❌ Download failed: {item.Name}");
                            MessageBox.Show("Download failed", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"❌ Download error: {ex.Message}");
                        MessageBox.Show($"Download error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    finally
                    {
                        Dispatcher.Invoke(() =>
                        {
                            TotalProgressBar.Value = 0;
                            TotalProgressText.Text = "";
                        });
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
            
            // Wait a moment before reloading
            await Task.Delay(500);
            await LoadPS5DirectoryAsync(_currentPS5Path);
            
            MessageBox.Show($"Bulk delete complete!\n\n✅ {successCount} deleted\n❌ {failCount} failed", 
                "Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static string FormatFileSize(long bytes)
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
            App.LogToFile($"Duplicate scan started: {allFiles.Count} files, {filesByDir.Count} directories");

            bool uploadsIdle = (_uploadCancellation == null || _uploadCancellation.IsCancellationRequested) && _activeTaskCount == 0;
            PS5Protocol? duplicateConnection = null;
            bool disposeDuplicate = false;

            if (!uploadsIdle)
            {
                duplicateConnection = new PS5Protocol();
                if (await duplicateConnection.ConnectAsync(_ps5IpAddress))
                {
                    disposeDuplicate = true;
                    App.LogToFile("Duplicate scan using temporary connection (uploads active)");
                }
                else
                {
                    App.LogToFile("Duplicate scan failed to create temporary connection - falling back to main connection");
                    duplicateConnection = null;
                }
            }

            int dirIndex = 0;
            const int UiLogInterval = 50; // reduce UI spam when scanning thousands of dirs

            bool ShouldLogToUi(int index)
            {
                return index == 1 || index % UiLogInterval == 0 || index == filesByDir.Count;
            }

            foreach (var dirGroup in filesByDir)
            {
                dirIndex++;
                string remoteDir = dirGroup.Key;

                App.LogToFile($"Duplicate scan dir {dirIndex}/{filesByDir.Count}: {remoteDir}");

                // Get existing files in this directory
                Dictionary<string, long> existingFiles;
                try
                {
                    if (ShouldLogToUi(dirIndex))
                    {
                        Log($"📂 Checking dir ({dirIndex}/{filesByDir.Count}): {remoteDir}");
                    }
                    var protocolToUse = duplicateConnection ?? _protocol;
                    var entries = await protocolToUse.ListDirAsync(remoteDir);
                    existingFiles = entries
                        .Where(e => !e.IsDirectory)
                        .ToDictionary(e => e.Name, e => e.Size);
                    if (ShouldLogToUi(dirIndex))
                    {
                        Log($"   Found {existingFiles.Count} existing files");
                    }
                    App.LogToFile($"Duplicate scan dir {remoteDir}: found {existingFiles.Count} files");
                }
                catch (Exception ex)
                {
                    // Directory doesn't exist or error - all files are new
                    if (ShouldLogToUi(dirIndex))
                    {
                        Log($"   Directory check failed - assuming {dirGroup.Count()} new files");
                    }
                    App.LogToFile($"Duplicate scan dir {remoteDir} failed: {ex.Message}");
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

            if (disposeDuplicate)
            {
                duplicateConnection?.Dispose();
            }

            App.LogToFile($"Duplicate scan finished: {filesToUpload.Count} files queued for upload");
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
    }

    public class LocalFileItem
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string Icon { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public string? RemotePathOverride { get; set; }
        public string SizeFormatted => IsDirectory ? "" : FileUtils.FormatFileSize(Size);
    }

    public class PS5FileItem
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string Icon { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public string SizeFormatted => IsDirectory ? "" : FileUtils.FormatFileSize(Size);
    }

    public static class FileUtils
    {
        public static string FormatFileSize(long bytes)
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
    
    // Shell Terminal Methods
    public partial class MainWindow
    {
        private async Task OpenShellAsync()
        {
            try
            {
                if (_shellActive)
                {
                    ShellLog("[Shell] Already connected");
                    return;
                }
                
                ShellLog($"[Shell] Connecting to {_ps5IpAddress}:9113...");
                ShellLog("[Shell] Creating TCP connection...");
                ShellLog("[Shell] TCP connected, getting stream...");
                ShellLog("[Shell] Sending SHELL_OPEN command...");
                
                bool success = await _protocol.OpenShellAsync();
                
                if (success)
                {
                    ShellLog("[Shell] Received response: Ok, data length: 20");
                    ShellLog("[Shell] Connection successful, starting read loop...");
                    _shellActive = true;
                    _shellCurrentDir = "/data";
                    ShellLog($"✅ Connected to PS5 at {_ps5IpAddress}");
                    ShellLog($"PS5:{_shellCurrentDir} $");
                }
                else
                {
                    ShellLog("[Shell] Failed to open shell session");
                }
            }
            catch (Exception ex)
            {
                ShellLog($"[Shell] Error: {ex.Message}");
            }
        }
        
        private async void ShellCommandInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter && sender is System.Windows.Controls.TextBox textBox)
            {
                string command = textBox.Text.Trim();
                if (string.IsNullOrEmpty(command)) return;
                
                textBox.Text = "";
                
                if (!_shellActive)
                {
                    ShellLog("Shell not connected. Connect to PS5 first.");
                    return;
                }
                
                ShellLog($"PS5:{_shellCurrentDir} $ {command}");
                
                try
                {
                    string output = await _protocol.ExecuteShellCommandAsync(command);
                    if (!string.IsNullOrEmpty(output))
                    {
                        ShellLog(output);
                    }
                    
                    // If command was 'cd', update current directory by calling pwd
                    if (command.Trim().StartsWith("cd ") || command.Trim() == "cd")
                    {
                        try
                        {
                            string pwdOutput = await _protocol.ExecuteShellCommandAsync("pwd");
                            if (!string.IsNullOrEmpty(pwdOutput))
                            {
                                _shellCurrentDir = pwdOutput.Trim();
                            }
                        }
                        catch { }
                    }
                    
                    ShellLog($"PS5:{_shellCurrentDir} $");
                }
                catch (Exception ex)
                {
                    ShellLog($"Error: {ex.Message}");
                }
            }
        }
        
        private void ShellLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                _shellOutput.Add(message);
                
                // Auto-scroll to bottom
                if (ShellOutputListBox != null)
                {
                    ShellOutputListBox.ScrollIntoView(_shellOutput.Last());
                }
            });
        }
        
        private void ClearShellButton_Click(object sender, RoutedEventArgs e)
        {
            _shellOutput.Clear();
            ShellLog("PS5 Shell Terminal - Ready");
            ShellLog("Type 'help' for available commands");
            if (_shellActive)
            {
                ShellLog($"PS5:{_shellCurrentDir} $");
            }
        }
        
        private void SaveShellLogButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                    FileName = $"ps5_shell_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
                };
                
                if (dialog.ShowDialog() == true)
                {
                    System.IO.File.WriteAllLines(dialog.FileName, _shellOutput);
                    Log($"✅ Shell log saved to {dialog.FileName}");
                }
            }
            catch (Exception ex)
            {
                Log($"❌ Failed to save shell log: {ex.Message}");
            }
        }
        
        // Search Index Methods
        private async void StartIndexButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_protocol.IsConnected)
                {
                    MessageBox.Show("Not connected to PS5", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                StartIndexButton.IsEnabled = false;
                IndexStatusText.Text = "Index Status: Starting...";
                Log("🔍 Starting index...");
                
                // Index root directory
                bool success = await _protocol.StartIndexAsync("/");
                
                if (success)
                {
                    Log("✅ Indexing started");
                    IndexStatusText.Text = "Index Status: Indexing...";
                    
                    // Auto-refresh status after 2 seconds
                    await Task.Delay(2000);
                    await RefreshIndexStatus();
                }
                else
                {
                    Log("❌ Failed to start indexing");
                    IndexStatusText.Text = "Index Status: Failed to start";
                }
                
                StartIndexButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Log($"❌ Index error: {ex.Message}");
                StartIndexButton.IsEnabled = true;
            }
        }
        
        private async void RefreshIndexButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshIndexStatus();
        }
        
        private async Task RefreshIndexStatus()
        {
            try
            {
                if (!_protocol.IsConnected)
                {
                    IndexStatusText.Text = "Index Status: Not connected";
                    return;
                }
                
                string status = await _protocol.GetIndexStatusAsync();
                IndexStatusText.Text = $"Index Status: {status}";
                Log($"📊 Index status: {status}");
            }
            catch (Exception ex)
            {
                Log($"❌ Failed to get index status: {ex.Message}");
            }
        }
        
        private async void SearchInputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter && sender is System.Windows.Controls.TextBox textBox)
            {
                string query = textBox.Text.Trim();
                if (string.IsNullOrEmpty(query)) return;
                
                await PerformSearch(query);
            }
        }
        
        private void ClearIndexSearchButton_Click(object sender, RoutedEventArgs e)
        {
            SearchInputBox.Text = "";
            SearchResultsGrid.ItemsSource = null;
        }
        
        private async Task PerformSearch(string query)
        {
            try
            {
                if (!_protocol.IsConnected)
                {
                    MessageBox.Show("Not connected to PS5", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                Log($"🔍 Searching: {query}");
                SearchResultsGrid.ItemsSource = null;
                
                var results = await _protocol.SearchIndexAsync(query);
                
                SearchResultsGrid.ItemsSource = results;
                Log($"✅ Found {results.Length} results");
            }
            catch (Exception ex)
            {
                Log($"❌ Search error: {ex.Message}");
                MessageBox.Show($"Search failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private async void SearchResultsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (SearchResultsGrid.SelectedItem is SearchResult result)
            {
                try
                {
                    // Navigate to the file's directory in PS5 Files
                    string directoryPath = System.IO.Path.GetDirectoryName(result.Path)?.Replace("\\", "/") ?? "/";
                    
                    Log($"📂 Navigating to: {directoryPath}");
                    
                    // Switch to PS5 Files view and load the directory
                    await LoadPS5DirectoryAsync(directoryPath);
                    
                    // Optional: Select the file in the list
                    await Task.Delay(100); // Give UI time to update
                    foreach (var item in _ps5FilesFiltered)
                    {
                        if (item.Name == result.Name)
                        {
                            PS5FilesListBox.SelectedItem = item;
                            PS5FilesListBox.ScrollIntoView(item);
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"❌ Navigation error: {ex.Message}");
                }
            }
        }
        
        private void AutoSendPayloadCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (AutoSendPayloadCheckBox != null)
            {
                _autoSendPayload = AutoSendPayloadCheckBox.IsChecked == true;
                SaveSettings();
            }
        }
        
        private void BrowsePayloadButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Payload files (*.elf;*.bin)|*.elf;*.bin|All files (*.*)|*.*",
                Title = "Select Payload File"
            };
            
            if (dialog.ShowDialog() == true)
            {
                _payloadPath = dialog.FileName;
                PayloadPathTextBox.Text = _payloadPath;
                SaveSettings();
                Log($"✅ Payload file selected: {_payloadPath}");
            }
        }
        
        private void PayloadPortTextBox_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (PayloadPortTextBox != null && int.TryParse(PayloadPortTextBox.Text, out int port))
            {
                _payloadPort = port;
                SaveSettings();
            }
        }
        
        private void PayloadPathTextBox_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }
        
        private void PayloadPathTextBox_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length > 0)
                {
                    string filePath = files[0];
                    string ext = Path.GetExtension(filePath).ToLower();
                    
                    if (ext == ".elf" || ext == ".bin")
                    {
                        _payloadPath = filePath;
                        PayloadPathTextBox.Text = filePath;
                        SaveSettings();
                        Log($"📦 Payload file selected: {Path.GetFileName(filePath)}");
                    }
                    else
                    {
                        MessageBox.Show("Please select a valid payload file (.elf or .bin)", "Invalid File", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
        }
        
        private async void SendPayloadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Get IP address directly from the textbox
                string ipAddress = IpAddressTextBox.Text.Trim();
                
                if (string.IsNullOrEmpty(ipAddress))
                {
                    MessageBox.Show("Please enter PS5 IP address first", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                if (string.IsNullOrEmpty(_payloadPath) || !File.Exists(_payloadPath))
                {
                    MessageBox.Show("Please select a valid payload file first", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                SendPayloadButton.IsEnabled = false;
                Log($"📤 Sending payload to {ipAddress}:{_payloadPort}...");
                
                var progress = new Progress<long>(bytes =>
                {
                    // Optional: Update progress in UI
                });
                
                bool success = await PS5Protocol.SendPayloadAsync(ipAddress, _payloadPath, _payloadPort, progress);
                
                if (success)
                {
                    Log($"✅ Payload sent successfully ({new FileInfo(_payloadPath).Length} bytes)");
                    MessageBox.Show($"Payload sent successfully!\n\nFile: {Path.GetFileName(_payloadPath)}\nSize: {new FileInfo(_payloadPath).Length} bytes\n\nThe payload should execute on the PS5 now.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    Log("❌ Failed to send payload - Connection timeout or refused");
                    MessageBox.Show($"Failed to send payload to {ipAddress}:{_payloadPort}\n\nPossible reasons:\n• PS5 is not ready to receive payloads\n• GoldHEN menu is not open\n• Wrong IP address or port\n• PS5 firewall blocking connection\n\nMake sure GoldHEN is running and the payload loader is active.", "Connection Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                
                SendPayloadButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Log($"❌ Payload send error: {ex.Message}");
                MessageBox.Show($"Error sending payload: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SendPayloadButton.IsEnabled = true;
            }
        }

        private async void MountGamesButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected)
            {
                MessageBox.Show("Not connected to PS5", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            MountGamesButton.IsEnabled = false;
            MountGamesButton.Content = "⏳ Mounting...";
            Log("🎮 Mount Games: Starting...");
            Log("Scanning /data/etaHEN/games, USB drives, M.2 SSD...");

            try
            {
                string? result = await _protocol.MountGamesAsync();

                if (result != null)
                {
                    Log("🎮 Mount Games Result:");
                    foreach (var line in result.Split('\n'))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            Log($"  {line.Trim()}");
                    }

                    MessageBox.Show(result, "Mount Games - Result", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    Log("❌ Mount Games: No response from PS5 (timeout or connection lost)");
                    MessageBox.Show("No response from PS5. The connection may have been lost.\n\nThe mount operation may still be running on the PS5.", 
                                    "Mount Games - Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Log($"❌ Mount Games error: {ex.Message}");
                MessageBox.Show($"Error mounting games: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                MountGamesButton.Content = "🎮 Mount Games";
                MountGamesButton.IsEnabled = _protocol.IsConnected;
            }
        }

        private void RetryFailedUpload_Click(object sender, RoutedEventArgs e)
        {
            if (FailedTransfersListBox.SelectedItem is TransferHistoryItem failedItem)
            {
                if (!File.Exists(failedItem.LocalPath) && !Directory.Exists(failedItem.LocalPath))
                {
                    MessageBox.Show($"File or folder not found: {failedItem.LocalPath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (_protocol == null || !_protocol.IsConnected)
                {
                    MessageBox.Show("Not connected to PS5. Please connect first.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _failedTransfers.Remove(failedItem);
                
                // Add file back to local files list for retry
                if (File.Exists(failedItem.LocalPath))
                {
                    FileInfo info = new FileInfo(failedItem.LocalPath);
                    _localFiles.Add(new LocalFileItem
                    {
                        Name = info.Name,
                        FullPath = failedItem.LocalPath,
                        Size = info.Length,
                        IsDirectory = false,
                        Icon = "📄"
                    });
                }
                else if (Directory.Exists(failedItem.LocalPath))
                {
                    DirectoryInfo dirInfo = new DirectoryInfo(failedItem.LocalPath);
                    _localFiles.Add(new LocalFileItem
                    {
                        Name = dirInfo.Name,
                        FullPath = failedItem.LocalPath,
                        Size = 0,
                        IsDirectory = true,
                        Icon = "📁"
                    });
                }
                
                Log($"🔄 Retrying upload: {failedItem.FileName}");
                MessageBox.Show($"Added {failedItem.FileName} back to upload queue. Click 'Upload to PS5' to retry.", "Retry Queued", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Please select a failed transfer to retry.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void RemoveFailedTransfer_Click(object sender, RoutedEventArgs e)
        {
            if (FailedTransfersListBox.SelectedItem is TransferHistoryItem failedItem)
            {
                _failedTransfers.Remove(failedItem);
                Log($"🗑️ Removed from failed transfers: {failedItem.FileName}");
            }
            else
            {
                MessageBox.Show("Please select a failed transfer to remove.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ClearTransferHistory_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Clear all transfer history (completed and failed)?", "Clear Transfer History", MessageBoxButton.YesNo, MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                _completedTransfers.Clear();
                _failedTransfers.Clear();
                Log("🗑️ Transfer history cleared");
            }
        }
        
        private bool _isRefreshingStorage = false;
        
        private async void RefreshStorageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isRefreshingStorage) return; // Prevent multiple simultaneous refreshes
            await RefreshStorageInfoAsync();
        }

        private async Task RefreshStorageInfoAsync()
        {
            if (_isRefreshingStorage) return;
            if (string.IsNullOrWhiteSpace(_ps5IpAddress))
            {
                Log("⚠️ Cannot refresh storage info - PS5 IP is not set");
                return;
            }
            
            try
            {
                _isRefreshingStorage = true;
                Log("💾 Fetching storage info...");
                var storageInfo = await FetchStorageInfoWithFallbackAsync();
                
                if (storageInfo != null)
                {
                    // Show storage panel
                    Dispatcher.Invoke(() =>
                    {
                        StorageInfoPanel.Visibility = Visibility.Visible;
                        
                        // Update UI
                        TotalCapacityText.Text = storageInfo.TotalGB;
                        RealFreeSpaceText.Text = storageInfo.RealFreeGB;
                        ReservedSpaceText.Text = storageInfo.ReservedGB;
                        MountedGamesText.Text = storageInfo.MountedGamesGB;
                        UserDataText.Text = storageInfo.UserDataGB;
                        
                        // Calculate total used
                        ulong totalUsed = storageInfo.MountedGamesSize + storageInfo.UserDataSize;
                        TotalUsedText.Text = PS5StorageInfo.FormatBytes(totalUsed);
                        
                        // Calculate percentage
                        double usedPercent = (double)totalUsed / storageInfo.TotalBytes * 100;
                        StorageProgressBar.Value = usedPercent;
                        StoragePercentText.Text = $"{usedPercent:F1}% Used";
                    });
                    
                    Log($"✅ Storage: {storageInfo.RealFreeGB} free of {storageInfo.TotalGB} (path: {storageInfo.StoragePath})");
                }
                else
                {
                    Log("❌ Failed to get storage info");
                }
            }
            catch (Exception ex)
            {
                Log($"❌ Storage info error: {ex.Message}");
            }
            finally
            {
                _isRefreshingStorage = false;
            }
        }
    }

    public class TransferHistoryItem
    {
        public string FileName { get; set; } = "";
        public string Status { get; set; } = "";
        public string Size { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public string FormattedTimestamp => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
        public string LocalPath { get; set; } = "";
        public string RemotePath { get; set; } = "";

        public override string ToString()
        {
            return $"{FileName} - {Status} ({Size}) [{FormattedTimestamp}]";
        }
    }

    public partial class MainWindow
    {
        private void BuyMeCoffeeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://buymeacoffee.com/manos555554",
                    UseShellExecute = true
                });
                Log("☕ Thank you for your support!");
            }
            catch (Exception ex)
            {
                Log($"❌ Failed to open link: {ex.Message}");
            }
        }

        // ============================================================================
        // SAVES TAB - Event Handlers
        // ============================================================================

        private List<PS5SaveGame> _currentSaves = new();

        private async void RefreshSavesButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshSavesAsync();
        }

        private async Task RefreshSavesAsync()
        {
            if (!_protocol.IsConnected)
            {
                Log("❌ Not connected to PS5");
                return;
            }

            Log("💾 Fetching save games list...");
            try
            {
                var saves = await _protocol.ListSavesAsync();
                _currentSaves = saves;

                // Enrich with game names + icons from mounted games list (if available)
                var mounted = await _protocol.GetGameListAsync();
                var nameByTitle = mounted.ToDictionary(g => g.TitleId, g => g.Name, StringComparer.OrdinalIgnoreCase);

                foreach (var save in saves)
                {
                    if (nameByTitle.TryGetValue(save.TitleId, out var gameName))
                        save.GameName = gameName;
                    else
                        save.GameName = save.TitleId;

                    if (_iconCache.TryGetValue(save.TitleId, out var cachedIcon))
                        save.Icon = cachedIcon;
                }

                Dispatcher.Invoke(() =>
                {
                    SavesListBox.ItemsSource = saves;
                    SaveCountText.Text = $" ({saves.Count} saves)";
                });

                Log($"💾 Found {saves.Count} save games");

                // Fetch missing icons in background
                _ = Task.Run(async () =>
                {
                    foreach (var save in saves)
                    {
                        if (save.Icon != null) continue;
                        try
                        {
                            var bytes = await _protocol.GetGameIconAsync(save.TitleId);
                            if (bytes != null && bytes.Length > 0)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    try
                                    {
                                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                                        bmp.BeginInit();
                                        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                        bmp.StreamSource = new MemoryStream(bytes);
                                        bmp.EndInit();
                                        bmp.Freeze();
                                        _iconCache[save.TitleId] = bmp;
                                        save.Icon = bmp;
                                    }
                                    catch { }
                                });
                            }
                        }
                        catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"❌ Error fetching saves: {ex.Message}");
            }
        }

        private async void BackupSaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (SavesListBox.SelectedItem is PS5SaveGame save)
                await BackupSaveAsync(save);
            else
                MessageBox.Show("Select a save first.", "No Selection",
                                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void BackupSaveMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (SavesListBox.SelectedItem is PS5SaveGame save)
                await BackupSaveAsync(save);
        }

        private async Task BackupSaveAsync(PS5SaveGame save)
        {
            if (!_protocol.IsConnected)
            {
                MessageBox.Show("Not connected to PS5.", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Choose backup location",
                FileName = $"{save.TitleId}_{save.UserId}_{DateTime.Now:yyyyMMdd_HHmmss}",
                Filter = "Folder (choose any file name to pick a folder)|*",
                OverwritePrompt = false,
                CheckPathExists = true
            };
            if (dlg.ShowDialog() != true) return;

            string parentDir = Path.GetDirectoryName(dlg.FileName) ?? "";
            string backupDir = Path.Combine(parentDir, Path.GetFileNameWithoutExtension(dlg.FileName));
            Directory.CreateDirectory(backupDir);

            Log($"📥 Backing up save {save.TitleId} to {backupDir}...");
            try
            {
                var result = await _protocol.DownloadFolderAsync(
                    save.SavePath,
                    backupDir,
                    _ps5IpAddress,
                    null,
                    System.Threading.CancellationToken.None);

                Log($"✅ Backup complete: {result.filesDownloaded} files, " +
                    $"{result.filesFailed} failed, {FormatFileSize(result.totalBytes)} total");

                MessageBox.Show($"Backup complete!\n\n" +
                                $"Location: {backupDir}\n" +
                                $"Files: {result.filesDownloaded}\n" +
                                $"Failed: {result.filesFailed}\n" +
                                $"Size: {FormatFileSize(result.totalBytes)}",
                                "Backup Complete",
                                MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log($"❌ Backup failed: {ex.Message}");
                MessageBox.Show($"Backup failed:\n{ex.Message}", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RestoreSaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (SavesListBox.SelectedItem is PS5SaveGame save)
                await RestoreSaveAsync(save);
            else
                MessageBox.Show("Select a save first.", "No Selection",
                                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void RestoreSaveMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (SavesListBox.SelectedItem is PS5SaveGame save)
                await RestoreSaveAsync(save);
        }

        private async Task RestoreSaveAsync(PS5SaveGame save)
        {
            if (!_protocol.IsConnected)
            {
                MessageBox.Show("Not connected to PS5.", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select any file from the backup folder to restore",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() != true) return;

            string backupFolder = Path.GetDirectoryName(dlg.FileName) ?? "";
            if (string.IsNullOrWhiteSpace(backupFolder) || !Directory.Exists(backupFolder))
            {
                MessageBox.Show("Invalid folder.", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                $"Restore save {save.TitleId} to PS5?\n\n" +
                $"From: {backupFolder}\n" +
                $"To:   {save.SavePath}\n\n" +
                $"Files in the PS5 save will be overwritten.",
                "Confirm Restore",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            Log($"📤 Restoring save {save.TitleId} from {backupFolder}...");
            int uploaded = 0, failed = 0;
            long totalBytes = 0;
            try
            {
                foreach (var localFile in Directory.EnumerateFiles(backupFolder, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(backupFolder, localFile).Replace('\\', '/');
                    string remote = save.SavePath.TrimEnd('/') + "/" + rel;
                    try
                    {
                        bool ok = await _protocol.UploadFileAsync(localFile, remote);
                        if (ok)
                        {
                            uploaded++;
                            totalBytes += new FileInfo(localFile).Length;
                        }
                        else failed++;
                    }
                    catch { failed++; }
                }

                Log($"✅ Restore complete: {uploaded} uploaded, {failed} failed, {FormatFileSize(totalBytes)}");
                MessageBox.Show($"Restore complete!\n\n" +
                                $"Uploaded: {uploaded}\n" +
                                $"Failed: {failed}\n" +
                                $"Size: {FormatFileSize(totalBytes)}",
                                "Restore Complete",
                                MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log($"❌ Restore failed: {ex.Message}");
                MessageBox.Show($"Restore failed:\n{ex.Message}", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopySavePathMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (SavesListBox.SelectedItem is PS5SaveGame save)
            {
                try
                {
                    System.Windows.Clipboard.SetText(save.SavePath);
                    Log($"📋 Copied: {save.SavePath}");
                }
                catch (Exception ex)
                {
                    Log($"❌ Clipboard: {ex.Message}");
                }
            }
        }

        // ============================================================================
        // GAMES TAB - Event Handlers
        // ============================================================================

        private async void RefreshGameListButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshGameListAsync();
        }

        // In-memory icon cache (title_id -> ImageSource) so we don't refetch on every refresh
        private readonly Dictionary<string, System.Windows.Media.ImageSource> _iconCache = new();

        private async Task RefreshGameListAsync()
        {
            if (!_protocol.IsConnected)
            {
                Log("❌ Not connected to PS5");
                return;
            }

            Log("🎮 Fetching mounted games list...");

            try
            {
                var games = await _protocol.GetGameListAsync();
                
                Dispatcher.Invoke(() =>
                {
                    MountedGamesListBox.ItemsSource = games;
                    GameCountText.Text = $" ({games.Count} games)";
                });

                if (games.Count > 0)
                {
                    Log($"🎮 Found {games.Count} mounted games:");
                    foreach (var game in games)
                    {
                        Log($"   • {game.TitleId} - {game.Name} [{game.Region}]");
                    }

                    // Fetch icons in background, one at a time to avoid swamping the payload
                    _ = Task.Run(async () =>
                    {
                        foreach (var game in games)
                        {
                            if (_iconCache.TryGetValue(game.TitleId, out var cached))
                            {
                                Dispatcher.Invoke(() => game.Icon = cached);
                                continue;
                            }

                            try
                            {
                                var iconBytes = await _protocol.GetGameIconAsync(game.TitleId);
                                if (iconBytes != null && iconBytes.Length > 0)
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        try
                                        {
                                            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                                            bitmap.BeginInit();
                                            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                            bitmap.StreamSource = new MemoryStream(iconBytes);
                                            bitmap.EndInit();
                                            bitmap.Freeze();
                                            _iconCache[game.TitleId] = bitmap;
                                            game.Icon = bitmap;
                                        }
                                        catch { /* corrupt/invalid image */ }
                                    });
                                }
                            }
                            catch { /* skip failed icons */ }
                        }
                    });
                }
                else
                {
                    Log("🎮 No mounted games found");
                }
            }
            catch (Exception ex)
            {
                Log($"❌ Error fetching game list: {ex.Message}");
            }
        }

        private async void MountedGamesListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (MountedGamesListBox.SelectedItem is PS5MountedGame game)
            {
                await ShowGameDetailsAsync(game);
            }
        }

        private async void ViewGameDetailsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (MountedGamesListBox.SelectedItem is PS5MountedGame game)
            {
                await ShowGameDetailsAsync(game);
            }
        }

        private async Task ShowGameDetailsAsync(PS5MountedGame game)
        {
            if (!_protocol.IsConnected)
            {
                Log("❌ Not connected to PS5");
                return;
            }

            Log($"ℹ️  Fetching details for {game.TitleId}...");
            var details = await _protocol.GetGameDetailsAsync(game.TitleId);
            if (details == null)
            {
                MessageBox.Show("Failed to fetch game details.", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new GameDetailsWindow(game, details, _protocol) { Owner = this };
            dlg.ShowDialog();
        }

        private async void UnmountGameMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (MountedGamesListBox.SelectedItem is PS5MountedGame game)
            {
                var result = MessageBox.Show(
                    $"Unmount game {game.TitleId}?\n\n{game.Name}\n\nThis will remove the game from the PS5 home screen.",
                    "Confirm Unmount",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    Log($"🗑️ Unmounting {game.TitleId}...");
                    var (success, message) = await _protocol.UnmountGameAsync(game.TitleId);
                    if (success)
                    {
                        Log($"✅ {message}");
                        await RefreshGameListAsync();
                    }
                    else
                    {
                        Log($"❌ Failed: {message}");
                    }
                }
            }
        }

        private async void OpenGamePathMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (MountedGamesListBox.SelectedItem is PS5MountedGame game)
            {
                // Navigate to the game's path in the PS5 file browser
                string gamePath = game.Path;
                if (!string.IsNullOrEmpty(gamePath))
                {
                    Log($"📂 Navigating to {gamePath}");
                    _currentPS5Path = gamePath;
                    await LoadPS5DirectoryAsync(gamePath);
                }
            }
        }

        // ============================================================
        // LAUNCH GAME
        // ============================================================
        private async void LaunchGameMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (MountedGamesListBox.SelectedItem is not PS5MountedGame game) return;
            if (_protocol == null || !_protocol.IsConnected)
            {
                Log("❌ Not connected to PS5");
                return;
            }

            Log($"▶ Launching {game.TitleId} ({game.Name})...");
            var (success, message) = await _protocol.LaunchGameAsync(game.TitleId);
            if (success)
                Log($"✅ {message}");
            else
                Log($"❌ Launch failed: {message}");
        }

        // ============================================================
        // HARDWARE TAB
        // ============================================================
        private System.Windows.Threading.DispatcherTimer? _hwAutoTimer;
        private int _hwBusyFlag; // 0 = idle, 1 = refresh in progress (Interlocked)

        private async void RefreshHardwareButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshHardwareAsync();
        }

        private void HwAutoRefresh_Changed(object sender, RoutedEventArgs e)
        {
            if (HwAutoRefreshCheckBox?.IsChecked == true)
            {
                _hwAutoTimer ??= new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(5)  // 5s is plenty for sensor polling
                };
                _hwAutoTimer.Tick -= HwAutoTimer_Tick;
                _hwAutoTimer.Tick += HwAutoTimer_Tick;
                _hwAutoTimer.Start();
            }
            else
            {
                _hwAutoTimer?.Stop();
            }
        }

        private async void HwAutoTimer_Tick(object? sender, EventArgs e)
        {
            // Auto-stop if disconnected
            if (_protocol == null || !_protocol.IsConnected)
            {
                _hwAutoTimer?.Stop();
                HwAutoRefreshCheckBox.IsChecked = false;
                HwStatusText.Text = "Auto-refresh stopped: disconnected";
                return;
            }
            // Skip if previous refresh still running or app is busy (uploads, etc.)
            if (Interlocked.CompareExchange(ref _hwBusyFlag, 1, 0) != 0) return;
            if (_activeTaskCount > 0)
            {
                Interlocked.Exchange(ref _hwBusyFlag, 0);
                return;
            }
            try
            {
                await RefreshHardwareSensorsAsync();
            }
            finally
            {
                Interlocked.Exchange(ref _hwBusyFlag, 0);
            }
        }

        private async Task RefreshHardwareAsync()
        {
            if (_protocol == null || !_protocol.IsConnected)
            {
                Log("❌ Not connected to PS5");
                return;
            }
            if (Interlocked.CompareExchange(ref _hwBusyFlag, 1, 0) != 0)
            {
                Log("⏳ Hardware refresh already in progress");
                return;
            }

            try
            {
                var hw = await _protocol.GetHardwareInfoAsync();
                if (hw != null)
                {
                    HwModelText.Text       = string.IsNullOrWhiteSpace(hw.Model) ? "PlayStation 5" : hw.Model;
                    HwSerialText.Text      = string.IsNullOrWhiteSpace(hw.Serial) ? "—" : hw.Serial;
                    HwMachineText.Text     = string.IsNullOrWhiteSpace(hw.HwMachine) ? "—" : hw.HwMachine;
                    HwOsText.Text          = string.IsNullOrWhiteSpace(hw.OsVersion) ? "—" : hw.OsVersion;
                    HwCpuCoresText.Text    = hw.NumCpu > 0 ? $"{hw.NumCpu} cores" : "—";
                    HwPhysMemText.Text     = hw.PhysMem > 0
                        ? $"{hw.PhysMem / (1024.0 * 1024.0 * 1024.0):0.0} GB"
                        : "—";
                    HwWlanBtText.Text      = hw.HasWlanBt ? "✓ Present" : "✗ None";
                    HwOpticalOutText.Text  = hw.HasOpticalOut ? "✓ Present" : "✗ None";
                }

                await RefreshHardwareSensorsAsync();
            }
            catch (Exception ex)
            {
                Log($"❌ Hardware refresh failed: {ex.Message}");
                HwStatusText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                Interlocked.Exchange(ref _hwBusyFlag, 0);
            }
        }

        private async Task RefreshHardwareSensorsAsync()
        {
            if (_protocol == null || !_protocol.IsConnected) return;

            try
            {
                var t = await _protocol.GetTemperatureInfoAsync();
                if (t == null)
                {
                    HwStatusText.Text = "Sensors unavailable";
                    return;
                }

                HwCpuTempText.Text = $"{t.CpuTemp} °C";
                HwCpuTempBar.Value = Math.Clamp(t.CpuTemp, 0, 100);

                HwSocTempText.Text = $"{t.SocTemp} °C";
                HwSocTempBar.Value = Math.Clamp(t.SocTemp, 0, 100);

                HwCpuFreqText.Text = t.CpuFreqMhz > 0 ? $"{t.CpuFreqMhz} MHz" : "—";
                HwCpuFreqBar.Value = Math.Clamp(t.CpuFreqMhz, 0, 3500);

                double watts = t.SocPowerMw / 1000.0;
                HwSocPowerText.Text = t.SocPowerMw > 0 ? $"{watts:0.0} W" : "—";
                HwSocPowerBar.Value = Math.Clamp(watts, 0, 250);

                HwStatusText.Text = $"Last updated: {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                HwStatusText.Text = $"Error: {ex.Message}";
            }
        }

        // ============================================================
        // SCREENSHOTS TAB
        // ============================================================
        private List<PS5Screenshot> _currentScreenshots = new();

        private async void RefreshScreenshotsButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshScreenshotsAsync();
        }

        // Thumbnail cache: remote PS5 path -> BitmapImage (Frozen)
        private readonly Dictionary<string, System.Windows.Media.ImageSource> _screenshotThumbCache = new();

        private async Task RefreshScreenshotsAsync()
        {
            if (_protocol == null || !_protocol.IsConnected)
            {
                Log("❌ Not connected to PS5");
                return;
            }

            Log("📷 Fetching screenshots list...");
            try
            {
                var shots = await _protocol.ListScreenshotsAsync();
                _currentScreenshots = shots;

                // Apply cached thumbnails immediately
                foreach (var s in shots)
                {
                    if (_screenshotThumbCache.TryGetValue(s.FullPath, out var cached))
                        s.Thumbnail = cached;
                }

                ScreenshotsListBox.ItemsSource = null;
                ScreenshotsListBox.ItemsSource = shots;
                ScreenshotsCountText.Text = $"({shots.Count} items)";
                Log($"✅ Found {shots.Count} screenshots");

                // Background: download thumbnails for items without one
                _ = Task.Run(async () =>
                {
                    string cacheDir = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(), "PS5UploadCache", "ss_thumbs");
                    Directory.CreateDirectory(cacheDir);

                    foreach (var shot in shots)
                    {
                        if (shot.Thumbnail != null) continue;
                        if (_protocol == null || !_protocol.IsConnected) break;

                        // Use hash of remote path as cache file name
                        string hash = shot.FullPath.GetHashCode().ToString("X8");
                        string ext = System.IO.Path.GetExtension(shot.FileName).ToLowerInvariant();
                        if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                        string localCache = System.IO.Path.Combine(cacheDir, hash + ext);

                        try
                        {
                            if (!File.Exists(localCache) || new FileInfo(localCache).Length == 0)
                            {
                                bool ok = await _protocol.DownloadFileAsync(shot.FullPath, localCache);
                                if (!ok) continue;
                            }

                            // Decode downsampled to save RAM (target ~200px wide)
                            var bmp = new System.Windows.Media.Imaging.BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            bmp.DecodePixelWidth = 200;
                            bmp.UriSource = new Uri(localCache, UriKind.Absolute);
                            bmp.EndInit();
                            bmp.Freeze();

                            Dispatcher.Invoke(() =>
                            {
                                _screenshotThumbCache[shot.FullPath] = bmp;
                                shot.Thumbnail = bmp;
                            });
                        }
                        catch
                        {
                            // Skip broken images; the emoji fallback stays
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"❌ Screenshots fetch failed: {ex.Message}");
            }
        }

        private async void DownloadScreenshotButton_Click(object sender, RoutedEventArgs e)
        {
            if (_protocol == null || !_protocol.IsConnected)
            {
                Log("❌ Not connected to PS5");
                return;
            }
            var selected = ScreenshotsListBox.SelectedItems.Cast<PS5Screenshot>().ToList();
            if (selected.Count == 0)
            {
                Log("⚠️ No screenshots selected");
                return;
            }

            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select folder to save screenshots"
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            string targetFolder = dlg.SelectedPath;

            int ok = 0, fail = 0;
            foreach (var shot in selected)
            {
                string localPath = System.IO.Path.Combine(targetFolder, shot.FileName);
                Log($"⬇ Downloading {shot.FileName} ({shot.SizeDisplay})...");
                try
                {
                    bool success = await _protocol.DownloadFileAsync(shot.FullPath, localPath);
                    if (success) { ok++; } else { fail++; Log($"❌ Failed: {shot.FileName}"); }
                }
                catch (Exception ex) { fail++; Log($"❌ {shot.FileName}: {ex.Message}"); }
            }
            Log($"✅ Downloaded {ok} screenshot(s), {fail} failed → {targetFolder}");
        }

        private async void DeleteScreenshotButton_Click(object sender, RoutedEventArgs e)
        {
            if (_protocol == null || !_protocol.IsConnected)
            {
                Log("❌ Not connected to PS5");
                return;
            }
            var selected = ScreenshotsListBox.SelectedItems.Cast<PS5Screenshot>().ToList();
            if (selected.Count == 0)
            {
                Log("⚠️ No screenshots selected");
                return;
            }

            var result = MessageBox.Show(
                $"Delete {selected.Count} screenshot(s) from PS5?\n\nThis cannot be undone.",
                "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            int ok = 0, fail = 0;
            foreach (var shot in selected)
            {
                try
                {
                    // Use dedicated screenshot-delete which also clears PS5's parallel thumbnail
                    var (success, _) = await _protocol.DeleteScreenshotAsync(shot.FullPath);
                    if (success) ok++; else fail++;
                }
                catch { fail++; }
            }
            Log($"🗑️ Deleted {ok} screenshot(s) (+ their PS5 thumbnails), {fail} failed");
            await RefreshScreenshotsAsync();
        }

        private async void ScreenshotsListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ScreenshotsListBox.SelectedItem is not PS5Screenshot shot) return;
            if (_protocol == null || !_protocol.IsConnected) return;

            // Download to %TEMP% and open with default viewer
            string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), shot.FileName);
            Log($"⬇ Opening preview: {shot.FileName}...");
            try
            {
                bool ok = await _protocol.DownloadFileAsync(shot.FullPath, tempPath);
                if (ok)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = tempPath,
                        UseShellExecute = true
                    });
                }
                else
                {
                    Log($"❌ Failed to download preview");
                }
            }
            catch (Exception ex)
            {
                Log($"❌ Preview failed: {ex.Message}");
            }
        }

        private void CopyScreenshotPathMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (ScreenshotsListBox.SelectedItem is PS5Screenshot shot)
            {
                try
                {
                    Clipboard.SetText(shot.FullPath);
                    Log($"📋 Copied path: {shot.FullPath}");
                }
                catch { }
            }
        }
    }
}
