using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DeskBox.Services;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DeskBox;

/// <summary>
/// The widget drawer: a glass bead that parks the whole widget layer, plus the
/// per-widget park/raise bookkeeping, the Ctrl+Alt+Space chord, staging, notes,
/// shortcuts, screenshots and the exam countdown.
///
/// Parked state lives in drawer.json, never in the settings' IsVisible flag:
/// that flag is what the tray raise/hide pass manipulates, and two owners of one
/// flag is exactly how widgets end up reappearing after a restart.
/// </summary>
public partial class App
{
    private WidgetDrawerWindow? _widgetDrawerWindow;
    private DrawerHotkeyService? _drawerHotkey;

    internal WidgetDrawerSettings DrawerSettings { get; } = WidgetDrawerSettings.Load();

    private CompanionStatus? _drawerStatus;
    private string? _drawerLibrary;
    private DispatcherQueueTimer? _drawerCountdownTimer;
    private DateTimeOffset _drawerCountdownEnd;
    private DispatcherQueueTimer? _drawerFullScreenTimer;
    private bool _drawerHiddenForFullscreen;

    /// <summary>True when no widget is currently on the desktop.</summary>
    internal bool WidgetsParked => WidgetManager is { } manager && !manager.HasVisibleWidgets;

    internal IReadOnlyList<(string Id, string Name)> DrawerWidgetChoices() =>
        WidgetManager?.DrawerWidgetChoices() ?? Array.Empty<(string Id, string Name)>();

    internal IReadOnlyList<(string Id, string Name, bool Visible)> DrawerWidgetStates() =>
        WidgetManager?.WidgetStates() ?? Array.Empty<(string Id, string Name, bool Visible)>();

    /// <summary>Fan sector click: flip one widget between shown and parked.</summary>
    internal async Task ToggleWidgetAsync(string widgetId)
    {
        bool visible = false;
        foreach ((string id, string name, bool isVisible) in DrawerWidgetStates())
        {
            if (string.Equals(id, widgetId, StringComparison.Ordinal))
            {
                visible = isVisible;
                _ = name;
                break;
            }
        }

        if (visible)
        {
            await CollapseWidgetAsync(widgetId);
        }
        else
        {
            await ExpandWidgetAsync(widgetId);
        }
    }

    // ------------------------------------------------------------- show / hide

    /// <summary>Shows the floating drawer (created on first use).</summary>
    internal void ShowWidgetDrawer()
    {
        if (!DrawerSettings.Visible)
        {
            return;
        }

        try
        {
            if (_widgetDrawerWindow is not null)
            {
                _widgetDrawerWindow.Activate();
                return;
            }

            var window = new WidgetDrawerWindow(DrawerSettings);
            window.Closed += (_, _) =>
            {
                _widgetDrawerWindow = null;
                _drawerHotkey?.Detach();
                _drawerHotkey = null;
            };

            _widgetDrawerWindow = window;
            window.ShowAtSavedSpot();
            RegisterDrawerHotkey(window.WindowHandle);
        }
        catch (Exception ex)
        {
            Log($"[Drawer] failed to open: {ex}");
        }
    }

    /// <summary>设置页 / 托盘 / 右键菜单共用的显示隐藏入口。</summary>
    internal void ApplyDrawerVisibility()
    {
        if (DrawerSettings.Visible)
        {
            ShowWidgetDrawer();
        }
        else
        {
            CloseWidgetDrawer();
        }
    }

    internal void ToggleWidgetDrawerVisibility()
    {
        DrawerSettings.Visible = !DrawerSettings.Visible;
        DrawerSettings.Save();
        ApplyDrawerVisibility();
    }

    /// <summary>球右键「隐藏悬浮球」：隐藏后球窗口关闭，托盘/设置/热键都能找回。</summary>
    internal void HideWidgetDrawerFromBall()
    {
        DrawerSettings.Visible = false;
        DrawerSettings.Save();
        CloseWidgetDrawer();
        Log("[Drawer] hidden from the ball menu");
    }

    /// <summary>
    /// 设置页写盘后调用：把磁盘上的设置抄进内存实例，重建外观并同步显示状态。
    /// 位置坐标不抄——设置页不该改变球的位置。
    /// </summary>
    internal void ReloadDrawerSettingsFromDisk()
    {
        WidgetDrawerSettings disk = WidgetDrawerSettings.Load(DrawerSettings.Path);
        DrawerSettings.CopyFrom(disk);
        if (DrawerSettings.Visible)
        {
            ShowWidgetDrawer();
            _widgetDrawerWindow?.ApplySettingsFromStore();
        }
        else
        {
            CloseWidgetDrawer();
        }

        ApplyDrawerFullScreenWatch();
    }

