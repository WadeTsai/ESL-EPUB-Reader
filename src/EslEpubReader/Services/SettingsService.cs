// ============================================================================
// Services/SettingsService.cs
// ============================================================================
// Persists the reading session between app launches so the reader can
// continue EXACTLY where they stopped:
//
//   * which .epub file was open,
//   * which chapter they were in,
//   * how far down that chapter they had scrolled (the "page").
//
// WHY SCROLL FRACTION INSTEAD OF A PAGE NUMBER?
//   This reader displays each chapter as one continuously scrolling page
//   (like a web page), so the natural equivalent of "the page I was on" is
//   the VERTICAL SCROLL POSITION. We store it as a FRACTION (0.0 = top,
//   1.0 = bottom) rather than a pixel offset, because pixels change whenever
//   the user resizes the window or changes zoom/font/line-spacing — a
//   fraction still lands in (almost) the same paragraph after such changes.
//
// STORAGE:
//   A tiny human-readable JSON file:
//       %LOCALAPPDATA%\EslEpubReader\settings.json
//   The same folder already holds the CC-CEDICT dictionary, so all app data
//   lives in one predictable place. (The app runs UNPACKAGED, so the
//   packaged-app ApplicationData store is not available — a plain file is
//   the right tool.)
//
// WRITE STRATEGY:
//   Save() is called at the few moments the position meaningfully changes
//   (book opened, chapter changed, app closing) plus a throttled save while
//   scrolling (see MainWindow), so a crash loses at most a few seconds of
//   position. The file is ~200 bytes; writing it is effectively free.
// ============================================================================

using System.Text.Json;

namespace EslEpubReader.Services;

/// <summary>
/// The data that gets serialized to settings.json. Plain mutable properties
/// (not init-only) because the same instance is updated throughout the
/// session and re-saved. Unknown/missing JSON fields simply fall back to
/// these defaults, which makes the format forward- and backward-compatible.
/// </summary>
public sealed class ReaderSettings
{
    /// <summary>Absolute path of the most recently opened .epub file.
    /// Empty string = the user has never opened a book yet.</summary>
    public string LastBookPath { get; set; } = "";

    /// <summary>0-based spine index of the chapter that was on screen.</summary>
    public int LastChapterIndex { get; set; }

    /// <summary>Vertical position inside that chapter: 0.0 (top) … 1.0
    /// (bottom). See the class comment for why a fraction, not pixels.</summary>
    public double LastScrollFraction { get; set; }

    /// <summary>UI theme chosen with the toolbar day/night button:
    /// "Dark", "Light", or "" (never toggled — follow the Windows setting).
    /// Stored as a string, not the ElementTheme enum, to keep the JSON
    /// readable and stable across SDK versions.</summary>
    public string Theme { get; set; } = "";

    /// <summary>Target language for Google Translate + Google Dictionary,
    /// as a Google language code ("zh-TW", "ja", "es", …). Chosen with the
    /// language picker in the dictionary panel and remembered across
    /// launches; unknown codes fall back to the default at load time
    /// (see LanguageCatalog.FromCode).</summary>
    public string TargetLanguageCode { get; set; } = LanguageCatalog.DefaultCode;
}

/// <summary>Loads and saves ReaderSettings. All failures are swallowed by
/// design: a corrupt/locked settings file must never break the app —
/// the worst outcome is simply starting fresh.</summary>
public sealed class SettingsService
{
    /// <summary>settings.json next to the dictionary file in
    /// %LOCALAPPDATA%\EslEpubReader\.</summary>
    private static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EslEpubReader", "settings.json");

    /// <summary>Indented JSON so users can inspect/edit the file by hand.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The live settings object. MainWindow mutates its properties
    /// directly and calls Save() when appropriate.</summary>
    public ReaderSettings Current { get; private set; } = new();

    /// <summary>Read settings.json into Current. Call once at startup,
    /// BEFORE anything queries Current. Missing or unreadable file ->
    /// defaults (fresh start), never an exception.</summary>
    public void Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return;
            ReaderSettings? loaded = JsonSerializer.Deserialize<ReaderSettings>(
                File.ReadAllText(SettingsFilePath));
            if (loaded is not null) Current = loaded;
        }
        catch
        {
            // Corrupt JSON (e.g. interrupted write, manual edit gone wrong):
            // keep the defaults; the next Save() rewrites a healthy file.
        }
    }

    /// <summary>Write Current to disk. Safe to call often — the file is tiny
    /// and any I/O error (disk full, folder locked) is ignored because
    /// losing a reading position is not worth an error dialog.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch
        {
            // Non-fatal by design — see method summary.
        }
    }
}
