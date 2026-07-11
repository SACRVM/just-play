using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace JustPlay.App.Controls;

/// <summary>
/// The ONE row-drag gesture for track lists, with two modes per list:
///
/// <para><b>Drag-out only</b> (<see cref="Attach(ListBox, Func{object?, string?})"/> — the finder's
/// file list + folder tree): press a row, drag past a small threshold, and Windows' real OLE
/// drag-drop takes over — dropping on Explorer/Traktor/an editor/an AI-agent chat window (anywhere
/// that accepts CF_HDROP) hands over the actual file or folder path. Chloe 2026-07-08: "wichtig wenn
/// man mal einen KI-Agenten auf ein file aufmerksam machen will."</para>
///
/// <para><b>Combined reorder + drag-out</b> (<see cref="Attach(ListBox, Func{object?, string?}, RowReorder)"/>
/// — the queue): dragging INSIDE the list reorders rows (accent insertion line, edge autoscroll,
/// multi-select moves the whole selection) — the set-building gesture, like every DJ software.
/// Dragging OUT of the list SIDEWAYS converts the gesture into the OS file drag above. Vertical
/// overshoot clamps the insertion point to the first/last row instead (so sorting to the very
/// top/bottom can't accidentally become a file drag). Chloe 2026-07-11: "wie willst du ein set
/// bauen wenn du es nicht bei hand sortieren kannst?"</para>
/// </summary>
/// <remarks>
/// <para>
/// OS-drag mechanics SOURCE-VERIFIED against Avalonia 12.0.3 (branch release/12.0.3) — night task
/// N24. Avalonia 12 replaced the old Avalonia-11 <c>IDataObject</c>/<c>DataFormats</c>/
/// <c>DragDrop.DoDragDrop</c> API (that old <c>DataFormats</c> class is now an empty,
/// <c>[Obsolete(error: true)]</c> shim — see <c>src/Avalonia.Base/Input/DataFormats.cs</c>) with a
/// new <c>IDataTransfer</c> API:
/// </para>
/// <list type="bullet">
/// <item><c>DragDrop.DoDragDropAsync(PointerPressedEventArgs, IDataTransfer, DragDropEffects)</c>
/// — async, returns <c>Task&lt;DragDropEffects&gt;</c> — <c>src/Avalonia.Base/Input/DragDrop.cs</c>.</item>
/// <item><c>DataFormat.File</c> is a <c>DataFormat&lt;IStorageItem&gt;</c>; build the payload with
/// <c>new DataTransfer()</c> + <c>DataTransferItem.CreateFile(IStorageItem)</c> per file —
/// <c>src/Avalonia.Base/Input/{DataTransfer,DataTransferItem,DataFormat}.cs</c>. The transfer must
/// NOT be disposed by the caller (the drag source disposes it when the drag completes) —
/// <c>IDataTransfer.cs</c> remarks. Multiple <see cref="DataFormat.File"/> items in one transfer are
/// explicitly supported on Windows (the "single item" platform limitation only applies to non-File
/// formats) — same file, <see cref="IDataTransfer.Items"/> remarks.</item>
/// <item>The Win32 backend (<c>src/Windows/Avalonia.Win32/DragSource.cs</c>) calls the REAL native
/// OLE <c>DoDragDrop</c> (via <c>UnmanagedMethods.DoDragDrop</c>), wrapping the transfer in
/// <c>DataTransferToOleDataObjectWrapper</c>. <c>ClipboardFormatRegistry</c>'s static constructor
/// maps <c>DataFormat.File</c> directly to <c>CF_HDROP</c>, and
/// <c>OleDataObjectHelper.WriteFileNamesToHGlobal</c> builds a genuine Win32 <c>DROPFILES</c> block
/// from each file's local path (<c>IStorageItem.TryGetLocalPath()</c>). This is a real OS file
/// drag — Explorer, Traktor, or any other CF_HDROP-aware app receives an actual file copy, not a
/// JustPlay-only simulation.</item>
/// </list>
/// <para>
/// Row-selection quirk (also source-verified): <c>ListBoxItem.OnPointerPressed</c> calls
/// <c>SelectingItemsControl.UpdateSelectionFromEvent</c>, which sets <c>e.Handled = true</c> for a
/// plain left click (<c>ListBoxItem.cs</c> / <c>SelectingItemsControl.cs</c>). A bubble-routed
/// Pointer{Pressed,Moved,Released} handler added to the ListBox itself therefore never fires unless
/// registered with <c>handledEventsToo: true</c> — the exact same pitfall this codebase already
/// documents for Enter/Space on KeyDown in <c>MaxView.axaml.cs</c>.
/// </para>
/// <para>
/// Reorder capture: on entering reorder mode the pointer is explicitly captured to the LIST —
/// without it, moves stop arriving the moment the pointer leaves the list bounds, so neither the
/// sideways hand-off nor a release-outside would ever be seen. Stealing capture from the pressed
/// ListBoxItem is safe: its selection work happened on the press.
/// </para>
/// </remarks>
public static class RowDragBehavior
{
    // Small movement slop so a plain click/double-click never accidentally starts a drag.
    private const double DragThresholdPx = 4;