    private void RegisterDrawerHotkey(IntPtr windowHandle)
    {
        try
        {
            _drawerHotkey ??= new DrawerHotkeyService(() => _ = ToggleWidgetsFromDrawerAsync());
            if (!_drawerHotkey.TryRegister(windowHandle, out string? error) &&
                !string.IsNullOrEmpty(error))
            {
                Log($"[Drawer] hotkey unavailable: {error}");
            }
        }
        catch (Exception ex)
        {
            Log($"[Drawer] hotkey setup failed: {ex}");
        }
    }

    internal void RefreshDrawerAppearance() => _widgetDrawerWindow?.Refresh();

    internal void CloseWidgetDrawer()
    {
        WidgetDrawerWindow? window = _widgetDrawerWindow;
        _widgetDrawerWindow = null;
        _drawerHotkey?.Detach();
        _drawerHotkey = null;
        try
        {
            window?.Close();
        }
        catch (Exception ex)
        {
            Log($"[Drawer] failed to close: {ex}");
        }
    }

    /// <summary>Startup hook: the handle is there before the user needs it, and
    /// the widgets parked last session stay off the desktop.</summary>
    internal void ShowWidgetDrawerOnStartup()
    {
        if (DrawerSettings.Visible)
        {
            ShowWidgetDrawer();
        }

        ApplyDrawerFullScreenWatch();
        _ = ApplyParkedWidgetsAsync();
    }

    private async Task ApplyParkedWidgetsAsync()
    {
        if (DrawerSettings.CollapsedWidgetIds.Count == 0 || WidgetManager is not { } manager)
        {
            return;
        }

        foreach (string widgetId in DrawerSettings.CollapsedWidgetIds.ToArray())
        {
            try
            {
                await manager.SetWidgetVisibleAsync(widgetId, false);
            }
            catch (Exception ex)
            {
                Log($"[Drawer] restoring parked widget {widgetId} failed: {ex}");
            }
        }

        RefreshDrawerAppearance();
    }

    /// <summary>One click on the ball: anything visible → park all; nothing
    /// visible → raise everything (and forget the parked list, because "raise
    /// all" means all).</summary>
    internal async Task ToggleWidgetsFromDrawerAsync()
    {
        if (WidgetManager is not { } manager)
        {
            return;
        }

        if (manager.HasVisibleWidgets)
        {
            await manager.SetAllWidgetsVisibleAsync(false);
        }
        else
        {
            await RaiseAllWidgetsAsync();
        }

        RefreshDrawerAppearance();
    }

    internal async Task RaiseAllWidgetsAsync()
    {
        if (WidgetManager is not { } manager)
        {
            return;
        }

        DrawerSettings.CollapsedWidgetIds.Clear();
        DrawerSettings.Save();
        await manager.SetAllWidgetsVisibleAsync(true);
        RefreshDrawerAppearance();
    }

    internal async Task SetAllWidgetsVisibleAsync(bool visible)
    {
        if (WidgetManager is not { } manager)
        {
            return;
        }

        await manager.SetAllWidgetsVisibleAsync(visible);
        RefreshDrawerAppearance();
    }

    internal async Task CollapseWidgetAsync(string widgetId)
    {
        DrawerSettings.SetCollapsed(widgetId, true);
        DrawerSettings.Save();
        if (WidgetManager is { } manager)
        {
            await manager.SetWidgetVisibleAsync(widgetId, false);
        }

        RefreshDrawerAppearance();
    }

    internal async Task ExpandWidgetAsync(string widgetId)
    {
        DrawerSettings.SetCollapsed(widgetId, false);
        DrawerSettings.Save();
        if (WidgetManager is { } manager)
        {
            await manager.SetWidgetVisibleAsync(widgetId, true);
        }

        RefreshDrawerAppearance();
    }

    internal void OpenCompanionPanelFromDrawer()
    {
        CancelBackgroundMemoryCleanup();
        ShowCompanionPanelWindow();
    }

    // ----------------------------------------------------------------- library

