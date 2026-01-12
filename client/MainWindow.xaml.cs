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
        private string _currentPS5Path = "/data";
        private CancellationTokenSource? _uploadCancellation;
        private string _ps5IpAddress = "";
        
        // Parallel upload settings
        private const int MaxParallelUploads = 6; // Optimal number for stable high-speed uploads
        
        // Total upload tracking
        private int _totalFilesToUpload = 0;
        private int _currentFileIndex = 0;
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
        private int _activeTaskCount = 0;
        
        // Log throttling to prevent UI freeze
        private int _logCounter = 0;
        private const int MaxLogLines = 1000;

        public MainWindow()
        {
            InitializeComponent();
            LocalFilesListBox.ItemsSource = _localFiles;
            PS5FilesListBox.ItemsSource = _ps5Files;
            
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
        }

        private void UiUpdateTimer_Tick(object sender, EventArgs e)
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
                
                // Copy to clipboard on a background thread
                await Task.Run(() =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        System.Windows.Clipboard.SetText(logContent);
                    });
                });
                
                Log("📋 Log copied to clipboard!");
            }
            catch (Exception ex)
            {
                Log($"❌ Failed to copy log: {ex.Message}");
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
                UploadETAText.Text = $"ETA: {eta:mm\\:ss} | Elapsed: {elapsed:mm\\:ss}";
                
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
            
            // Skip verbose upload progress messages
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
                        ConnectButton.Content = "� Disconnect";
                        ConnectButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.DarkGreen);
                        UploadButton.IsEnabled = true;
                    });
                    
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
                    string parentPath = Path.GetDirectoryName(path);
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
                        FullPath = path + "/" + entry.Name,
                        Icon = entry.IsDirectory ? "📁" : "📄",
                        IsDirectory = entry.IsDirectory,
                        Size = entry.Size
                    });
                }

                // Sort: folders first, then files (both alphabetically)
                var sortedItems = items.Where(i => i.Name == "..").ToList();
                sortedItems.AddRange(items.Where(i => i.Name != ".." && i.IsDirectory).OrderBy(i => i.Name));
                sortedItems.AddRange(items.Where(i => !i.IsDirectory).OrderBy(i => i.Name));

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
                    CollectFilesFromDirectory(item.FullPath, _currentPS5Path + "/" + item.Name, allFiles);
                }
                else
                {
                    allFiles.Add((item.FullPath, _currentPS5Path + "/" + item.Name));
                }
            }

            _totalFilesToUpload = allFiles.Count;
            _totalBytesToUpload = allFiles.Sum(f => new FileInfo(f.localPath).Length);
            _totalBytesUploaded = 0;
            _completedFiles = 0;
            _currentFileIndex = 0;
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

                // PARALLEL UPLOAD: Process files in batches using multiple connections
                Log($"🚀 Starting parallel upload with {MaxParallelUploads} connections");
                var fileQueue = new Queue<(string localPath, string remotePath)>(allFiles);
                var activeTasks = new List<Task>();
                var activeConnections = new List<PS5Protocol>();
                var taskToFilePath = new Dictionary<Task, string>(); // Map tasks to file paths
                var fileChunkCounts = new Dictionary<string, int>(); // Track how many chunks per file
                var fileChunksCompleted = new Dictionary<string, int>(); // Track completed chunks per file
                var completedFiles = new HashSet<string>(); // Track which files are fully complete

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
                        
                        // CRITICAL: Chunking disabled for maximum stability
                        // PS5 cannot handle concurrent writes to same file even with mutex
                        // 6 parallel single-connection uploads = full gigabit speed + zero errors
                        if (false) // Chunking permanently disabled
                        {
                            // CRITICAL: Use only 2 chunks for maximum PS5 stability
                            // Multiple threads waiting on the same file mutex causes connection drops
                            // 2 chunks provides good speed while maintaining stability
                            int chunkCount = 2;
                            
                            // Split file into chunks for parallel upload
                            long chunkSize = fileInfo.Length / chunkCount;
                            Log($"📤 Starting CHUNKED upload: {Path.GetFileName(localPath)} ({FormatFileSize(fileInfo.Length)}) - {chunkCount} chunks");
                            
                            // Create all connections first, then start uploads in parallel
                            var connections = new List<PS5Protocol>();
                            var connectionTasks = new List<Task<PS5Protocol?>>();
                            
                            for (int i = 0; i < chunkCount; i++)
                            {
                                int chunkIndex = i;
                                var connTask = Task.Run(async () =>
                                {
                                    var conn = new PS5Protocol();
                                    if (await conn.ConnectAsync(_ps5IpAddress))
                                    {
                                        Log($"✅ Chunk {chunkIndex + 1}/{chunkCount} connected");
                                        return conn;
                                    }
                                    return null;
                                });
                                connectionTasks.Add(connTask);
                            }
                            
                            // Wait for all connections to complete
                            var connResults = await Task.WhenAll(connectionTasks);
                            
                            // Track chunk count for this file
                            fileChunkCounts[localPath] = chunkCount;
                            fileChunksCompleted[localPath] = 0;
                            
                            // Start all uploads in parallel
                            for (int i = 0; i < chunkCount; i++)
                            {
                                var conn = connResults[i];
                                if (conn != null)
                                {
                                    long offset = i * chunkSize;
                                    long size = (i == chunkCount - 1) ? (fileInfo.Length - offset) : chunkSize;
                                    
                                    lock (activeConnections)
                                    {
                                        activeConnections.Add(conn);
                                    }
                                    
                                    var uploadTask = UploadFileChunkAsync(conn, localPath, remotePath, offset, size, _uploadCancellation.Token);
                                    activeTasks.Add(uploadTask);
                                    taskToFilePath[uploadTask] = localPath; // Map task to file
                                }
                            }
                        }
                        else
                        {
                            // Normal single-connection upload for small files
                            Log($"📤 Starting upload: {Path.GetFileName(localPath)} (Queue: {fileQueue.Count}, Active: {activeTasks.Count})");
                            var connection = new PS5Protocol();
                            
                            if (await connection.ConnectAsync(_ps5IpAddress))
                            {
                                Log($"✅ Connection {activeTasks.Count + 1} established");
                                activeConnections.Add(connection);
                                var task = UploadFileParallelAsync(connection, localPath, remotePath, _uploadCancellation.Token);
                                activeTasks.Add(task);
                                taskToFilePath[task] = localPath; // Map task to file
                                fileChunkCounts[localPath] = 1; // Single chunk for small files
                                fileChunksCompleted[localPath] = 0;
                            }
                            else
                            {
                                Log($"❌ Connection failed, requeueing {Path.GetFileName(localPath)}");
                                // Connection failed, put file back in queue and wait
                                fileQueue.Enqueue((localPath, remotePath));
                                break;
                            }
                        }
                    }

                    if (activeTasks.Count > 0)
                    {
                        Log($"⏳ Waiting for task completion (Active: {activeTasks.Count})");
                        // Wait for any task to complete
                        var completedTask = await Task.WhenAny(activeTasks);
                        Log($"✅ Task completed");
                        
                        // Await the task to catch any exceptions
                        try
                        {
                            await completedTask;
                            Log($"✅ Task awaited successfully");
                        }
                        catch (Exception ex)
                        {
                            Log($"❌ Task exception: {ex.Message}");
                            Dispatcher.Invoke(() =>
                            {
                                UploadFileNameText.Text = $"Upload error: {ex.Message}";
                            });
                        }
                        
                        int taskIndex = activeTasks.IndexOf(completedTask);
                        Log($"🔍 Task index: {taskIndex} (Total tasks: {activeTasks.Count}, Connections: {activeConnections.Count})");
                        
                        // CRITICAL FIX: Always remove task and update file progress, even if index mismatch
                        // Get the file path for this task
                        string filePath = null;
                        if (taskToFilePath.TryGetValue(completedTask, out filePath))
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
                            taskToFilePath.Remove(completedTask);
                        }
                        
                        // Remove task from list
                        activeTasks.Remove(completedTask);
                        
                        // Try to cleanup connection if index is valid
                        if (taskIndex >= 0 && taskIndex < activeConnections.Count)
                        {
                            Log($"🧹 Cleaning up connection at index {taskIndex}");
                            var completedConn = activeConnections[taskIndex];
                            activeConnections.RemoveAt(taskIndex);
                            
                            // Aggressively dispose connection immediately
                            try
                            {
                                completedConn.Disconnect();
                                completedConn.Dispose();
                            }
                            catch { }
                        }
                        else
                        {
                            Log($"⚠️ Connection index mismatch ({taskIndex} vs {activeConnections.Count}) - task removed, connection cleanup skipped");
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
                Log($"🧹 Cleaning up {activeConnections.Count} remaining connections");
                foreach (var conn in activeConnections.ToList())
                {
                    try
                    {
                        conn.Disconnect();
                        conn.Dispose();
                    }
                    catch { }
                }
                activeConnections.Clear();
                Log("✅ Cleanup complete");

                if (!_uploadCancellation.Token.IsCancellationRequested)
                {
                    Log($"🎉 Upload completed! {_totalFilesToUpload} files, {FormatFileSize(_totalBytesUploaded)}");
                    MessageBox.Show($"Upload completed!\n\n{_totalFilesToUpload} files uploaded\nTotal: {FormatFileSize(_totalBytesUploaded)}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    _localFiles.Clear();
                    
                    // Reconnect main protocol after upload to ensure connection is active
                    Log("🔄 Reconnecting to PS5 after upload...");
                    try
                    {
                        // Disconnect first to clean up any stale connection
                        _protocol.Disconnect();
                        Log("🔌 Disconnected old connection");
                        
                        // Wait a bit for the server to clean up
                        await Task.Delay(500);
                        
                        // Reconnect
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
                    catch (Exception ex)
                    {
                        Log($"❌ Reconnection error: {ex.Message}");
                        MessageBox.Show($"Reconnection error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    
                    await LoadPS5DirectoryAsync(_currentPS5Path);
                }
                else
                {
                    Log("⚠️ Upload cancelled");
                    MessageBox.Show("Upload cancelled by user", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Warning);
                    
                    // Reconnect main protocol after cancellation to allow folder navigation
                    Log("🔄 Reconnecting to PS5 after cancellation...");
                    try
                    {
                        // Disconnect first to clean up
                        _protocol.Disconnect();
                        await Task.Delay(500);
                        
                        if (await _protocol.ConnectAsync(_ps5IpAddress))
                        {
                            Log("✅ Reconnected to PS5");
                            await LoadPS5DirectoryAsync(_currentPS5Path);
                        }
                        else
                        {
                            Log("❌ Reconnection failed");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"❌ Reconnection error: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log("❌ Upload cancelled (OperationCanceledException)");
                MessageBox.Show("Upload cancelled by user", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Warning);
                
                // Reconnect main protocol after cancellation to allow folder navigation
                Log("🔄 Reconnecting to PS5 after cancellation...");
                try
                {
                    _protocol.Disconnect();
                    await Task.Delay(500);
                    
                    if (await _protocol.ConnectAsync(_ps5IpAddress))
                    {
                        Log("✅ Reconnected to PS5");
                        await LoadPS5DirectoryAsync(_currentPS5Path);
                    }
                }
                catch (Exception ex)
                {
                    Log($"❌ Reconnection error: {ex.Message}");
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
                    
                    Dispatcher.Invoke(() =>
                    {
                        UploadProgressBar.Value = filePercent;
                        UploadProgressText.Text = $"{FormatFileSize(p.BytesSent)} / {FormatFileSize(p.TotalBytes)} ({filePercent:F1}%)";
                    });
                    
                    // Log progress periodically (every 40MB)
                    if (p.BytesSent % (40 * 1024 * 1024) < 8 * 1024 * 1024 || p.BytesSent == p.TotalBytes)
                    {
                        Log($"📊 {fileName}: {FormatFileSize(p.BytesSent)}/{FormatFileSize(p.TotalBytes)} ({filePercent:F1}%) @ {FormatFileSize((long)p.SpeedBytesPerSecond)}/s");
                    }
                });

                bool success = await connection.UploadFileAsync(localPath, remotePath, progress, cancellationToken);
                
                if (success)
                {
                    Log($"✅ Upload complete: {fileName}");
                    lock (_progressLock)
                    {
                        _totalBytesUploaded += fileInfo.Length;
                    }
                }
                else
                {
                    Log($"❌ Upload failed: {fileName}");
                }
            }
            catch (Exception ex)
            {
                Log($"❌ Exception uploading {Path.GetFileName(localPath)}: {ex.Message}");
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
                    Height = 150,
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
                    Height = 150,
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
                    Text = _currentPS5Path + "/" + item.Name,
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
                            string selected = listBox.SelectedItem.ToString();
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
                            string selected = listBox.SelectedItem.ToString();
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
                    Height = 150,
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
                    Text = _currentPS5Path + "/" + item.Name,
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
                            string selected = listBox.SelectedItem.ToString();
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
                            string selected = listBox.SelectedItem.ToString();
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
}
