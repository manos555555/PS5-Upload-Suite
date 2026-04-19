#nullable enable
using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;

namespace PS5UploadMobile.Pages;

public partial class ShellPage : ContentPage
{
    private readonly StringBuilder _buffer = new();
    private bool _shellOpen;

    public ShellPage()
    {
        InitializeComponent();
    }

    private void AppendLine(string s)
    {
        _buffer.AppendLine(s);
        OutputLabel.Text = _buffer.ToString();
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await Task.Delay(50);
            await OutputScroll.ScrollToAsync(0, double.MaxValue, false);
        });
    }

    private async void OnOpenClicked(object sender, EventArgs e)
    {
        var proto = await PageHelper.EnsureConnectedAsync(this);
        if (proto == null) return;
        bool ok = await proto.OpenShellAsync();
        _shellOpen = ok;
        AppendLine(ok ? "🟢 Shell opened" : "🔴 Failed to open shell");
    }

    private async void OnCloseClicked(object sender, EventArgs e)
    {
        var proto = PageHelper.Conn.Protocol;
        if (proto == null) { AppendLine("(no connection)"); return; }
        bool ok = await proto.CloseShellAsync();
        _shellOpen = false;
        AppendLine(ok ? "🟡 Shell closed" : "⚠️ Close failed");
    }

    private void OnClearClicked(object sender, EventArgs e)
    {
        _buffer.Clear();
        OutputLabel.Text = "";
    }

    private async void OnCmdCompleted(object sender, EventArgs e) => await RunCmdAsync();
    private async void OnRunClicked(object sender, EventArgs e) => await RunCmdAsync();

    private async Task RunCmdAsync()
    {
        string cmd = CmdEntry.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(cmd)) return;

        var proto = await PageHelper.EnsureConnectedAsync(this);
        if (proto == null) return;

        if (!_shellOpen)
        {
            bool opened = await proto.OpenShellAsync();
            _shellOpen = opened;
            if (!opened) { AppendLine("🔴 Shell not open"); return; }
        }

        AppendLine($"$ {cmd}");
        string result = await proto.ExecuteShellCommandAsync(cmd);
        if (!string.IsNullOrEmpty(result)) AppendLine(result.TrimEnd('\n', '\r'));
    }
}
