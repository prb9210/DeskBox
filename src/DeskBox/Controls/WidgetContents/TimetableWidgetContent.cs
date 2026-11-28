using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.Timetable;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// 课程表格子的内容视图。整块用代码搭：
/// 5 天 × 8 节的网格用 XAML 模板反而要额外定义一堆 DataTemplate，
/// 而切换班级、按时间高亮「现在」这一列/这节都是运行时算出来的，
/// 直接在代码里重建更直白，也不受 XAML 编译期的类型限制。
/// </summary>
public sealed class TimetableWidgetContent : UserControl
{
    private static readonly Color s_myClassTint = Color.FromArgb(0x2E, 0x2F, 0x6F, 0xD0);
    private static readonly Color s_todayTint = Color.FromArgb(0x18, 0xFF, 0xD4, 0x79);
    private static readonly Color s_nowStroke = Color.FromArgb(0xFF, 0xFF, 0xD4, 0x79);
    private static readonly Color s_cellDark = Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);
    private static readonly Color s_cellLight = Color.FromArgb(0x12, 0x00, 0x00, 0x00);

    private readonly TimetableWidgetViewModel _viewModel;
    private readonly Grid _gridHost = new();
    private readonly TextBlock _footer = new();
    private readonly TextBlock _status = new();
    private readonly Button _classButton = new();
    private readonly StackPanel _classStrip = new();
    private bool _built;

    public TimetableWidgetContent(TimetableWidgetViewModel viewModel)
    {
        _viewModel = viewModel;
        _viewModel.ReminderPresenter = ShowReminder;
        Content = BuildRoot();
        _viewModel.ContentChanged += ViewModel_ContentChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += TimetableWidgetContent_Loaded;
        AllowDrop = true;
        DragOver += TimetableWidgetContent_DragOver;
        Drop += TimetableWidgetContent_Drop;
    }

    private async void TimetableWidgetContent_Loaded(object sender, RoutedEventArgs e)
    {
        if (_built)
        {
            return;
        }

        _built = true;
        await _viewModel.InitializeAsync();
        Rebuild();
    }

    /// <summary>
    /// 系统通知出口。放在视图层（Controls）是因为边界契约只允许这里
    /// 触达应用级服务；视图模型只负责判定「该不该提醒」。
    /// </summary>
    private static bool ShowReminder(TimetableReminderCandidate candidate)
    {
        NativeAppNotificationService? service = (App.Current as App)?.NativeNotificationService;
        if (service is null)
        {
            return false;
        }

        (string title, string message, IReadOnlyDictionary<string, string> arguments) =
            TimetableWidgetViewModel.DescribeReminder(candidate);

        return service.TryShow(
            title,
            message,
            arguments,
            actions: null,
            comboBoxes: null,
            options: new NativeAppNotificationOptions(Tag: "timetable", Group: "timetable"));
    }

    private void ViewModel_ContentChanged(object? sender, EventArgs e)
    {
        Rebuild();
        UpdateFooter();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TimetableWidgetViewModel.NextLessonText)
            or nameof(TimetableWidgetViewModel.StatusText)
            or nameof(TimetableWidgetViewModel.ClassSummary))
        {
            UpdateFooter();
        }
    }

    private UIElement BuildRoot()
    {
        Grid root = new() { Padding = new Thickness(10, 6, 10, 6), RowSpacing = 6 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(BuildHeader());
        Grid.SetRow(_gridHost, 1);
        root.Children.Add(_gridHost);

        _footer.FontSize = 12;
        _footer.TextWrapping = TextWrapping.Wrap;
        _footer.Opacity = 0.9;
        Grid.SetRow(_footer, 2);
        root.Children.Add(_footer);
        return root;
    }

    private UIElement BuildHeader()
    {
        Grid header = new() { ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _classButton.FontSize = 12;
        _classButton.Padding = new Thickness(8, 2, 8, 2);
        _classButton.Content = "选择班级";
        _classButton.Flyout = BuildClassFlyout();
        header.Children.Add(_classButton);

        Button previous = SmallButton("‹", () => _ = StepClassAsync(-1));
        Grid.SetColumn(previous, 1);
        header.Children.Add(previous);

        Button next = SmallButton("›", () => _ = StepClassAsync(1));
        Grid.SetColumn(next, 2);
        header.Children.Add(next);

        StackPanel spacer = new();
        Grid.SetColumn(spacer, 3);
        header.Children.Add(spacer);

        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
        actions.Children.Add(BuildViewToggle());
        actions.Children.Add(BuildMoreMenu());
        Grid.SetColumn(actions, 4);
        header.Children.Add(actions);

        _status.Text = _viewModel.StatusText;
        _status.FontSize = 11;
        _status.Opacity = 0.7;
        _status.Visibility = Visibility.Collapsed;
        return header;
    }

    private Flyout BuildClassFlyout()
    {
        Flyout flyout = new() { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        _classStrip.Spacing = 2;
        _classStrip.Width = 200;
        flyout.Content = new ScrollViewer { Content = _classStrip, MaxHeight = 320 };
        flyout.Opening += (_, _) => FillClassStrip();
        return flyout;
    }

    private void FillClassStrip()
    {
        _classStrip.Children.Clear();
        IReadOnlyList<TimetableClass> classes = _viewModel.Classes;
        if (classes.Count == 0)
        {
            _classStrip.Children.Add(new TextBlock { Text = "还没有导入课程表", FontSize = 12 });
            return;
        }

        foreach (TimetableClass item in classes)
        {
            Button entry = new()
            {
                Content = item.Name + (item.IsMine ? "（我的班）" : string.Empty),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(6, 3, 6, 3)
            };
            string classId = item.Id;
            entry.Click += async (_, _) =>
            {
                _classButton.Flyout?.Hide();
                await _viewModel.SelectClassAsync(classId);
            };
            _classStrip.Children.Add(entry);
        }
    }

    private Button BuildViewToggle()
    {
        Button toggle = SmallButton("今日", () =>
        {
            _viewModel.SetTodayView(!_viewModel.IsTodayView);
        });
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TimetableWidgetViewModel.IsTodayView))
            {
                toggle.Content = _viewModel.IsTodayView ? "本周" : "今日";
            }
        };
        return toggle;
    }

    private Button BuildMoreMenu()
    {
        Button more = SmallButton("⋯", null);
        MenuFlyout menu = new();

        MenuFlyoutItem paste = new() { Text = "从剪贴板导入课表" };
        paste.Click += async (_, _) => await ImportFromClipboardAsync();
        menu.Items.Add(paste);

        MenuFlyoutItem reload = new() { Text = "重新读取" };
        reload.Click += async (_, _) => await _viewModel.ReloadAsync();
        menu.Items.Add(reload);

        menu.Items.Add(new MenuFlyoutSeparator());

        ToggleMenuFlyoutItem reminder = new()
        {
            Text = "上课提醒",
            IsChecked = _viewModel.Settings.ReminderEnabled
        };
        reminder.Click += async (_, _) => await _viewModel.SetReminderEnabledAsync(reminder.IsChecked);
        menu.Items.Add(reminder);

        MenuFlyoutSubItem lead = new() { Text = "提前量" };
        foreach (int minutes in new[] { 0, 2, 5, 10, 15 })
        {
            ToggleMenuFlyoutItem item = new()
            {
                Text = minutes == 0 ? "上课时提醒" : minutes + " 分钟",
                IsChecked = _viewModel.Settings.LeadMinutes == minutes
            };
            int value = minutes;
            item.Click += async (_, _) => await _viewModel.SetLeadMinutesAsync(value);
            lead.Items.Add(item);
        }

        menu.Items.Add(lead);
        more.Flyout = menu;
        return more;
    }

    private static Button SmallButton(string text, Action? onClick)
    {
        Button button = new()
        {
            Content = text,
            FontSize = 12,
            Padding = new Thickness(8, 2, 8, 2),
            MinWidth = 28
        };

        if (onClick is not null)
        {
            button.Click += (_, _) => onClick();
        }

        return button;
    }

    private async Task StepClassAsync(int delta)
    {
        IReadOnlyList<TimetableClass> classes = _viewModel.Classes;
        if (classes.Count == 0)
        {
            return;
        }

        string currentId = _viewModel.CurrentClass?.Id ?? string.Empty;
        int index = 0;
        for (int i = 0; i < classes.Count; i++)
        {
            if (classes[i].Id == currentId)
            {
                index = i;
                break;
            }
        }

        int target = ((index + delta) % classes.Count + classes.Count) % classes.Count;
        await _viewModel.SelectClassAsync(classes[target].Id);
    }

    private async Task ImportFromClipboardAsync()
    {
        try
        {
            DataPackageView view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text))
            {
                SetStatus("剪贴板里没有文本。先在 Excel/WPS 里选中课表区域（含星期表头）复制。");
                return;
            }

            string text = await view.GetTextAsync();
            (bool ok, string message) = await _viewModel.ImportTextAsync(text);
            SetStatus(message);
        }
        catch (Exception ex)
        {
            SetStatus("读取剪贴板失败：" + ex.Message);
        }
    }

    private void TimetableWidgetContent_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "导入课程表";
        }
    }

    private async void TimetableWidgetContent_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        try
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            foreach (IStorageItem item in items)
            {
                if (item is not StorageFile file)
                {
                    continue;
                }

                (bool ok, string message) = await _viewModel.ImportFileAsync(file.Path);
                SetStatus(message);
                if (ok)
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus("导入失败：" + ex.Message);
        }
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        _status.Visibility = Visibility.Visible;
        UpdateFooter();
    }

    private void UpdateFooter()
    {
        string next = _viewModel.NextLessonText;
        _footer.Text = next.Length > 0 ? next : _viewModel.StatusText;
        if (_status.Text.Length == 0)
        {
            _status.Text = _viewModel.StatusText;
        }

        _classButton.Content = _viewModel.CurrentClass?.Name ?? "选择班级";
    }

    private void Rebuild()
    {
        _gridHost.Children.Clear();
        _gridHost.RowDefinitions.Clear();
        _gridHost.ColumnDefinitions.Clear();

        TimetableClass? current = _viewModel.CurrentClass;
        _classButton.Content = current?.Name ?? "选择班级";

        if (current is null)
        {
            BuildEmptyState();
            return;
        }

        bool dark = ActualTheme is ElementTheme.Dark;
        List<int> dayIndexes = new();
        if (_viewModel.IsTodayView)
        {
            int today = TimetableSchedule.DayIndex(DateTimeOffset.Now);
            if (today >= 0)
            {
                dayIndexes.Add(today);
            }
        }
        else
        {
            for (int i = 0; i < _viewModel.Document.Days.Count; i++)
            {
                dayIndexes.Add(i);
            }
        }

        if (dayIndexes.Count == 0)
        {
            BuildMessage("今天休息");
            return;
        }

        IReadOnlyList<TimetablePeriod> periods = _viewModel.Settings.Periods;
        int columns = dayIndexes.Count + 1;
        for (int i = 0; i < columns; i++)
        {
            _gridHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = i == 0 ? new GridLength(40) : new GridLength(1, GridUnitType.Star)
            });
        }

        _gridHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        for (int i = 0; i < periods.Count; i++)
        {
            _gridHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        _gridHost.ColumnSpacing = 4;
        _gridHost.RowSpacing = 3;

        int todayIndex = TimetableSchedule.DayIndex(DateTimeOffset.Now);
        TimetablePeriod? running = TimetableSchedule.RunningPeriod(periods, DateTimeOffset.Now);

        for (int c = 0; c < dayIndexes.Count; c++)
        {
            int day = dayIndexes[c];
            TextBlock header = new()
            {
                Text = _viewModel.Document.Days[day],
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = day == todayIndex ? 1.0 : 0.65
            };
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, c + 1);
            _gridHost.Children.Add(header);

            for (int r = 0; r < periods.Count; r++)
            {
                TimetablePeriod period = periods[r];
                TimetableCell? cell = current.CellAt(day, period.Index);
                Border tile = BuildTile(cell, dark, day == todayIndex, running?.Index == period.Index);
                Grid.SetRow(tile, r + 1);
                Grid.SetColumn(tile, c + 1);
                _gridHost.Children.Add(tile);
            }
        }

        for (int r = 0; r < periods.Count; r++)
        {
            TextBlock label = new()
            {
                Text = periods[r].Index.ToString(CultureInfo.InvariantCulture) + "\n" + periods[r].Start,
                FontSize = 9.5,
                Opacity = 0.55,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(label, r + 1);
            Grid.SetColumn(label, 0);
            _gridHost.Children.Add(label);
        }

        UpdateFooter();
    }

    private Border BuildTile(TimetableCell? cell, bool dark, bool today, bool running)
    {
        Border tile = new()
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(5, 2, 5, 2)
        };

        Color baseTint = dark ? s_cellDark : s_cellLight;
        if (cell is not null && _viewModel.CurrentClass is { IsMine: true })
        {
            baseTint = s_myClassTint;
        }
        else if (today && cell is null)
        {
            baseTint = s_todayTint;
        }

        tile.Background = new SolidColorBrush(baseTint);
        tile.BorderThickness = new Thickness(running ? 2 : 0);
        tile.BorderBrush = new SolidColorBrush(s_nowStroke);

        if (cell is null)
        {
            return tile;
        }

        StackPanel stack = new() { Spacing = 0 };
        stack.Children.Add(new TextBlock
        {
            Text = cell.Subject,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        });

        if (!string.IsNullOrEmpty(cell.Teacher))
        {
            stack.Children.Add(new TextBlock
            {
                Text = cell.Teacher,
                FontSize = 10.5,
                Opacity = 0.65,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            });
        }

        tile.Child = stack;
        if (!string.IsNullOrEmpty(cell.Note))
        {
            ToolTipService.SetToolTip(tile, cell.Note);
        }

        return tile;
    }

    private void BuildEmptyState()
    {
        _gridHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _gridHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        StackPanel stack = new()
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        stack.Children.Add(new TextBlock
        {
            Text = "还没有课程表",
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        stack.Children.Add(new TextBlock
        {
            Text = "在 Excel/WPS 里选中课表区域（含星期表头）复制，再点下面的按钮；也可以把 .xlsx 拖进来。",
            FontSize = 11,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 320
        });

        Button paste = new()
        {
            Content = "从剪贴板导入课表",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        paste.Click += async (_, _) => await ImportFromClipboardAsync();
        stack.Children.Add(paste);

        _status.FontSize = 11;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.TextAlignment = TextAlignment.Center;
        _status.MaxWidth = 340;
        stack.Children.Add(_status);

        Grid.SetRow(stack, 0);
        Grid.SetColumn(stack, 0);
        _gridHost.Children.Add(stack);
        UpdateFooter();
    }

    private void BuildMessage(string text)
    {
        _gridHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _gridHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TextBlock block = new()
        {
            Text = text,
            FontSize = 12,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(block, 0);
        Grid.SetColumn(block, 0);
        _gridHost.Children.Add(block);
        UpdateFooter();
    }
}
