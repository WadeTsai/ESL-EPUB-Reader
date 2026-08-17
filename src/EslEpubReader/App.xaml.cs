// ============================================================================
// App.xaml.cs
// ============================================================================
// Code-behind for App.xaml — the entry point of the WinUI 3 application.
//
// Responsibilities:
//   1. Initialize the XAML framework (done by InitializeComponent()).
//   2. Create and activate the single main window when the app is launched.
//
// WinUI 3 desktop apps (unlike UWP) have exactly one process-lifetime launch
// path: OnLaunched. There is no suspend/resume lifecycle to handle here.
// ============================================================================

using Microsoft.UI.Xaml;

namespace EslEpubReader;

/// <summary>
/// The application object. Created once per process by the auto-generated
/// Main() method (see obj/**/App.g.i.cs produced by the XAML compiler).
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Keep a strong reference to the main window. If we did not store it in
    /// a field, the garbage collector could collect the Window object while
    /// it is still on screen (WinUI does NOT root windows for you), which
    /// would make the app exit unexpectedly.
    /// </summary>
    private Window? _mainWindow;

    /// <summary>
    /// Constructor: runs before any UI exists. InitializeComponent() loads
    /// App.xaml so that application-wide resources (the WinUI control styles)
    /// become available to every window we create later.
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Called by the framework when the application is launched.
    /// We create the main reader window and show it.
    /// </summary>
    /// <param name="args">Launch arguments (unused — we always open the
    /// library/reader view; file-association launch could be added here).</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();
        _mainWindow.Activate();   // Activate() = show the window + give it focus.
    }
}
