using System;
using System.Collections.Generic;
using System.Globalization;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.UI;
using WinRT.Interop;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace DeskBox.Views;

/// <summary>
/// The desktop drawer: one glass bead that expands into a full ring of sectors,
/// one sector per格子, all of them together forming a circle.
///
/// Geometry rule: nothing is hard-coded in DIP. The ball sits at the centre of
/// the window's real client area, and the ring radius is derived from that same
/// area, so DPI scaling can never push the ball out of the frame (which is what
/// made earlier builds show an empty rectangle).
///
/// 1.6.0: the ball is a glass bead that follows the widget material instead of
/// a saturated solid circle, supports five collapsed shapes, can be hidden
/// entirely, carries a badge, accepts dropped files as "staging", and offers a
/// full action menu (search / organize / notes / shortcuts / screenshots /
/// countdown).
/// </summary>
public sealed partial class WidgetDrawerWindow : Window
{
    private const double WindowPadding = 14;

    // 扇形几何与窗口尺寸解耦（照 ClassFlow 的 RadialMenu：半径是常量、
    // 圆心固定在画布正中）。窗口装不下时整块等比缩放，绝不再按窗口重算半径
    // —— 重算 + 边缘钳制会把环拉成非圆/偏心，扇片就变成一条条斜片。
    private const double FanInner = 44;
    private const double FanOuter = 132;
    private const double ExpandedSize = (FanOuter + WindowPadding) * 2; // 292 DIP
    private const double FanGapDegrees = 2.2;

    private const int ScreenMargin = 18;
    private const int EdgeSnapDistance = 110;

    private readonly WidgetDrawerSettings _settings;

    private bool _expanded;
    private bool _pointerDown;
    private bool _collapseHandledPress;
    /// <summary>Press started on the bead while the ring is open. Release
    /// without movement collapses the ring; movement drags the whole ring.</summary>
    private bool _centerPressWhileExpanded;
    private double _layoutDiameterDip;
    private bool _dragging;
    private Windows.Foundation.Point _pressPoint;
    private PointInt32 _pressWindowOrigin;
    private double _scale = 1.0;

    // 扇片不再开关格子：它们是拖放目标。拖拽进来时展开整圈，松手按落点
    // （角度 + 半径）决定动作，而不是依赖 DragOver 有没有及时到达 ——
    // OLE 拖放中途改变窗口尺寸时，命中测试可能不会立刻重跑。
    private bool _dragActive;
    private bool _expandedForDrag;
    private string? _hoverActionId;
    private readonly List<FanSectorVisual> _sectorVisuals = new();

    private sealed record FanSectorVisual(string Id, XamlPath Path, TextBlock Label);

    // 1.6.0: the collapsed geometry is per-shape instead of two constants.
    private double _ballW = WidgetDrawerSettings.DefaultBallDiameter;
    private double _ballH = WidgetDrawerSettings.DefaultBallDiameter;
    private double _closedSize = WidgetDrawerSettings.DefaultBallDiameter + 8;

    private DispatcherQueueTimer? _idleTimer;
    private DispatcherQueueTimer? _badgeTimer;
    private DispatcherQueueTimer? _flashTimer;
    private bool _badgeBusy;

    /// <summary>收起态球心（屏幕像素）。展开时如果为了让整圈扇片可见而把窗口
    /// 推进了屏幕内，收起就按这个中心放回去，球不会越点越往中间跑。</summary>
    private PointInt32? _restoreCenterPixels;
    private bool _sizeEnforcing;

    // Recomputed每一次 from the actual client size.
    private double _centerX = 32;
    private double _centerY = 32;
    private double _fanInner = 44;
    private double _fanOuter = FanOuter;

