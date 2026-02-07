using System.Windows;

namespace PS5Upload
{
    public partial class DuplicateFileDialog : Window
    {
        public enum FileAction
        {
            Replace,
            Skip,
            ReplaceAll,
            SkipAll
        }

        public FileAction UserAction { get; private set; }

        public DuplicateFileDialog(string fileName, long localSize, long remoteSize)
        {
            InitializeComponent();
            
            FileNameTextBlock.Text = fileName;
            LocalSizeTextBlock.Text = $"Local file:  {FormatFileSize(localSize)}";
            RemoteSizeTextBlock.Text = $"Remote file: {FormatFileSize(remoteSize)}";
        }

        private void Replace_Click(object sender, RoutedEventArgs e)
        {
            UserAction = FileAction.Replace;
            DialogResult = true;
            Close();
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            UserAction = FileAction.Skip;
            DialogResult = true;
            Close();
        }

        private void ReplaceAll_Click(object sender, RoutedEventArgs e)
        {
            UserAction = FileAction.ReplaceAll;
            DialogResult = true;
            Close();
        }

        private void SkipAll_Click(object sender, RoutedEventArgs e)
        {
            UserAction = FileAction.SkipAll;
            DialogResult = true;
            Close();
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
}
