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
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
// Both Microsoft.UI.Xaml and Windows.ApplicationModel.Activation declare a
// LaunchActivatedEventArgs; OnLaunched must take the WinUI one.
using LaunchActivatedEventArgs = Microsoft.UI.Xaml.LaunchActivatedEventArgs;

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
    /// The .epub file this process was launched WITH (double-clicked in
    /// Explorer / "Open with…"), or null for a normal start. MainWindow
    /// reads this during startup: an explicitly opened file wins over the
    /// "continue where you left off" session restore.
    /// </summary>
    public static string? StartupEpubPath { get; private set; }

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
    /// We resolve any file-activation payload, then create and show the
    /// main reader window.
    /// </summary>
    /// <param name="args">WinUI launch args — note these do NOT carry file
    /// activation; that arrives via AppLifecycle (see below).</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupEpubPath = TryGetStartupEpubPath();

        _mainWindow = new MainWindow();
        _mainWindow.Activate();   // Activate() = show the window + give it focus.
    }

    /// <summary>
    /// Find the .epub the user launched us with, covering BOTH deployment
    /// flavors (the Store package declares an .epub file association in
    /// Package.appxmanifest, so this must actually work):
    ///
    ///   * PACKAGED (Microsoft Store): double-clicking an .epub raises a
    ///     FILE ACTIVATION, delivered through the Windows App SDK's
    ///     AppLifecycle API — NOT through Main()'s argv.
    ///
    ///   * UNPACKAGED (portable exe): Explorer passes the file path as a
    ///     plain command-line argument.
    ///
    /// Returns null when the app was started normally.
    /// </summary>
    private static string? TryGetStartupEpubPath()
    {
        // ---- packaged: AppLifecycle file activation ------------------------
        try
        {
            AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind == ExtendedActivationKind.File &&
                activation.Data is IFileActivatedEventArgs fileArgs &&
                fileArgs.Files.Count > 0)
            {
                return fileArgs.Files[0].Path;
            }
        }
        catch
        {
            // Activation info can be unavailable in exotic hosts — fall
            // through to the command-line path.
        }

        // ---- unpackaged: .epub path on the command line --------------------
        // GetCommandLineArgs()[0] is the exe itself; look for the first
        // argument that is an existing .epub file.
        return Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(a => a.EndsWith(".epub", StringComparison.OrdinalIgnoreCase) &&
                                 File.Exists(a));
    }
}
