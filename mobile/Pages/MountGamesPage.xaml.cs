#nullable enable
using System;
using System.Text;
using Microsoft.Maui.Controls;

namespace PS5UploadMobile.Pages;

public partial class MountGamesPage : ContentPage
{
    private readonly StringBuilder _buffer = new();

    public MountGamesPage()
    {
        InitializeComponent();
    }

    private void AppendLine(string s)
    {
        _buffer.AppendLine(s);
        LogLabel.Text = _buffer.ToString();
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await System.Threading.Tasks.Task.Delay(50);
            await LogScroll.ScrollToAsync(0, double.MaxValue, false);
        });
    }

    private async void OnStartClicked(object sender, EventArgs e)
    {
        var proto = await PageHelper.EnsureConnectedAsync(this);
        if (proto == null) return;

        StartBtn.IsEnabled = false;
        _buffer.Clear();
        AppendLine("🚀 Starting mount scan…");
        AppendLine("Scanning: /data/etaHEN/games, /mnt/usb0-3, /mnt/ext0/games …");

        var (ok, summary) = await proto.MountGamesAsync(msg =>
            MainThread.BeginInvokeOnMainThread(() => AppendLine(msg)));

        AppendLine("");
        AppendLine(ok ? "✅ Mount scan complete" : "❌ Mount scan failed");
        AppendLine("");
        AppendLine("—— Summary ——");
        AppendLine(summary);
        StartBtn.IsEnabled = true;
    }
}