    // Sideways hysteresis: the pointer must leave the list bounds horizontally by this much before
    // the reorder converts to an OS file drag (grazing the edge/scrollbar must not fire it).
    private const double SidewaysExitSlopPx = 8;

    // Edge autoscroll while reordering: pointer within this zone of the top/bottom edge scrolls.
    private const double EdgeZonePx = 36;
    private const double ScrollStepPx = 8;                       // per tick
    private static readonly TimeSpan ScrollTick = TimeSpan.FromMilliseconds(50);

    private sealed class State
    {
        public PointerPressedEventArgs? PressArgs;
        public string? PressedPath;
        public object? PressedItem;
        public Point StartPoint;

        // Reorder-mode extras.
        public bool Reordering;
        public int InsertIndex = -1;
        public Point LastPos;                                    // list coordinates
        public DispatcherTimer? ScrollTimer;

        public void Reset(RowReorder? reorder)
        {
            PressArgs = null;
            PressedPath = null;
            PressedItem = null;
            Reordering = false;
            InsertIndex = -1;
            ScrollTimer?.Stop();
            ScrollTimer = null;
            if (reorder is not null) reorder.Indicator.IsVisible = false;
        }
    }

    /// <summary>Drag-out only: enable the OS file/folder-copy drag on the list's rows.
    /// <paramref name="pathOf"/> maps a row's DataContext to its local path (return null/empty for a
    /// non-draggable row, e.g. the finder's ".." hop). Does not interfere with selection,
    /// double-click-to-play/enter, arrow-key navigation + Enter, the context menu, or drag-INTO the app.</summary>
    public static void Attach(ListBox list, Func<object?, string?> pathOf)
        => AttachCore(list, pathOf, reorder: null);

    /// <summary>Combined mode: drag inside the list = reorder rows (via <paramref name="reorder"/>),
    /// drag out of the list sideways = the OS file drag.</summary>
    public static void Attach(ListBox list, Func<object?, string?> pathOf, RowReorder reorder)
        => AttachCore(list, pathOf, reorder);

