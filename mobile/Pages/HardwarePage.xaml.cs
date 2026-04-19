#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using PS5Upload;

namespace PS5UploadMobile.Pages;

public partial class HardwarePage : ContentPage
{
    private IDispatcherTimer? _autoTimer;
    private int _busy; // Interlocked guard

    public HardwarePage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAllAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopAutoTimer();
    }

    private async void OnRefreshClicked(object sender, EventArgs e) => await RefreshAllAsync();

    private void OnAutoRefreshChanged(object sender, CheckedChangedEventArgs e)
    {
        if (e.Value) StartAutoTimer();
        else StopAutoTimer();
    }

    private void StartAutoTimer()
    {
        StopAutoTimer();
        _autoTimer = Dispatcher.CreateTimer();
        _autoTimer.Interval = TimeSpan.FromSeconds(5);
        _autoTimer.Tick += async (_, __) => await RefreshSensorsAsync();
        _autoTimer.Start();
    }

    private void StopAutoTimer()
    {
        if (_autoTimer != null) { _autoTimer.Stop(); _autoTimer = null; }
    }

    private async Task RefreshAllAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var proto = await PageHelper.EnsureConnectedAsync(this);
            if (proto == null) { StatusLabel.Text = "Disconnected"; return; }
            StatusLabel.Text = "Loading hardware info…";

            var hw = await proto.GetHardwareInfoAsync();
            if (hw != null)
            {
                ModelLabel.Text = hw.Model;
                SerialLabel.Text = hw.Serial;
                MachineLabel.Text = hw.HwMachine;
                OsLabel.Text = hw.OsVersion;
                CpuCoresLabel.Text = hw.NumCpu > 0 ? hw.NumCpu.ToString() : "—";
                RamLabel.Text = hw.PhysMemDisplay;
                WlanLabel.Text = hw.WlanBtDisplay;
                OpticalLabel.Text = hw.OpticalDisplay;
            }

            await UpdateSensorsAsync(proto);
            await UpdatePowerAsync(proto);

            StatusLabel.Text = $"Updated {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { StatusLabel.Text = $"Error: {ex.Message}"; }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private async Task RefreshSensorsAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var proto = PageHelper.Conn.Protocol;
            if (proto == null || !proto.IsConnected) { StopAutoTimer(); return; }
            await UpdateSensorsAsync(proto);
            StatusLabel.Text = $"Updated {DateTime.Now:HH:mm:ss}";
        }
        catch { }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private async Task UpdateSensorsAsync(PS5Protocol proto)
    {
        var temps = await proto.GetTemperatureInfoAsync();
        if (temps == null) return;

        CpuTempLabel.Text = temps.CpuTempDisplay;
        SocTempLabel.Text = temps.SocTempDisplay;
        CpuFreqLabel.Text = temps.CpuFreqDisplay;
        SocPowerLabel.Text = temps.SocPowerDisplay;

        // Progress bars - map sensible ranges
        CpuTempBar.Progress = Math.Clamp(temps.CpuTemp / 100.0, 0, 1);
        SocTempBar.Progress = Math.Clamp(temps.SocTemp / 100.0, 0, 1);
        // PS5 CPU typical 3.5 GHz max
        CpuFreqBar.Progress = Math.Clamp(temps.CpuFreqMhz / 3500.0, 0, 1);
        // SoC power 0–200W range
        SocPowerBar.Progress = Math.Clamp(temps.SocPowerMw / 200000.0, 0, 1);
    }

    private async Task UpdatePowerAsync(PS5Protocol proto)
    {
        var power = await proto.GetPowerInfoAsync();
        if (power == null) return;
        OpTimeLabel.Text = $"{power.OperatingTimeHours}h {power.OperatingTimeMinutes}m";
        BootCountLabel.Text = power.BootCount.ToString();
        PowerConsLabel.Text = power.PowerConsumptionMw > 0
            ? $"{power.PowerConsumptionMw / 1000.0:F2} kW·s (lifetime)"
            : "—";
    }
}
