using System;
using System.Threading.Tasks;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// In-app panel for the bundled organizer engine, so the user never has to
/// open a browser to check status or press 立即整理 / 立即同步 / 立即备份.
/// Reads the engine's loopback dashboard at <see cref="CompanionClient.BaseUrl"/>
/// and fails soft when the engine is not running.
/// </summary>
public sealed partial class CompanionPanelWindow : Window
{
    // Kept in sync with the engine's clamps (5–1440 minutes).
    private static readonly int[] IntervalChoices = { 15, 30, 60, 120, 240, 720 };

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(6) };
    private bool _refreshing;
    private bool _intervalTouched;

    public CompanionPanelWindow()
    {
        InitializeComponent();
        Title = "桌面管家";

        try
        {
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            AppWindow appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hWnd));
            appWindow.Resize(new SizeInt32(800, 780));
        }
        catch (Exception)
        {
            // Sizing is cosmetic; a failed resize must never block the panel.
        }

        foreach (int minutes in IntervalChoices)
        {
            IntervalBox.Items.Add(minutes + " 分钟");
        }

        _timer.Tick += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Activates the panel and starts polling the engine.</summary>
    public void ShowWindow()
    {
        Activate();
        _timer.Start();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            CompanionStatus status = await CompanionClient.GetStatusAsync();
            if (!status.Reachable)
            {
                SubtitleText.Text = "管家引擎未运行（companion 没有启动）";
                ActionText.Text = "-";
                ScanText.Text = "-";
                ApplyText.Text = "-";
                SyncText.Text = "-";
                ErrorText.Text = "-";
                LibraryText.Text = "-";
                TrashText.Text = "-";
                MessageText.Text = "引擎没有响应。先退出 DeskBox 再重新打开；仍然没有，就检查 %LOCALAPPDATA%\\DeskBox\\companion 里有没有 deskbox-smart.exe。";
                ApplyButton.IsEnabled = false;
                SyncButton.IsEnabled = false;
                BackupButton.IsEnabled = false;
                return;
            }

            ApplyButton.IsEnabled = true;
            SyncButton.IsEnabled = true;
            BackupButton.IsEnabled = true;

            SubtitleText.Text = status.Device
                + " · AI" + (status.AiEnabled ? "已启用 " + status.AiModel : "未启用")
                + " · 调用 " + status.AiCalls + " 次";

            ActionText.Text = status.SyncBusy ? "同步中" : status.OrgBusy ? "整理中" : "空闲";
            ScanText.Text = Or(status.LastScan);
            ApplyText.Text = status.LastApplyCount > 0
                ? Or(status.LastApply) + " · " + status.LastApplyCount + " 个"
                : Or(status.LastApply);
            SyncText.Text = Or(status.LastSync)
                + (string.IsNullOrEmpty(status.LastSyncError) ? string.Empty : "　⚠ " + status.LastSyncError)
                + (string.IsNullOrEmpty(status.SyncNote) ? string.Empty : "　" + status.SyncNote);
            ErrorText.Text = Or(status.LastError);
            LibraryText.Text = Or(status.Library);
            TrashText.Text = Or(status.TrashDir);

            if (!_intervalTouched)
            {
                SelectInterval(status.IntervalMinutes);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static string Or(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private void SelectInterval(int minutes)
    {
        int index = Array.IndexOf(IntervalChoices, minutes);
        if (index < 0)
        {
            index = Array.IndexOf(IntervalChoices, 30);
        }

        if (index >= 0 && IntervalBox.SelectedIndex != index)
        {
            IntervalBox.SelectedIndex = index;
        }
    }

    private void IntervalBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IntervalBox.SelectedIndex >= 0)
        {
            _intervalTouched = true;
        }
    }

    private async void SaveIntervalButton_Click(object sender, RoutedEventArgs e)
    {
        if (IntervalBox.SelectedIndex < 0 || IntervalBox.SelectedIndex >= IntervalChoices.Length)
        {
            return;
        }

        int minutes = IntervalChoices[IntervalBox.SelectedIndex];
        (bool ok, string message) = await CompanionClient.SetIntervalMinutesAsync(minutes);
        MessageText.Text = ok
            ? "同步周期已改为 " + minutes + " 分钟，下一轮起生效。"
            : "保存失败：" + message;
        _intervalTouched = false;
        await RefreshAsync();
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync("/api/apply", "整理");
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync("/api/sync", "同步");
    }

    private async void BackupButton_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync("/api/backup", "备份");
    }

    private async Task RunActionAsync(string path, string label)
    {
        MessageText.Text = "正在" + label + "…";
        (bool ok, string message) = await CompanionClient.PostAsync(path);
        MessageText.Text = ok
            ? label + "已开始，几秒后状态会刷新。"
            : label + "没成功：" + message;
        await RefreshAsync();
    }

    private async void OpenDashboardButton_Click(object sender, RoutedEventArgs e)
    {
        await Windows.System.Launcher.LaunchUriAsync(new Uri(CompanionClient.BaseUrl + "/"));
    }
}
