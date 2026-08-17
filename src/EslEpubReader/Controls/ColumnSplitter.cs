// ============================================================================
// Controls/ColumnSplitter.cs
// ============================================================================
// A minimal draggable "grid splitter" for resizing Grid columns by mouse.
//
// WHY A CUSTOM CONTROL?
//   WinUI 3 ships no built-in GridSplitter (the old WPF one never made the
//   jump). The usual alternative is the CommunityToolkit "Sizers" NuGet
//   package, but this project deliberately stays dependency-free — and the
//   whole mechanism is small enough to own: capture the pointer, watch it
//   move, write the new width into the target ColumnDefinition.
//
// HOW TO USE (see MainWindow.xaml):
//   Give the splitter its own narrow Auto column BETWEEN the two panes and
//   tell it which column it resizes:
//
//     <Grid.ColumnDefinitions>
//         <ColumnDefinition Width="240" MinWidth="140" MaxWidth="480"/>  0: pane
//         <ColumnDefinition Width="Auto"/>                               1: splitter
//         <ColumnDefinition Width="*"/>                                  2: rest
//     </Grid.ColumnDefinitions>
//     <controls:ColumnSplitter Grid.Column="1" TargetColumn="0"/>
//
//   * TargetColumn        — index of the ColumnDefinition whose Width this
//                           splitter drags (must be a FIXED-width column,
//                           the neighbouring star column absorbs the rest).
//   * ResizeRightColumn   — false (default): the target sits LEFT of the
//                           splitter, so dragging right GROWS it.
//                           true: the target sits RIGHT of the splitter, so
//                           dragging right SHRINKS it (mirror logic).
//
//   Min/Max limits are read from the target ColumnDefinition's
//   MinWidth/MaxWidth, so the layout constraints live in XAML next to the
//   layout itself.
//
// WHY POINTER CAPTURE MATTERS HERE:
//   The reader pane is a WebView2 — a child HWND that normally swallows all
//   mouse input the moment the cursor crosses into it. CapturePointer()
//   routes every subsequent move/release back to this control even while
//   the cursor is physically over the WebView2, which is exactly what makes
//   dragging across the reading surface feel solid instead of "sticking".
// ============================================================================

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EslEpubReader.Controls;

/// <summary>
/// A vertical drag handle that resizes one fixed-width column of its parent
/// Grid. Derives from Grid — NOT Border or ContentControl — for two reasons:
///   * Border is sealed in WinUI 3, so it cannot be subclassed at all;
///   * a Panel paints its Background directly, which makes the whole 8px
///     strip hit-testable (a Transparent background still receives pointer
///     events), while XAML can drop the visible divider line in as a child.
/// </summary>
public sealed partial class ColumnSplitter : Grid
{
    // -------------------------------------------------- configuration (XAML)

    /// <summary>Index of the ColumnDefinition (in the parent Grid) whose
    /// Width this splitter changes. Plain CLR property is enough — it is
    /// assigned once from XAML and never data-bound.</summary>
    public int TargetColumn { get; set; }

    /// <summary>
    /// Direction semantics (see file header):
    ///   false = target column is LEFT of the splitter  → drag right = grow.
    ///   true  = target column is RIGHT of the splitter → drag right = shrink.
    /// </summary>
    public bool ResizeRightColumn { get; set; }

    // -------------------------------------------------------- drag state

    /// <summary>True while a drag is in progress (pointer captured).</summary>
    private bool _dragging;

    /// <summary>Pointer X at drag start, in WINDOW coordinates. Window space
    /// is used because the splitter element itself MOVES while the column
    /// resizes — measuring relative to the splitter would feed back into
    /// itself and make the drag jitter.</summary>
    private double _startPointerX;

    /// <summary>The target column's width when the drag started.</summary>
    private double _startWidth;

    public ColumnSplitter()
    {
        // A resize cursor is the affordance that tells users "this edge is
        // draggable". ProtectedCursor is a protected UIElement member, which
        // is precisely why this exists as a subclass instead of plain XAML.
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

        // Panels expose no OnPointerXxx virtuals (those live on Control),
        // so subscribe to the routed events instead.
        PointerPressed += OnSplitterPointerPressed;
        PointerMoved += OnSplitterPointerMoved;
        PointerReleased += OnSplitterPointerReleased;

        // Capture can be lost without a Released event (window deactivated,
        // touch cancel, …) — always leave drag mode cleanly.
        PointerCaptureLost += (_, _) => _dragging = false;
    }

    /// <summary>The parent Grid's definition of the column we resize, or
    /// null when the control is (mis)placed outside a Grid.</summary>
    private ColumnDefinition? TargetDefinition =>
        Parent is Grid grid && TargetColumn < grid.ColumnDefinitions.Count
            ? grid.ColumnDefinitions[TargetColumn]
            : null;

    /// <summary>Drag start: remember the anchor position + width and capture
    /// the pointer so ALL further events come to us (even over WebView2).</summary>
    private void OnSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Left mouse button only (ignore right-clicks, pen barrel, …).
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (TargetDefinition is not ColumnDefinition column) return;

        _dragging = CapturePointer(e.Pointer);
        if (!_dragging) return;

        // GetCurrentPoint(null) = position relative to the XAML root
        // (window content) — see _startPointerX for why not "this".
        _startPointerX = e.GetCurrentPoint(null).Position.X;
        _startWidth = column.ActualWidth;
        e.Handled = true;
    }

    /// <summary>Drag move: convert the pointer's horizontal travel into a
    /// new pixel width for the target column, honoring its Min/MaxWidth.</summary>
    private void OnSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || TargetDefinition is not ColumnDefinition column) return;

        double deltaX = e.GetCurrentPoint(null).Position.X - _startPointerX;

        // Mirror the delta when the target column lies to the RIGHT of the
        // splitter: moving the handle right must SHRINK that column.
        double proposed = ResizeRightColumn ? _startWidth - deltaX
                                            : _startWidth + deltaX;

        // Clamp to the limits declared on the ColumnDefinition in XAML.
        // (MaxWidth defaults to +Infinity, which Math.Clamp handles fine.)
        double newWidth = Math.Clamp(proposed, column.MinWidth, column.MaxWidth);

        // Assigning a pixel GridLength makes the Grid re-layout immediately;
        // the neighbouring star-sized column absorbs the difference.
        column.Width = new GridLength(newWidth);
        e.Handled = true;
    }

    /// <summary>Drag end: release the capture and leave drag mode.</summary>
    private void OnSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }
}
