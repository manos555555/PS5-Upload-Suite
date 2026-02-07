using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace PS5Upload
{
    public partial class App : Application
    {
        private static readonly string LogFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, 
            $"ps5upload_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
        
        private static readonly object _logLock = new object();
        
        public App()
        {
            // Global exception handlers
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            
            LogToFile("=== Application Started ===");
        }
        
        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogToFile($"DISPATCHER EXCEPTION: {e.Exception}");
            e.Handled = true; // Prevent crash, but log it
            
            MessageBox.Show($"Error: {e.Exception.Message}\n\nCheck log file: {LogFilePath}", 
                "PS5 Upload Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        
        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            LogToFile($"UNHANDLED EXCEPTION: {ex}");
        }
        
        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LogToFile($"TASK EXCEPTION: {e.Exception}");
            e.SetObserved(); // Prevent crash
        }
        
        public static void LogToFile(string message)
        {
            try
            {
                lock (_logLock)
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    File.AppendAllText(LogFilePath, $"[{timestamp}] {message}\n");
                }
            }
            catch { /* Ignore logging errors */ }
        }
    }
}
