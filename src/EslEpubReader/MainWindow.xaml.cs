// ============================================================================
// MainWindow.xaml.cs
// ============================================================================
// Code-behind for the reader window. This is the "conductor" of the app:
//
//   * opens ePub files via the Windows file picker,
//   * hands them to EpubParserService and shows the chapter list,
//   * hosts the WebView2 that renders chapters,
//   * injects the JavaScript that watches for TEXT SELECTIONS,
//   * receives selection messages and fires all THREE lookups in parallel:
//       1. English–English dictionary        (online, dictionaryapi.dev)
//       2. Bing Dict                         (online, ranked word translations)
//       3. Bing Translator                   (online, whole-selection MT —
//                                             also handles full SENTENCES)
//   * applies the reader's typography choices (text zoom, font family,
//     line spacing) to every chapter via injected CSS.
//
// THE SELECTION → LOOKUP DATA FLOW (the app's must-have feature):
//
//   user selects text in chapter
//        │  (JavaScript 'mouseup'/'dblclick'/'keyup' listeners)
//        ▼
//   window.chrome.webview.postMessage({type:"selection", text:"..."})
//        │  (WebView2 bridge)
//        ▼
//   CoreWebView2_WebMessageReceived  (C#, UI thread)
//        │  normalize text, cancel any older lookup
//        ▼
//   Task.WhenAll( EnglishDictionaryService.LookupAsync,   ┐ skipped for long
//                 BingDictionaryService.LookupAsync,      ┘ sentence selections
//                 BingTranslateService.TranslateAsync )   ── always runs
//        │
//        ▼
//   side panel updated (definitions, pinyin, 繁體字, full translation)
// ============================================================================

using System.Text;
using System.Text.Json;
using EslEpubReader.Models;
using EslEpubReader.Services;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace EslEpubReader;

public sealed partial class MainWindow : Window
{
    // ------------------------------------------------------------ constants

    /// <summary>
    /// Fake host name that WebView2 maps onto the extracted-book folder.
    /// Chapters are then loaded from "https://epub.reader.local/…", which
    /// (a) makes the publisher's relative CSS/image links work unchanged and
    /// (b) keeps the content inside a proper secure origin (no file:// mess).
    /// </summary>
    private const string VirtualHost = "epub.reader.local";

    /// <summary>
    /// Selections up to this length are treated as a WORD/PHRASE and sent to
    /// the two dictionaries (dictionaries cannot explain whole sentences —
    /// they would just produce noise for every individual word).
    /// </summary>
    private const int MaxDictionaryTermLength = 60;

    /// <summary>Dictionaries are also skipped for selections with more words
    /// than this (a 6+ word selection is a sentence, not a phrase).</summary>
    private const int MaxDictionaryTermWords = 5;

    /// <summary>Hard cap for Bing Translator — selections longer than this
    /// (several paragraphs) are ignored entirely to avoid abusing the free
    /// endpoint and flooding the panel.</summary>
    private const int MaxTranslationLength = 500;

    // ------------------------------------------------------------- services

    private readonly EpubParserService _epubParser = new();
    private readonly EnglishDictionaryService _englishDict = new();
    private readonly BingDictionaryService _bingDict = new();
    private readonly BingTranslateService _translator = new();

    /// <summary>Persists the reading session (last book / chapter / scroll
    /// position) across app launches — see Services/SettingsService.cs.</summary>
    private readonly SettingsService _settings = new();

    // --------------------------------------------------- text-to-speech
    // Windows BUILT-IN speech synthesis (Windows.Media.SpeechSynthesis) —
    // no cloud service, no extra package: the same voices as Windows
    // Narrator / Settings > Time & language > Speech.
    //
    //   SpeechSynthesizer  : turns text into an in-memory audio stream.
    //   MediaPlayer        : plays that stream through the default speakers.
    //
    // Both objects are created once and reused; assigning a new Source to
    // the MediaPlayer automatically stops whatever was still playing, which
    // gives exactly the wanted behavior when the user selects a new word
    // mid-playback.

    /// <summary>Synthesizes the selected text into audio (see InitializeSpeech
    /// for the English-voice selection logic).</summary>
    private readonly SpeechSynthesizer _speech = new();

    /// <summary>Plays the synthesized audio stream.</summary>
    private readonly MediaPlayer _mediaPlayer = new();

    // ---------------------------------------------------------------- state

    /// <summary>The currently open book, or null before the first open.</summary>
    private EpubBook? _book;

    /// <summary>
    /// Cancels the in-flight dictionary lookups when the user selects a NEW
    /// term before the previous one finished. Without this, a slow network
    /// answer for an old word could overwrite the results of a newer word.
    /// </summary>
    private CancellationTokenSource? _lookupCts;

    /// <summary>Current text zoom of the reader (1.0 = 100%).</summary>
    private double _zoom = 1.0;

    /// <summary>CSS font-family override for the chapter text, taken from the
    /// toolbar ComboBox. Empty string = keep the book's own fonts.</summary>
    private string _fontFamily = "";

    /// <summary>CSS line-height override (e.g. "1.6"), taken from the toolbar
    /// ComboBox. Empty string = keep the book's default spacing.</summary>
    private string _lineHeight = "";

    /// <summary>Page side margin in em (LEFT and RIGHT are always equal,
    /// like a book page), taken from the toolbar ComboBox. Empty string =
    /// the built-in default (2.5em).</summary>
    private string _pageMargin = "";

    /// <summary>True = dual-page layout (side-by-side columns that flip
    /// horizontally, like an open book); false = the default single
    /// continuous vertically-scrolling page.</summary>
    private bool _dualPage;

    /// <summary>
    /// Guards settings WRITES during construction. The typography ComboBoxes
    /// raise SelectionChanged while XAML is still parsing (SelectedIndex="0")
    /// and again while the constructor re-selects the remembered items —
    /// both BEFORE/DURING restore. Saving at those moments would overwrite
    /// the settings file with defaults. Set to true at the end of the
    /// constructor, after every stored value has been re-applied.
    /// </summary>
    private bool _settingsRestored;

    /// <summary>True once the CoreWebView2 runtime finished initializing.</summary>
    private bool _webViewReady;

    // ------------------------------------------- reading-position tracking

    /// <summary>
    /// Scroll fraction (0 = top … 1 = bottom) to restore ONCE after the next
    /// chapter finishes loading. Set when the last session's book is
    /// reopened; consumed (and cleared) in CoreWebView2_NavigationCompleted.
    /// Null = the next chapter starts at the top as usual.
    /// </summary>
    private double? _pendingScrollFraction;

    /// <summary>Live scroll fraction of the current chapter, continuously
    /// updated by "scroll" messages from the injected JavaScript. This is
    /// the value that gets persisted as "the page the user was reading".</summary>
    private double _currentScrollFraction;

    /// <summary>Timestamp of the last settings write triggered by scrolling,
    /// used to throttle disk writes to at most one every few seconds.</summary>
    private DateTime _lastScrollSave = DateTime.MinValue;

    /// <summary>The term currently shown in the dictionary panel — what the
    /// header's speak button pronounces when pressed. Empty until the first
    /// lookup of the session.</summary>
    private string _lastLookedUpTerm = "";

    // ---------------------------------------------------------- construction

