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
using Avalonia.VisualTree;

namespace JustPlay.App.Controls;

/// <summary>
/// Wires an OS-level outgoing file/folder-copy drag onto every row of a <see cref="ListBox"/> — press a
/// row, drag past a small threshold, and Windows' real OLE drag-drop takes over: dropping on
/// Explorer/Traktor/an editor/an AI-agent chat window (anywhere that accepts CF_HDROP) hands over the
/// actual file or folder path, exactly like dragging out of Explorer. The <c>pathOf</c> selector maps a
/// row's DataContext to its local path, so the SAME behaviour serves the queue (<see cref="TrackViewModel"/>),
/// the finder's file list, and the finder's folder tree — a folder path drags the whole folder. Chloe
/// 2026-07-08: "wichtig wenn man mal einen KI-Agenten auf ein file aufmerksam machen will."
/// </summary>
/// <remarks>
/// <para>
/// SOURCE-VERIFIED against Avalonia 12.0.3 (branch release/12.0.3) — night task N24. Avalonia 12
/// replaced the old Avalonia-11 <c>IDataObject</c>/<c>DataFormats</c>/<c>DragDrop.DoDragDrop</c>
/// API (that old <c>DataFormats</c> class is now an empty, <c>[Obsolete(error: true)]</c> shim —
/// see <c>src/Avalonia.Base/Input/DataFormats.cs</c>) with a new <c>IDataTransfer</c> API:
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
/// <item><c>StorageProviderExtensions.TryGetFileFromPathAsync(this IStorageProvider, string)</c>
/// resolves a plain path to the <c>IStorageFile</c> the transfer needs (on desktop this is a
/// <c>BclStorageProvider</c> and resolves synchronously) —
/// <c>src/Avalonia.Base/Platform/Storage/StorageProviderExtensions.cs</c>.</item>
/// </list>
/// <para>
/// Row-selection quirk (also source-verified): <c>ListBoxItem.OnPointerPressed</c> calls
/// <c>SelectingItemsControl.UpdateSelectionFromEvent</c>, which sets <c>e.Handled = true</c> for a
/// plain left click (<c>ListBoxItem.cs</c> / <c>SelectingItemsControl.cs</c>). A bubble-routed
/// Pointer{Pressed,Moved,Released} handler added to the ListBox itself therefore never fires unless
/// registered with <c>handledEventsToo: true</c> — the exact same pitfall this codebase already
/// documents for Enter/Space on KeyDown in <c>MaxView.axaml.cs</c>.
/// </para>
/// </remarks>
public static class RowDragOutBehavior
{
    // Small movement slop so a plain click/double-click never accidentally starts a drag.
    private const double DragThresholdPx = 4;

    private sealed class State
    {
        public PointerPressedEventArgs? PressArgs;
        public string? PressedPath;
        public Point StartPoint;

        public void Reset()
        {
            PressArgs = null;
            PressedPath = null;
        }
    }

    /// <summary>Call once per list (e.g. from the view's constructor) to enable OS file/folder-copy
    /// drag-out on its rows. <paramref name="pathOf"/> maps a row's DataContext to its local path (return
    /// null/empty for a non-draggable row, e.g. the finder's ".." hop). Does not interfere with selection,
    /// double-click-to-play/enter, arrow-key navigation + Enter, the context menu, or drag-INTO the app.</summary>
    public static void Attach(ListBox list, Func<object?, string?> pathOf)
    {
        var state = new State();

        list.AddHandler(InputElement.PointerPressedEvent,
            (_, e) => OnPressed(list, state, pathOf, e), RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(InputElement.PointerMovedEvent,
            (_, e) => OnMoved(list, state, pathOf, e), RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(InputElement.PointerReleasedEvent,
            (_, _) => state.Reset(), RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(InputElement.PointerCaptureLostEvent,
            (_, _) => state.Reset(), RoutingStrategies.Bubble);
    }

    private static void OnPressed(ListBox list, State state, Func<object?, string?> pathOf, PointerPressedEventArgs e)
    {
        state.Reset();
        if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
        var dc = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext;
        if (pathOf(dc) is not { Length: > 0 } path) return; // row has no draggable path (e.g. the ".." hop)

        state.PressArgs = e;
        state.PressedPath = path;
        state.StartPoint = e.GetPosition(list);
    }

    private static void OnMoved(ListBox list, State state, Func<object?, string?> pathOf, PointerEventArgs e)
    {
        if (state.PressArgs is null || state.PressedPath is null) return;
        if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) { state.Reset(); return; }

        var pos = e.GetPosition(list);
        var dx = pos.X - state.StartPoint.X;
        var dy = pos.Y - state.StartPoint.Y;
        if (dx * dx + dy * dy < DragThresholdPx * DragThresholdPx) return;

        // Multi-select drag: if the pressed row is part of a multi-row selection, carry the whole
        // selection (mirrors Explorer). Otherwise just the single row that was pressed.
        var pressedPath = state.PressedPath;
        var pressArgs = state.PressArgs;
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
        state.Reset();
        _ = StartDragAsync(list, pressArgs, dragSet);
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
            JustPlay.App.ErrorReporter.Report(ex, "Row drag-out (file/folder copy to external app)");
        }
    }
}