    private static void AttachCore(ListBox list, Func<object?, string?> pathOf, RowReorder? reorder)
    {
        var state = new State();

        list.AddHandler(InputElement.PointerPressedEvent,
            (_, e) => OnPressed(list, state, pathOf, reorder, e), RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(InputElement.PointerMovedEvent,
            (_, e) => OnMoved(list, state, pathOf, reorder, e), RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(InputElement.PointerReleasedEvent,
            (_, e) => OnReleased(list, state, pathOf, reorder, e), RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(InputElement.PointerCaptureLostEvent,
            (_, _) => state.Reset(reorder), RoutingStrategies.Bubble);
    }

    private static void OnPressed(
        ListBox list, State state, Func<object?, string?> pathOf, RowReorder? reorder, PointerPressedEventArgs e)
    {
        state.Reset(reorder); // also clears a stale indicator from any abnormally-ended gesture
        if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
        // Ctrl/Shift-press is selection building (extend/toggle) — never start a drag gesture from it.
        if (e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Shift) return;

        var dc = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext;
        if (pathOf(dc) is not { Length: > 0 } path) return; // row has no draggable path (e.g. the ".." hop)

        state.PressArgs = e;
        state.PressedPath = path;
        state.PressedItem = dc;
        state.StartPoint = e.GetPosition(list);
    }

    private static void OnMoved(
        ListBox list, State state, Func<object?, string?> pathOf, RowReorder? reorder, PointerEventArgs e)
    {
        if (state.PressArgs is null) return;
        if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) { state.Reset(reorder); return; }

        var pos = e.GetPosition(list);
        state.LastPos = pos;

        if (!state.Reordering)
        {
            var dx = pos.X - state.StartPoint.X;
            var dy = pos.Y - state.StartPoint.Y;
            if (dx * dx + dy * dy < DragThresholdPx * DragThresholdPx) return;

            if (reorder is null)
            {
                // Drag-out-only list: threshold crossed → straight into the OS drag.
                StartOsDrag(list, state, pathOf, reorder);
                return;
            }

            // Combined list: threshold crossed → reorder mode. Capture to the LIST so moves keep
            // arriving outside its bounds (see class remarks). Reordering a column-sorted list is
            // deliberately allowed: the commit adopts the sorted order as the new hand order and
            // clears the sort (Chloe 2026-07-11 — implicit beats a dead gesture).
            state.Reordering = true;
            e.Pointer.Capture(list);
            StartAutoscroll(list, state, reorder);
        }

        // ── Reordering ────────────────────────────────────────────────────────
        if (pos.X < -SidewaysExitSlopPx || pos.X > list.Bounds.Width + SidewaysExitSlopPx)
        {
            // Left the list sideways → this became a file drag to another app.
            StartOsDrag(list, state, pathOf, reorder);
            return;
        }

        UpdateIndicator(list, state, reorder!);
    }

    private static void OnReleased(
        ListBox list, State state, Func<object?, string?> pathOf, RowReorder? reorder, PointerReleasedEventArgs e)
    {
        if (state.Reordering && reorder is not null && state.InsertIndex >= 0)
        {
            var items = DragSetItems(list, state);
            if (items.Count > 0)
                reorder.Move(items, state.InsertIndex);
        }
        state.Reset(reorder);
    }

    // ── Insertion point + indicator ──────────────────────────────────────────

    private static void UpdateIndicator(ListBox list, State state, RowReorder reorder)
    {
        var index = ComputeInsertIndex(list, state.LastPos.Y, out var lineY);
        state.InsertIndex = index;

        var indicator = reorder.Indicator;
        if (indicator.Parent is not Visual host) return;

        // The line is positioned with a translate (layout-neutral); XAML owns the side margins.
        var y = list.TranslatePoint(new Point(0, lineY), host)?.Y ?? lineY;
        if (indicator.RenderTransform is not Avalonia.Media.TranslateTransform tt)
            indicator.RenderTransform = tt = new Avalonia.Media.TranslateTransform();
        tt.Y = Math.Max(0, y - 1);   // centre the 2px line on the row boundary
        indicator.IsVisible = true;
    }

    /// <summary>Insertion index for a pointer at <paramref name="pointerY"/> (list coordinates):
    /// before the first realized row whose vertical midpoint lies below the pointer; after the last
    /// row otherwise. Vertical overshoot clamps to 0 / ItemCount by construction. Only realized
    /// (visible) containers are considered — the pointer is inside the viewport, so the row under it
    /// is always realized; autoscroll brings the rest.</summary>
    private static int ComputeInsertIndex(ListBox list, double pointerY, out double lineY)
    {
        var firstTop = 0.0;
        var lastBottom = 0.0;
        var haveAny = false;
        var afterAll = list.ItemCount;

        for (var i = 0; i < list.ItemCount; i++)
        {
            if (list.ContainerFromIndex(i) is not { } c) continue;
            if (c.TranslatePoint(new Point(0, 0), list) is not { } top) continue;

            var h = c.Bounds.Height;
            if (!haveAny) { firstTop = top.Y; haveAny = true; }
            if (pointerY < top.Y + h / 2)
            {
                lineY = top.Y;
                return i;
            }
            lastBottom = top.Y + h;
        }

        lineY = haveAny ? lastBottom : firstTop;
        return afterAll;
    }

    // ── Edge autoscroll ──────────────────────────────────────────────────────

    private static void StartAutoscroll(ListBox list, State state, RowReorder reorder)
    {
        var timer = new DispatcherTimer { Interval = ScrollTick };
        timer.Tick += (_, _) =>
        {
            if (!state.Reordering) { timer.Stop(); return; }
            if (list.FindDescendantOfType<ScrollViewer>() is not { } sv) return;

            var y = state.LastPos.Y;
            double delta;
            if (y < EdgeZonePx) delta = -ScrollStepPx;
            else if (y > list.Bounds.Height - EdgeZonePx) delta = ScrollStepPx;
            else return;

            var maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            var newY = Math.Clamp(sv.Offset.Y + delta, 0, maxY);
            if (Math.Abs(newY - sv.Offset.Y) < 0.1) return;
            sv.Offset = sv.Offset.WithY(newY);

            // Rows shifted under the (stationary) pointer — recompute the insertion line.
            UpdateIndicator(list, state, reorder);
        };
        state.ScrollTimer = timer;
        timer.Start();
    }

    // ── The OS file drag (shared by both modes) ──────────────────────────────

    private static void StartOsDrag(ListBox list, State state, Func<object?, string?> pathOf, RowReorder? reorder)
    {
        var pressArgs = state.PressArgs!;
        var pressedPath = state.PressedPath!;

        // Multi-select drag: if the pressed row is part of a multi-row selection, carry the whole
        // selection (mirrors Explorer). Otherwise just the single row that was pressed.
        var selected = list.SelectedItems;
        IReadOnlyList<string> dragSet;
        if (selected is { Count: > 1 })
        {
            var paths = selected.Cast<object?>().Select(pathOf)
                                .Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).Distinct().ToList();
            dragSet = paths.Contains(pressedPath) ? paths : [pressedPath];
        }
        else dragSet = [pressedPath];

        // Reset BEFORE the OS drag loop takes over the message pump, so a stray pointer event that
        // sneaks through doesn't re-enter with a stale/consumed PointerPressedEventArgs.
        state.Reset(reorder);
        _ = StartDragAsync(list, pressArgs, dragSet);
    }

    /// <summary>The rows a reorder commit moves: the whole multi-selection when the pressed row is
    /// part of it (mirrors the drag-out rule), else just the pressed row.</summary>
    private static IReadOnlyList<object> DragSetItems(ListBox list, State state)
    {
        var pressed = state.PressedItem;
        if (pressed is null) return [];

        var selected = list.SelectedItems;
        if (selected is { Count: > 1 })
        {
            var items = selected.Cast<object?>().Where(o => o is not null).Select(o => o!).ToList();
            if (items.Contains(pressed)) return items;
        }
        return [pressed];
    }

    private static async Task StartDragAsync(ListBox list, PointerPressedEventArgs pressArgs, IReadOnlyList<string> paths)
    {
        try
        {
            var storageProvider = TopLevel.GetTopLevel(list)?.StorageProvider;
            if (storageProvider is null) return;

            var dataTransfer = new DataTransfer();
            foreach (var path in paths)
            {
                // Resolve to a folder OR a file storage item (both are IStorageItem → CF_HDROP). Skip
                // anything that can't be resolved (e.g. a NAS path that just went offline) instead of
                // failing the whole drag — never block dragging the rest of the selection.
                IStorageItem? item = Directory.Exists(path)
                    ? await storageProvider.TryGetFolderFromPathAsync(path)
                    : await storageProvider.TryGetFileFromPathAsync(path);
                if (item is not null)
                    dataTransfer.Add(DataTransferItem.CreateFile(item));
            }

            if (dataTransfer.Items.Count == 0) return; // nothing resolvable — silently no-op the drag

            // DataTransfer must NOT be disposed here — DragDrop.DoDragDropAsync's platform drag
            // source disposes it once the OS-level drag operation completes (IDataTransfer.cs remarks).
            await DragDrop.DoDragDropAsync(pressArgs, dataTransfer, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            JustPlay.App.ErrorReporter.Report(ex, "Row drag (reorder / file copy to external app)");
        }
    }
}

/// <summary>Reorder hookup for <see cref="RowDragBehavior"/>'s combined mode: the overlay
/// insertion-line Border (layout-neutral, positioned via translate) and the commit callback —
/// <c>Move(items, insertIndex)</c> receives the dragged rows' DataContexts and the target index in
/// the ItemsSource BEFORE removal (the view-model adjusts for items that sit above the target).</summary>
public sealed class RowReorder
{
    public required Border Indicator { get; init; }
    public required Action<IReadOnlyList<object>, int> Move { get; init; }
}