    public MainWindow()
    {
        InitializeComponent();
        Title = "ESL ePub Reader";

        // ---- Modern Windows 11 chrome --------------------------------------
        // MICA: the desktop wallpaper subtly tints the window background.
        // RootGrid deliberately has NO background brush, so the material is
        // visible behind the floating pane cards. Mica needs Win11; on older
        // systems fall back to Acrylic, and if even that is unavailable the
        // theme fallback brush is applied in ApplyThemeVisuals().
        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop();
        else if (DesktopAcrylicController.IsSupported())
            SystemBackdrop = new DesktopAcrylicBackdrop();

        // Extend our XAML into the title-bar area and register AppTitleBar
        // as the drag region; Windows overlays its caption buttons on top.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Taskbar / Alt+Tab icon: the same open-book mark the in-app title
        // bar shows (Segoe Fluent glyph E736, pre-rendered into app.ico by
        // tools/make-icon.ps1). Without SetIcon, an UNPACKAGED WinUI 3
        // window shows a generic icon in the taskbar even though the exe
        // itself embeds one. File.Exists guards the single-file publish
        // case, where loose content may sit elsewhere — the embedded exe
        // icon still covers Explorer/pinned shortcuts there.
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        // Restore the persisted session data (last book/chapter/position/
        // theme) BEFORE anything else can query it.
        _settings.Load();

        // Apply the remembered theme (or follow the Windows setting when the
        // user never toggled). Must run after InitializeComponent so the
        // named elements the visuals touch already exist.
        RootGrid.RequestedTheme = _settings.Current.Theme switch
        {
            "Dark" => ElementTheme.Dark,
            "Light" => ElementTheme.Light,
            _ => ElementTheme.Default,     // follow the OS setting
        };
        ApplyThemeVisuals();

        // Populate the translation-language picker and restore the last
        // choice (default: Traditional Chinese). Setting SelectedItem fires
        // LanguageCombo_SelectionChanged, which pushes the code into both
        // Bing services and refreshes the section headers — so a single
        // code path covers startup AND user changes.
        LanguageCombo.ItemsSource = LanguageCatalog.All;
        LanguageCombo.SelectedItem = LanguageCatalog.FromCode(_settings.Current.TargetLanguageCode);

        // Restore the remembered typography. The ComboBox re-selections fire
        // ReaderStyleCombo_SelectionChanged, which re-applies the CSS values —
        // the same code path as a live user change (but no premature save:
        // _settingsRestored is still false here).
        _zoom = Math.Clamp(_settings.Current.ReaderZoom, 0.5, 3.0);
        SelectComboItemByTag(FontFamilyCombo, _settings.Current.ReaderFontFamily);
        SelectComboItemByTag(LineSpacingCombo, _settings.Current.ReaderLineHeight);
        SelectComboItemByTag(MarginCombo, _settings.Current.ReaderPageMargin);
        _ = ApplyReaderStyleAsync();   // refresh the zoom label with the restored value

        // From here on, control changes are USER changes — persist them.
        _settingsRestored = true;

        // On close: persist the final reading position, free the temp
        // extraction folder, and release the native audio resources
        // (MediaPlayer/SpeechSynthesizer hold OS handles).
        Closed += (_, _) =>
        {
            SaveReadingPosition();
            EpubParserService.TryCleanupExtractedFolder(_book?.ExtractedFolder);
            _mediaPlayer.Dispose();
            _speech.Dispose();
        };

        // Synchronous setup that must not wait: pick the TTS voice before
        // the first lookup can possibly happen.
        InitializeSpeech();

        // Fire-and-forget async init. "async void" is acceptable ONLY for
        // top-level event/lifecycle handlers like this one, and both called
        // methods catch their own exceptions.
        _ = InitializeAsync();
    }

    /// <summary>Startup work that cannot run in the constructor because it awaits.</summary>
    private async Task InitializeAsync()
    {
        await InitializeWebViewAsync();     // must be ready before a book opens

        // A book the user explicitly launched us with (double-clicked .epub,
        // file activation from the Store package) beats session restore.
        if (App.StartupEpubPath is string startupBook && File.Exists(startupBook) && _webViewReady)
            await OpenBookAsync(startupBook);
        else
            await ReopenLastBookAsync();    // "continue where you left off"
    }

