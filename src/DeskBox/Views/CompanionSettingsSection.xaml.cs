using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views;

/// <summary>
/// The whole organizer engine, surfaced with DeskBox's own settings building
/// blocks (toolkit SettingsCards + SectionPanelStyle) instead of a web page.
/// Everything is parsed through JsonDocument and rendered by hand, so no
/// reflection-based binding is needed under Native AOT.
/// </summary>
public sealed partial class CompanionSettingsSection : UserControl
{
    private static readonly int[] IntervalChoices = { 15, 30, 60, 120, 240, 720 };

    private bool _statusBusy;
    private bool _settingsLoaded;

    public CompanionSettingsSection()
    {
        InitializeComponent();
        foreach (int minutes in IntervalChoices)
        {
            IntervalBox.Items.Add(minutes + " 分钟");
        }

        Loaded += async (_, _) =>
        {
            await RefreshStatusAsync();
            await RefreshPlanAsync();
            await RefreshConflictsAsync();
            await RefreshPendingAsync();
            await RefreshProposalsAsync();
        };
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
                SyncValue.Text = "-";
                ErrorValue.Text = "引擎没有响应，退出 DeskBox 再打开一次";
                AiValue.Text = "-";
                ApplyNowButton.IsEnabled = false;
                SyncNowButton.IsEnabled = false;
                BackupNowButton.IsEnabled = false;
                return;
            }

            ApplyNowButton.IsEnabled = true;
            SyncNowButton.IsEnabled = true;
            BackupNowButton.IsEnabled = true;

            ActionValue.Text = status.SyncBusy ? "同步中" : status.OrgBusy ? "整理中" : "空闲";
            ScanValue.Text = Or(status.LastScan);
            ApplyValue.Text = status.LastApplyCount > 0
                ? Or(status.LastApply) + " · " + status.LastApplyCount + " 个"
                : Or(status.LastApply);
            SyncValue.Text = Or(status.LastSync)
                + (string.IsNullOrEmpty(status.LastSyncError) ? string.Empty : "　⚠ " + status.LastSyncError)
                + (string.IsNullOrEmpty(status.SyncNote) ? string.Empty : "　" + status.SyncNote);
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
            UserBox.Text = Str(root, "webdav_user");
            SyncEnabledCheck.IsChecked = Bool(root, "sync_enabled");

            int interval = (int)Num(root, "interval_minutes");
            int index = Array.IndexOf(IntervalChoices, interval);
            if (index < 0)
            {
                index = Array.IndexOf(IntervalChoices, 30);
            }

            if (index >= 0)
            {
                IntervalBox.SelectedIndex = index;
            }
        }

        _settingsLoaded = true;
    }

    private void IntervalBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        int minutes = IntervalBox.SelectedIndex >= 0 && IntervalBox.SelectedIndex < IntervalChoices.Length
            ? IntervalChoices[IntervalBox.SelectedIndex]
            : 30;

        (bool ok, string message) = await CompanionClient.PostJsonAsync("/api/settings", SettingsJson(minutes));
        SaveMessage.Text = ok ? "已保存，立即生效。" : "保存失败：" + message;
        PasswordBoxControl.Password = string.Empty;
        ApiKeyBox.Password = string.Empty;
        await RefreshStatusAsync();
    }

    // --------------------------------------------------------------- actions

    private async void ApplyNowButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync("/api/apply", "整理", RefreshPlanAsync);

    private async void SyncNowButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync("/api/sync", "同步", null);

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

                var row = new RowModel
                {
                    Title = name,
                    Detail = "放了 " + (int)Num(item, "age_days") + " 天"
                        + (string.IsNullOrEmpty(suggestDir) ? string.Empty : "　建议：" + suggestDir)
                };

                if (!string.IsNullOrEmpty(suggestId))
                {
                    row.Buttons.Add(("移到建议分类", async () =>
                    {
                        string body = PendingJson(name, suggestId);
                        await CompanionClient.PostJsonAsync("/api/pending/apply", body);
                        await RefreshPendingAsync();
                    }));
                }

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

            if (row.Buttons.Count > 0)
            {
                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center
                };
                foreach ((string text, Func<Task> action) in row.Buttons)
                {
                    var button = new Button { Content = text, FontSize = 12, Padding = new Thickness(10, 4, 10, 4) };
                    Func<Task> captured = action;
                    button.Click += async (_, _) => await captured();
                    buttons.Children.Add(button);
                }

                Grid.SetColumn(buttons, 1);
                grid.Children.Add(buttons);
            }

            host.Children.Add(grid);
        }
    }

    private static void AddHint(StackPanel host, string text) =>
        host.Children.Add(new TextBlock { Text = text, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });


    // ------------------------------------------------------- JSON (AOT-safe)
    // Utf8JsonWriter instead of JsonSerializer: the reflection-based overloads
    // raise IL2026/IL3050 and the AOT retail publish treats those as errors.

    private string SettingsJson(int minutes)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("desktop", DesktopBox.Text.Trim());
            writer.WriteString("library", LibraryBox.Text.Trim());
            writer.WriteString("webdav_user", UserBox.Text.Trim());
            writer.WriteNumber("interval_minutes", minutes);
            writer.WriteBoolean("sync_enabled", SyncEnabledCheck.IsChecked == true);
            if (!string.IsNullOrEmpty(PasswordBoxControl.Password))
            {
                writer.WriteString("webdav_password", PasswordBoxControl.Password);
            }

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
