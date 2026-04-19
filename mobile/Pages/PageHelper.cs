#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using PS5Upload;
using PS5UploadMobile.Services;

namespace PS5UploadMobile.Pages
{
    internal static class PageHelper
    {
        public static ConnectionManager Conn => ConnectionManager.Instance;

        public static async Task<PS5Protocol?> EnsureConnectedAsync(Page page)
        {
            var proto = await Conn.EnsureConnectedAsync();
            if (proto == null)
            {
                await page.DisplayAlert("Not Connected",
                    $"Could not connect to {Conn.IpAddress}:{Conn.Port}. Please connect from the Files tab first.",
                    "OK");
            }
            return proto;
        }

        public static string DownloadsDir
        {
            get
            {
                string? root = Microsoft.Maui.Storage.FileSystem.AppDataDirectory;
                string path = Path.Combine(root, "PS5Downloads");
                Directory.CreateDirectory(path);
                return path;
            }
        }

        public static string ThumbnailCacheDir
        {
            get
            {
                string path = Path.Combine(Microsoft.Maui.Storage.FileSystem.CacheDirectory, "thumbnails");
                Directory.CreateDirectory(path);
                return path;
            }
        }
    }
}