    /// <summary>引擎状态里的资料库；引擎没起时退回 companion\config.json。</summary>
    internal async Task<string> ResolveDrawerLibraryAsync()
    {
        if (!string.IsNullOrEmpty(_drawerLibrary) && Directory.Exists(_drawerLibrary))
        {
            return _drawerLibrary;
        }

        CompanionStatus status = await CompanionClient.GetStatusAsync();
        NoteDrawerStatus(status);
        if (status.Reachable && !string.IsNullOrWhiteSpace(status.Library) && Directory.Exists(status.Library))
        {
            _drawerLibrary = status.Library;
            return _drawerLibrary;
        }

        string fallback = ReadLibraryFromEngineConfig();
        if (Directory.Exists(fallback))
        {
            _drawerLibrary = fallback;
        }

        return _drawerLibrary ?? string.Empty;
    }

    /// <summary>引擎的 companion\config.json 与 exe 同级。</summary>
    private static string ReadLibraryFromEngineConfig()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskBox",
                "companion",
                "config.json");
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("library", out System.Text.Json.JsonElement library) &&
                library.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return library.GetString() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            Log($"[Drawer] read engine config failed: {ex.Message}");
        }

        return string.Empty;
    }

    internal string DrawerStagingRoot()
    {
        if (!string.IsNullOrWhiteSpace(DrawerSettings.StagingDir))
        {
            return Environment.ExpandEnvironmentVariables(DrawerSettings.StagingDir);
        }

        string library = _drawerLibrary ?? string.Empty;
        if (!string.IsNullOrEmpty(library))
        {
            return Path.Combine(library, "_暂存");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskBox",
            "staging");
    }

    // ----------------------------------------------------------------- staging

    /// <summary>拖到球上的文件：移动到「资料库\_暂存\yyyyMMdd」，等下班再归位。</summary>
    internal async Task<(int Moved, string Message)> StageFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return (0, "没有可暂存的文件");
        }

        await ResolveDrawerLibraryAsync();
        string root = DrawerStagingRoot();
        string day = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd"));
        try
        {
            Directory.CreateDirectory(day);
        }
        catch (Exception ex)
        {
            return (0, "建暂存目录失败：" + ex.Message);
        }

        (int moved, List<string> failures) = Platform.DrawerStaging.MoveFiles(day, paths);

        FlashDrawerGlyph(
            moved > 0 ? "存" : "!",
            moved > 0 ? "已暂存 " + moved + " 个文件" : "暂存失败",
            failures.Count == 0 ? null : string.Join("；", failures));
        return (moved, failures.Count == 0 ? "已暂存" : string.Join("；", failures));
    }

    /// <summary>暂存目录里的文件数（角标用；最多数到 999）。</summary>
    internal Task<int> CountStagedFilesAsync()
    {
        try
        {
            string root = DrawerStagingRoot();
            if (!Directory.Exists(root))
            {
                return Task.FromResult(0);
            }

            int count = 0;
            foreach (string _ in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (++count >= 999)
                {
                    break;
                }
            }

            return Task.FromResult(count);
        }
        catch (Exception)
        {
            return Task.FromResult(0);
        }
    }

    internal async Task OpenDrawerStagingAsync()
    {
        await ResolveDrawerLibraryAsync();
        OpenDrawerFolder(DrawerStagingRoot());
    }

    internal async Task OpenDrawerLibraryAsync()
    {
        string library = await ResolveDrawerLibraryAsync();
        if (string.IsNullOrEmpty(library))
        {
            FlashDrawerGlyph("?", "找不到资料库", "先在设置里指定共享库");
            return;
        }

        OpenDrawerFolder(library);
    }

    // -------------------------------------------------------------- organize

    /// <summary>「预览整理」直接开现有的桌面整理窗口（它本来就是预览 + 执行界面）。</summary>
    internal Task ShowDrawerOrganizePreviewAsync()
    {
        CancelBackgroundMemoryCleanup();
        ShowDesktopOrganizationWindow();
        return Task.CompletedTask;
    }

    /// <summary>「立即整理」：POST /api/apply，结束后刷新角标并闪一下。</summary>
    internal async Task RunDrawerOrganizeAsync()
    {
        (bool ok, string message) = await CompanionClient.PostAsync("/api/apply");
        if (!ok)
        {
            FlashDrawerGlyph("!", "整理失败", message);
            return;
        }

        CompanionStatus status = await CompanionClient.GetStatusAsync();
        NoteDrawerStatus(status);
        string text = status.Reachable
            ? "已整理 " + status.LastApplyCount + " 个文件"
            : "已提交整理";
        FlashDrawerGlyph("✓", text, null);
        RefreshDrawerAppearance();
    }

    internal void NoteDrawerStatus(CompanionStatus status)
    {
        _drawerStatus = status;
        if (status.Reachable && !string.IsNullOrWhiteSpace(status.Library))
        {
            _drawerLibrary = status.Library;
        }
    }

    /// <summary>「最近整理」菜单；引擎没给记录时退回空表。</summary>
    internal IReadOnlyList<(string Name, string Folder)> DrawerRecentMoves()
    {
        var rows = new List<(string Name, string Folder)>();
        if (_drawerStatus is null)
        {
            return rows;
        }

        foreach (CompanionRecentMove move in _drawerStatus.Recent)
        {
            if (string.IsNullOrWhiteSpace(move.To))
            {
                continue;
            }

            string folder = Path.GetDirectoryName(move.To) ?? move.To;
            string label = move.Name;
            if (label.Length > 26)
            {
                label = label.Substring(0, 26) + "…";
            }

            rows.Add((label, folder));
            if (rows.Count >= 6)
            {
                break;
            }
        }

        return rows;
    }

    internal void OpenDrawerFolder(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + folder + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log($"[Drawer] open folder failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ shortcuts

    internal async Task OpenDrawerShortcutAsync(DrawerShortcut shortcut)
    {
        await ResolveDrawerLibraryAsync();
        string target = shortcut.Target ?? string.Empty;

        if (string.Equals(target, "@library", StringComparison.OrdinalIgnoreCase))
        {
            target = _drawerLibrary ?? string.Empty;
        }
        else if (string.Equals(target, "@staging", StringComparison.OrdinalIgnoreCase))
        {
            target = DrawerStagingRoot();
        }
        else if (string.Equals(target, "@downloads", StringComparison.OrdinalIgnoreCase))
        {
            target = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
        else if (target.StartsWith("@library\\", StringComparison.OrdinalIgnoreCase))
        {
            target = Path.Combine(_drawerLibrary ?? string.Empty, target.Substring("@library\\".Length));
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            FlashDrawerGlyph("?", "入口无效", shortcut.Name + " 没有可打开的目标");
            return;
        }

        try
        {
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
                return;
            }

            if (Directory.Exists(target))
            {
                OpenDrawerFolder(target);
                return;
            }

            if (File.Exists(target))
            {
                Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
                return;
            }

            FlashDrawerGlyph("?", "找不到", target);
        }
        catch (Exception ex)
        {
            Log($"[Drawer] shortcut failed: {ex.Message}");
        }
    }

    internal void CopyDrawerPhrase(string phrase)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(phrase);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            FlashDrawerGlyph("✓", "已复制到剪贴板", phrase.Length > 40 ? phrase.Substring(0, 40) + "…" : phrase);
        }
        catch (Exception ex)
        {
            Log($"[Drawer] copy phrase failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------- screenshot

    /// <summary>
    /// 全屏截图。AOT 下不能用 System.Drawing，所以交给系统 PowerShell（Win10/11
    /// 自带）画一张 PNG，再放进剪贴板；存到「资料库\_截图」。
    /// </summary>
    internal async Task CaptureDrawerScreenshotAsync()
    {
        await ResolveDrawerLibraryAsync();
        string library = _drawerLibrary ?? string.Empty;
        if (string.IsNullOrEmpty(library))
        {
            FlashDrawerGlyph("?", "找不到资料库", "截图无处可放，先在设置里指定共享库");
            return;
        }

        string folder = Path.Combine(library, "_截图");
        string file = Path.Combine(folder, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
        try
        {
            Directory.CreateDirectory(folder);

            string script = Path.Combine(Path.GetTempPath(), "deskbox-capture.ps1");
            File.WriteAllText(script, CaptureScript, new UTF8Encoding(true));

            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" -out \"" + file + "\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process? process = Process.Start(start);
            if (process is null)
            {
                FlashDrawerGlyph("!", "截图失败", "无法启动 PowerShell");
                return;
            }

            await process.WaitForExitAsync();
            if (!File.Exists(file))
            {
                FlashDrawerGlyph("!", "截图失败", "系统没有生成 PNG");
                return;
            }

            try
            {
                StorageFile storage = await StorageFile.GetFileFromPathAsync(file);
                var package = new DataPackage();
                package.SetStorageItems(new[] { storage });
                global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            }
            catch (Exception ex)
            {
                Log($"[Drawer] clipboard copy failed: {ex.Message}");
            }

            FlashDrawerGlyph("✓", "已截图并复制", Path.GetFileName(file));
        }
        catch (Exception ex)
        {
            Log($"[Drawer] screenshot failed: {ex.Message}");
            FlashDrawerGlyph("!", "截图失败", ex.Message);
        }
    }

    private const string CaptureScript = """
param([string]$out)
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap $vs.Width, $vs.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($vs.X, $vs.Y, 0, 0, $bmp.Size)
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
""";

    // ----------------------------------------------------------- fullscreen

    /// <summary>「全屏时自动隐藏」：3 秒看一次前台窗口，进出全屏时隐藏/恢复球。</summary>
    private void ApplyDrawerFullScreenWatch()
    {
        try
        {
            _drawerFullScreenTimer?.Stop();
            _drawerFullScreenTimer = null;
            _drawerHiddenForFullscreen = false;

            if (!DrawerSettings.AutoHideFullScreen || !DrawerSettings.Visible)
            {
                return;
            }

            _drawerFullScreenTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _drawerFullScreenTimer.Interval = TimeSpan.FromSeconds(3);
            _drawerFullScreenTimer.IsRepeating = true;
            _drawerFullScreenTimer.Tick -= DrawerFullScreenTick;
            _drawerFullScreenTimer.Tick += DrawerFullScreenTick;
            _drawerFullScreenTimer.Start();
        }
        catch (Exception ex)
        {
            Log($"[Drawer] fullscreen watch failed: {ex.Message}");
        }
    }

    private void DrawerFullScreenTick(DispatcherQueueTimer sender, object args)
    {
        bool fullscreen = Platform.ForegroundFullscreen.IsForegroundFullscreen();
        if (fullscreen == _drawerHiddenForFullscreen)
        {
            return;
        }

        _drawerHiddenForFullscreen = fullscreen;
        _widgetDrawerWindow?.SetTemporarilyHidden(fullscreen);
    }

    // -------------------------------------------------------------- countdown

    internal TimeSpan? DrawerCountdownRemaining
    {
        get
        {
            if (_drawerCountdownTimer is null)
            {
                return null;
            }

            TimeSpan left = _drawerCountdownEnd - DateTimeOffset.Now;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    internal void StartDrawerCountdown(TimeSpan duration)
    {
        try
        {
            _drawerCountdownEnd = DateTimeOffset.Now + duration;
            _drawerCountdownTimer ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _drawerCountdownTimer.Interval = TimeSpan.FromSeconds(1);
            _drawerCountdownTimer.IsRepeating = true;
            _drawerCountdownTimer.Tick -= DrawerCountdownTick;
            _drawerCountdownTimer.Tick += DrawerCountdownTick;
            _drawerCountdownTimer.Start();
            _widgetDrawerWindow?.UpdateCountdownGlyph();
        }
        catch (Exception ex)
        {
            Log($"[Drawer] countdown failed: {ex.Message}");
        }
    }

    private void DrawerCountdownTick(DispatcherQueueTimer sender, object args)
    {
        TimeSpan? left = DrawerCountdownRemaining;
        if (left is null)
        {
            sender.Stop();
            return;
        }

        if (left.Value <= TimeSpan.Zero)
        {
            sender.Stop();
            _drawerCountdownTimer = null;
            Platform.Win32Helper.MessageBeep(0x00000040);
            FlashDrawerGlyph("铃", "时间到", "考试倒计时结束");
            _widgetDrawerWindow?.UpdateCountdownGlyph();
            return;
        }

        _widgetDrawerWindow?.UpdateCountdownGlyph();
    }

    // ------------------------------------------------------------------ flash

    /// <summary>球面上闪一个字符 + 更新悬停气泡，让动作有可见反馈。</summary>
    internal void FlashDrawerGlyph(string glyph, string title, string? detail)
    {
        try
        {
            _widgetDrawerWindow?.FlashGlyph(glyph, title, detail);
        }
        catch (Exception ex)
        {
            Log($"[Drawer] flash failed: {ex.Message}");
        }
    }
}
