using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DeskBox.Contracts;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace DeskBox.Views;

/// <summary>
/// The whole organizer engine, surfaced with DeskBox's own settings building
/// blocks (toolkit SettingsCards + SectionPanelStyle) instead of a web page.
/// Everything is parsed through JsonDocument and rendered by hand, so no
/// reflection-based binding is needed under Native AOT.
/// </summary>
public sealed partial class CompanionSettingsSection : UserControl
{
    private readonly List<(string Id, string Dir)> _categories = new();

    private bool _statusBusy;
    private bool _updateUiReady;
    private bool _settingsLoaded;
    private bool _categoriesLoaded;

    /// <summary>Owner window handle, set by the hosting window so the file /
    /// folder pickers can be attached to it.</summary>
    public IntPtr OwnerHandle { get; set; }

    public CompanionSettingsSection()
    {
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            RefreshUpdateUi();
            await RefreshStatusAsync();
            await LoadCategoriesAsync();
            await RefreshPlanAsync();
            await RefreshConflictsAsync();
            await RefreshPendingAsync();
            await RefreshProposalsAsync();
        };
    }

    // ----------------------------------------------------------------- update

    /// <summary>
    /// Reflects the nutstore update channel: what is installed, whether the
    /// synced folder offers something newer, and whether the boot-time check is
    /// registered as a scheduled task.
    /// </summary>
    private void RefreshUpdateUi()
    {
        _updateUiReady = false;
        try
        {
            UpdateVersionValue.Text = "v" + LocalUpdateChannel.CurrentVersion;

            LocalUpdateChannel.UpdateStatus status = LocalUpdateChannel.Check();
            UpdateStateValue.Text = status.Message;

            string? lastRun = LocalUpdateChannel.LastLogLine();
            if (!string.IsNullOrEmpty(lastRun))
            {
                UpdateStateValue.Text += "\n上次自动检查：" + lastRun;
            }

            AutoUpdateCheckBox.IsChecked = LocalUpdateChannel.IsAutoUpdateEnabled();
        }
        catch (Exception ex)
        {
            UpdateStateValue.Text = "读取更新状态失败：" + ex.Message;
        }
        finally
        {
            _updateUiReady = true;
        }
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        UpdateNowButton.IsEnabled = false;
        try
        {
            UpdateStateValue.Text = "正在检查…";
            LocalUpdateChannel.UpdateStatus status = LocalUpdateChannel.Check();
            if (!status.UpdateAvailable)
            {
                // No local sync folder (or already current): the worker also
                // knows the cloud channel, so let it decide instead of stopping.
                UpdateStateValue.Text = "正在检查云端通道…";
                string probe = await LocalUpdateChannel.RunWorkerAsync();
                UpdateStateValue.Text = probe + "\n" + status.Message;
                RefreshUpdateUi();
                return;
            }

            UpdateStateValue.Text = "正在安装 " + status.RemoteVersion + "…程序会自动重启";
            string output = await LocalUpdateChannel.RunWorkerAsync();
            UpdateStateValue.Text = output;
            RefreshUpdateUi();
        }
        catch (Exception ex)
        {
            UpdateStateValue.Text = "更新失败：" + ex.Message;
        }
        finally
        {
            UpdateNowButton.IsEnabled = true;
        }
    }

    private void AutoUpdateCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (!_updateUiReady)
        {
            return;
        }

        (bool ok, string message) = LocalUpdateChannel.SetAutoUpdateEnabled(true);
        UpdateStateValue.Text = message;
        if (!ok)
        {
            _updateUiReady = false;
            AutoUpdateCheckBox.IsChecked = false;
            _updateUiReady = true;
        }
    }

    private void AutoUpdateCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!_updateUiReady)
        {
            return;
        }

        (bool _, string message) = LocalUpdateChannel.SetAutoUpdateEnabled(false);
        UpdateStateValue.Text = message;
    }

    // ---------------------------------------------------------------- status

    public async Task RefreshStatusAsync()
    {
        if (_statusBusy)
        {
            return;
        }

        _statusBusy = true;
        try
        {
            CompanionStatus status = await CompanionClient.GetStatusAsync();
            if (!status.Reachable)
            {
                ActionValue.Text = "管家引擎未运行";
                ScanValue.Text = "-";
                ApplyValue.Text = "-";
                ErrorValue.Text = "引擎没有响应，退出 DeskBox 再打开一次";
                AiValue.Text = "-";
                ApplyNowButton.IsEnabled = false;
                BackupNowButton.IsEnabled = false;
                return;
            }

            ApplyNowButton.IsEnabled = true;
            BackupNowButton.IsEnabled = true;

            ActionValue.Text = status.OrgBusy ? "整理中" : "空闲";
            ScanValue.Text = Or(status.LastScan);
            ApplyValue.Text = status.LastApplyCount > 0
                ? Or(status.LastApply) + " · " + status.LastApplyCount + " 个"
                : Or(status.LastApply);
            ErrorValue.Text = Or(status.LastError);
            AiValue.Text = status.AiEnabled
                ? status.AiModel + " · 调用 " + status.AiCalls + " 次"
                : "未启用（不影响规则整理）";
            TrashText.Text = "待删除目录：" + Or(status.TrashDir);

            if (!_settingsLoaded)
            {
                await LoadSettingsAsync();
            }
        }
        finally
        {
            _statusBusy = false;
        }
    }

    private async Task LoadSettingsAsync()
    {
        JsonDocument? document = await CompanionClient.GetJsonAsync("/api/settings");
        if (document is null)
        {
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            DesktopBox.Text = Str(root, "desktop");
            LibraryBox.Text = Str(root, "library");
        }

        _settingsLoaded = true;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        (bool ok, string message) = await CompanionClient.PostJsonAsync("/api/settings", SettingsJson());
        SaveMessage.Text = ok ? "已保存，立即生效。" : "保存失败：" + message;
        ApiKeyBox.Password = string.Empty;
        await RefreshStatusAsync();
    }

    // --------------------------------------------------------------- actions

    private async void ApplyNowButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync("/api/apply", "整理", RefreshPlanAsync);

    private async void BackupNowButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync("/api/backup", "备份", null);

    private async Task RunActionAsync(string path, string label, Func<Task>? after)
    {
        ActionMessage.Text = "正在" + label + "…";
        (bool ok, string message) = await CompanionClient.PostAsync(path);
        ActionMessage.Text = ok ? label + "已开始。" : label + "没成功：" + message;
        await RefreshStatusAsync();
        if (ok && after is not null)
        {
            await after();
        }
    }

    // ------------------------------------------------------------ categories

    /// <summary>Fallback taxonomy: the engine's 8 top-level folders, used when
    /// neither the running engine nor its config.json can be read. Keeping it
    /// here means the button still builds the格 even if the engine is down.</summary>
    private static readonly string[] FallbackCategoryRoots =
    {
        "01 数学教学",
        "02 教务考务",
        "03 控辍保学",
        "04 上级文件",
        "05 班级管理",
        "06 家校沟通",
        "07 教研与进修",
        "08 行政与事务"
    };

    private static string CompanionConfigPath() =>
        Path.Combine(AppContext.BaseDirectory, "companion", "config.json");

    /// <summary>Reads <c>library</c> straight from the engine's own config so the
    /// button works even when the engine is not answering on 127.0.0.1:8765.</summary>
    private static string ReadLibraryFromCompanionConfig()
    {
        try
        {
            string configPath = CompanionConfigPath();
            if (!File.Exists(configPath))
            {
                return string.Empty;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!document.RootElement.TryGetProperty("library", out JsonElement element))
            {
                return string.Empty;
            }

            string raw = element.GetString() ?? string.Empty;
            return Environment.ExpandEnvironmentVariables(raw).Trim().Trim('"');
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Top-level folder names from the engine config's <c>paths[]</c>.</summary>
    private static List<string> ReadRootsFromCompanionConfig()
    {
        var roots = new List<string>();
        try
        {
            string configPath = CompanionConfigPath();
            if (!File.Exists(configPath))
            {
                return roots;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!document.RootElement.TryGetProperty("paths", out JsonElement paths) ||
                paths.ValueKind != JsonValueKind.Array)
            {
                return roots;
            }

            foreach (JsonElement item in paths.EnumerateArray())
            {
                string dir = Str(item, "dir");
                string first = dir.Split('/', '\\')[0].Trim();
                if (first.Length > 0 && !first.StartsWith("_", StringComparison.Ordinal) && !roots.Contains(first))
                {
                    roots.Add(first);
                }
            }
        }
        catch
        {
            // Fall through: the caller uses its built-in list.
        }

        return roots;
    }

    private async Task LoadCategoriesAsync()
    {
        if (_categoriesLoaded)
        {
            return;
        }

        JsonDocument? document = await CompanionClient.GetJsonAsync("/api/paths");
        if (document is null)
        {
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in document.RootElement.EnumerateArray())
                {
                    string id = Str(item, "id");
                    string dir = Str(item, "dir");
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(dir))
                    {
                        _categories.Add((id, dir));
                    }
                }
            }
        }

        TargetCategoryBox.Items.Clear();
        foreach ((string _, string dir) in _categories)
        {
            TargetCategoryBox.Items.Add(DisplayCategory(dir));
        }

        if (TargetCategoryBox.Items.Count > 0)
        {
            TargetCategoryBox.SelectedIndex = 0;
        }

        _categoriesLoaded = _categories.Count > 0;
    }

    private static string DisplayCategory(string dir) => dir.Replace("/", " → ");

    private ComboBox BuildCategoryPicker(string? suggestedId)
    {
        var picker = new ComboBox { MinWidth = 190, FontSize = 12 };
        foreach ((string _, string dir) in _categories)
        {
            picker.Items.Add(DisplayCategory(dir));
        }

        int index = 0;
        if (!string.IsNullOrEmpty(suggestedId))
        {
            int found = _categories.FindIndex(c => c.Id == suggestedId);
            if (found >= 0)
            {
                index = found;
            }
        }

        if (picker.Items.Count > 0)
        {
            picker.SelectedIndex = index;
        }

        return picker;
    }

    private string? CategoryIdAt(ComboBox picker) =>
        picker.SelectedIndex >= 0 && picker.SelectedIndex < _categories.Count
            ? _categories[picker.SelectedIndex].Id
            : null;

    // ------------------------------------------------------- 重新归类（任意文件/文件夹）

    private async void PickFileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = await FileOpenPickerService.PickSingleFileAsync(
                OwnerHandle,
                new[] { "*" },
                PickerLocationId.DocumentsLibrary);
            if (!string.IsNullOrWhiteSpace(path))
            {
                SourcePathBox.Text = path;
            }
        }
        catch (Exception ex)
        {
            MoveMessage.Text = "选择文件失败：" + ex.Message;
        }
    }

    private async void PickFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = await FolderPickerService.PickFolderAsync(OwnerHandle);
            if (!string.IsNullOrWhiteSpace(path))
            {
                SourcePathBox.Text = path;
            }
        }
        catch (Exception ex)
        {
            MoveMessage.Text = "选择文件夹失败：" + ex.Message;
        }
    }

    // ------------------------------------------------- 文件夹（桌面 / 共享库）浏览

    /// <summary>Names a Nutstore/OneDrive style sync root ends with. When the
    /// user picks one of these as the library we append 教学资料库, because
    /// dropping the eight category folders straight into the sync root would
    /// mix them with everything else that is being synced.</summary>
    private static readonly string[] SyncRootTailNames = { "我的坚果云", "坚果云", "nutstore", "同步" };

    private async void PickDesktopFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = await FolderPickerService.PickFolderAsync(OwnerHandle);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            DesktopBox.Text = path;
            SaveMessage.Text = "已选桌面文件夹，点下面「保存设置」生效。";
        }
        catch (Exception ex)
        {
            SaveMessage.Text = "选择文件夹失败：" + ex.Message;
        }
    }

    private async void PickLibraryFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = await FolderPickerService.PickFolderAsync(OwnerHandle);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string tail = Path.GetFileName(path.TrimEnd('\\', '/'));
            bool looksLikeSyncRoot = false;
            foreach (string name in SyncRootTailNames)
            {
                if (string.Equals(tail, name, StringComparison.OrdinalIgnoreCase))
                {
                    looksLikeSyncRoot = true;
                    break;
                }
            }

            if (looksLikeSyncRoot && !string.Equals(tail, "教学资料库", StringComparison.OrdinalIgnoreCase))
            {
                LibraryBox.Text = Path.Combine(path, "教学资料库");
                SaveMessage.Text = "已选坚果云同步目录，共享库自动补成 …\\教学资料库（点「保存设置」生效，目录会自动创建）。";
            }
            else
            {
                LibraryBox.Text = path;
                SaveMessage.Text = "已选共享库文件夹，点下面「保存设置」生效。";
            }
        }
        catch (Exception ex)
        {
            SaveMessage.Text = "选择文件夹失败：" + ex.Message;
        }
    }

    private async void GenerateCategoryWidgets_Click(object sender, RoutedEventArgs e)
    {
        GenerateWidgetsButton.IsEnabled = false;
        try
        {
            GenerateWidgetsMessage.Text = "正在准备分类格子…";

            // 1) 共享库：输入框 → 引擎状态 → 引擎自己的 config.json（引擎没起也能干活）
            string library = LibraryBox.Text.Trim().Trim('"');
            if (library.Length == 0)
            {
                CompanionStatus status = await CompanionClient.GetStatusAsync();
                if (status.Reachable && !string.IsNullOrWhiteSpace(status.Library))
                {
                    library = status.Library.Trim().Trim('"');
                }
            }

            if (library.Length == 0)
            {
                library = ReadLibraryFromCompanionConfig();
            }

            if (library.Length == 0)
            {
                GenerateWidgetsMessage.Text =
                    "找不到共享库文件夹：请先在上面点「浏览…」选一个（例如 …\\我的坚果云\\教学资料库），再点这个按钮。";
                return;
            }

            if (LibraryBox.Text.Trim().Length == 0)
            {
                LibraryBox.Text = library;
            }

            try
            {
                Directory.CreateDirectory(library);
            }
            catch (Exception ex)
            {
                GenerateWidgetsMessage.Text = "共享库文件夹不可用：" + ex.Message;
                return;
            }

            // 2) 分类清单：引擎 /api/paths → config.json paths[] → 内置八类
            if (!_categoriesLoaded)
            {
                await LoadCategoriesAsync();
            }

            List<string> roots = new();
            foreach ((string _, string dir) in _categories)
            {
                string first = dir.Split('/', '\\')[0].Trim();
                if (first.Length > 0 && !first.StartsWith("_", StringComparison.Ordinal) && !roots.Contains(first))
                {
                    roots.Add(first);
                }
            }

            if (roots.Count == 0)
            {
                roots = ReadRootsFromCompanionConfig();
            }

            if (roots.Count == 0)
            {
                roots.AddRange(FallbackCategoryRoots);
            }

            // 4) 重建：走 ISettingsWidgetActions。设置窗口跑在独立的设置进程里，
            //    那边永远没有 WidgetManager —— 直接等 App.WidgetManager 只会空等
            //    20 秒。宿主把这一调用转给主进程执行：旧分类的格子先清掉（文件夹
            //    内容搬到桌面，让引擎重新归位），分类补齐，剩下的全部折叠成贴着
            //    屏幕右边缘的胶囊（天气最上，01…08 按序，搜索最下）。
            if (_widgetActions is not { } widgetActions)
            {
                GenerateWidgetsMessage.Text =
                    "格子服务没接上：请关闭设置窗口（Esc）后重新打开，再点一次。";
                return;
            }

            GenerateWidgetsMessage.Text = "正在重建分类格子（清旧格子 → 补分类 → 折叠排版）…";
            CompanionWidgetResetResult result =
                await widgetActions.ResetCompanionCategoryWidgetsAsync(library, roots);

            var summary = new StringBuilder();
            summary.Append("已按分类重建：新建 ").Append(result.CreatedCount)
                .Append(" 个、保留 ").Append(result.ReusedCount).Append(" 个");
            if (result.RemovedCount > 0)
            {
                summary.Append("；清掉旧格子 ").Append(result.RemovedCount).Append(" 个");
                if (result.RemovedNames.Count > 0)
                {
                    summary.Append("（").Append(string.Join("、", result.RemovedNames.Take(8)));
                    if (result.RemovedNames.Count > 8)
                    {
                        summary.Append(" 等");
                    }

                    summary.Append("）");
                }

                if (result.MovedFolderCount > 0)
                {
                    summary.Append("，其中 ").Append(result.MovedFolderCount)
                        .Append(" 个文件夹的文件已移到桌面");
                }

                if (result.LeftInPlaceCount > 0)
                {
                    summary.Append("，").Append(result.LeftInPlaceCount)
                        .Append(" 个不在共享库内的文件夹原样保留");
                }
            }

            summary.Append("。已折叠 ").Append(result.RailCount)
                .Append(" 个格子贴到屏幕右侧：天气在最上，01…08 按顺序，搜索在最下。");

            if (result.Errors.Count > 0)
            {
                summary.Append("\n有 ").Append(result.Errors.Count).Append(" 处没成功：")
                    .Append(string.Join("；", result.Errors));
            }

            GenerateWidgetsMessage.Text = summary.ToString();
        }
        catch (Exception ex)
        {
            GenerateWidgetsMessage.Text = "生成格子失败：" + ex.Message;
        }
        finally
        {
            GenerateWidgetsButton.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------ 布局同步

    private async void LayoutPushButton_Click(object sender, RoutedEventArgs e)
    {
        LayoutPushButton.IsEnabled = false;
        try
        {
            if (_widgetActions is not { } widgetActions)
            {
                LayoutMessage.Text = "格子服务没接上：请关闭设置窗口（Esc）后重新打开，再点一次。";
                return;
            }

            LayoutMessage.Text = "正在推送本机布局…";

            string library = await ResolveLibraryFolderAsync();
            if (library.Length == 0)
            {
                LayoutMessage.Text = "先在上面选好共享库文件夹，再推送布局。";
                return;
            }

            // 布局属于主进程的格子管理器；设置进程只负责发起与显示结果。
            LayoutTransferResult result = await widgetActions.PushLayoutAsync(library);
            LayoutMessage.Text = result.Message;
        }
        catch (Exception ex)
        {
            LayoutMessage.Text = "推送布局失败：" + ex.Message;
        }
        finally
        {
            LayoutPushButton.IsEnabled = true;
        }
    }

    private async void LayoutPullButton_Click(object sender, RoutedEventArgs e)
    {
        LayoutPullButton.IsEnabled = false;
        try
        {
            if (_widgetActions is not { } widgetActions)
            {
                LayoutMessage.Text = "格子服务没接上：请关闭设置窗口（Esc）后重新打开，再点一次。";
                return;
            }

            LayoutMessage.Text = "正在读取另一台的布局…";

            string library = await ResolveLibraryFolderAsync();
            if (library.Length == 0)
            {
                LayoutMessage.Text = "先在上面选好共享库文件夹，再读取布局。";
                return;
            }

            LayoutTransferResult result = await widgetActions.PullLayoutAsync(library);
            LayoutMessage.Text = result.Message;
        }
        catch (Exception ex)
        {
            LayoutMessage.Text = "应用布局失败：" + ex.Message;
        }
        finally
        {
            LayoutPullButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// The widget facade injected by the hosting settings window. It is the
    /// settings host's remote facade over the IPC pipe, so "按分类生成格子"
    /// and the layout buttons execute in the MAIN process — the host process
    /// never owns a WidgetManager (waiting for one here could only time out).
    /// </summary>
    private ISettingsWidgetActions? _widgetActions;

    public void Configure(ISettingsWidgetActions widgetActions, IntPtr ownerHandle)
    {
        _widgetActions = widgetActions;
        OwnerHandle = ownerHandle;
    }

    /// <summary>共享库路径：输入框 → 引擎状态 → 引擎 config.json。</summary>
    private async Task<string> ResolveLibraryFolderAsync()
    {
        string library = LibraryBox.Text.Trim().Trim('"');
        if (library.Length == 0)
        {
            CompanionStatus status = await CompanionClient.GetStatusAsync();
            if (status.Reachable && !string.IsNullOrWhiteSpace(status.Library))
            {
                library = status.Library.Trim().Trim('"');
            }
        }

        if (library.Length == 0)
        {
            library = ReadLibraryFromCompanionConfig();
        }

        return library;
    }

    private async void MoveSourceButton_Click(object sender, RoutedEventArgs e)
    {
        string source = SourcePathBox.Text.Trim().Trim('"');
        string? pathId = CategoryIdAt(TargetCategoryBox);
        if (string.IsNullOrEmpty(source))
        {
            MoveMessage.Text = "先选一个文件或文件夹。";
            return;
        }

        if (pathId is null)
        {
            MoveMessage.Text = "先选目标分类。";
            return;
        }

        MoveMessage.Text = "正在移动…";
        string body = ReclassifyJson(source, pathId);
        (bool ok, string message) = await CompanionClient.PostJsonAsync("/api/reclassify", body);
        MoveMessage.Text = ok ? "已移动到「" + TargetCategoryBox.SelectedItem + "」。" : "移动失败：" + message;
        if (ok)
        {
            SourcePathBox.Text = string.Empty;
            await RefreshPendingAsync();
            await RefreshStatusAsync();
        }
    }

    // ----------------------------------------------------------------- plan

    private async Task RefreshPlanAsync()
    {
        ProposalListPlaceholder();
        await FillListAsync(PlanList, "/api/plan", root =>
        {
            if (root.ValueKind != JsonValueKind.Array)
            {
                return new List<RowModel>();
            }

            var rows = new List<RowModel>();
            foreach (JsonElement item in root.EnumerateArray())
            {
                rows.Add(new RowModel
                {
                    Title = Str(item, "name"),
                    Detail = Str(item, "action") + " → " + Str(item, "target_dir")
                        + "　依据：" + Str(item, "source") + " " + Str(item, "reason")
                        + "　置信 " + Num(item, "confidence").ToString("0.00")
                });
            }

            return rows;
        }, "桌面上没有可整理的文件（或都还在静置期内）");
    }

    private void ProposalListPlaceholder()
    {
        // Intentionally empty: keeps the plan refresh signature symmetric with
        // the other list refreshers.
    }

    private async void RefreshPlanButton_Click(object sender, RoutedEventArgs e) => await RefreshPlanAsync();

    // ------------------------------------------------------------ conflicts

    private async Task RefreshConflictsAsync()
    {
        await FillListAsync(ConflictList, "/api/conflict-groups", root =>
        {
            var rows = new List<RowModel>();
            if (!root.TryGetProperty("groups", out JsonElement groups) || groups.ValueKind != JsonValueKind.Array)
            {
                return rows;
            }

            foreach (JsonElement group in groups.EnumerateArray())
            {
                string dir = Str(group, "dir");
                string baseName = Str(group, "base");
                var paths = new List<string>();
                if (group.TryGetProperty("variants", out JsonElement variants) && variants.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement variant in variants.EnumerateArray())
                    {
                        string path = Str(variant, "path");
                        if (!string.IsNullOrEmpty(path))
                        {
                            paths.Add(path);
                        }
                    }
                }

                if (group.TryGetProperty("original", out JsonElement original) && original.ValueKind == JsonValueKind.Object)
                {
                    string path = Str(original, "path");
                    if (!string.IsNullOrEmpty(path))
                    {
                        paths.Insert(0, path);
                    }
                }

                if (paths.Count == 0)
                {
                    continue;
                }

                var row = new RowModel
                {
                    Title = baseName,
                    Detail = dir + " · 共 " + paths.Count + " 份，保留一份、其余归集到 _冲突"
                };

                string keep = paths[0];
                var remove = new List<string>();
                for (int i = 1; i < paths.Count; i++)
                {
                    remove.Add(paths[i]);
                }

                row.Buttons.Add(("保留最新那份", async () =>
                {
                    string body = ConflictJson(keep, remove);
                    await CompanionClient.PostJsonAsync("/api/conflicts/resolve", body);
                    await RefreshConflictsAsync();
                }));

                rows.Add(row);
            }

            return rows;
        }, "没有冲突副本");
    }

    private async void ArchiveConflictsButton_Click(object sender, RoutedEventArgs e)
    {
        await CompanionClient.PostAsync("/api/archive-conflicts");
        await RefreshConflictsAsync();
    }

    // -------------------------------------------------------------- pending

    private async Task RefreshPendingAsync()
    {
        await FillListAsync(PendingList, "/api/pending", root =>
        {
            var rows = new List<RowModel>();
            if (!root.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
            {
                return rows;
            }

            foreach (JsonElement item in items.EnumerateArray())
            {
                string name = Str(item, "name");
                string suggestId = Str(item, "suggest_id");
                string suggestDir = Str(item, "suggest_dir");
                bool isDir = Bool(item, "is_dir");

                var row = new RowModel
                {
                    Title = (isDir ? "📁 " : string.Empty) + name,
                    Detail = (isDir ? "文件夹" : "文件") + " · 放了 " + (int)Num(item, "age_days") + " 天"
                        + (string.IsNullOrEmpty(suggestDir) ? string.Empty : "　建议：" + suggestDir)
                };

                // Every row gets a category picker: the suggestion is only the
                // default, the user can drop it anywhere in the tree in one click.
                ComboBox picker = BuildCategoryPicker(suggestId);
                row.Extra = picker;
                row.Buttons.Add(("移动到这里", async () =>
                {
                    string? targetId = CategoryIdAt(picker);
                    if (targetId is null)
                    {
                        return;
                    }

                    string body = PendingJson(name, targetId);
                    await CompanionClient.PostJsonAsync("/api/pending/apply", body);
                    await RefreshPendingAsync();
                }));

                rows.Add(row);
            }

            return rows;
        }, "没有待归类文件");
    }

    private async void RefreshPendingButton_Click(object sender, RoutedEventArgs e) => await RefreshPendingAsync();

    // ----------------------------------------------------------- duplicates

    private async Task RefreshDuplicatesAsync()
    {
        await FillListAsync(DuplicateList, "/api/duplicates", root =>
        {
            var rows = new List<RowModel>();
            if (!root.TryGetProperty("groups", out JsonElement groups) || groups.ValueKind != JsonValueKind.Array)
            {
                return rows;
            }

            foreach (JsonElement group in groups.EnumerateArray())
            {
                if (!group.TryGetProperty("paths", out JsonElement pathsElement) || pathsElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var paths = new List<string>();
                foreach (JsonElement path in pathsElement.EnumerateArray())
                {
                    paths.Add(path.GetString() ?? string.Empty);
                }

                if (paths.Count < 2)
                {
                    continue;
                }

                var extras = new List<string>();
                for (int i = 1; i < paths.Count; i++)
                {
                    extras.Add(paths[i]);
                }

                var row = new RowModel
                {
                    Title = System.IO.Path.GetFileName(paths[0]),
                    Detail = paths.Count + " 份内容相同：" + System.IO.Path.GetDirectoryName(paths[0])
                };
                row.Buttons.Add(("多余的移到 _重复", async () =>
                {
                    string body = ArrayJson("paths", extras);
                    await CompanionClient.PostJsonAsync("/api/duplicates/archive", body);
                    await RefreshDuplicatesAsync();
                }));
                rows.Add(row);
            }

            return rows;
        }, "没有发现重复文件");

        if (DuplicateList.Children.Count == 0)
        {
            AddHint(DuplicateList, "重复扫描要遍历整个库，点「重新扫描」才跑。");
        }
    }

    private async void RefreshDuplicatesButton_Click(object sender, RoutedEventArgs e) => await RefreshDuplicatesAsync();

    // ------------------------------------------------------------ proposals

    private async Task RefreshProposalsAsync()
    {
        await FillListAsync(ProposalList, "/api/proposals", root =>
        {
            var rows = new List<RowModel>();
            if (!root.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
            {
                return rows;
            }

            foreach (JsonElement item in items.EnumerateArray())
            {
                string id = Str(item, "id");
                var row = new RowModel
                {
                    Title = Str(item, "name"),
                    Detail = "建议放在 " + Str(item, "parent") + " 下 · 已有 " + (int)Num(item, "count") + " 个文件"
                };
                row.Buttons.Add(("同意新建", async () =>
                {
                    await DecideAsync(id, "accept");
                }));
                row.Buttons.Add(("忽略", async () =>
                {
                    await DecideAsync(id, "reject");
                }));
                rows.Add(row);
            }

            return rows;
        }, "暂时没有新分类建议");
    }

    private async Task DecideAsync(string id, string action)
    {
        string body = ProposalJson(id, action);
        await CompanionClient.PostJsonAsync("/api/proposals", body);
        await RefreshProposalsAsync();
    }

    private async void RefreshProposalsButton_Click(object sender, RoutedEventArgs e) => await RefreshProposalsAsync();

    // --------------------------------------------------------------- render

    private sealed class RowModel
    {
        public string Title { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public List<(string Text, Func<Task> Action)> Buttons { get; } = new();

        /// <summary>Optional control (the category picker) shown above the buttons.</summary>
        public FrameworkElement? Extra { get; set; }
    }

    private async Task FillListAsync(
        StackPanel host,
        string path,
        Func<JsonElement, List<RowModel>> project,
        string emptyText)
    {
        host.Children.Clear();
        JsonDocument? document = await CompanionClient.GetJsonAsync(path);
        if (document is null)
        {
            AddHint(host, "读取失败：管家引擎没有响应。");
            return;
        }

        List<RowModel> rows;
        using (document)
        {
            rows = project(document.RootElement);
        }

        if (rows.Count == 0)
        {
            AddHint(host, emptyText);
            return;
        }

        foreach (RowModel row in rows)
        {
            // Text on the left, actions on the right — the same idiom as a
            // SettingsCard row, so the lists read as part of the page.
            var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 7, 0, 7) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock
            {
                Text = row.Title,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });

            if (!string.IsNullOrEmpty(row.Detail))
            {
                texts.Children.Add(new TextBlock
                {
                    Text = row.Detail,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.7
                });
            }

            Grid.SetColumn(texts, 0);
            grid.Children.Add(texts);

            if (row.Extra is not null || row.Buttons.Count > 0)
            {
                var side = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                if (row.Extra is not null)
                {
                    side.Children.Add(row.Extra);
                }

                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                foreach ((string text, Func<Task> action) in row.Buttons)
                {
                    var button = new Button { Content = text, FontSize = 12, Padding = new Thickness(10, 4, 10, 4) };
                    Func<Task> captured = action;
                    button.Click += async (_, _) => await captured();
                    buttons.Children.Add(button);
                }

                side.Children.Add(buttons);
                Grid.SetColumn(side, 1);
                grid.Children.Add(side);
            }

            host.Children.Add(grid);
        }
    }

    private static void AddHint(StackPanel host, string text) =>
        host.Children.Add(new TextBlock { Text = text, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });


    // ------------------------------------------------------- JSON (AOT-safe)
    // Utf8JsonWriter instead of JsonSerializer: the reflection-based overloads
    // raise IL2026/IL3050 and the AOT retail publish treats those as errors.

    private string SettingsJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("desktop", DesktopBox.Text.Trim());
            writer.WriteString("library", LibraryBox.Text.Trim());
            if (!string.IsNullOrEmpty(ApiKeyBox.Password))
            {
                writer.WriteString("api_key", ApiKeyBox.Password);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ConflictJson(string keep, List<string> remove)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("keep", keep);
            writer.WriteStartArray("remove");
            foreach (string path in remove)
            {
                writer.WriteStringValue(path);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string PendingJson(string name, string pathId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("names");
            writer.WriteStringValue(name);
            writer.WriteEndArray();
            writer.WriteString("path_id", pathId);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ArrayJson(string property, List<string> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray(property);
            foreach (string value in values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ReclassifyJson(string src, string pathId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("src", src);
            writer.WriteString("path_id", pathId);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ProposalJson(string id, string action)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("action", action);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // ---------------------------------------------------------------- JSON

    private static string Or(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double Num(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    private static bool Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
