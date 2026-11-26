using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Views;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    private const double CapsuleBarEdgeMargin = 12;

    /// <summary>
    /// Trailing debounce for the post-restore capsule bar consistency check
    /// (feedback 327). All trigger points are completion events that can
    /// burst (topology restore + verification re-run, tray reveal batches),
    /// so the check waits for the burst to end before rearranging once.
    /// </summary>
    internal static readonly TimeSpan CapsuleArrangementConsistencyDelay = TimeSpan.FromMilliseconds(500);

    private readonly Dictionary<string, RectInt32> _lastCapsuleBarBounds =
        new(StringComparer.Ordinal);
    private string _lastEffectiveCapsuleArrangementMode = SettingsService.WidgetCapsuleArrangementFree;
    private string _lastCapsuleBarPlacement = SettingsService.WidgetCapsuleBarPlacementFloating;
    private string _lastCapsuleBarDirection = SettingsService.WidgetCapsuleBarDirectionAuto;
    private string _lastCapsuleBarGeometrySignature = string.Empty;
    private double _lastCapsuleBarSpacing = SettingsService.DefaultWidgetCapsuleBarSpacing;
    private string _lastCapsuleArrangementMemberSignature = string.Empty;
    private bool _isApplyingCapsuleArrangement;
    private int _capsuleArrangementConsistencyTicket;
    private long _capsuleArrangementConsistencyRunCount;
    private CapsuleBarDragSession? _capsuleBarDragSession;

    private void InitializeCapsuleArrangementState()
    {
        AppSettings settings = _settingsService.Settings;
        _lastEffectiveCapsuleArrangementMode = ResolveEffectiveCapsuleArrangementMode();
        _lastCapsuleBarPlacement = SettingsService.NormalizeWidgetCapsuleBarPlacement(
            settings.WidgetCapsuleBarPlacement);
        _lastCapsuleBarDirection = SettingsService.NormalizeWidgetCapsuleBarDirection(
            settings.WidgetCapsuleBarDirection);
        _lastCapsuleBarSpacing = SettingsService.NormalizeWidgetCapsuleBarSpacing(
            settings.WidgetCapsuleBarSpacing);
    }

    internal void RefreshCapsuleBarLayout()
    {
        ApplyCapsuleArrangementIfChanged(force: true);
    }

    /// <summary>
    /// Diagnostic/test observation of how many consistency passes actually
    /// reached the forced bar arrangement (feedback 327).
    /// </summary>
    internal long CapsuleArrangementConsistencyRunCount =>
        Volatile.Read(ref _capsuleArrangementConsistencyRunCount);

    /// <summary>
    /// Schedules the one-shot capsule bar consistency check (feedback 327):
    /// display-topology restores, the deferred startup bounds pass, and tray
    /// reveal batches re-resolve every window independently, which can land
    /// two bar members on the same slot (diagnostics #9/#11 fully overlapping)
    /// with no later event to heal it until a manual drag. The check is
    /// trailing-edge debounced so a burst of completions coalesces into one
    /// forced arrangement pass, and it only constrains bar members — free
    /// capsules are user placements and must never be forced back.
    /// </summary>
    internal void ScheduleCapsuleArrangementConsistencyCheck(string reason)
    {
        if (!HasUiThreadAccess())
        {
            UiDispatch.RunOrDefer(() => ScheduleCapsuleArrangementConsistencyCheck(reason));
            return;
        }

        int ticket = ++_capsuleArrangementConsistencyTicket;
        _ = RunCapsuleArrangementConsistencyCheckAsync(ticket, reason);
    }

    private async Task RunCapsuleArrangementConsistencyCheckAsync(int ticket, string reason)
    {
        try
        {
            await Task.Delay(CapsuleArrangementConsistencyDelay);
            ApplyCapsuleArrangementConsistencyCheck(ticket, reason);
        }
        catch (Exception ex)
        {
            // Fire-and-forget pass: a failing arrangement must never surface
            // as an unobserved task exception; the next completion event
            // schedules a fresh check anyway.
            App.Log($"[CapsuleArrangement] Consistency check failed: {ex.Message}");
        }
    }

    private void ApplyCapsuleArrangementConsistencyCheck(int ticket, string reason)
    {
        if (!HasUiThreadAccess())
        {
            UiDispatch.RunOrDefer(() => ApplyCapsuleArrangementConsistencyCheck(ticket, reason));
            return;
        }

        // A newer completion event superseded this one: its own delayed pass
        // owns the arrangement, so this ticket dissolves into the debounce.
        if (ticket != _capsuleArrangementConsistencyTicket)
        {
            return;
        }

        // Only the bar is a system-owned layout contract. Free capsules are
        // user placements and are deliberately left wherever they restored
        // to (this also skips applying any stale free-placement backups).
        if (ResolveEffectiveCapsuleArrangementMode() != SettingsService.WidgetCapsuleArrangementBar)
        {
            return;
        }

        Interlocked.Increment(ref _capsuleArrangementConsistencyRunCount);
        App.LogVerbose($"[CapsuleArrangement] Consistency check reason={reason}");
        ApplyCapsuleArrangementIfChanged(force: true);
    }

    private void ApplyCapsuleArrangementIfChanged(bool force = false)
    {
        if (!HasUiThreadAccess())
        {
            UiDispatch.RunOrDefer(() => ApplyCapsuleArrangementIfChanged(force));
            return;
        }

        if (_isApplyingCapsuleArrangement || _capsuleBarDragSession is not null)
        {
            return;
        }

        AppSettings settings = _settingsService.Settings;
        string mode = ResolveEffectiveCapsuleArrangementMode();
        string placement = SettingsService.NormalizeWidgetCapsuleBarPlacement(
            settings.WidgetCapsuleBarPlacement);
        string direction = SettingsService.NormalizeWidgetCapsuleBarDirection(
            settings.WidgetCapsuleBarDirection);
        double spacing = SettingsService.NormalizeWidgetCapsuleBarSpacing(
            settings.WidgetCapsuleBarSpacing);
        // 胶囊候选 + 常开/点开的展开卡片：两种成员都占这一列的一格。
        (IReadOnlyList<WidgetConfig> capsuleCandidates, IReadOnlyList<WidgetConfig> expandedMembers) =
            GetColumnCandidates(settings);
        var columnMembers = capsuleCandidates
            .Select(config => new ColumnMember(config, IsExpandedSlot: false))
            .Concat(expandedMembers.Select(config => new ColumnMember(config, IsExpandedSlot: true)))
            .ToList();
        string memberSignature = string.Join('|', columnMembers.Select(member =>
            member.IsExpandedSlot ? $"{member.Config.Id}*" : member.Config.Id));
        string geometrySignature = BuildCapsuleBarGeometrySignature(settings, columnMembers);
        if (!force &&
            string.Equals(mode, _lastEffectiveCapsuleArrangementMode, StringComparison.Ordinal) &&
            string.Equals(placement, _lastCapsuleBarPlacement, StringComparison.Ordinal) &&
            string.Equals(direction, _lastCapsuleBarDirection, StringComparison.Ordinal) &&
            Math.Abs(spacing - _lastCapsuleBarSpacing) <= 0.0001 &&
            string.Equals(
                memberSignature,
                _lastCapsuleArrangementMemberSignature,
                StringComparison.Ordinal) &&
            string.Equals(
                geometrySignature,
                _lastCapsuleBarGeometrySignature,
                StringComparison.Ordinal))
        {
            return;
        }

        string previousMode = _lastEffectiveCapsuleArrangementMode;
        HashSet<string> previouslyConstrainedIds =
            _lastCapsuleBarBounds.Keys.ToHashSet(StringComparer.Ordinal);
        _lastEffectiveCapsuleArrangementMode = mode;
        _lastCapsuleBarPlacement = placement;
        _lastCapsuleBarDirection = direction;
        _lastCapsuleBarSpacing = spacing;
        _lastCapsuleArrangementMemberSignature = memberSignature;
        _lastCapsuleBarGeometrySignature = geometrySignature;

        _isApplyingCapsuleArrangement = true;
        try
        {
            bool changed = CleanupCapsuleArrangementState(settings);
            if (mode == SettingsService.WidgetCapsuleArrangementFree)
            {
                _lastCapsuleBarBounds.Clear();
                if (previousMode != SettingsService.WidgetCapsuleArrangementFree ||
                    settings.WidgetCapsuleFreePlacements.Count > 0)
                {
                    changed |= RestoreFreeCapsulePlacements(settings);
                }
            }
            else
            {
                bool rebuildOrder = previousMode == SettingsService.WidgetCapsuleArrangementFree;
                changed |= ArrangeCapsuleCandidates(
                    settings,
                    columnMembers,
                    placement,
                    direction,
                    spacing,
                    rebuildOrder);
            }

            ClearRetiredCapsuleArrangementConstraints(
                previouslyConstrainedIds,
                mode == SettingsService.WidgetCapsuleArrangementBar
                    ? _lastCapsuleBarBounds.Keys
                    : Array.Empty<string>());

            if (changed)
            {
                _settingsService.SaveDebounced(notifySubscribers: false);
            }
        }
        finally
        {
            _isApplyingCapsuleArrangement = false;
        }
    }

    private void ClearRetiredCapsuleArrangementConstraints(
        IEnumerable<string> previouslyConstrainedIds,
        IEnumerable<string> currentlyConstrainedIds)
    {
        var current = currentlyConstrainedIds.ToHashSet(StringComparer.Ordinal);
        foreach (string id in previouslyConstrainedIds)
        {
            if (!current.Contains(id))
            {
                FindLoadedWindow(id)?.ClearCompactArrangementConstraint();
            }
        }
    }

    internal bool BeginCapsuleBarDrag(string widgetId, bool reorderMember)
    {
        if (_capsuleBarDragSession is not null ||
            ResolveEffectiveCapsuleArrangementMode() != SettingsService.WidgetCapsuleArrangementBar ||
            !_lastCapsuleBarBounds.TryGetValue(widgetId, out RectInt32 activeBounds))
        {
            return false;
        }

        RectInt32 workArea = DisplayArea.GetFromRect(
            activeBounds,
            DisplayAreaFallback.Nearest).WorkArea;
        string workAreaKey = FormatWorkAreaKey(workArea);
        var memberBounds = _lastCapsuleBarBounds
            .Where(entry => FormatWorkAreaKey(DisplayArea.GetFromRect(
                entry.Value,
                DisplayAreaFallback.Nearest).WorkArea) == workAreaKey)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        if (!memberBounds.ContainsKey(widgetId))
        {
            return false;
        }

        AppSettings settings = _settingsService.Settings;
        var memberOrder = settings.WidgetCapsuleBarOrder
            .Where(memberBounds.ContainsKey)
            .ToList();
        foreach (string id in memberBounds.Keys)
        {
            if (!memberOrder.Contains(id, StringComparer.Ordinal))
            {
                memberOrder.Add(id);
            }
        }

        string placement = SettingsService.NormalizeWidgetCapsuleBarPlacement(
            settings.WidgetCapsuleBarPlacement);
        string direction = ResolveBarDirection(
            settings.WidgetCapsuleBarDirection,
            placement,
            workArea);
        string firstId = memberOrder[0];
        RectInt32 firstBounds = memberBounds[firstId];
        WidgetConfig? firstConfig = settings.Widgets.FirstOrDefault(config =>
            string.Equals(config.Id, firstId, StringComparison.Ordinal));
        (PointInt32 anchorPoint, string anchor) = ResolveBarAnchor(
            firstBounds,
            firstConfig?.CompactPlacement?.PositionAnchor ?? firstConfig?.PositionAnchor,
            workArea,
            placement);
        int spacing = Math.Max(0, (int)Math.Round(
            SettingsService.NormalizeWidgetCapsuleBarSpacing(settings.WidgetCapsuleBarSpacing) *
            WidgetPositioningService.GetDpiScale(workArea)));

        _capsuleBarDragSession = new CapsuleBarDragSession(
            widgetId,
            activeBounds,
            memberBounds,
            workArea,
            reorderMember,
            memberOrder,
            direction,
            anchorPoint,
            anchor,
            spacing);
        return true;
    }

    internal bool TryMoveCapsuleBar(
        string widgetId,
        RectInt32 proposedActiveBounds,
        out RectInt32 resolvedActiveBounds)
    {
        resolvedActiveBounds = proposedActiveBounds;
        if (_capsuleBarDragSession is not { } session ||
            !string.Equals(session.ActiveWidgetId, widgetId, StringComparison.Ordinal))
        {
            return false;
        }

        if (session.ReordersMember)
        {
            return TryReorderCapsuleBarMember(
                session,
                proposedActiveBounds,
                out resolvedActiveBounds);
        }

        string placement = SettingsService.NormalizeWidgetCapsuleBarPlacement(
            _settingsService.Settings.WidgetCapsuleBarPlacement);
        int deltaX = proposedActiveBounds.X - session.ActiveStartBounds.X;
        int deltaY = proposedActiveBounds.Y - session.ActiveStartBounds.Y;
        if (placement is SettingsService.WidgetCapsuleBarPlacementTop or
            SettingsService.WidgetCapsuleBarPlacementBottom)
        {
            deltaY = 0;
        }
        else if (placement is SettingsService.WidgetCapsuleBarPlacementLeft or
                 SettingsService.WidgetCapsuleBarPlacementRight)
        {
            deltaX = 0;
        }

        RectInt32 workArea = UpdateDragWorkArea(session, placement, proposedActiveBounds);
        RectInt32 groupBounds = GetUnion(session.MemberStartBounds.Values);
        deltaX = ClampGroupDelta(
            groupBounds.X,
            groupBounds.Width,
            deltaX,
            workArea.X,
            workArea.Width);
        deltaY = ClampGroupDelta(
            groupBounds.Y,
            groupBounds.Height,
            deltaY,
            workArea.Y,
            workArea.Height);

        foreach ((string id, RectInt32 startBounds) in session.MemberStartBounds)
        {
            var target = new RectInt32(
                startBounds.X + deltaX,
                startBounds.Y + deltaY,
                startBounds.Width,
                startBounds.Height);
            _lastCapsuleBarBounds[id] = target;
            if (FindLoadedWindow(id) is { IsCompactArrangementActive: true } window)
            {
                window.PreviewCompactArrangement(target);
            }
        }

        NoteWidgetWindowsMoved();
        resolvedActiveBounds = _lastCapsuleBarBounds[widgetId];
        return true;
    }

    private bool TryReorderCapsuleBarMember(
        CapsuleBarDragSession session,
        RectInt32 proposedActiveBounds,
        out RectInt32 resolvedActiveBounds)
    {
        // Resolve the work area through the same per-frame logic as the
        // move branch; a Floating reorder drag crossing monitors re-anchors
        // the whole bar next to the pointer on the same frame so the drop
        // can never fall back to a slot on the drag-start screen.
        string placement = SettingsService.NormalizeWidgetCapsuleBarPlacement(
            _settingsService.Settings.WidgetCapsuleBarPlacement);
        RectInt32 previousWorkArea = session.WorkArea;
        RectInt32 workArea = UpdateDragWorkArea(session, placement, proposedActiveBounds);
        if (workArea != previousWorkArea)
        {
            ReanchorCapsuleBarToWorkArea(session, workArea, placement, proposedActiveBounds);
        }

        IReadOnlyList<string> reordered = WidgetCapsuleOrderCalculator.MoveToNearestSlot(
            session.MemberOrder,
            session.MemberSlots,
            session.ActiveWidgetId,
            proposedActiveBounds,
            session.Direction);
        if (!session.MemberOrder.SequenceEqual(reordered, StringComparer.Ordinal))
        {
            session.MemberOrder = reordered.ToList();
            AppSettings settings = _settingsService.Settings;
            IReadOnlyList<string> merged = WidgetCapsuleOrderCalculator.MergeGroupOrder(
                settings.WidgetCapsuleBarOrder,
                session.MemberOrder);
            settings.WidgetCapsuleBarOrder.Clear();
            settings.WidgetCapsuleBarOrder.AddRange(merged);

            var items = session.MemberOrder
                .Select(id =>
                {
                    RectInt32 bounds = session.MemberStartBounds[id];
                    return new WidgetCapsuleArrangementItem(id, bounds.Width, bounds.Height);
                })
                .ToList();
            IReadOnlyDictionary<string, RectInt32> arranged =
                WidgetCapsuleArrangementCalculator.Calculate(
                    items,
                    session.WorkArea,
                    session.AnchorPoint,
                    session.PositionAnchor,
                    session.Direction,
                    session.Spacing);
            foreach ((string id, RectInt32 target) in arranged)
            {
                _lastCapsuleBarBounds[id] = target;
                // The dragged capsule tracks the pointer itself; only siblings
                // snap to their slots so it never teleports mid-drag.
                if (!string.Equals(id, session.ActiveWidgetId, StringComparison.Ordinal) &&
                    FindLoadedWindow(id) is { IsCompactArrangementActive: true } window)
                {
                    window.PreviewCompactArrangement(target);
                }
            }

            NoteWidgetWindowsMoved();
        }

        // Keep the active capsule under the pointer every frame. Parking it on
        // its last slot between reorders made it visibly jump instead of drag.
        RectInt32 tracked = ClampBoundsToRect(proposedActiveBounds, workArea);
        if (FindLoadedWindow(session.ActiveWidgetId) is
            { IsCompactArrangementActive: true } activeWindow)
        {
            activeWindow.PreviewCompactArrangement(tracked);
        }

        resolvedActiveBounds = _lastCapsuleBarBounds.TryGetValue(
            session.ActiveWidgetId,
            out RectInt32 activeBounds)
                ? activeBounds
                : session.ActiveStartBounds;
        return true;
    }

    internal void CompleteCapsuleBarDrag(string widgetId)
    {
        if (_capsuleBarDragSession is not { } session ||
            !string.Equals(session.ActiveWidgetId, widgetId, StringComparison.Ordinal))
        {
            return;
        }

        _capsuleBarDragSession = null;
        foreach (string id in session.MemberStartBounds.Keys)
        {
            if (!_lastCapsuleBarBounds.TryGetValue(id, out RectInt32 bounds))
            {
                continue;
            }

            WidgetConfig? config = _settingsService.Settings.Widgets.FirstOrDefault(
                candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (config is null)
            {
                continue;
            }

            config.CompactPlacement = CreatePlacement(bounds);
            FindLoadedWindow(id)?.ApplyCompactArrangement(bounds, constrainSize: true);
            SynchronizeGroupLayoutFromMember(config);
            // Every capsule on the bar is a user placement (spec 5.4): the
            // bar drag can carry capsules across displays, so each one
            // commits its own home/entry update.
            CommitUserPlacement(config, bounds, bounds, WidgetPlacementSource.CapsuleBarDrag, captureIntent: false);
        }

        NoteWidgetWindowsMoved();
        _settingsService.SaveDebounced(notifySubscribers: false);
    }

    internal bool MoveCapsuleBarFromExpandedWidget(
        string widgetId,
        int requestedDeltaX,
        int requestedDeltaY,
        out RectInt32 activeBounds)
    {
        activeBounds = default;
        if (ResolveEffectiveCapsuleArrangementMode() != SettingsService.WidgetCapsuleArrangementBar ||
            !_lastCapsuleBarBounds.TryGetValue(widgetId, out RectInt32 originalActiveBounds))
        {
            return false;
        }

        RectInt32 originalWorkArea = DisplayArea.GetFromRect(
            originalActiveBounds,
            DisplayAreaFallback.Nearest).WorkArea;
        string workAreaKey = FormatWorkAreaKey(originalWorkArea);
        Dictionary<string, RectInt32> memberBounds = _lastCapsuleBarBounds
            .Where(entry => FormatWorkAreaKey(DisplayArea.GetFromRect(
                entry.Value,
                DisplayAreaFallback.Nearest).WorkArea) == workAreaKey)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        if (!memberBounds.ContainsKey(widgetId))
        {
            return false;
        }

        string placement = SettingsService.NormalizeWidgetCapsuleBarPlacement(
            _settingsService.Settings.WidgetCapsuleBarPlacement);
        int deltaX = requestedDeltaX;
        int deltaY = requestedDeltaY;
        if (placement is SettingsService.WidgetCapsuleBarPlacementTop or
            SettingsService.WidgetCapsuleBarPlacementBottom)
        {
            deltaY = 0;
        }
        else if (placement is SettingsService.WidgetCapsuleBarPlacementLeft or
                 SettingsService.WidgetCapsuleBarPlacementRight)
        {
            deltaX = 0;
        }

        RectInt32 proposedActive = new(
            originalActiveBounds.X + deltaX,
            originalActiveBounds.Y + deltaY,
            originalActiveBounds.Width,
            originalActiveBounds.Height);
        RectInt32 workArea = placement == SettingsService.WidgetCapsuleBarPlacementFloating
            ? DisplayArea.GetFromRect(proposedActive, DisplayAreaFallback.Nearest).WorkArea
            : originalWorkArea;
        RectInt32 groupBounds = GetUnion(memberBounds.Values);
        deltaX = ClampGroupDelta(
            groupBounds.X,
            groupBounds.Width,
            deltaX,
            workArea.X,
            workArea.Width);
        deltaY = ClampGroupDelta(
            groupBounds.Y,
            groupBounds.Height,
            deltaY,
            workArea.Y,
            workArea.Height);

        foreach ((string id, RectInt32 bounds) in memberBounds)
        {
            var target = new RectInt32(
                bounds.X + deltaX,
                bounds.Y + deltaY,
                bounds.Width,
                bounds.Height);
            _lastCapsuleBarBounds[id] = target;
            WidgetConfig? config = _settingsService.Settings.Widgets.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (config is not null)
            {
                config.CompactPlacement = CreatePlacement(target);
                SynchronizeGroupLayoutFromMember(config);
            }
            FindLoadedWindow(id)?.ApplyCompactArrangement(target, constrainSize: true);
        }

        NoteWidgetWindowsMoved();
        activeBounds = _lastCapsuleBarBounds[widgetId];
        _settingsService.SaveDebounced(notifySubscribers: false);
        return true;
    }

    private string ResolveEffectiveCapsuleArrangementMode()
    {
        return SettingsService.NormalizeWidgetCapsuleArrangementMode(
            _settingsService.Settings.WidgetCapsuleArrangementMode);
    }

    /// <summary>
    /// 整列成员：普通胶囊，以及常开（CollapseBehavior=Expanded）或用户手动点开
    /// （Click 且 IsCollapsed=false）的卡片——后者按展开尺寸占一格。
    /// Smart/System 的悬停展开不落盘，不占格，否则鼠标划过整列会抖。
    /// </summary>
    private (IReadOnlyList<WidgetConfig> Capsules, IReadOnlyList<WidgetConfig> Expanded) GetColumnCandidates(
        AppSettings settings)
    {
        var capsules = new List<WidgetConfig>();
        var expanded = new List<WidgetConfig>();
        foreach (WidgetConfig config in settings.Widgets.Where(config =>
                     config.IsVisible &&
                     !config.IsDisabled &&
                     WidgetGroupSettings.IsActiveMember(settings, config.Id) &&
                     _widgetRegistry.IsAvailableForSession(config, settings)))
        {
            WidgetCollapseBehavior behavior = WidgetCollapseBehaviorNames.Resolve(
                config,
                settings.WidgetCollapseBehavior);
            bool expandedSlot = behavior == WidgetCollapseBehavior.Expanded ||
                (behavior == WidgetCollapseBehavior.Click && !config.IsCollapsed);
            if (expandedSlot)
            {
                expanded.Add(config);
            }
            else
            {
                capsules.Add(config);
            }
        }

        return (capsules, expanded);
    }

    /// <summary>
    /// 一个竖向列里的成员：普通胶囊，或常开/点开、按展开尺寸占位的卡片。
    /// </summary>
    private readonly record struct ColumnMember(WidgetConfig Config, bool IsExpandedSlot);

    private static string BuildCapsuleBarGeometrySignature(
        AppSettings settings,
        IReadOnlyList<ColumnMember> candidates)
    {
        string widthMode = SettingsService.NormalizeWidgetCompactWidthMode(
            settings.WidgetCompactWidthMode);
        bool usesAlignedWidth = widthMode == SettingsService.WidgetCompactWidthModeAligned;
        string candidateWidths = string.Join(';', candidates.Select(member =>
        {
            WidgetConfig config = member.Config;
            string width = usesAlignedWidth
                ? config.Width.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                : config.CompactWidth?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "auto";
            string slot = member.IsExpandedSlot
                ? "e" + config.Height.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                : "c";
            return $"{config.Id}:{config.WidgetKind}:{width}:{slot}";
        }));
        return string.Join(
            '|',
            settings.WidgetCompactContentMode,
            settings.WidgetCompactHideSensitiveContent,
            widthMode,
            candidateWidths);
    }

    private static bool CleanupCapsuleArrangementState(AppSettings settings)
    {
        bool changed = false;
        var validIds = settings.Widgets
            .Select(config => config.Id)
            .ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int removedOrderEntries = settings.WidgetCapsuleBarOrder.RemoveAll(
            id => !validIds.Contains(id) || !seen.Add(id));
        changed |= removedOrderEntries > 0;
        foreach (string id in settings.WidgetCapsuleFreePlacements.Keys
                     .Where(id => !validIds.Contains(id))
                     .ToList())
        {
            settings.WidgetCapsuleFreePlacements.Remove(id);
            changed = true;
        }

        return changed;
    }

    private bool ArrangeCapsuleCandidates(
        AppSettings settings,
        IReadOnlyList<ColumnMember> members,
        string placement,
        string configuredDirection,
        double logicalSpacing,
        bool rebuildOrder)
    {
        bool changed = false;
        List<string> barOrder = settings.WidgetCapsuleBarOrder;
        var resolvedById = members.ToDictionary(
            member => member.Config.Id,
            member => ResolveColumnCandidate(member),
            StringComparer.Ordinal);
        HashSet<string> expandedIds = members
            .Where(member => member.IsExpandedSlot)
            .Select(member => member.Config.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (ColumnMember member in members)
        {
            if (member.IsExpandedSlot)
            {
                // 展开卡片是用户自己的窗口，不进「浮动胶囊」的位置备份。
                continue;
            }

            if (!settings.WidgetCapsuleFreePlacements.ContainsKey(member.Config.Id))
            {
                settings.WidgetCapsuleFreePlacements[member.Config.Id] =
                    member.Config.CompactPlacement is { } freePlacement
                        ? ClonePlacement(freePlacement)
                        : CreatePlacement(resolvedById[member.Config.Id].Bounds);
                changed = true;
            }
        }

        if (rebuildOrder)
        {
            barOrder.RemoveAll(resolvedById.ContainsKey);
            foreach (ResolvedCapsuleCandidate candidate in resolvedById.Values
                         .OrderBy(item => FormatWorkAreaKey(item.WorkArea), StringComparer.Ordinal)
                         .ThenBy(item => ResolveBarDirection(configuredDirection, placement, item.WorkArea) ==
                             SettingsService.WidgetCapsuleBarDirectionVertical
                                 ? item.Bounds.X
                                 : item.Bounds.Y)
                         .ThenBy(item => ResolveBarDirection(configuredDirection, placement, item.WorkArea) ==
                             SettingsService.WidgetCapsuleBarDirectionVertical
                                 ? item.Bounds.Y
                                 : item.Bounds.X))
            {
                barOrder.Add(candidate.Config.Id);
            }

            changed = true;
        }
        else
        {
            foreach (ColumnMember member in members)
            {
                if (member.IsExpandedSlot ||
                    barOrder.Contains(member.Config.Id, StringComparer.Ordinal))
                {
                    continue;
                }

                barOrder.Add(member.Config.Id);
                changed = true;
            }

            // 展开卡片也要占一格：缺队列条目时按它此刻在屏幕上的上下位置插进去，
            // 一律追加到末尾会让「点开」直接跳到整列最下面。
            foreach (string id in expandedIds
                         .Where(id => !barOrder.Contains(id, StringComparer.Ordinal))
                         .ToList())
            {
                int orderIndex = barOrder.Count;
                if (resolvedById.TryGetValue(id, out ResolvedCapsuleCandidate? self))
                {
                    for (int index = 0; index < barOrder.Count; index++)
                    {
                        if (resolvedById.TryGetValue(
                                barOrder[index],
                                out ResolvedCapsuleCandidate? other) &&
                            other.Bounds.Y > self.Bounds.Y)
                        {
                            orderIndex = index;
                            break;
                        }
                    }
                }

                barOrder.Insert(orderIndex, id);
                changed = true;
            }
        }

        var orderedCandidates = barOrder
            .Where(resolvedById.ContainsKey)
            .Select(id => resolvedById[id])
            .ToList();
        _lastCapsuleBarBounds.Clear();
        foreach (IGrouping<string, ResolvedCapsuleCandidate> group in orderedCandidates.GroupBy(
                     candidate => FormatWorkAreaKey(candidate.WorkArea),
                     StringComparer.Ordinal))
        {
            List<ResolvedCapsuleCandidate> groupMembers = group.ToList();
            if (groupMembers.Count == 0)
            {
                continue;
            }

            ResolvedCapsuleCandidate first = groupMembers[0];
            string direction = ResolveBarDirection(configuredDirection, placement, first.WorkArea);
            // 只有竖列能「占一格、下面的让位」。横排里展开卡片仍然独立摆放，
            // 免得把一整行撑得歪掉。
            List<ResolvedCapsuleCandidate> columnMembers =
                direction == SettingsService.WidgetCapsuleBarDirectionVertical
                    ? groupMembers
                    : groupMembers
                        .Where(item => !expandedIds.Contains(item.Config.Id))
                        .ToList();
            if (columnMembers.Count == 0)
            {
                continue;
            }

            first = columnMembers[0];
            (PointInt32 anchorPoint, string anchor) = ResolveBarAnchor(
                first.Bounds,
                first.Config.CompactPlacement?.PositionAnchor ?? first.Config.PositionAnchor,
                first.WorkArea,
                placement);
            double dpiScale = WidgetPositioningService.GetDpiScale(first.WorkArea);
            int physicalSpacing = Math.Max(0, (int)Math.Round(logicalSpacing * dpiScale));
            var items = columnMembers
                .Select(member => new WidgetCapsuleArrangementItem(
                    member.Config.Id,
                    member.Bounds.Width,
                    member.Bounds.Height))
                .ToList();
            IReadOnlyDictionary<string, RectInt32> arranged =
                WidgetCapsuleArrangementCalculator.Calculate(
                    items,
                    first.WorkArea,
                    anchorPoint,
                    anchor,
                    direction,
                    physicalSpacing);

            foreach (ResolvedCapsuleCandidate member in columnMembers)
            {
                RectInt32 target = arranged[member.Config.Id];
                _lastCapsuleBarBounds[member.Config.Id] = target;
                bool expandedSlot = expandedIds.Contains(member.Config.Id);
                bool moved = !BoundsEqual(member.Bounds, target);
                if (moved)
                {
                    if (expandedSlot)
                    {
                        // 展开卡片按列宽排进队列：宽高和位置都交给这一列，
                        // 让位由同一趟排布完成（CommitUserPlacement 会写回
                        // 锚点与 X/Y/宽/高，但不碰展开态）。
                        CommitUserPlacement(
                            member.Config,
                            target,
                            target,
                            WidgetPlacementSource.CapsuleBarDrag,
                            captureIntent: true);
                    }

                    member.Config.CompactPlacement = CreatePlacement(target);
                    SynchronizeGroupLayoutFromMember(member.Config);
                    changed = true;
                }

                if (expandedSlot)
                {
                    if (moved)
                    {
                        FindLoadedWindow(member.Config.Id)?.RestoreBoundsForCurrentTopology();
                    }

                    continue;
                }

                FindLoadedWindow(member.Config.Id)?.ApplyCompactArrangement(
                    target,
                    constrainSize: true);
            }
        }

        if (changed)
        {
            NoteWidgetWindowsMoved();
        }

        return changed;
    }

    private bool RestoreFreeCapsulePlacements(AppSettings settings)
    {
        bool changed = false;
        foreach ((string id, WidgetCompactPlacement placement) in
                 settings.WidgetCapsuleFreePlacements.ToList())
        {
            WidgetConfig? config = settings.Widgets.FirstOrDefault(
                candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (config is null)
            {
                continue;
            }

            config.CompactPlacement = ClonePlacement(placement);
            SynchronizeGroupLayoutFromMember(config);
            RectInt32 target = ResolveCompactBounds(config);
            FindLoadedWindow(id)?.ApplyCompactArrangement(target, constrainSize: false);
            changed = true;
        }

        if (settings.WidgetCapsuleFreePlacements.Count > 0)
        {
            settings.WidgetCapsuleFreePlacements.Clear();
            changed = true;
        }

        if (changed)
        {
            NoteWidgetWindowsMoved();
        }

        return changed;
    }

    private ResolvedCapsuleCandidate ResolveColumnCandidate(ColumnMember member)
    {
        if (!member.IsExpandedSlot)
        {
            return ResolveCandidate(member.Config);
        }

        // 展开卡片按它自己的展开几何量尺寸，胶囊那套 compact 宽高对它没意义。
        RectInt32 bounds = WidgetPositioningService.ResolveBoundsForCurrentTopology(member.Config);
        RectInt32 workArea = DisplayArea.GetFromRect(
            bounds,
            DisplayAreaFallback.Nearest).WorkArea;
        return new ResolvedCapsuleCandidate(member.Config, bounds, workArea);
    }

    private ResolvedCapsuleCandidate ResolveCandidate(WidgetConfig config)
    {
        RectInt32 bounds = ResolveCompactBounds(config);
        RectInt32 workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        return new ResolvedCapsuleCandidate(config, bounds, workArea);
    }

    private RectInt32 ResolveCompactBounds(WidgetConfig config)
    {
        RectInt32 expandedBounds = WidgetPositioningService.ResolveBoundsForCurrentTopology(config);
        RectInt32 workArea = DisplayArea.GetFromRect(
            expandedBounds,
            DisplayAreaFallback.Nearest).WorkArea;
        return WidgetCompactBoundsCalculator.Resolve(
            config,
            expandedBounds,
            WidgetPositioningService.GetDpiScale(workArea),
            WidgetCompactPrivacyPolicy.ResolveContentMode(
                _settingsService.Settings.WidgetCompactContentMode,
                _settingsService.Settings.WidgetCompactHideSensitiveContent,
                config.WidgetKind),
            alignToExpandedWidth:
                SettingsService.NormalizeWidgetCompactWidthMode(
                    _settingsService.Settings.WidgetCompactWidthMode) ==
                SettingsService.WidgetCompactWidthModeAligned,
            heightContentMode: _settingsService.Settings.WidgetCompactContentMode);
    }

    private static string ResolveBarDirection(
        string configuredDirection,
        string placement,
        RectInt32 workArea)
    {
        string normalized = SettingsService.NormalizeWidgetCapsuleBarDirection(configuredDirection);
        if (normalized != SettingsService.WidgetCapsuleBarDirectionAuto)
        {
            return normalized;
        }

        string normalizedPlacement = SettingsService.NormalizeWidgetCapsuleBarPlacement(placement);
        if (normalizedPlacement is SettingsService.WidgetCapsuleBarPlacementLeft or
            SettingsService.WidgetCapsuleBarPlacementRight)
        {
            return SettingsService.WidgetCapsuleBarDirectionVertical;
        }

        if (normalizedPlacement is SettingsService.WidgetCapsuleBarPlacementTop or
            SettingsService.WidgetCapsuleBarPlacementBottom)
        {
            return SettingsService.WidgetCapsuleBarDirectionHorizontal;
        }

        return workArea.Width >= workArea.Height
            ? SettingsService.WidgetCapsuleBarDirectionHorizontal
            : SettingsService.WidgetCapsuleBarDirectionVertical;
    }

    private static (PointInt32 Point, string Anchor) ResolveBarAnchor(
        RectInt32 firstBounds,
        string? originalAnchor,
        RectInt32 workArea,
        string placement)
    {
        string normalizedPlacement = SettingsService.NormalizeWidgetCapsuleBarPlacement(placement);
        bool originalRight = originalAnchor is
            WidgetPositionAnchors.RightTop or WidgetPositionAnchors.RightBottom;
        bool originalBottom = originalAnchor is
            WidgetPositionAnchors.LeftBottom or WidgetPositionAnchors.RightBottom;
        bool anchorRight = normalizedPlacement == SettingsService.WidgetCapsuleBarPlacementRight ||
            normalizedPlacement is not SettingsService.WidgetCapsuleBarPlacementLeft && originalRight;
        bool anchorBottom = normalizedPlacement == SettingsService.WidgetCapsuleBarPlacementBottom ||
            normalizedPlacement is not SettingsService.WidgetCapsuleBarPlacementTop && originalBottom;
        string anchor = (anchorRight, anchorBottom) switch
        {
            (true, true) => WidgetPositionAnchors.RightBottom,
            (true, false) => WidgetPositionAnchors.RightTop,
            (false, true) => WidgetPositionAnchors.LeftBottom,
            _ => WidgetPositionAnchors.LeftTop
        };
        double scale = WidgetPositioningService.GetDpiScale(workArea);
        int margin = Math.Max(0, (int)Math.Round(CapsuleBarEdgeMargin * scale));
        int x = normalizedPlacement switch
        {
            SettingsService.WidgetCapsuleBarPlacementLeft => workArea.X + margin,
            SettingsService.WidgetCapsuleBarPlacementRight => workArea.X + workArea.Width - margin,
            _ => anchorRight ? firstBounds.X + firstBounds.Width : firstBounds.X
        };
        int y = normalizedPlacement switch
        {
            SettingsService.WidgetCapsuleBarPlacementTop => workArea.Y + margin,
            SettingsService.WidgetCapsuleBarPlacementBottom => workArea.Y + workArea.Height - margin,
            _ => anchorBottom ? firstBounds.Y + firstBounds.Height : firstBounds.Y
        };
        return (new PointInt32(x, y), anchor);
    }

    private IDesktopWidgetWindow? FindLoadedWindow(string widgetId)
    {
        if (_fileWidgets.TryGetValue(widgetId, out var fileEntry))
        {
            return fileEntry.Host;
        }

        return _contentWidgets.TryGetValue(widgetId, out ContentWidgetWindow? contentWindow)
            ? contentWindow
            : null;
    }

    private static WidgetCompactPlacement CreatePlacement(RectInt32 bounds)
    {
        var config = new WidgetConfig();
        WidgetCompactBoundsCalculator.CapturePlacement(config, bounds);
        return config.CompactPlacement!;
    }

    private static WidgetCompactPlacement ClonePlacement(WidgetCompactPlacement placement)
    {
        return new WidgetCompactPlacement
        {
            X = placement.X,
            Y = placement.Y,
            PositionAnchor = placement.PositionAnchor,
            PositionMarginX = placement.PositionMarginX,
            PositionMarginY = placement.PositionMarginY,
            PositionMonitorKey = placement.PositionMonitorKey,
            PositionMonitorDeviceName = placement.PositionMonitorDeviceName,
            PositionMonitorStableId = placement.PositionMonitorStableId,
            PositionMonitorWasPrimary = placement.PositionMonitorWasPrimary,
            BoundsCoordinateVersion = placement.BoundsCoordinateVersion
        };
    }

    private static RectInt32 GetUnion(IEnumerable<RectInt32> bounds)
    {
        RectInt32[] items = bounds.ToArray();
        int minX = items.Min(item => item.X);
        int minY = items.Min(item => item.Y);
        int maxX = items.Max(item => item.X + item.Width);
        int maxY = items.Max(item => item.Y + item.Height);
        return new RectInt32(minX, minY, maxX - minX, maxY - minY);
    }

    private static RectInt32 ClampBoundsToRect(RectInt32 bounds, RectInt32 workArea)
    {
        int width = Math.Min(Math.Max(1, bounds.Width), Math.Max(1, workArea.Width));
        int height = Math.Min(Math.Max(1, bounds.Height), Math.Max(1, workArea.Height));
        int maxX = workArea.X + workArea.Width - width;
        int maxY = workArea.Y + workArea.Height - height;
        return new RectInt32(
            Math.Clamp(bounds.X, workArea.X, Math.Max(workArea.X, maxX)),
            Math.Clamp(bounds.Y, workArea.Y, Math.Max(workArea.Y, maxY)),
            width,
            height);
    }

    // Shared by both drag branches: a Floating bar may follow the pointer
    // across monitors mid-drag, so the tracking work area is re-resolved from
    // the proposed bounds every frame and written back into the session;
    // docked placements stay pinned to the monitor captured at drag start.
    private static RectInt32 UpdateDragWorkArea(
        CapsuleBarDragSession session,
        string placement,
        RectInt32 proposedActiveBounds)
    {
        RectInt32 workArea = placement == SettingsService.WidgetCapsuleBarPlacementFloating
            ? DisplayArea.GetFromRect(proposedActiveBounds, DisplayAreaFallback.Nearest).WorkArea
            : session.WorkArea;
        session.WorkArea = workArea;
        return workArea;
    }

    /// <summary>
    /// Moves the whole capsule bar next to the pointer when a Floating drag
    /// enters another monitor — the same frame, like the move branch — so the
    /// drop lands on the pointer's screen instead of teleporting back to a
    /// drag-start slot. Also refreshes the session's slots and anchor so
    /// subsequent reorder math keys off the new screen.
    /// </summary>
    private void ReanchorCapsuleBarToWorkArea(
        CapsuleBarDragSession session,
        RectInt32 workArea,
        string placement,
        RectInt32 proposedActiveBounds)
    {
        RectInt32 tracked = ClampBoundsToRect(proposedActiveBounds, workArea);
        RectInt32 activeCurrent = _lastCapsuleBarBounds.TryGetValue(
            session.ActiveWidgetId,
            out RectInt32 activeBounds)
            ? activeBounds
            : session.ActiveStartBounds;
        var current = new Dictionary<string, RectInt32>(StringComparer.Ordinal);
        foreach (string id in session.MemberOrder)
        {
            current[id] = _lastCapsuleBarBounds.TryGetValue(id, out RectInt32 bounds)
                ? bounds
                : session.MemberStartBounds[id];
        }

        RectInt32 groupBounds = GetUnion(current.Values);
        int deltaX = ClampGroupDelta(
            groupBounds.X,
            groupBounds.Width,
            tracked.X - activeCurrent.X,
            workArea.X,
            workArea.Width);
        int deltaY = ClampGroupDelta(
            groupBounds.Y,
            groupBounds.Height,
            tracked.Y - activeCurrent.Y,
            workArea.Y,
            workArea.Height);

        foreach (string id in session.MemberOrder)
        {
            RectInt32 target = new RectInt32(
                current[id].X + deltaX,
                current[id].Y + deltaY,
                current[id].Width,
                current[id].Height);
            _lastCapsuleBarBounds[id] = target;
            // The dragged capsule keeps tracking the pointer; siblings move
            // with the bar so the group lands together on the new monitor.
            if (!string.Equals(id, session.ActiveWidgetId, StringComparison.Ordinal) &&
                FindLoadedWindow(id) is { IsCompactArrangementActive: true } window)
            {
                window.PreviewCompactArrangement(target);
            }
        }

        session.MemberSlots = session.MemberOrder
            .Select(id => _lastCapsuleBarBounds[id])
            .ToArray();
        session.AnchorPoint = ResolveBarAnchor(
            _lastCapsuleBarBounds[session.MemberOrder[0]],
            session.PositionAnchor,
            workArea,
            placement).Point;
        NoteWidgetWindowsMoved();
    }

    private static int ClampGroupDelta(
        int groupStart,
        int groupLength,
        int requestedDelta,
        int workStart,
        int workLength)
    {
        int minimum = workStart - groupStart;
        int maximum = workStart + workLength - (groupStart + groupLength);
        if (minimum > maximum)
        {
            return minimum;
        }

        return Math.Clamp(requestedDelta, minimum, maximum);
    }

    private static string FormatWorkAreaKey(RectInt32 workArea) =>
        $"{workArea.X}:{workArea.Y}:{workArea.Width}:{workArea.Height}";

    private static bool BoundsEqual(RectInt32 left, RectInt32 right) =>
        left.X == right.X &&
        left.Y == right.Y &&
        left.Width == right.Width &&
        left.Height == right.Height;

    private sealed record ResolvedCapsuleCandidate(
        WidgetConfig Config,
        RectInt32 Bounds,
        RectInt32 WorkArea);

    private sealed class CapsuleBarDragSession(
        string activeWidgetId,
        RectInt32 activeStartBounds,
        IReadOnlyDictionary<string, RectInt32> memberStartBounds,
        RectInt32 workArea,
        bool reordersMember,
        IReadOnlyList<string> memberOrder,
        string direction,
        PointInt32 anchorPoint,
        string positionAnchor,
        int spacing)
    {
        public string ActiveWidgetId { get; } = activeWidgetId;
        public RectInt32 ActiveStartBounds { get; } = activeStartBounds;
        public IReadOnlyDictionary<string, RectInt32> MemberStartBounds { get; } = memberStartBounds;
        public RectInt32 WorkArea { get; set; } = workArea;
        public bool ReordersMember { get; } = reordersMember;
        public List<string> MemberOrder { get; set; } = memberOrder.ToList();
        // Both refresh when a Floating drag crosses monitors: later reorder
        // decisions and arrangement math must key off the current screen,
        // not the drag-start one.
        public IReadOnlyList<RectInt32> MemberSlots { get; set; } = memberOrder
            .Select(id => memberStartBounds[id])
            .ToArray();
        public string Direction { get; } = direction;
        public PointInt32 AnchorPoint { get; set; } = anchorPoint;
        public string PositionAnchor { get; } = positionAnchor;
        public int Spacing { get; } = spacing;
    }
}