    /// <summary>
    /// Select the ComboBoxItem whose Tag equals the given value — the
    /// reverse of what ReaderStyleCombo_SelectionChanged reads. Used to
    /// restore the remembered font/spacing at startup. An unknown tag (e.g.
    /// a hand-edited settings file) leaves the current selection (the XAML
    /// default, index 0) untouched.
    /// </summary>
    private static void SelectComboItemByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && (item.Tag as string ?? "") == tag)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    // ================================================= session persistence

    /// <summary>
    /// If a book was open when the app last closed AND that file still
    /// exists, reopen it automatically. OpenBookAsync recognizes it as the
    /// remembered book and jumps to the saved chapter + scroll position.
    /// </summary>
    private async Task ReopenLastBookAsync()
    {
        string lastBook = _settings.Current.LastBookPath;
        if (lastBook.Length == 0) return;            // first-ever launch

        if (!File.Exists(lastBook))
        {
            // The file was moved/deleted since last time — forget it so we
            // don't retry (and fail) on every future launch.
            _settings.Current.LastBookPath = "";
            _settings.Save();
            return;
        }

        if (!_webViewReady) return;   // WebView2 failed to start; user saw the error
        await OpenBookAsync(lastBook);
    }

    /// <summary>
    /// Persist "where the user is right now": book path, chapter index, and
    /// scroll fraction. Called on chapter changes, throttled scrolls, and
    /// app close.
    /// </summary>
    private void SaveReadingPosition()
    {
        if (_book is null) return;    // nothing open — keep previous values

        _settings.Current.LastChapterIndex = ChapterList.SelectedIndex;
        _settings.Current.LastScrollFraction = _currentScrollFraction;
        _settings.Save();
    }

    // ====================================================== panel visibility

    /// <summary>Chapter-panel width to restore when it is unhidden (the
    /// user may have dragged the splitter before hiding it).</summary>
    private double _savedChapterWidth = 240;

    /// <summary>Dictionary-panel width to restore when it is unhidden.</summary>
    private double _savedDictWidth = 380;

    /// <summary>Toolbar toggle: show/hide the CHAPTERS panel (left card).</summary>
    private void ChapterPaneToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // Fires once during XAML parse (IsChecked="True") before the named
        // elements below exist — skip that call, the panel is visible anyway.
        if (ChapterPane is null || ChapterColumn is null || ChapterSplitter is null) return;

        SetPaneVisibility(
            visible: ChapterPaneToggle.IsChecked == true,
            pane: ChapterPane, splitter: ChapterSplitter, column: ChapterColumn,
            restoredMinWidth: 140, savedWidth: ref _savedChapterWidth);

        ReturnFocusToReader();
    }

    /// <summary>Toolbar toggle: show/hide the DICTIONARY panel (right card).</summary>
    private void DictPaneToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (DictPane is null || DictColumn is null || DictSplitter is null) return;

        SetPaneVisibility(
            visible: DictPaneToggle.IsChecked == true,
            pane: DictPane, splitter: DictSplitter, column: DictColumn,
            restoredMinWidth: 240, savedWidth: ref _savedDictWidth);

        ReturnFocusToReader();
    }

    /// <summary>
    /// Hand keyboard focus back to the reading page. Clicking any toolbar
    /// control moves focus OUT of the WebView, which silently kills the
    /// reading keys (PgDn/PgUp page flips run inside the web page) until
    /// the user clicks the text again — so every control that changes the
    /// reading layout calls this after doing its work.
    /// </summary>
    private void ReturnFocusToReader() =>
        ReaderWebView?.Focus(FocusState.Programmatic);

    /// <summary>
    /// Show or hide one side panel. Hiding needs THREE coordinated changes,
    /// because a Grid column is not an element:
    ///
    ///   1. collapse the card  — stops rendering + hit-testing its content;
    ///   2. collapse the splitter — a drag handle for a hidden pane would be
    ///      a confusing 10px dead zone;
    ///   3. zero the COLUMN — and crucially also its MinWidth, because a
    ///      Grid enforces MinWidth even on a zero-width column (the layout
    ///      would keep a 140/240px hole without this).
    ///
    /// The current width is remembered on hide and restored on show, so a
    /// pane comes back exactly as wide as the user had dragged it. The
    /// star-sized reader column automatically absorbs/returns the space.
    /// </summary>
    /// <param name="visible">Target state: true = show, false = hide.</param>
    /// <param name="pane">The card Border to collapse/restore.</param>
    /// <param name="splitter">The pane's adjacent drag handle.</param>
    /// <param name="column">The pane's ColumnDefinition.</param>
    /// <param name="restoredMinWidth">The MinWidth the column gets back when
    /// shown (matches the value declared in XAML).</param>
    /// <param name="savedWidth">Storage slot for the width across hide/show.</param>
    private static void SetPaneVisibility(
        bool visible, FrameworkElement pane, FrameworkElement splitter,
        ColumnDefinition column, double restoredMinWidth, ref double savedWidth)
    {
        if (visible)
        {
            column.MinWidth = restoredMinWidth;
            column.Width = new GridLength(savedWidth);
            pane.Visibility = Visibility.Visible;
            splitter.Visibility = Visibility.Visible;
        }
        else
        {
            // Remember the width the user had (ActualWidth is 0 if we are
            // somehow already collapsed — keep the previous value then).
            if (column.ActualWidth > 0) savedWidth = column.ActualWidth;

            column.MinWidth = 0;                  // MUST precede Width = 0
            column.Width = new GridLength(0);
            pane.Visibility = Visibility.Collapsed;
            splitter.Visibility = Visibility.Collapsed;
        }
    }

    // ==================================================== translation language

    /// <summary>
    /// Translation-language picker (dictionary panel header). One handler
    /// serves both startup restore and user changes:
    ///
    ///   1. push the language code into BOTH Bing services (translation
    ///      and dictionary — they cache per-language, so switching back and
    ///      forth reuses earlier answers);
    ///   2. persist the choice so the next launch starts in this language;
    ///   3. retitle the two section headers with the language's native name;
    ///   4. if a term is already on display, re-run the lookup so the panel
    ///      switches language immediately — WITHOUT re-reading the term
    ///      aloud (nothing new was selected).
    /// </summary>
    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageCombo.SelectedItem is not TranslationLanguage language) return;

        _translator.TargetLanguage = language.Code;
        _bingDict.TargetLanguage = language.Code;

        _settings.Current.TargetLanguageCode = language.Code;
        _settings.Save();

        // Section headers show the native name ("繁體中文", "日本語", …) so
        // the reader instantly sees which language the results are in.
        TranslateSectionHeader.Text = $"Bing Translator ({language.ShortName})";
        DictSectionHeader.Text = $"Bing Dict ({language.ShortName})";

        if (_lastLookedUpTerm.Length > 0)
            _ = LookupAllSourcesAsync(_lastLookedUpTerm, speakAloud: false);
    }

    // ======================================================== day/night theme

    /// <summary>True when the UI is currently rendered in the dark theme.
    /// ActualTheme (not RequestedTheme) is used so "Default" correctly
    /// resolves to whatever Windows is set to.</summary>
    private bool IsDarkTheme => RootGrid.ActualTheme == ElementTheme.Dark;

    /// <summary>
    /// Toolbar day/night button. Toggles between explicit Light and Dark
    /// (the first click "captures" whatever the OS default resolved to and
    /// flips it), persists the choice, and refreshes every themed visual.
    /// </summary>
    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ElementTheme next = IsDarkTheme ? ElementTheme.Light : ElementTheme.Dark;

        // Setting RequestedTheme on the ROOT element re-resolves every
        // {ThemeResource} in the tree instantly — that is the whole switch.
        RootGrid.RequestedTheme = next;

        _settings.Current.Theme = next == ElementTheme.Dark ? "Dark" : "Light";
        _settings.Save();

        ApplyThemeVisuals();
    }

    /// <summary>
    /// Everything theme-related that {ThemeResource} bindings CANNOT cover:
    ///
    ///   * the toggle's own icon — shows the theme you would switch TO
    ///     (moon while light, sun while dark);
    ///   * the Windows caption buttons (min/max/close) — they are drawn by
    ///     the OS outside the XAML tree, so their colors are set through
    ///     AppWindow.TitleBar;
    ///   * the WebView2 default background — prevents a white flash between
    ///     navigations in dark mode;
    ///   * the book content itself — ApplyReaderStyleAsync injects a
    ///     night-reading stylesheet when dark (re-run here on every switch);
    ///   * a solid fallback window background for machines where neither
    ///     Mica nor Acrylic exists (SystemBackdrop stayed null).
    /// </summary>
    private void ApplyThemeVisuals()
    {
        bool dark = IsDarkTheme;

        // Icon swap via visibility — the two FontIcons live in XAML, which
        // avoids building "\uE7xx" glyph strings in C#.
        SunIcon.Visibility = dark ? Visibility.Visible : Visibility.Collapsed;
        MoonIcon.Visibility = dark ? Visibility.Collapsed : Visibility.Visible;

        // Caption buttons: transparent background so Mica shows through,
        // foreground matched to the theme, gentle hover wash.
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        titleBar.ButtonHoverForegroundColor = titleBar.ButtonForegroundColor;
        titleBar.ButtonHoverBackgroundColor = dark
            ? Microsoft.UI.ColorHelper.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
            : Microsoft.UI.ColorHelper.FromArgb(0x14, 0x00, 0x00, 0x00);

        // Match the WebView2's pre-render surface to the reading background
        // so chapter navigations never flash the opposite color.
        ReaderWebView.DefaultBackgroundColor = dark
            ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x1E, 0x1E, 0x1E)
            : Microsoft.UI.Colors.White;

        // No backdrop material available (old Windows 10): paint a solid
        // themed background so the window is not transparent black.
        if (SystemBackdrop is null)
            RootGrid.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];

        // Restyle the book content (night-reading CSS on/off). Fire-and-
        // forget: it no-ops until the WebView is ready.
        _ = ApplyReaderStyleAsync();
    }

    // ======================================================= text-to-speech

    /// <summary>
    /// Configure the speech synthesizer for ESL use:
    ///
    ///   * VOICE — the system default voice follows the Windows display
    ///     language, which for our target users is often Chinese; reading
    ///     English text with a Chinese voice sounds wrong. So we explicitly
    ///     pick an installed ENGLISH voice: en-US preferred, then any "en-*"
    ///     (en-GB, en-AU, …). If none is installed we keep the default and
    ///     let Windows do its best.
    ///
    ///   * RATE — slowed slightly below normal (0.9×) so learners can hear
    ///     each syllable; still natural enough not to sound robotic.
    /// </summary>
    private void InitializeSpeech()
    {
        try
        {
            VoiceInformation? englishVoice =
                SpeechSynthesizer.AllVoices.FirstOrDefault(
                    v => v.Language.StartsWith("en-US", StringComparison.OrdinalIgnoreCase))
                ?? SpeechSynthesizer.AllVoices.FirstOrDefault(
                    v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase));

            if (englishVoice is not null)
                _speech.Voice = englishVoice;

            _speech.Options.SpeakingRate = 0.9;   // 1.0 = normal speed
        }
        catch
        {
            // No voices installed at all (rare) — lookups still work, only
            // the audio is silently unavailable.
        }
    }

    /// <summary>
    /// Speak the given text through the default audio output using the
    /// Windows built-in voice chosen in InitializeSpeech. Called by the
    /// lookup pipeline when the "Read aloud" toolbar toggle is ON.
    /// Assigning a new Source interrupts any previous playback, so rapid
    /// consecutive selections never overlap audibly.
    /// </summary>
    private async Task SpeakSelectionAsync(string text)
    {
        try
        {
            // Synthesize to an in-memory WAV-like stream…
            SpeechSynthesisStream stream = await _speech.SynthesizeTextToStreamAsync(text);

            // …and hand it to the media player. CreateFromStream needs the
            // stream's MIME type, which the synthesizer provides.
            _mediaPlayer.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            _mediaPlayer.Play();
        }
        catch (Exception ex)
        {
            // e.g. audio device unavailable. Never crash a lookup over sound.
            StatusText.Text = $"Text-to-speech failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Speak button at the end of the selected text in the dictionary panel.
    /// Re-reads the current term ON DEMAND — deliberately independent of the
    /// toolbar "Read aloud" toggle, so a learner who keeps automatic speech
    /// OFF can still hear a specific word, and anyone can replay a tricky
    /// pronunciation as many times as they like.
    /// </summary>
    private void SpeakTermButton_Click(object sender, RoutedEventArgs e)
    {
        // The button is disabled until the first lookup, so the term is
        // always non-empty here; the guard is just defense in depth.
        if (_lastLookedUpTerm.Length > 0)
            _ = SpeakSelectionAsync(_lastLookedUpTerm);
    }

    /// <summary>
    /// Toolbar "Read aloud" toggle. Handles BOTH Checked and Unchecked:
    /// updates the speaker/muted glyph, and when switched OFF also stops any
    /// speech that is currently playing (turning a feature off should take
    /// effect immediately, not after the current sentence finishes).
    /// </summary>
    private void ReadAloudToggle_Toggled(object sender, RoutedEventArgs e)
    {
        bool enabled = ReadAloudToggle.IsChecked == true;

        // The icon element can still be null while XAML is loading (this
        // handler fires for the initial IsChecked="True" during parse).
        if (ReadAloudIcon is not null)
            ReadAloudIcon.Glyph = enabled ? "" : "";   // speaker / muted glyphs

        if (!enabled)
            _mediaPlayer.Pause();   // cut off any in-progress speech now
    }

    // ============================================================ WebView2

    /// <summary>
    /// Create the CoreWebView2 (the Chromium engine behind the XAML control)
    /// and wire up the selection-watching script + the message bridge.
    /// </summary>
    private async Task InitializeWebViewAsync()
    {
        try
        {
            // EnsureCoreWebView2Async spins up the WebView2 runtime process.
            // (Windows 11 ships the runtime; see README for Windows 10.)
            await ReaderWebView.EnsureCoreWebView2Async();
            CoreWebView2 core = ReaderWebView.CoreWebView2;

            // Reader hygiene: the book is a document, not a browser session.
            core.Settings.AreDefaultContextMenusEnabled = true;  // keep copy/paste
            core.Settings.AreDevToolsEnabled = false;            // no F12 for end users
            core.Settings.IsStatusBarEnabled = false;            // hide link status bubble

            // Inject the selection watcher into EVERY document before it runs
            // its own scripts, so even the first chapter reports selections.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(SelectionWatcherScript);

            // The JS side calls postMessage(...) -> this event on the UI thread.
            core.WebMessageReceived += CoreWebView2_WebMessageReceived;

            // After every navigation: re-apply typography and, when resuming
            // a previous session, jump to the saved reading position.
            core.NavigationCompleted += CoreWebView2_NavigationCompleted;

            _webViewReady = true;
            StatusText.Text = "Ready. Open an .epub file to start reading.";
        }
        catch (Exception ex)
        {
            // Most common cause: WebView2 runtime missing (older Windows 10).
            StatusText.Text = $"WebView2 could not start: {ex.Message} " +
                              "(install the WebView2 Runtime from Microsoft and restart).";
        }
    }

    /// <summary>
    /// JavaScript injected into every chapter document. Two responsibilities:
    ///
    /// 1. SELECTION WATCHING — reports the current text selection to C#
    ///    whenever the user finishes selecting:
    ///      * mouseup    — after dragging a selection with the mouse,
    ///      * dblclick   — double-clicking a single word (fastest gesture),
    ///      * keyup      — keyboard selection with Shift+arrows.
    ///    A tiny setTimeout(…, 0) lets the browser finalize the selection
    ///    before we read it; duplicates are suppressed on the JS side.
    ///
    /// 2. SCROLL TRACKING — reports how far down the chapter the reader is,
    ///    as a FRACTION (0 = top, 1 = bottom), debounced to fire only after
    ///    scrolling pauses for 400 ms. C# persists this so the next launch
    ///    can reopen the book at the exact same reading position.
    /// </summary>
    private const string SelectionWatcherScript =
        """
        (function () {
            // ---------- 1. selection watching ----------
            let lastSent = "";

            function reportSelection() {
                const sel = window.getSelection();
                const text = sel ? sel.toString().trim() : "";

                if (text.length === 0) {          // selection was cleared
                    lastSent = "";
                    return;
                }
                if (text === lastSent) return;    // same selection as before
                lastSent = text;

                // Hand the selection to the C# host. postMessage delivers a
                // JSON string to CoreWebView2.WebMessageReceived.
                window.chrome.webview.postMessage(
                    JSON.stringify({ type: "selection", text: text }));
            }

            // Defer with setTimeout so the selection object is final.
            document.addEventListener("mouseup",  () => setTimeout(reportSelection, 0));
            document.addEventListener("dblclick", () => setTimeout(reportSelection, 0));
            document.addEventListener("keyup", (e) => {
                // Only react to keys that can change a selection.
                if (e.key === "Shift" || e.key.startsWith("Arrow"))
                    setTimeout(reportSelection, 0);
            });

            // ---------- 2. scroll-position tracking ----------
            let scrollTimer = null;

            // Where in the chapter is the reader, as a 0..1 fraction?
            // Works in BOTH layout modes:
            //   * dual-page: the BODY overflows horizontally (CSS columns) —
            //     detected by scrollWidth > clientWidth — so the fraction is
            //     measured along the horizontal axis;
            //   * single-page: the window scrolls vertically as usual.
            function currentScrollFraction() {
                const body = document.body;
                if (body && body.scrollWidth > body.clientWidth + 1) {
                    const max = body.scrollWidth - body.clientWidth;
                    return max > 0 ? Math.min(1, Math.max(0, body.scrollLeft / max)) : 0;
                }
                const max = document.documentElement.scrollHeight - window.innerHeight;
                return max > 0 ? Math.min(1, Math.max(0, window.scrollY / max)) : 0;
            }

            // The last known dual-page reading position as a 0..1 fraction.
            // Kept for the RESIZE handler below: when the viewport changes
            // (hiding/showing the side panels, resizing the window), the
            // multicol layout REFLOWS into different column geometry and the
            // browser's preserved pixel scrollLeft becomes meaningless —
            // this fraction is what lets us put the reader back on the same
            // part of the chapter, aligned to a whole page pair.
            let lastDualFraction = 0;

            // capture:true because in dual-page mode the scroll happens on
            // the BODY element, and element scroll events do NOT bubble —
            // capturing at the document level sees both modes' events.
            document.addEventListener("scroll", () => {
                // Debounce: report once scrolling has PAUSED, not for every
                // wheel tick — keeps the message channel quiet.
                if (scrollTimer) clearTimeout(scrollTimer);
                scrollTimer = setTimeout(() => {
                    const fraction = currentScrollFraction();
                    const body = document.body;
                    if (body && body.scrollWidth > body.clientWidth + 1)
                        lastDualFraction = fraction;
                    window.chrome.webview.postMessage(JSON.stringify(
                        { type: "scroll", fraction: fraction }));
                }, 400);
            }, { capture: true, passive: true });

            // ---------- 3. dual-page paging primitives ----------
            // Dual-page mode is STRICTLY PAGINATED: the view always shows
            // two COMPLETE pages, and every navigation jumps whole page
            // pairs. There is no free-form scrolling in this mode (the
            // scrollbar is hidden by the injected CSS; the wheel is
            // intercepted below), so a half-scrolled, half-column state is
            // unreachable.
            //
            // One "page pair" advances the scroll by the column period:
            // content width (client width minus the body's own padding)
            // plus one column gap — that is exactly where the next column
            // pair starts in a CSS multicol overflow layout.
            function dualPageStep() {
                const cs = getComputedStyle(document.body);
                return document.body.clientWidth
                     - (parseFloat(cs.paddingLeft)  || 0)
                     - (parseFloat(cs.paddingRight) || 0)
                     + (parseFloat(cs.columnGap)    || 0);
            }

            // Show the NEXT (+1) or PREVIOUS (-1) page pair. The current
            // position is snapped to the nearest pair boundary first (a
            // safety net for any stray offset), so the result is always an
            // exact pair start. The end clamp is safe too: maxScroll in a
            // multicol layout is itself a whole-column boundary, so even
            // the chapter's final view shows only complete columns.
            function flipPage(direction) {
                const body = document.body;
                const step = dualPageStep();
                const maxScroll = body.scrollWidth - body.clientWidth;
                const target = Math.round(body.scrollLeft / step) * step + direction * step;
                body.scrollLeft = Math.max(0, Math.min(target, maxScroll));
                // Record the position immediately (not just via the debounced
                // scroll reporter) so a resize right after a flip restores
                // the correct place.
                lastDualFraction = maxScroll > 0 ? body.scrollLeft / maxScroll : 0;
            }

            // ---------- viewport resize = re-align to a whole page pair ----
            // Hiding/showing the Chapters or Dictionary panel (or resizing
            // the window) REFLOWS the multicol layout: column widths and the
            // page period change, and the browser keeps the OLD pixel
            // scrollLeft — which now points into the middle of a column, so
            // the view shows sliced pages and subsequent PgDn steps from a
            // misaligned base. Fix: after the reflow settles, put the reader
            // back at the remembered FRACTION of the chapter, snapped to the
            // nearest whole page pair of the NEW geometry.
            let resizeTimer = null;
            window.addEventListener("resize", () => {
                const body = document.body;
                if (!body || body.scrollWidth <= body.clientWidth + 1) return;   // single-page: browser handles it
                if (resizeTimer) clearTimeout(resizeTimer);
                resizeTimer = setTimeout(() => {
                    const step = dualPageStep();
                    const maxScroll = body.scrollWidth - body.clientWidth;
                    const target = Math.round((lastDualFraction * maxScroll) / step) * step;
                    body.scrollLeft = Math.max(0, Math.min(target, maxScroll));

                    // Force a full repaint: after a resize reflow the
                    // renderer can leave STALE column-rule strokes from the
                    // old geometry on screen (ghost vertical lines through
                    // the text). Blink skips the visibility no-op unless the
                    // layout is flushed in between — hence the offsetHeight
                    // read.
                    body.style.visibility = "hidden";
                    void body.offsetHeight;
                    body.style.visibility = "";
                }, 150);   // debounced: splitter drags fire resize storms
            });

            // Wheel = flip EXACTLY one page pair per gesture — "wheel down
            // shows the next two pages", never a partial scroll. Small
            // deltas accumulate so precision touchpads still flip; the
            // cooldown swallows the inertia tail of a flick so one gesture
            // means one page turn. passive:false because preventDefault()
            // must suppress the default free-form scrolling.
            let wheelAccumulator = 0;
            let lastWheelFlip = 0;
            window.addEventListener("wheel", (e) => {
                const body = document.body;
                if (!body || body.scrollWidth <= body.clientWidth + 1 || e.ctrlKey) return;
                e.preventDefault();
                const now = Date.now();
                if (now - lastWheelFlip < 250) return;    // inertia guard
                wheelAccumulator += e.deltaY;
                if (Math.abs(wheelAccumulator) < 40) return;
                flipPage(Math.sign(wheelAccumulator));
                wheelAccumulator = 0;
                lastWheelFlip = now;
            }, { passive: false });

            // ---------- 4. keyboard paging in dual-page mode ----------
            // PageDown/PageUp (plus Space, plain arrows, Home/End) normally
            // scroll VERTICALLY — which dual-page mode locks, so the keys
            // appeared dead. Map them to whole page-pair flips instead:
            // PgDn = show the NEXT two pages, PgUp = the previous two.
            window.addEventListener("keydown", (e) => {
                const body = document.body;
                // Single-page mode: leave every key to its default behavior.
                if (!body || body.scrollWidth <= body.clientWidth + 1) return;
                // Never hijack keys that are part of a keyboard shortcut,
                // or Shift+arrows (used to extend the text selection).
                if (e.ctrlKey || e.altKey || e.metaKey) return;

                if (e.key === "Home") { body.scrollLeft = 0; e.preventDefault(); return; }
                if (e.key === "End")  { body.scrollLeft = body.scrollWidth; e.preventDefault(); return; }

                let direction = 0;
                if (e.key === "PageDown" || e.key === "ArrowRight" ||
                    (e.key === " " && !e.shiftKey)) direction = 1;       // next pair
                else if (e.key === "PageUp" || e.key === "ArrowLeft" ||
                    (e.key === " " && e.shiftKey)) direction = -1;       // previous pair
                if (direction === 0 || (e.shiftKey && e.key !== " ")) return;

                flipPage(direction);
                e.preventDefault();
            }, { capture: true });
        })();
        """;

    /// <summary>
    /// Runs when a chapter has finished loading. Two jobs, in order:
    ///
    ///  1. Re-apply the reader's typography (zoom/font/line-spacing) — a
    ///     freshly loaded document knows nothing about earlier choices.
    ///     This must happen FIRST because changing typography reflows the
    ///     text and therefore changes the page height.
    ///
    ///  2. If this navigation is the "resume last session" one, scroll to
    ///     the saved fraction of the page. The fraction is consumed exactly
    ///     once (_pendingScrollFraction is cleared), so normal chapter
    ///     navigation afterwards starts at the top as expected.
    ///
    /// Note: scrollHeight can still grow slightly afterwards if the chapter
    /// contains images that load lazily; using a FRACTION (not pixels) keeps
    /// the landing spot close to the right paragraph even then.
    /// </summary>
    private async void CoreWebView2_NavigationCompleted(
        CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        await ApplyReaderStyleAsync();

        if (_pendingScrollFraction is double fraction)
        {
            _pendingScrollFraction = null;   // one-shot: consume it

            // Seed the live tracker too, so closing the app right away
            // (before any scroll event fires) re-saves the same position.
            _currentScrollFraction = fraction;

            // Mirror of currentScrollFraction() in the injected script:
            // horizontal restore in dual-page mode (SNAPPED to a whole page
            // pair — a raw fractional offset would show sliced columns),
            // vertical otherwise.
            string js =
                $$"""
                (function () {
                    const f = {{fraction.ToString(System.Globalization.CultureInfo.InvariantCulture)}};
                    const body = document.body;
                    if (body && body.scrollWidth > body.clientWidth + 1) {
                        const cs = getComputedStyle(body);
                        const step = body.clientWidth
                                   - (parseFloat(cs.paddingLeft)  || 0)
                                   - (parseFloat(cs.paddingRight) || 0)
                                   + (parseFloat(cs.columnGap)    || 0);
                        const max = body.scrollWidth - body.clientWidth;
                        const target = Math.round((f * max) / step) * step;
                        body.scrollLeft = Math.max(0, Math.min(target, max));
                        return;
                    }
                    const maxScroll =
                        document.documentElement.scrollHeight - window.innerHeight;
                    window.scrollTo(0, maxScroll > 0 ? maxScroll * f : 0);
                })();
                """;
            try { await ReaderWebView.CoreWebView2.ExecuteScriptAsync(js); }
            catch { /* document gone (rapid re-navigation) — harmless */ }
        }
    }

    /// <summary>
    /// Bridge endpoint: a chapter document sent us a message. Validate it is
    /// our selection message and start the double dictionary lookup.
    /// </summary>
    private void CoreWebView2_WebMessageReceived(
        CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            // WebMessageAsJson is the JSON-encoded form of what JS posted;
            // since JS posted a *string*, unwrap the outer string first.
            string? payload = JsonSerializer.Deserialize<string>(args.WebMessageAsJson);
            if (payload is null) return;

            using JsonDocument doc = JsonDocument.Parse(payload);
            switch (doc.RootElement.GetProperty("type").GetString())
            {
                case "selection":
                {
                    string raw = doc.RootElement.GetProperty("text").GetString() ?? "";
                    string term = NormalizeSelection(raw);
                    if (term.Length == 0) return;   // nothing lookup-worthy

                    // Fire and forget: the method manages its own cancellation
                    // and reports all outcomes via the UI, never exceptions.
                    _ = LookupAllSourcesAsync(term);
                    break;
                }
                case "scroll":
                {
                    // Track the reading position continuously in memory…
                    _currentScrollFraction = doc.RootElement.GetProperty("fraction").GetDouble();

                    // …and persist it at most once every 5 seconds, so even a
                    // crash/kill loses only a few seconds of position while
                    // normal scrolling never hammers the disk.
                    if ((DateTime.UtcNow - _lastScrollSave).TotalSeconds > 5)
                    {
                        _lastScrollSave = DateTime.UtcNow;
                        SaveReadingPosition();
                    }
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // A malformed message (e.g. from unexpected page script) is
            // simply ignored — never crash the reader over it.
        }
    }

    /// <summary>
    /// Clean a raw selection into a dictionary-friendly term:
    ///   * collapse ALL whitespace runs (incl. newlines across elements) to
    ///     single spaces,
    ///   * strip punctuation stuck to the edges ("word." / “word”),
    ///   * reject selections that are too long or contain no letters.
    /// Returns "" when the selection should be ignored.
    /// </summary>
    private static string NormalizeSelection(string raw)
    {
        // Collapse whitespace: "give\n  up" -> "give up".
        string[] parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string text = string.Join(' ', parts);

        // Trim non-letter/digit junk from both ends (quotes, commas, dashes…)
        // but keep internal punctuation like the apostrophe in "don't"
        // or the hyphen in "well-known".
        int start = 0, end = text.Length - 1;
        while (start <= end && !char.IsLetterOrDigit(text[start])) start++;
        while (end >= start && !char.IsLetterOrDigit(text[end])) end--;
        if (start > end) return "";
        text = text[start..(end + 1)];

        // Sanity limits: the generous MaxTranslationLength cap (whole
        // sentences are allowed — Bing Translator handles them), plus the
        // text must contain at least one Latin letter (selecting an
        // illustration caption of digits shouldn't trigger a lookup).
        if (text.Length is 0 or > MaxTranslationLength) return "";
        if (!text.Any(char.IsAsciiLetter)) return "";

        return text;
    }

    // ================================================== dictionary lookups

    /// <summary>
    /// THE core feature: look the selection up in all THREE sources at the
    /// same time and render whichever answers arrive:
    ///
    ///   * both dictionaries  — only for word/phrase-sized selections,
    ///   * Bing Translator   — always, including whole sentences.
    ///
    /// A newer selection cancels this one via _lookupCts, so results can
    /// never appear out of order.
    /// </summary>
    /// <param name="term">The normalized selection to look up.</param>
    /// <param name="speakAloud">False when the lookup is a REFRESH of the
    /// term already on display (e.g. after changing the translation
    /// language) — refreshes should not re-read the text aloud.</param>
    private async Task LookupAllSourcesAsync(string term, bool speakAloud = true)
    {
        // Cancel the previous lookup (if still running) and start a new scope.
        _lookupCts?.Cancel();
        _lookupCts?.Dispose();
        _lookupCts = new CancellationTokenSource();
        CancellationToken ct = _lookupCts.Token;

        // Is this selection dictionary-sized (a word or short phrase) or a
        // sentence? Dictionaries only make sense for the former; for a
        // sentence, word-by-word definitions would bury the useful answer.
        bool dictionarySized = term.Length <= MaxDictionaryTermLength &&
                               term.Split(' ').Length <= MaxDictionaryTermWords;

        // Remember the term for the panel's speak button and light it up —
        // from the first lookup on there is always something to pronounce.
        _lastLookedUpTerm = term;
        SpeakTermButton.IsEnabled = true;

        // Read the selection aloud when the toolbar toggle is ON (and this
        // is a genuinely new selection, not a language-switch refresh).
        // Fired in parallel with the lookups (not awaited): the learner
        // HEARS the pronunciation while the definitions are still loading.
        // Errors are handled inside SpeakSelectionAsync itself.
        if (speakAloud && ReadAloudToggle.IsChecked == true)
            _ = SpeakSelectionAsync(term);

        // Immediate UI feedback: show the term and a "looking up…" state
        // before any network round-trip completes.
        SelectedTermText.Text = term;
        PhoneticText.Text = "";
        EnglishStatusText.Text = dictionarySized
            ? "Looking up…"
            : "Selection is a sentence — see the Bing Translator section above.";
        EnglishStatusText.Visibility = Visibility.Visible;
        ChineseStatusText.Text = dictionarySized ? "Looking up…" : "";
        ChineseStatusText.Visibility = dictionarySized ? Visibility.Visible : Visibility.Collapsed;
        TranslationStatusText.Text = "Translating…";
        TranslationStatusText.Visibility = Visibility.Visible;
        EnglishSensesList.ItemsSource = null;
        ChineseEntriesList.ItemsSource = null;
        TranslationText.Text = "";

        try
        {
            // Start every lookup CONCURRENTLY — all three are independent
            // web calls; none of them waits for another to start.
            Task<EnglishLookupResult>? englishTask =
                dictionarySized ? _englishDict.LookupAsync(term, ct) : null;
            Task<ChineseLookupResult>? chineseTask =
                dictionarySized ? _bingDict.LookupAsync(term, ct) : null;
            Task<TranslationResult> translateTask = _translator.TranslateAsync(term, ct);

            // WhenAll over the tasks that are actually running this time.
            await Task.WhenAll(new Task[] { englishTask!, chineseTask!, translateTask }
                               .Where(t => t is not null));

            // If a newer selection arrived while we awaited, drop these
            // results silently — the newer call owns the panel now.
            if (ct.IsCancellationRequested) return;

            if (englishTask is not null) RenderEnglishResult(englishTask.Result);
            if (chineseTask is not null) RenderChineseResult(chineseTask.Result);
            RenderTranslationResult(translateTask.Result);
        }
        catch (OperationCanceledException)
        {
            // Expected when the user selected something newer — no UI change.
        }
    }

    /// <summary>Paint the English–English half of the panel.</summary>
    private void RenderEnglishResult(EnglishLookupResult result)
    {
        PhoneticText.Text = result.Phonetic;
        EnglishSensesList.ItemsSource = result.Senses;

        // Status line doubles as the "not found / offline" explanation and
        // as the "phrase decoded word-by-word" hint; hide it when empty.
        EnglishStatusText.Text = result.StatusMessage;
        EnglishStatusText.Visibility =
            result.StatusMessage.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Paint the Bing Dict section of the panel.</summary>
    private void RenderChineseResult(ChineseLookupResult result)
    {
        ChineseEntriesList.ItemsSource = result.Entries;
        ChineseStatusText.Text = result.StatusMessage;
        ChineseStatusText.Visibility =
            result.StatusMessage.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Paint the Bing Translator section of the panel.</summary>
    private void RenderTranslationResult(TranslationResult result)
    {
        TranslationText.Text = result.TranslatedText;
        TranslationStatusText.Text = result.StatusMessage;
        TranslationStatusText.Visibility =
            result.StatusMessage.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ============================================================ book open

    /// <summary>Toolbar "Open ePub": pick a file, parse it, show chapter 1.</summary>
    private async void OpenBookButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady)
        {
            StatusText.Text = "The reader engine is still starting — try again in a moment.";
            return;
        }

        // WinUI 3 desktop quirk: pickers need the owning window's HWND,
        // because (unlike UWP) there is no implicit app window context.
        var picker = new FileOpenPicker();
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".epub");

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null) return;   // user pressed Cancel

        await OpenBookAsync(file.Path);
    }

    /// <summary>Parse the chosen ePub and wire the UI to it.</summary>
    private async Task OpenBookAsync(string path)
    {
        StatusText.Text = $"Opening {Path.GetFileName(path)}…";
        try
        {
            EpubBook book = await _epubParser.ParseAsync(path);

            // Replace any previously open book: free its temp folder and
            // point the virtual host at the new book's folder instead.
            if (_book is not null)
            {
                ReaderWebView.CoreWebView2.ClearVirtualHostNameToFolderMapping(VirtualHost);
                EpubParserService.TryCleanupExtractedFolder(_book.ExtractedFolder);
            }
            _book = book;

            // Map https://epub.reader.local/ -> extracted folder. "Allow"
            // lets chapter pages fetch their own CSS/images from that origin.
            ReaderWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHost, book.ExtractedFolder,
                CoreWebView2HostResourceAccessKind.Allow);

            // ---- Resume support -------------------------------------------
            // Is this the SAME file the user was reading last session? Then
            // start at the remembered chapter and queue the remembered scroll
            // fraction (consumed after the chapter loads). Any other file
            // starts at chapter 0 / top, like a brand-new book.
            bool isRememberedBook = string.Equals(
                path, _settings.Current.LastBookPath, StringComparison.OrdinalIgnoreCase);

            int startChapter = isRememberedBook
                ? Math.Clamp(_settings.Current.LastChapterIndex, 0, book.Chapters.Count - 1)
                : 0;
            _pendingScrollFraction = isRememberedBook && _settings.Current.LastScrollFraction > 0
                ? _settings.Current.LastScrollFraction
                : null;

            // Remember THIS file as the most recent book from now on.
            _settings.Current.LastBookPath = path;
            _settings.Save();

            // Populate the chapter list; setting SelectedIndex triggers
            // ChapterList_SelectionChanged which performs the navigation.
            ChapterList.ItemsSource = book.Chapters;
            ChapterList.SelectedIndex = startChapter;

            BookTitleText.Text = $"{book.Title} — {book.Author}";
            PrevChapterButton.IsEnabled = NextChapterButton.IsEnabled = true;
            StatusText.Text = isRememberedBook
                ? $"Welcome back to “{book.Title}” — continuing where you left off."
                : $"Opened “{book.Title}” ({book.Chapters.Count} chapters). " +
                  "Select any word or phrase to look it up.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open this ePub: {ex.Message}";
        }
    }

    // ==================================================== chapter navigation

    /// <summary>Chapter list selection (user click OR programmatic) → navigate.</summary>
    private void ChapterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_book is null || ChapterList.SelectedItem is not EpubChapter chapter) return;

        // Build the chapter URL. Each path segment is escaped individually
        // so file names with spaces ("My Chapter.xhtml") form a valid URL
        // while the '/' separators stay intact.
        string escapedPath = string.Join('/',
            chapter.RelativePath.Split('/').Select(Uri.EscapeDataString));
        ReaderWebView.CoreWebView2.Navigate($"https://{VirtualHost}/{escapedPath}");

        // Update the persisted reading position. A NEW chapter starts at the
        // top (fraction 0) — unless this navigation is the session restore,
        // in which case the queued fraction is the real starting position.
        _currentScrollFraction = _pendingScrollFraction ?? 0;
        SaveReadingPosition();

        StatusText.Text = $"Chapter {chapter.SpineIndex + 1} of {_book.Chapters.Count}: {chapter.Title}";
    }

    /// <summary>Toolbar "previous chapter" — just moves the list selection.</summary>
    private void PrevChapterButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChapterList.SelectedIndex > 0)
            ChapterList.SelectedIndex--;
    }

    /// <summary>Toolbar "next chapter".</summary>
    private void NextChapterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_book is not null && ChapterList.SelectedIndex < _book.Chapters.Count - 1)
            ChapterList.SelectedIndex++;
    }

    // ================================================== reader typography
    // Text zoom, font family, and line spacing are all realized the same
    // way: a single <style id="esl-reader-style"> element is injected into
    // (or updated inside) the chapter document. Using a stylesheet with
    // "!important" lets the reader's choices beat the publisher's CSS, and
    // NavigationCompleted re-applies it to each newly loaded chapter.

    /// <summary>Enlarge the chapter text (max 300%).</summary>
    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        _zoom = Math.Min(3.0, _zoom + 0.1);
        SaveZoomSetting();
        _ = ApplyReaderStyleAsync();
    }

    /// <summary>Shrink the chapter text (min 50%).</summary>
    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        _zoom = Math.Max(0.5, _zoom - 0.1);
        SaveZoomSetting();
        _ = ApplyReaderStyleAsync();
    }

    /// <summary>Persist the text-zoom factor (user clicks only — the +/-
    /// buttons cannot fire during construction, but the guard keeps all
    /// typography saves consistent).</summary>
    private void SaveZoomSetting()
    {
        if (!_settingsRestored) return;
        _settings.Current.ReaderZoom = _zoom;
        _settings.Save();
    }

    /// <summary>
    /// Toolbar single/dual-page switch — just records the mode and rebuilds
    /// the injected stylesheet; the CSS block in ApplyReaderStyleAsync does
    /// the actual page-column magic.
    /// </summary>
    private void DualPageToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _dualPage = DualPageToggle.IsChecked == true;
        _ = ApplyReaderStyleAsync();
        ReturnFocusToReader();   // keep PgDn/PgUp working right after the click
    }

    /// <summary>
    /// Shared handler for BOTH toolbar ComboBoxes (font family and line
    /// spacing). Each ComboBoxItem carries its CSS value in Tag; an empty
    /// Tag means "no override — keep the book's own styling".
    /// </summary>
    private void ReaderStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem item)
            return;

        string cssValue = item.Tag as string ?? "";

        // ReferenceEquals tells us which of the two combos fired. (The named
        // fields can still be null while XAML is loading — the null branch
        // simply does nothing, and ApplyReaderStyleAsync is a no-op then too.)
        if (ReferenceEquals(combo, FontFamilyCombo)) _fontFamily = cssValue;
        else if (ReferenceEquals(combo, LineSpacingCombo)) _lineHeight = cssValue;
        else if (ReferenceEquals(combo, MarginCombo)) _pageMargin = cssValue;

        // Persist the choice — but only for USER changes, never for the
        // parse-time/restore-time events (see _settingsRestored).
        if (_settingsRestored)
        {
            _settings.Current.ReaderFontFamily = _fontFamily;
            _settings.Current.ReaderLineHeight = _lineHeight;
            _settings.Current.ReaderPageMargin = _pageMargin;
            _settings.Save();
        }

        _ = ApplyReaderStyleAsync();
    }

    /// <summary>
    /// Build the reader-preferences stylesheet from the current settings and
    /// push it into the chapter document.
    ///
    /// CSS decisions, each made for a reading app:
    ///   * zoom on body        — reflows text (lines re-wrap) instead of
    ///                           scaling the viewport like Ctrl+wheel would;
    ///                           chosen over CoreWebView2Controller.ZoomFactor,
    ///                           which the WinUI 3 WebView2 doesn't expose.
    ///   * font-family with    — publishers often set fonts on <p>/<span>
    ///     "body *" +important   directly, so overriding body alone is not
    ///                           enough; the universal selector wins them all.
    ///   * line-height on the  — same reason; a unitless value ("1.6") scales
    ///     same selectors        correctly with every font size in the book.
    /// </summary>
    private async Task ApplyReaderStyleAsync()
    {
        ZoomLabel.Text = $"{Math.Round(_zoom * 100)}%";
        if (!_webViewReady) return;

        // Compose the stylesheet. Only non-default choices emit a rule, so
        // "Book default" genuinely leaves the publisher's design untouched.
        var css = new StringBuilder();
        // Culture-invariant number formatting: "1.2" — never "1,2".
        css.Append($"body {{ zoom: {_zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)}; }}\n");
        if (_fontFamily.Length > 0)
            css.Append($"body, body * {{ font-family: {_fontFamily} !important; }}\n");
        if (_lineHeight.Length > 0)
            css.Append($"body, body * {{ line-height: {_lineHeight} !important; }}\n");

        // BOOK-PAGE LAYOUT (single-page mode): make the chapter read like a
        // real book page instead of a raw web document. Most ePubs ship NO
        // page geometry at all — publishers (e.g. Standard Ebooks) assume
        // the READING SYSTEM supplies the page frame, the way Kindle/Kobo
        // do. Without this block, text runs edge-to-edge across the whole
        // window at eye-watering line lengths.
        //
        //   * max-width 40em  — the classic comfortable book measure
        //     (~65-75 characters per line); em-based, so it scales with the
        //     user's zoom and font choices;
        //   * margin auto     — centers the text column like a printed page;
        //   * generous padding — the page's inner margins;
        //   * heading-margin caps — some publishers use huge decorative
        //     gaps (this is where a 10rem hole under a chapter title comes
        //     from); 2em/1.5em keeps titles bookish without the void.
        //     hgroup children stay tight so grouped title+subtitle lines
        //     read as one block;
        //   * responsive images — a full-page illustration must shrink to
        //     the column, never force a horizontal scrollbar.
        //
        // Dual-page mode skips this frame: its viewport-height columns ARE
        // the page geometry there, and a max-width would fight them.
        // Page side margins (LEFT = RIGHT, per the book-page metaphor), from
        // the toolbar Margins ComboBox. The Tag encodes "margin|columnCap":
        // the per-side margin AND the widest text column that margin level
        // allows ("none" = fill the pane). "" = defaults (2.5em / 40em).
        string[] marginParts = _pageMargin.Split('|');
        string sideMargin = marginParts[0].Length > 0 ? marginParts[0] : "2.5";
        string columnCap = marginParts.Length > 1 ? marginParts[1] : "40";

        // RESPONSIVE column width (RWD): the usable width is expressed in
        // vw units so the column reflows INSTANTLY when the viewport grows —
        // hiding the Chapters/Dictionary panels or widening the window lets
        // the text expand up to the margin level's cap, instead of leaving
        // dead whitespace around a fixed-width column.
        //
        // The ÷zoom is load-bearing: vw resolves against the real viewport
        // and is then SCALED by the CSS zoom applied above, so 100vw inside
        // a zoomed body would overflow the pane; dividing the zoom back out
        // makes the value mean "the visible pane width" again (same trick
        // as the dual-page column height).
        string usableWidth =
            $"calc({(100.0 / _zoom).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}vw)";
        string maxWidth = columnCap == "none"
            ? usableWidth
            : $"min({columnCap}em, {usableWidth})";

        if (!_dualPage)
        {
            css.Append(
                $$"""
                body {
                    max-width: {{maxWidth}} !important;
                    margin: 0 auto !important;
                    padding: 3em {{sideMargin}}em 6em {{sideMargin}}em !important;
                    box-sizing: border-box !important;
                }
                h1, h2, h3, h4, h5, h6, hgroup, header {
                    margin-top: 2em !important;
                    margin-bottom: 1.5em !important;
                }
                hgroup > * { margin-top: 0 !important; margin-bottom: 0 !important; }
                img, svg { max-width: 100%; height: auto; }
                """);
            css.Append('\n');
        }

        // DUAL-PAGE MODE: lay the chapter out as an open book. CSS
        // multi-columns pinned to the viewport height do the pagination:
        //
        //   * column-count: 2 + a FIXED body height + column-fill: auto
        //     → the browser generates page-height columns; content that
        //       doesn't fit the first two spills into OVERFLOW COLUMNS to
        //       the right, which scroll HORIZONTALLY (page flipping);
        //   * the body height must be 100vh ÷ zoom, because the CSS zoom
        //     applied above SCALES the laid-out box — dividing first makes
        //     the zoomed result exactly one viewport tall again;
        //   * a thin column-rule down the middle suggests the book's spine;
        //   * navigation is STRICTLY PAGINATED — wheel and keys flip whole
        //     page pairs via the injected script (SelectionWatcherScript
        //     parts 3–4), and the scrollbar is hidden so there is no
        //     free-form scrolling that could leave half pages on screen.
        if (_dualPage)
        {
            string pageHeightVh = (100.0 / _zoom).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            string spineColor = IsDarkTheme ? "#4a4a4a" : "#d9d9d9";
            css.Append(
                $$"""
                html { height: 100%; overflow: hidden !important; }
                body {
                    height: {{pageHeightVh}}vh !important;
                    margin: 0 !important;
                    box-sizing: border-box !important;
                    padding: 2.5em {{sideMargin}}em !important;
                    column-count: 2;
                    column-gap: 5em;
                    column-fill: auto;
                    column-rule: 1px solid {{spineColor}};
                    overflow-y: hidden !important;
                    overflow-x: auto !important;
                }
                body::-webkit-scrollbar { display: none; }
                img, svg { max-width: 100%; height: auto; }
                """);
            css.Append('\n');
        }

        // NIGHT-READING MODE: when the app theme is dark, restyle the book
        // itself — most ePubs assume a white page. Dark background + soft
        // off-white text (pure white on black strains the eyes), links kept
        // readable, images slightly dimmed so photos don't glare at night.
        if (IsDarkTheme)
        {
            css.Append(
                """
                html, body { background-color: #1e1e1e !important; }
                body, body * { color: #e8e6e3 !important;
                               background-color: transparent !important;
                               border-color: #555 !important; }
                a, a * { color: #6cb2f7 !important; }
                img, svg { opacity: 0.85; }
                """);
            css.Append('\n');
        }

        // JsonSerializer.Serialize produces a correctly quoted+escaped JS
        // string literal (handles the quotes inside font names like
        // 'Segoe UI'), so the CSS can be embedded into the script safely.
        string cssJsLiteral = JsonSerializer.Serialize(css.ToString());

        // Create the <style> element on first use, then just update its
        // text on subsequent calls — cheap and idempotent.
        string js =
            $$"""
            (function () {
                let style = document.getElementById("esl-reader-style");
                if (!style) {
                    style = document.createElement("style");
                    style.id = "esl-reader-style";
                    document.head.appendChild(style);
                }
                style.textContent = {{cssJsLiteral}};
            })();
            """;

        try { await ReaderWebView.CoreWebView2.ExecuteScriptAsync(js); }
        catch { /* no document loaded yet — the NavigationCompleted hook re-applies */ }
    }
}