    public WidgetDrawerWindow(WidgetDrawerSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Title = "格子抽屉";

        ApplyShape();

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(null);
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] extend-into-titlebar failed: {ex.Message}");
        }

        ApplyColor();
        ApplyChrome();

        var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            if (_expanded)
            {
                SetExpanded(false);
                args.Handled = true;
            }
        };
        RootGrid.KeyboardAccelerators.Add(escape);

        RootGrid.Loaded += (_, _) =>
        {
            UpdateStateDot();
            UpdateTooltip();

            // XamlRoot 只有 Loaded 之后才有 RasterizationScale。构造时读到的
            // 永远是 1.0，于是在 150%/200% 缩放的机器上会按 64 像素建窗，
            // 实际只有 43 DIP —— 56 DIP 的球比窗口还大，被裁成「缺一块的球」。
            // 这是「同一版本，宿舍的球不完整」的根因。
            ApplyScale();
            EnsureWindowPixels();
            Relayout();
            ApplyWindowClip();
            UpdateCountdownGlyph();
            StartBadgeTimer();
            _ = RefreshBadgeAsync();
        };

        RootGrid.SizeChanged += (_, _) =>
        {
            Relayout();
            if (_expanded)
            {
                BuildFan();
            }

            // 关键：窗口真正改变尺寸后必须重新裁圆。
            // SetExpanded/ShowAtSavedSpot 里调 Resize 时 Size 还没生效，
            // 那次裁出来的圆偏小 → 球被裁成"剩一部分"。这里用生效后的真实尺寸重裁。
            ApplyWindowClip();
        };
    }

    public IntPtr WindowHandle
    {
        get
        {
            try
            {
                return WindowNative.GetWindowHandle(this);
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }
    }

    private AppWindow GetAppWindow()
    {
        IntPtr hWnd = WindowNative.GetWindowHandle(this);
        return AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hWnd));
    }

    // ------------------------------------------------------------------ shape

    /// <summary>形态只影响「收起」时的球和窗口：展开永远是固定的 292 DIP 圆环。</summary>
    private void ApplyShape()
    {
        switch ((_settings.Shape ?? "ball").ToLowerInvariant())
        {
            case "large":
                _ballW = 64;
                _ballH = 64;
                break;
            case "small":
                _ballW = 44;
                _ballH = 44;
                break;
            case "dot":
                _ballW = 36;
                _ballH = 36;
                break;
            case "strip":
                // 竖向胶囊：贴屏幕边时像一条玻璃边，点开仍是完整圆环。
                _ballW = 18;
                _ballH = 52;
                break;
            default:
                double size = Math.Clamp(_settings.Size, 28, 96);
                _ballW = size;
                _ballH = size;
                break;
        }

        _closedSize = Math.Max(_ballW, _ballH) + 8;
        _layoutDiameterDip = _closedSize;

        Ball.Width = _ballW;
        Ball.Height = _ballH;
        BallHighlight.Width = _ballW;
        BallHighlight.Height = _ballH;
        BallRing.Width = _ballW + 4;
        BallRing.Height = _ballH + 4;
        BallGlyph.FontSize = Math.Clamp(Math.Min(_ballW, _ballH) * 0.30, 10, 22);

        double badge = Math.Clamp(Math.Min(_ballW, _ballH) * 0.34, 14, 20);
        Badge.Width = badge;
        Badge.Height = badge;
        Badge.CornerRadius = new CornerRadius(badge / 2);
        BadgeText.FontSize = badge < 17 ? 9 : 10;
    }

    private bool IsStripShape() =>
        string.Equals(_settings.Shape, "strip", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ chrome

    private void ApplyChrome()
    {
        try
        {
            AppWindow appWindow = GetAppWindow();
            WindowShellState.TryApplyBorderlessOverlappedPresenter(appWindow);
            WindowShellState.TryHideFromSwitchers(appWindow);
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = true;
            }

            AppBranding.ApplyWindowIcon(appWindow);

            // A borderless overlapped window still gets a 1px frame from DWM, and
            // that frame is what用户 saw as「白色矩形框」. WS_POPUP has no frame at
            // all, so the window becomes pure pixels.
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            int style = Win32Helper.GetWindowLong(hWnd, Win32Helper.GWL_STYLE);
            style &= ~(Win32Helper.WS_CAPTION | Win32Helper.WS_THICKFRAME | Win32Helper.WS_BORDER);
            style |= Win32Helper.WS_POPUP | 0x10000000; // WS_POPUP | WS_VISIBLE
            Win32Helper.SetWindowLongPtr(hWnd, Win32Helper.GWL_STYLE, (IntPtr)style);

            int noBorder = -2; // DWMWA_COLOR_NONE
            Win32Helper.TrySetDwmWindowAttribute(hWnd, Win32Helper.DWMWA_BORDER_COLOR, ref noBorder);

            int doNotRound = 1; // DWMWCP_DONOTROUND
            Win32Helper.TrySetDwmWindowAttribute(hWnd, Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE, ref doNotRound);
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] chrome setup failed: {ex.Message}");
        }

        try
        {
            SystemBackdrop = new WinUIEx.TransparentTintBackdrop(Colors.Transparent);
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] transparent backdrop unavailable: {ex.Message}");
            SystemBackdrop = null;
        }

        ApplyScale();
        ApplyWindowClip();
    }

    /// <summary>
    /// 窗口裁切：收起是球的形状（普通球=内切圆；贴边条=圆角胶囊），展开是
    /// 「扇形外径 + 1.5 DIP」的圆。
    ///
    /// 早前「展开一律不裁」是为了避免把扇片切成斜条；那条约束来自用
    /// DIP×_scale 或屏幕边缘距离去算半径。现在半径只由窗口真实像素决定
    /// （且恒 ≤ min(宽,高)/2），扇形画在窗口正中、直径不超过窗口，
    /// 所以既不会切到扇片，也不会在背景材质不透明的机器上露出深色方框
    /// —— 宿舍那台的「方形底板 + 扇片被切」就是这两件事。
    /// </summary>
    private void ApplyWindowClip()
    {
        try
        {
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            if (hWnd == IntPtr.Zero)
            {
                return;
            }

            SizeInt32 size = GetAppWindow().Size;
            int w = size.Width;
            int h = size.Height;
            int inscribed = Math.Min(w, h) / 2;
            if (inscribed < 8)
            {
                App.Log($"[Drawer] clip skipped: win={w}x{h}");
                return;
            }

            int cx = w / 2;
            int cy = h / 2;

            if (!_expanded && IsStripShape())
            {
                int rw = Math.Max(4, (int)Math.Round(_ballW * _scale));
                int rh = Math.Max(8, (int)Math.Round(_ballH * _scale));
                int corner = Math.Max(2, (int)Math.Round(9 * _scale)) * 2;
                IntPtr pill = Win32Helper.CreateRoundRectRgn(
                    cx - (rw / 2), cy - (rh / 2), cx + (rw / 2) + 1, cy + (rh / 2) + 1, corner, corner);
                bool pillOk = Win32Helper.SetWindowRgn(hWnd, pill, true) != 0;
                App.Log($"[Drawer] clip pill {rw}x{rh} win={w}x{h} scale={_scale:0.##} ok={pillOk}");
                return;
            }

            int radius = inscribed;
            if (_expanded)
            {
                double fanScale = Math.Clamp(_layoutDiameterDip / ExpandedSize, 0.4, 1.0);
                int fanRadius = (int)Math.Round(((FanOuter * fanScale) + 1.5) * _scale);
                if (fanRadius > 0)
                {
                    radius = Math.Min(inscribed, fanRadius);
                }
            }

            IntPtr region = Win32Helper.CreateEllipticRgn(cx - radius, cy - radius, cx + radius, cy + radius);
            bool ok = Win32Helper.SetWindowRgn(hWnd, region, true) != 0;
            App.Log($"[Drawer] clip d={radius * 2} win={w}x{h} expanded={_expanded} scale={_scale:0.##} ok={ok}");
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] window clip failed: {ex.Message}");
        }
    }

    private void ApplyScale()
    {
        try
        {
            _scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        }
        catch (Exception)
        {
            _scale = 1.0;
        }

        if (_scale <= 0)
        {
            _scale = 1.0;
        }
    }

    /// <summary>窗口像素尺寸必须等于 DIP × 缩放。构造期拿不到真实缩放时按 1.0 建窗，
    /// 桌面缩放到 150% 的机器上球就会比窗口大。这里以「真实像素 / 实际 DIP」反推
    /// 缩放并纠正一次尺寸，窗口中心不动。</summary>
    private void EnsureWindowPixels()
    {
        if (_sizeEnforcing)
        {
            return;
        }

        try
        {
            AppWindow appWindow = GetAppWindow();
            SizeInt32 size = appWindow.Size;
            double dipWidth = RootGrid.ActualWidth;
            if (dipWidth > 4)
            {
                double derived = size.Width / dipWidth;
                if (derived > 0.5 && derived < 4 && Math.Abs(derived - _scale) > 0.05)
                {
                    App.Log($"[Drawer] scale corrected {_scale:0.###} -> {derived:0.###}");
                    _scale = derived;
                }
            }

            double dip = _expanded ? _layoutDiameterDip : _closedSize;
            int want = (int)Math.Round(dip * _scale);
            if (want < 8 || (size.Width == want && size.Height == want))
            {
                return;
            }

            _sizeEnforcing = true;
            PointInt32 position = appWindow.Position;
            int centerX = position.X + (size.Width / 2);
            int centerY = position.Y + (size.Height / 2);

            RectInt32 work = WorkArea(appWindow);
            int x = Clamp(centerX - (want / 2), work.X, Math.Max(work.X, work.X + work.Width - want));
            int y = Clamp(centerY - (want / 2), work.Y, Math.Max(work.Y, work.Y + work.Height - want));

            appWindow.Resize(new SizeInt32(want, want));
            appWindow.Move(new PointInt32(x, y));
            App.Log($"[Drawer] size corrected {size.Width}x{size.Height} -> {want}px (dip={dip:0.#} scale={_scale:0.###})");
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] size correction failed: {ex.Message}");
        }
        finally
        {
            _sizeEnforcing = false;
        }
    }

    /// <summary>球放在窗口真实中心；扇形画在固定坐标里再等比缩放。</summary>
    private void Relayout()
    {
        // 不能用 ActualWidth：SetExpanded 里 Resize 之后马上布局，框架还没测量，
        // 读到的是旧尺寸（曾把环缩成球内的小圈，表现成“点了没反应”）。
        // 目标尺寸只认 _layoutDiameterDip。
        double windowSide = _layoutDiameterDip > 0 ? _layoutDiameterDip : _closedSize;
        double ballCx = windowSide / 2;
        double ballCy = windowSide / 2;

        // 扇形画在固定的 ExpandedSize 坐标系里，再由 ScaleTransform 等比缩进窗口，
        // 所以圆心永远是 ExpandedSize/2，与窗口实际尺寸无关。
        _centerX = ExpandedSize / 2;
        _centerY = ExpandedSize / 2;
        _fanOuter = FanOuter;
        _fanInner = FanInner;

        double fanScale = Math.Clamp(windowSide / ExpandedSize, 0.4, 1.0);
        FanCanvas.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        FanCanvas.RenderTransform = new ScaleTransform { ScaleX = fanScale, ScaleY = fanScale };

        Canvas.SetLeft(Ball, ballCx - (_ballW / 2));
        Canvas.SetTop(Ball, ballCy - (_ballH / 2));
        Canvas.SetLeft(BallHighlight, ballCx - (_ballW / 2));
        Canvas.SetTop(BallHighlight, ballCy - (_ballH / 2));
        Canvas.SetLeft(BallRing, ballCx - (BallRing.Width / 2));
        Canvas.SetTop(BallRing, ballCy - (BallRing.Height / 2));

        BallGlyph.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(BallGlyph, ballCx - (BallGlyph.DesiredSize.Width / 2));
        Canvas.SetTop(BallGlyph, ballCy - (BallGlyph.DesiredSize.Height / 2));

        Canvas.SetLeft(StateDot, ballCx + (_ballW / 2) - 8);
        Canvas.SetTop(StateDot, ballCy + (_ballH / 2) - 8);

        Canvas.SetLeft(Badge, ballCx + (_ballW / 2) - Badge.Width + 2);
        Canvas.SetTop(Badge, ballCy - (_ballH / 2) - 2);
    }

    private SizeInt32 WindowSizePixels(double diameterDip)
    {
        int pixels = (int)Math.Round(diameterDip * _scale);
        return new SizeInt32(pixels, pixels);
    }

    /// <summary>展开窗口的几何：扇形几何是常量（292 DIP），窗口永远按这个尺寸
    /// 摆放；屏幕装不下时把窗口（连同球）往工作区里推，而不是把扇片缩小。
    /// 缩小会让整块 ScaleTransform 把 12 DIP 的文字压到 7 DIP，看不清
    /// —— 这正是「宿舍那边展开后扇片名字被截、两边样式不一样」的来源。</summary>
    private readonly record struct ExpandedPlan(double DiameterDip, int SizePixels, int OriginX, int OriginY);

    private RectInt32 WorkArea(AppWindow appWindow) =>
        DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;

    private ExpandedPlan PlanExpandedWindow(AppWindow appWindow, PointInt32 ballCenter)
    {
        RectInt32 work = WorkArea(appWindow);
        int margin = (int)Math.Round(ScreenMargin * _scale);

        // 尺寸只跟屏幕本身有关，与球的位置无关：正常显示器永远 292 DIP，
        // 两台机器上看到的环一模一样；只有工作区小于整圈时才按屏缩。
        double workDip = Math.Min(work.Width / _scale, work.Height / _scale);
        double dip = Math.Max(160, Math.Min(ExpandedSize, workDip - (ScreenMargin * 2)));

        int sizePixels = Math.Max(8, (int)Math.Round(dip * _scale));
        int x = ballCenter.X - (sizePixels / 2);
        int y = ballCenter.Y - (sizePixels / 2);

        // 装不下就整体推进工作区：球跟着挪，但整圈扇片一定完整可见。
        int maxX = work.X + work.Width - sizePixels;
        int maxY = work.Y + work.Height - sizePixels;
        if (maxX > work.X)
        {
            x = Clamp(x, work.X, maxX);
        }

        if (maxY > work.Y)
        {
            y = Clamp(y, work.Y, maxY);
        }

        int minX = work.X + margin;
        int minY = work.Y + margin;
        if (maxX - margin > minX)
        {
            x = Clamp(x, minX, maxX - margin);
        }

        if (maxY - margin > minY)
        {
            y = Clamp(y, minY, maxY - margin);
        }

        return new ExpandedPlan(dip, sizePixels, x, y);
    }

    public void ShowAtSavedSpot()
    {
        ApplyShape();
        ApplyScale();

        AppWindow appWindow = GetAppWindow();
        RectInt32 work = DisplayArea
            .GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary)
            .WorkArea;

        int size = (int)Math.Round(_closedSize * _scale);
        int margin = (int)Math.Round(ScreenMargin * _scale);

        int x = work.X + work.Width - size - margin;
        int y = work.Y + work.Height - size - margin;

        if (_settings.X is double savedX && _settings.Y is double savedY)
        {
            x = Clamp((int)savedX, work.X, work.X + work.Width - size);
            y = Clamp((int)savedY, work.Y, work.Y + work.Height - size);
        }

        try
        {
            appWindow.Resize(WindowSizePixels(_closedSize));
            appWindow.Move(new PointInt32(x, y));
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] positioning failed: {ex.Message}");
        }

        Activate();
    }

    /// <summary>Ball centre in screen pixels, derived from what the window
    /// actually is right now. Using ClosedSize here while the window was
    /// expanded is what made the bead creep up-left on every click.</summary>
    private PointInt32 BallCenterPixels(AppWindow appWindow)
    {
        PointInt32 position = appWindow.Position;
        SizeInt32 size = appWindow.Size;
        return new PointInt32(position.X + (size.Width / 2), position.Y + (size.Height / 2));
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded)
        {
            return;
        }

        AppWindow appWindow = GetAppWindow();
        PointInt32 center = BallCenterPixels(appWindow);

        _expanded = expanded;

        double diameter;
        int originX;
        int originY;

        if (expanded)
        {
            // 记住收起时的球心，收起时按它放回去。
            _restoreCenterPixels = center;
            ExpandedPlan plan = PlanExpandedWindow(appWindow, center);
            diameter = plan.DiameterDip;
            originX = plan.OriginX;
            originY = plan.OriginY;
        }
        else
        {
            diameter = _closedSize;
            int closedPixels = Math.Max(8, (int)Math.Round(diameter * _scale));
            PointInt32 back = _restoreCenterPixels ?? center;
            _restoreCenterPixels = null;

            // 收起回到原来的球心（展开时可能为了完整显示被推进过屏幕内）。
            RectInt32 work = WorkArea(appWindow);
            int maxX = work.X + work.Width - closedPixels;
            int maxY = work.Y + work.Height - closedPixels;
            originX = back.X - (closedPixels / 2);
            originY = back.Y - (closedPixels / 2);
            originX = Clamp(originX, work.X, Math.Max(work.X, maxX));
            originY = Clamp(originY, work.Y, Math.Max(work.Y, maxY));
        }

        _layoutDiameterDip = diameter;

        try
        {
            appWindow.Resize(WindowSizePixels(diameter));
            appWindow.Move(new PointInt32(originX, originY));
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] resize failed: {ex.Message}");
        }

        Relayout();

        if (expanded)
        {
            BuildFan();
        }
        else
        {
            FanCanvas.Children.Clear();
        }

        Animate(FanCanvas, expanded ? 1 : 0);
        ApplyWindowClip();
    }

    // ------------------------------------------------------------- appearance

    /// <summary>
    /// 玻璃渲染：底色取玻璃主题（Fill=null 时跟随格子材料色），不透明度写进
    /// 颜色 alpha 而不是 Ball.Opacity —— 后者会把描边一起淡掉，球就显得脏。
    /// </summary>
    private void ApplyColor()
    {
        DrawerGlassPreset preset = _settings.ResolveGlass();
        bool dark = RootGrid.ActualTheme != ElementTheme.Light;

        Color baseColor = preset.Fill is null
            ? (dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243))
            : ParseColor(preset.Fill);

        byte alpha = (byte)Math.Clamp(Math.Round(preset.Opacity * 255), 40, 255);
        Color top = WithAlpha(Shade(baseColor, 1.16), alpha);
        Color bottom = WithAlpha(Shade(baseColor, 0.86), alpha);

        Ball.Fill = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0.25, 0.05),
            EndPoint = new Windows.Foundation.Point(0.75, 0.95),
            GradientStops =
            {
                new GradientStop { Color = top, Offset = 0 },
                new GradientStop { Color = bottom, Offset = 1 }
            }
        };

        BallHighlight.Opacity = Math.Clamp(preset.Highlight, 0, 1);

        Color rim = preset.LightText
            ? Color.FromArgb(255, 255, 255, 255)
            : Color.FromArgb(255, 28, 28, 28);

        Ball.Stroke = new SolidColorBrush(WithAlpha(rim, (byte)Math.Clamp(Math.Round(preset.RimOpacity * 255), 20, 255)));
        Ball.StrokeThickness = 1;
        BallRing.Stroke = new SolidColorBrush(WithAlpha(rim, 70));
        BallGlyph.Foreground = new SolidColorBrush(preset.LightText
            ? Colors.White
            : Color.FromArgb(255, 28, 28, 28));
        Badge.BorderBrush = new SolidColorBrush(WithAlpha(rim, 200));
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color ParseColor(string hex)
    {
        string value = (hex ?? string.Empty).TrimStart('#');
        if (value.Length == 6 &&
            byte.TryParse(value.Substring(0, 2), NumberStyles.HexNumber, null, out byte r) &&
            byte.TryParse(value.Substring(2, 2), NumberStyles.HexNumber, null, out byte g) &&
            byte.TryParse(value.Substring(4, 2), NumberStyles.HexNumber, null, out byte b))
        {
            return Color.FromArgb(255, r, g, b);
        }

        return Color.FromArgb(255, 32, 32, 32);
    }

    private void UpdateStateDot()
    {
        bool parked = (Application.Current as App)?.WidgetsParked == true;
        StateDot.Visibility = parked ? Visibility.Visible : Visibility.Collapsed;
        if (BallGlyph.Text != "记" && (Application.Current as App)?.DrawerCountdownRemaining is null)
        {
            BallGlyph.Text = parked ? "呼" : "格";
        }
    }

    public void Refresh()
    {
        ApplyShape();
        ApplyColor();
        UpdateStateDot();
        UpdateTooltip();
        Relayout();
        ApplyWindowClip();
        if (_expanded)
        {
            BuildFan();
        }

        _ = RefreshBadgeAsync();
    }

    /// <summary>球面上闪一个字符（✓ / 存 / 铃 / !），约 2 秒后恢复。</summary>
    public void FlashGlyph(string glyph, string title, string? detail)
    {
        try
        {
            if (!string.IsNullOrEmpty(glyph))
            {
                BallGlyph.Text = glyph.Substring(0, 1);
            }

            var panel = new StackPanel { Spacing = 3 };
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            if (!string.IsNullOrWhiteSpace(detail))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = detail,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 280,
                    Foreground = new SolidColorBrush(Color.FromArgb(200, 120, 120, 120))
                });
            }

            ToolTipService.SetToolTip(RootGrid, panel);

            _flashTimer ??= DispatcherQueue.CreateTimer();
            _flashTimer.Interval = TimeSpan.FromSeconds(2.2);
            _flashTimer.IsRepeating = false;
            _flashTimer.Tick -= FlashTimerTick;
            _flashTimer.Tick += FlashTimerTick;
            _flashTimer.Stop();
            _flashTimer.Start();
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] flash glyph failed: {ex.Message}");
        }
    }

    private void FlashTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        UpdateStateDot();
        UpdateTooltip();
    }

    /// <summary>设置页改完从磁盘重载后调用：形态/玻璃/尺寸可能都变了。</summary>
    public void ApplySettingsFromStore()
    {
        ApplyShape();
        ApplyColor();
        Relayout();
        ApplyWindowClip();
        ShowAtSavedSpot();
        _ = RefreshBadgeAsync();
    }

    /// <summary>全屏应用（投影/视频）在前台时把球藏起来——是隐藏窗口，不是关掉。</summary>
    public void SetTemporarilyHidden(bool hidden)
    {
        try
        {
            AppWindow appWindow = GetAppWindow();
            if (hidden)
            {
                appWindow.Hide();
                return;
            }

            appWindow.Show();
            Activate();
            ApplyWindowClip();
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] temporary hide failed: {ex.Message}");
        }
    }

    private void Animate(UIElement target, double to)
    {
        try
        {
            var storyboard = new Storyboard();
            var fade = new DoubleAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(140)),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(fade, target);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);
            storyboard.Begin();
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] fade failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ badge

    private void StartBadgeTimer()
    {
        try
        {
            _badgeTimer ??= DispatcherQueue.CreateTimer();
            _badgeTimer.Interval = TimeSpan.FromSeconds(60);
            _badgeTimer.IsRepeating = true;
            _badgeTimer.Tick += (_, _) => _ = RefreshBadgeAsync();
            _badgeTimer.Start();
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] badge timer failed: {ex.Message}");
        }
    }

    /// <summary>角标的数字 = 待归类文件数 + 暂存文件数；备忘未完成数优先显示。</summary>
    private async System.Threading.Tasks.Task RefreshBadgeAsync()
    {
        if (_badgeBusy)
        {
            return;
        }

        _badgeBusy = true;
        try
        {
            int notes = _settings.OpenNoteCount();
            int pending = 0;
            int staged = 0;
            CompanionStatus status = await CompanionClient.GetStatusAsync();
            if (status.Reachable)
            {
                pending = status.PendingCount;
            }

            if (Application.Current is App app)
            {
                app.NoteDrawerStatus(status);
                staged = await app.CountStagedFilesAsync();
            }

            int total = notes + pending + staged;
            if (total > 0)
            {
                Badge.Visibility = Visibility.Visible;
                BadgeText.Text = total > 99 ? "99+" : total.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                Badge.Visibility = Visibility.Collapsed;
            }

            UpdateTooltip(status, pending, staged, notes);
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] badge refresh failed: {ex.Message}");
        }
        finally
        {
            _badgeBusy = false;
        }
    }

    // ----------------------------------------------------------------- tooltip

    private void UpdateTooltip(
        CompanionStatus? status = null,
        int pending = 0,
        int staged = 0,
        int notes = 0)
    {
        try
        {
            var panel = new StackPanel { Spacing = 3 };
            panel.Children.Add(new TextBlock
            {
                Text = status is { Reachable: true }
                    ? "最近整理 " + status.LastApplyCount + " 个"
                    : "管家引擎未运行",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });

            if (pending > 0)
            {
                panel.Children.Add(new TextBlock { Text = "待归类 " + pending + " 个", FontSize = 12 });
            }

            if (staged > 0)
            {
                panel.Children.Add(new TextBlock { Text = "暂存 " + staged + " 个", FontSize = 12 });
            }

            if (notes > 0)
            {
                panel.Children.Add(new TextBlock { Text = "备忘 " + notes + " 条", FontSize = 12 });
            }

            if (status is { Reachable: true } && !string.IsNullOrWhiteSpace(status.LastError))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "错误：" + status.LastError,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 214, 90, 90)),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 260
                });
            }

            panel.Children.Add(new TextBlock
            {
                Text = "左键展开功能　拖文件到扇片　右键更多",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(180, 120, 120, 120)),
                Margin = new Thickness(0, 3, 0, 0)
            });

            ToolTipService.SetToolTip(RootGrid, panel);
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] tooltip failed: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------- ring

    private void BuildFan()
    {
        FanCanvas.Children.Clear();
        _sectorVisuals.Clear();
        _hoverActionId = null;

        IReadOnlyList<DrawerFanAction> actions = DrawerFanActions.All;
        if (actions.Count == 0)
        {
            return;
        }

        int count = actions.Count;
        double step = 360.0 / count;
        double span = Math.Max(8, step - FanGapDegrees);

        for (int i = 0; i < count; i++)
        {
            DrawerFanAction action = actions[i];
            double mid = DrawerFanActions.MidAngle(i, count);
            // 拖拽进行中：不接受文件的扇片暗下去，一眼看出能往哪放。
            bool active = !_dragActive || action.AcceptsFiles;

            var sector = new XamlPath
            {
                Data = BuildSector(mid - (span / 2), mid + (span / 2)),
                Fill = BuildSectorBrush(mid, active),
                Stroke = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
                StrokeThickness = 1,
                Tag = action.Id
            };

            sector.PointerEntered += (_, _) => HighlightFanSector(action.Id);
            sector.PointerExited += (_, _) => HighlightFanSector(null);

            DrawerFanAction captured = action;
            sector.Tapped += async (_, args) =>
            {
                args.Handled = true;
                await RunFanActionAsync(captured);
            };

            FanCanvas.Children.Add(sector);
            TextBlock label = AddSectorLabel(mid, action.Title, active);
            _sectorVisuals.Add(new FanSectorVisual(action.Id, sector, label));
        }
    }

    /// <summary>悬停/拖拽时只改填色与描边，不重建几何（DragOver 一秒能来几十次）。</summary>
    private void HighlightFanSector(string? id)
    {
        if (string.Equals(_hoverActionId, id, StringComparison.Ordinal))
        {
            return;
        }

        _hoverActionId = id;
        int count = _sectorVisuals.Count;
        if (count == 0)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            FanSectorVisual visual = _sectorVisuals[i];
            DrawerFanAction? action = DrawerFanActions.Find(visual.Id);
            if (action is null)
            {
                continue;
            }

            double mid = DrawerFanActions.MidAngle(i, count);
            bool active = !_dragActive || action.AcceptsFiles;
            bool hovered = string.Equals(visual.Id, id, StringComparison.Ordinal);
            visual.Path.Fill = BuildSectorBrush(mid, active, hovered);
            visual.Path.Stroke = new SolidColorBrush(Color.FromArgb(
                hovered ? (byte)235 : active ? (byte)48 : (byte)22, 255, 255, 255));
            visual.Path.StrokeThickness = hovered ? 2 : 1;
            visual.Label.Foreground = new SolidColorBrush(Color.FromArgb(
                hovered || active ? (byte)255 : (byte)120, 255, 255, 255));
        }
    }

    private TextBlock AddSectorLabel(double degrees, string text, bool active)
    {
        // Labels run along their own wedge (radially), so a long name can never
        // spill over the neighbouring sector. The half on the left is rotated
        // 180 more so the characters stay upright.
        double radius = (_fanInner + _fanOuter) / 2;
        double rad = degrees * Math.PI / 180;
        double x = _centerX + (radius * Math.Cos(rad));
        double y = _centerY + (radius * Math.Sin(rad));

        double norm = ((degrees % 360) + 360) % 360;
        bool flip = norm > 90 && norm < 270;
        double span = Math.Max(56, (_fanOuter - _fanInner) - 16);

        var label = new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(active ? (byte)255 : (byte)120, 255, 255, 255)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = span,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = flip ? degrees + 180 : degrees }
        };

        Canvas.SetLeft(label, x - (span / 2));
        Canvas.SetTop(label, y - 10);
        FanCanvas.Children.Add(label);
        return label;
    }

    private Brush BuildSectorBrush(double directionDegrees, bool visible, bool hovered = false)
    {
        DrawerGlassPreset preset = _settings.ResolveGlass();
        bool dark = RootGrid.ActualTheme != ElementTheme.Light;
        Color baseColor = preset.Fill is null
            ? (dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243))
            : ParseColor(preset.Fill);

        Color inner = Shade(baseColor, hovered ? 1.10 : 0.82);
        Color outer = Shade(baseColor, hovered ? 0.86 : 0.56);
        byte alpha = hovered ? (byte)245 : visible ? (byte)238 : (byte)150;

        double rad = directionDegrees * Math.PI / 180;
        double cx = Math.Cos(rad) / 2;
        double cy = Math.Sin(rad) / 2;

        return new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0.5 - cx, 0.5 - cy),
            EndPoint = new Windows.Foundation.Point(0.5 + cx, 0.5 + cy),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(alpha, inner.R, inner.G, inner.B), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(alpha, outer.R, outer.G, outer.B), Offset = 1 }
            }
        };
    }

    private static Color Shade(Color color, double factor)
    {
        return Color.FromArgb(
            color.A,
            (byte)Math.Clamp(color.R * factor, 0, 255),
            (byte)Math.Clamp(color.G * factor, 0, 255),
            (byte)Math.Clamp(color.B * factor, 0, 255));
    }

    private Geometry BuildSector(double a0, double a1)
    {
        Windows.Foundation.Point innerStart = PointAt(_fanInner, a0);
        Windows.Foundation.Point outerStart = PointAt(_fanOuter, a0);
        Windows.Foundation.Point outerEnd = PointAt(_fanOuter, a1);
        Windows.Foundation.Point innerEnd = PointAt(_fanInner, a1);

        var figure = new PathFigure { StartPoint = innerStart, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment { Point = outerStart });
        figure.Segments.Add(new ArcSegment
        {
            Point = outerEnd,
            Size = new Windows.Foundation.Size(_fanOuter, _fanOuter),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise
        });
        figure.Segments.Add(new LineSegment { Point = innerEnd });
        figure.Segments.Add(new ArcSegment
        {
            Point = innerStart,
            Size = new Windows.Foundation.Size(_fanInner, _fanInner),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private Windows.Foundation.Point PointAt(double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Windows.Foundation.Point(
            _centerX + (radius * Math.Cos(rad)),
            _centerY + (radius * Math.Sin(rad)));
    }

    // ---------------------------------------------------------------- pointer

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        StopIdleTimer();
        if (_dragActive)
        {
            // 拖拽可能在窗口外被取消，那样不会再有 Drop；回到正常外观。
            _dragActive = false;
            if (_expanded)
            {
                BuildFan();
            }
        }

        if (!_expanded)
        {
            Animate(BallLayer, 1.0);
        }
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging && !_expanded)
        {
            if (_settings.IdleFade)
            {
                StartIdleTimer();
            }
            else
            {
                Animate(BallLayer, 1.0);
            }
        }
    }

    private void StartIdleTimer()
    {
        try
        {
            _idleTimer ??= DispatcherQueue.CreateTimer();
            _idleTimer.Interval = TimeSpan.FromSeconds(8);
            _idleTimer.IsRepeating = false;
            _idleTimer.Tick += (_, _) =>
            {
                if (!_dragging && !_expanded)
                {
                    Animate(BallLayer, 0.35);
                }

                _idleTimer?.Stop();
            };
            _idleTimer.Stop();
            _idleTimer.Start();
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] idle timer failed: {ex.Message}");
        }
    }

    private void StopIdleTimer()
    {
        try
        {
            _idleTimer?.Stop();
        }
        catch (Exception)
        {
            // timers are cosmetic
        }
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_expanded)
        {
            bool onBead = PressIsOnBead(e);
            if (!onBead && IsSectorSource(e.OriginalSource))
            {
                // A press that started on a sector must reach the sector's own
                // tap handler; only presses on empty space close the ring.
                return;
            }

            if (!onBead)
            {
                SetExpanded(false);
                _collapseHandledPress = true;
                e.Handled = true;
                return;
            }

            // The bead sits inside the ring: a click collapses it, a drag moves
            // the whole ring. Which one it is can only be known on release, so
            // the press is captured here and decided there. Deciding on press
            // (the old behaviour) meant a one-pixel wobble on the bead was read
            // as "clicked the neighbouring sector" and the ring never closed.
            _centerPressWhileExpanded = true;
            BeginPressTracking(e);
            return;
        }

        _centerPressWhileExpanded = false;
        BeginPressTracking(e);
    }

    private void BeginPressTracking(PointerRoutedEventArgs e)
    {
        RootGrid.CapturePointer(e.Pointer);
        _pointerDown = true;
        _dragging = false;
        _pressPoint = e.GetCurrentPoint(RootGrid).Position;
        try
        {
            _pressWindowOrigin = GetAppWindow().Position;
        }
        catch (Exception)
        {
            _pressWindowOrigin = new PointInt32(0, 0);
        }
    }

    /// <summary>True when the press landed on the bead at the window centre.
    /// Hit-testing by distance instead of by element is deliberate: the ring's
    /// inner radius follows the window size, so at small sizes the sectors
    /// creep over the bead and claim the click.</summary>
    private bool PressIsOnBead(PointerRoutedEventArgs e)
    {
        Windows.Foundation.Point p = e.GetCurrentPoint(RootGrid).Position;
        double dx = p.X - (RootGrid.ActualWidth / 2);
        double dy = p.Y - (RootGrid.ActualHeight / 2);
        double limitX = (_ballW / 2) + 14;
        double limitY = (_ballH / 2) + 14;
        return ((dx * dx) / (limitX * limitX)) + ((dy * dy) / (limitY * limitY)) <= 1.0;
    }

    private static bool IsSectorSource(object? source)
    {
        DependencyObject? node = source as DependencyObject;
        int guard = 0;
        while (node is not null && guard++ < 12)
        {
            if (node is XamlPath path && path.Tag is string)
            {
                return true;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown)
        {
            return;
        }

        Windows.Foundation.Point current = e.GetCurrentPoint(RootGrid).Position;
        double dx = current.X - _pressPoint.X;
        double dy = current.Y - _pressPoint.Y;
        // 5 DIP of travel starts a drag: 6 felt sticky on a 56 DIP bead and made
        // the whole gesture read as a click.
        if (!_dragging && ((dx * dx) + (dy * dy)) < 25)
        {
            return;
        }

        _dragging = true;
        StopIdleTimer();
        try
        {
            GetAppWindow().Move(new PointInt32(
                _pressWindowOrigin.X + (int)Math.Round(dx * _scale),
                _pressWindowOrigin.Y + (int)Math.Round(dy * _scale)));
        }
        catch (Exception)
        {
            // Dragging is cosmetic; never let it break the click path.
        }
    }

    private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        RootGrid.ReleasePointerCapture(e.Pointer);
        _pointerDown = false;

        if (_collapseHandledPress)
        {
            // This release belongs to the press that collapsed the ring; without
            // this guard the ring reopened immediately, so a second click on the
            // ball could never close it.
            _collapseHandledPress = false;
            Animate(BallLayer, 1.0);
            return;
        }

        if (_dragging)
        {
            _dragging = false;
            _centerPressWhileExpanded = false;
            SnapAndPersistPosition();
            Animate(BallLayer, 1.0);
            return;
        }

        if (_centerPressWhileExpanded)
        {
            // Click on the bead while the ring was open: collapse. The press
            // path already returned, so no sector ever saw this gesture.
            _centerPressWhileExpanded = false;
            SetExpanded(false);
            Animate(BallLayer, 1.0);
            return;
        }

        SetExpanded(true);
        Animate(BallLayer, 1.0);
    }

    private void SnapAndPersistPosition()
    {
        try
        {
            AppWindow appWindow = GetAppWindow();
            RectInt32 work = DisplayArea
                .GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary)
                .WorkArea;
            PointInt32 current = appWindow.Position;
            SizeInt32 real = appWindow.Size;
            int sizeW = real.Width > 0 ? real.Width : (int)Math.Round(_closedSize * _scale);
            int sizeH = real.Height > 0 ? real.Height : (int)Math.Round(_closedSize * _scale);
            int margin = (int)Math.Round(ScreenMargin * _scale);
            int x = current.X;
            int y = current.Y;

            if (current.X - work.X < EdgeSnapDistance)
            {
                x = work.X + margin;
            }
            else if (work.X + work.Width - (current.X + sizeW) < EdgeSnapDistance)
            {
                x = work.X + work.Width - sizeW - margin;
            }

            if (current.Y - work.Y < EdgeSnapDistance)
            {
                y = work.Y + margin;
            }
            else if (work.Y + work.Height - (current.Y + sizeH) < EdgeSnapDistance)
            {
                y = work.Y + work.Height - sizeH - margin;
            }

            appWindow.Move(new PointInt32(x, y));

            // 用户自己拖过球：展开时的“临时推进”作废，收起就落在拖到的地方。
            _restoreCenterPixels = null;

            // Persist the position of the *collapsed* window: a drag can happen
            // while the ring is open, and saving that bigger window's origin
            // made the bead reappear offset by half the ring on the next launch.
            int closed = (int)Math.Round(_closedSize * _scale);
            _settings.X = x + ((sizeW - closed) / 2);
            _settings.Y = y + ((sizeH - closed) / 2);
            _settings.Save();
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] snap failed: {ex.Message}");
        }
    }

    // -------------------------------------------------------------- drag/drop

    /// <summary>
    /// Dragging files onto the bead opens the ring so the file can be aimed at
    /// a sector. Drop is authoritative (the pointer position is read there),
    /// DragOver only paints the highlight — resizing a window in the middle of
    /// an OLE drag is not guaranteed to be re-hit-tested by the shell.
    /// </summary>
    private void RootGrid_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        _dragActive = true;
        if (!_expanded)
        {
            _expandedForDrag = true;
            SetExpanded(true);
        }

        BuildFan();
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;

        Windows.Foundation.Point point = e.GetPosition(RootGrid);
        string? actionId = SectorAtPoint(point);
        HighlightFanSector(actionId);

        DrawerFanAction action = DrawerFanActions.Find(actionId) ?? DrawerFanActions.Default;
        try
        {
            e.DragUIOverride.Caption = "松手：" + action.Hint;
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
        catch (Exception)
        {
            // DragUIOverride is best-effort on some shells.
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        Windows.Foundation.Point dropPoint = e.GetPosition(RootGrid);
        DrawerFanAction action = DrawerFanActions.Find(SectorAtPoint(dropPoint)) ?? DrawerFanActions.Default;
        bool wasExpandedForDrag = _expandedForDrag;
        _dragActive = false;
        _expandedForDrag = false;

        try
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            var paths = new List<string>();
            foreach (IStorageItem item in items)
            {
                if (!string.IsNullOrEmpty(item.Path))
                {
                    paths.Add(item.Path);
                }
            }

            if (paths.Count == 0)
            {
                return;
            }

            await ExecuteFanActionAsync(action, paths);
            App.Log($"[Drawer] drop {paths.Count} → {action.Id}");
            _ = RefreshBadgeAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] drop failed: {ex.Message}");
            (Application.Current as App)?.FlashDrawerGlyph("!", "处理失败", ex.Message);
        }
        finally
        {
            if (wasExpandedForDrag && _expanded)
            {
                SetExpanded(false);
            }
            else if (_expanded)
            {
                BuildFan();
            }
        }
    }

    private async System.Threading.Tasks.Task RunFanActionAsync(DrawerFanAction action)
    {
        await ExecuteFanActionAsync(action, Array.Empty<string>());
        if (_expanded)
        {
            SetExpanded(false);
        }
    }

    private async System.Threading.Tasks.Task ExecuteFanActionAsync(
        DrawerFanAction action,
        IReadOnlyList<string> paths)
    {
        if (Application.Current is not App app)
        {
            return;
        }

        switch (action.Kind)
        {
            case DrawerFanKind.Stage:
                if (paths.Count > 0)
                {
                    await app.StageFilesAsync(paths);
                }
                else
                {
                    await app.OpenDrawerStagingAsync();
                }

                break;

            case DrawerFanKind.Pending:
                if (paths.Count > 0)
                {
                    await app.FileDrawerToPendingAsync(paths);
                }
                else
                {
                    await app.OpenDrawerPendingAsync();
                }

                break;

            case DrawerFanKind.Scan:
                if (paths.Count > 0)
                {
                    await app.ScanImagesToPdfAsync(paths);
                }
                else
                {
                    await app.OpenDrawerScanFolderAsync();
                }

                break;

            case DrawerFanKind.Organize:
                // 整理的是整张桌面；真有文件被投到这里就先暂存，别让用户白拖。
                if (paths.Count > 0)
                {
                    await app.StageFilesAsync(paths);
                }
                else
                {
                    await app.RunDrawerOrganizeAsync();
                }

                break;

            case DrawerFanKind.Screenshot:
                await app.CaptureDrawerScreenshotAsync();
                break;
        }
    }

    /// <summary>扇片命中：与绘制同一坐标系（292 DIP 画布 + 等比缩放），用角度 + 半径判定。</summary>
    private string? SectorAtPoint(Windows.Foundation.Point point)
    {
        IReadOnlyList<DrawerFanAction> actions = DrawerFanActions.All;
        if (actions.Count == 0)
        {
            return null;
        }

        double side = _layoutDiameterDip > 0 ? _layoutDiameterDip : ExpandedSize;
        double fanScale = Math.Clamp(side / ExpandedSize, 0.4, 1.0);
        double dx = (point.X - (RootGrid.ActualWidth / 2)) / fanScale;
        double dy = (point.Y - (RootGrid.ActualHeight / 2)) / fanScale;
        double distance = Math.Sqrt((dx * dx) + (dy * dy));
        if (distance < FanInner || distance > FanOuter + 16)
        {
            return null;
        }

        double degrees = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        int index = DrawerFanActions.IndexAt(degrees, actions.Count);
        return index >= 0 && index < actions.Count ? actions[index].Id : null;
    }

    // ------------------------------------------------------------------- menu

    private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Application.Current is not App app)
        {
            return;
        }

        SetExpanded(false);

        var flyout = new MenuFlyout { ShouldConstrainToRootBounds = false };

        var search = new MenuFlyoutItem { Text = "搜索文件…", Icon = new FontIcon { Glyph = "\uE721" } };
        search.Click += (_, _) => app.OpenSearchPopup();

        var plan = new MenuFlyoutItem { Text = "预览整理" };
        plan.Click += async (_, _) => await app.ShowDrawerOrganizePreviewAsync();

        var organize = new MenuFlyoutItem { Text = "立即整理" };
        organize.Click += async (_, _) => await app.RunDrawerOrganizeAsync();

        var library = new MenuFlyoutItem { Text = "打开资料库", Icon = new FontIcon { Glyph = "\uE8B7" } };
        library.Click += async (_, _) => await app.OpenDrawerLibraryAsync();

        var staging = new MenuFlyoutItem { Text = "打开暂存" };
        staging.Click += async (_, _) => await app.OpenDrawerStagingAsync();

        var note = new MenuFlyoutItem { Text = "记一条备忘…", Icon = new FontIcon { Glyph = "\uE70B" } };
        note.Click += async (_, _) => await AddNoteAsync(app);

        var notes = new MenuFlyoutItem { Text = "查看备忘" };
        notes.Click += async (_, _) => await ShowNotesAsync(app);

        // ── 我的常用 ──────────────────────────────────────────────
        var shortcuts = new MenuFlyoutSubItem { Text = "我的常用", Icon = new FontIcon { Glyph = "\uE735" } };
        foreach (DrawerShortcut shortcut in _settings.Shortcuts)
        {
            DrawerShortcut captured = shortcut;
            var item = new MenuFlyoutItem { Text = captured.Name };
            item.Click += async (_, _) => await app.OpenDrawerShortcutAsync(captured);
            shortcuts.Items.Add(item);
        }

        if (shortcuts.Items.Count == 0)
        {
            shortcuts.Items.Add(new MenuFlyoutItem { Text = "（在设置页添加）", IsEnabled = false });
        }

        // ── 剪贴板历史 ────────────────────────────────────────────
        var clipboard = new MenuFlyoutSubItem { Text = "剪贴板", Icon = new FontIcon { Glyph = "\uE77F" } };
        foreach (string entry in _settings.Clipboard)
        {
            string captured = entry;
            string oneLine = captured.Replace("\r", " ").Replace("\n", " ");
            string preview = oneLine.Length > 20 ? oneLine.Substring(0, 20) + "…" : oneLine;
            var item = new MenuFlyoutItem { Text = preview };
            item.Click += (_, _) => app.CopyClipboardEntry(captured);
            clipboard.Items.Add(item);
        }

        if (clipboard.Items.Count == 0)
        {
            clipboard.Items.Add(new MenuFlyoutItem { Text = "（还没有记录）", IsEnabled = false });
        }
        else
        {
            var clearClipboard = new MenuFlyoutItem { Text = "清空历史" };
            clearClipboard.Click += (_, _) => app.ClearClipboardHistory();
            clipboard.Items.Add(new MenuFlyoutSeparator());
            clipboard.Items.Add(clearClipboard);
        }

        // ── 常用短语 ──────────────────────────────────────────────
        var phrases = new MenuFlyoutSubItem { Text = "常用短语", Icon = new FontIcon { Glyph = "\uE8BD" } };
        foreach (string phrase in _settings.Phrases)
        {
            string captured = phrase;
            string preview = captured.Length > 18 ? captured.Substring(0, 18) + "…" : captured;
            var item = new MenuFlyoutItem { Text = preview };
            item.Click += (_, _) => app.CopyDrawerPhrase(captured);
            phrases.Items.Add(item);
        }

        if (phrases.Items.Count == 0)
        {
            phrases.Items.Add(new MenuFlyoutItem { Text = "（在设置页添加）", IsEnabled = false });
        }

        // ── 最近整理 ──────────────────────────────────────────────
        var recent = new MenuFlyoutSubItem { Text = "最近整理", Icon = new FontIcon { Glyph = "\uE81C" } };
        IReadOnlyList<(string Name, string Folder)> recentItems = app.DrawerRecentMoves();
        foreach ((string name, string folder) in recentItems)
        {
            string capturedFolder = folder;
            var item = new MenuFlyoutItem { Text = name };
            item.Click += (_, _) => app.OpenDrawerFolder(capturedFolder);
            recent.Items.Add(item);
        }

        if (recent.Items.Count == 0)
        {
            recent.Items.Add(new MenuFlyoutItem { Text = "（还没有整理记录）", IsEnabled = false });
        }

        var capture = new MenuFlyoutItem { Text = "截图（存到暂存）", Icon = new FontIcon { Glyph = "\uE722" } };
        capture.Click += async (_, _) => await app.CaptureDrawerScreenshotAsync();

        var timer = new MenuFlyoutItem { Text = "考试倒计时…", Icon = new FontIcon { Glyph = "\uE916" } };
        timer.Click += async (_, _) => await StartCountdownAsync();

        var raiseAll = new MenuFlyoutItem { Text = "呼出全部格子" };
        raiseAll.Click += async (_, _) =>
        {
            await app.RaiseAllWidgetsAsync();
            UpdateStateDot();
        };

        var hideAll = new MenuFlyoutItem { Text = "收起全部格子" };
        hideAll.Click += async (_, _) =>
        {
            await app.SetAllWidgetsVisibleAsync(false);
            UpdateStateDot();
        };

        // ── 玻璃主题 ──────────────────────────────────────────────
        var glasses = new MenuFlyoutSubItem { Text = "玻璃主题", Icon = new FontIcon { Glyph = "\uE790" } };
        foreach (DrawerGlassPreset preset in WidgetDrawerSettings.Glasses)
        {
            DrawerGlassPreset captured = preset;
            var item = new MenuFlyoutItem
            {
                Text = string.Equals(captured.Id, _settings.Glass, StringComparison.OrdinalIgnoreCase)
                    ? "✓ " + captured.Name
                    : "　" + captured.Name
            };
            item.Click += (_, _) =>
            {
                _settings.Glass = captured.Id;
                _settings.Save();
                ApplyColor();
                app.RefreshDrawerAppearance();
            };
            glasses.Items.Add(item);
        }

        // ── 形态 ──────────────────────────────────────────────────
        var shapes = new MenuFlyoutSubItem { Text = "形态", Icon = new FontIcon { Glyph = "\uE7C4" } };
        foreach ((string id, string name) in WidgetDrawerSettings.Shapes)
        {
            string capturedId = id;
            var item = new MenuFlyoutItem
            {
                Text = string.Equals(capturedId, _settings.Shape, StringComparison.OrdinalIgnoreCase)
                    ? "✓ " + name
                    : "　" + name
            };
            item.Click += (_, _) =>
            {
                _settings.Shape = capturedId;
                _settings.Save();
                ApplySettingsFromStore();
                app.RefreshDrawerAppearance();
            };
            shapes.Items.Add(item);
        }

        var panel = new MenuFlyoutItem { Text = "打开桌面管家面板" };
        panel.Click += (_, _) => app.OpenCompanionPanelFromDrawer();

        var hide = new MenuFlyoutItem { Text = "隐藏悬浮球", Icon = new FontIcon { Glyph = "\uE7B3" } };
        hide.Click += (_, _) => app.HideWidgetDrawerFromBall();

        flyout.Items.Add(search);
        flyout.Items.Add(plan);
        flyout.Items.Add(organize);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(library);
        flyout.Items.Add(staging);
        flyout.Items.Add(shortcuts);
        flyout.Items.Add(recent);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(note);
        flyout.Items.Add(notes);
        flyout.Items.Add(clipboard);
        flyout.Items.Add(phrases);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(capture);
        flyout.Items.Add(timer);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(raiseAll);
        flyout.Items.Add(hideAll);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(glasses);
        flyout.Items.Add(shapes);
        flyout.Items.Add(panel);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(hide);

        flyout.ShowAt(Ball, new FlyoutShowOptions { Position = e.GetPosition(Ball) });
        e.Handled = true;
    }

    // ------------------------------------------------------------------- notes

    private async System.Threading.Tasks.Task AddNoteAsync(App app)
    {
        SetExpanded(false);
        try
        {
            var box = new TextBox
            {
                PlaceholderText = "例如：明天交控辍保学汇总表",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 96
            };

            var dialog = new ContentDialog
            {
                Title = "记一条备忘",
                Content = box,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            string text = box.Text.Trim();
            if (text.Length == 0)
            {
                return;
            }

            _settings.Notes.Insert(0, new DrawerNote { Text = text });
            _settings.Save();
            _ = RefreshBadgeAsync();
            app.RefreshDrawerAppearance();
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] note dialog failed: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task ShowNotesAsync(App app)
    {
        SetExpanded(false);
        try
        {
            var panel = new StackPanel { Spacing = 6 };
            bool any = false;
            foreach (DrawerNote note in _settings.Notes)
            {
                if (note.Done)
                {
                    continue;
                }

                any = true;
                var check = new CheckBox
                {
                    Content = note.Created + "　" + note.Text,
                    IsChecked = false,
                    Tag = note.Id
                };
                check.Checked += async (sender, args) =>
                {
                    if (sender is CheckBox checkbox && checkbox.Tag is string id)
                    {
                        foreach (DrawerNote target in _settings.Notes)
                        {
                            if (target.Id == id)
                            {
                                target.Done = true;
                                break;
                            }
                        }

                        _settings.Save();
                        await RefreshBadgeAsync();
                    }
                };
                panel.Children.Add(check);
            }

            if (!any)
            {
                panel.Children.Add(new TextBlock { Text = "没有未完成的备忘。", Opacity = 0.7 });
            }

            var dialog = new ContentDialog
            {
                Title = "备忘",
                Content = new ScrollViewer { Content = panel, MaxHeight = 360 },
                PrimaryButtonText = "清除已完成",
                CloseButtonText = "关闭",
                XamlRoot = RootGrid.XamlRoot
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                _settings.Notes.RemoveAll(note => note.Done);
                _settings.Save();
                _ = RefreshBadgeAsync();
                app.RefreshDrawerAppearance();
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] notes dialog failed: {ex.Message}");
        }
    }

    // --------------------------------------------------------------- countdown

    private async System.Threading.Tasks.Task StartCountdownAsync()
    {
        SetExpanded(false);
        try
        {
            var picker = new ComboBox { SelectedIndex = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
            picker.Items.Add("45 分钟");
            picker.Items.Add("60 分钟");
            picker.Items.Add("90 分钟");
            picker.Items.Add("120 分钟");

            var dialog = new ContentDialog
            {
                Title = "考试倒计时",
                Content = picker,
                PrimaryButtonText = "开始",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            int minutes = picker.SelectedIndex switch
            {
                0 => 45,
                2 => 90,
                3 => 120,
                _ => 60
            };

            if (Application.Current is App app)
            {
                app.StartDrawerCountdown(TimeSpan.FromMinutes(minutes));
            }

            UpdateCountdownGlyph();
        }
        catch (Exception ex)
        {
            App.Log($"[Drawer] countdown dialog failed: {ex.Message}");
        }
    }

    /// <summary>倒计时期间球面上显示剩余分钟数；由 App 的计时器每秒推一次。</summary>
    public void UpdateCountdownGlyph()
    {
        if (Application.Current is not App app)
        {
            return;
        }

        TimeSpan? remaining = app.DrawerCountdownRemaining;
        if (remaining is null)
        {
            UpdateStateDot();
            return;
        }

        int minutes = (int)Math.Ceiling(remaining.Value.TotalMinutes);
        BallGlyph.Text = minutes > 99 ? "99" : Math.Max(0, minutes).ToString(CultureInfo.InvariantCulture);
    }
}
