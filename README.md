# ESL ePUB Reader

A Windows desktop ePub reader designed for **ESL (English as a Second
Language) readers**, built with **C# 14**, **.NET 10**, and **WinUI 3**
(Windows App SDK). Runs on Windows 10 (1809+) and Windows 11.

## Download (portable, no installation)

Grab the single-file portable `.exe` from the
[**Releases**](https://github.com/WadeTsai/ESL-EPUB-Reader/releases) page —
one file, no installer, no runtime to install:

| File | For |
|---|---|
| `ESL-EPUB-Reader-win-x64.exe` | Windows 10/11 on Intel/AMD (most PCs) |
| `ESL-EPUB-Reader-win-arm64.exe` | Windows 11 on ARM (Surface Pro X etc.) |

> **Why no macOS build?** The app is built on WinUI 3 / Windows App SDK,
> Microsoft's Windows-only UI framework — it renders through the Windows
> compositor and has no macOS target. A Mac version would require porting
> the UI layer to a cross-platform framework (e.g. Avalonia or Uno
> Platform). The Services/ layer (ePub parsing, dictionaries, translation)
> is plain .NET and would carry over unchanged.

## The core feature: select → instant double dictionary lookup

While reading, **select any word or phrase** (drag, double-click, or
Shift+arrows). The selection is **read aloud** through the Windows built-in
text-to-speech voice (toggleable from the toolbar), and the side panel
immediately shows, top to bottom (the **translation language is selectable**
from a picker in the panel header — every language Microsoft Translator
supports, ~130 of them; the default is Traditional Chinese and the last
choice is remembered across launches):

1. **Bing Translator (繁體中文)** (shown first) — a fluent machine
   translation of the whole selection. Unlike the dictionaries this also
   works for **complete sentences** (up to ~500 characters); for
   sentence-length selections the dictionaries are skipped and only the
   translation is shown.
2. **English – English** — definitions, part of speech, IPA pronunciation,
   example sentences, and synonyms (from the free
   [dictionaryapi.dev](https://dictionaryapi.dev) web API).
3. **Bing Dict (繁體中文)** — ranked translations of the word, grouped by
   part of speech, each with English back-translations for sense-checking.
   Served by the bilingual-dictionary data behind bing.com/translator's
   word cards (`tlookupv3`), ordered by Bing's confidence. For Traditional
   Chinese the results are converted from Simplified using Windows' own
   converter, since Bing's dictionary covers zh-Hans only. No dictionary
   installation needed.

Phrases are handled too: idioms are tried whole first; if the dictionary
doesn't know the phrase, each word is decoded individually.

> Note: translation and the bilingual dictionary use the free endpoints of
> Bing's own web translator (session tokens are negotiated automatically) —
> fine for personal reading, but unofficial. For production-scale use swap
> in the official Azure Translator API (`BingTranslateService.cs` /
> `BingDictionaryService.cs` are the drop-in replacement points).

## Other features

- Full ePub 2 / ePub 3 support: chapters render with the publisher's own CSS
  and images (no third-party ePub library — the parser is ~300 commented
  lines using only `System.IO.Compression` + LINQ-to-XML).
- Chapter list from the book's table of contents (nav.xhtml or toc.ncx).
- Text zoom 50–300% for comfortable reading.
- **Font family picker** (Segoe UI, Verdana, Georgia, Times New Roman, …)
  that overrides the publisher's fonts via injected CSS — or keeps the
  book's default.
- **Line spacing control** (1.4× – 2.5×) for easier line tracking while
  decoding unfamiliar words.
- **Page margin control** — extra-narrow / narrow / default / medium /
  wide / extra-wide side margins (left and right always equal, like a book
  page).
  Each level also sets how wide the text may grow, and the column is fully
  **responsive**: hide the Chapters or Dictionary panel (or resize the
  window) and the text reflows into the freed space immediately —
  extra-narrow margins fill the whole pane.
- **Single / dual-page view** — a toolbar toggle switches between one
  continuous scrolling page and an open-book layout that always shows two
  complete pages; in dual-page mode PgDn/PgUp, Space, arrows, and the
  mouse wheel flip whole page pairs (never partial scrolling), and the
  reading position is still tracked and restored.
- **Read-aloud (text-to-speech)** — every looked-up selection is spoken by
  the Windows built-in speech engine (`Windows.Media.SpeechSynthesis`, fully
  offline). An installed English voice is picked automatically (en-US
  preferred) and slowed slightly (0.9×) for learners. The toolbar
  **"Read aloud" toggle** turns the feature on/off; turning it off also
  stops any speech mid-sentence. A **speaker button** next to the selected
  text in the dictionary panel replays the pronunciation on demand — it
  works even when the automatic toggle is off.
- **Day/night theme** — a sun/moon toolbar button switches the whole app
  between light and dark instantly (persisted across launches; follows the
  Windows setting until first toggled). Dark mode also restyles the **book
  content itself** with an injected night-reading stylesheet (dark page,
  soft off-white text, dimmed images) and recolors the window caption
  buttons.
- **Modern Windows 11 look** — Mica backdrop, content extended into the
  title bar, and the three panes floating as rounded cards on the backdrop.
- **Resizable layout** — drag the gaps between the chapter list, the
  reading area, and the dictionary panel to adjust their widths (custom
  zero-dependency splitter control; each pane has sensible min/max limits).
- **Collapsible side panels** — two toolbar toggles hide/unhide the chapter
  list and the dictionary panel for distraction-free, full-width reading;
  a re-shown panel comes back at exactly the width it had.
- **Continue where you left off** — the app remembers the last opened book,
  chapter, and scroll position, plus the **font family, text size, line
  spacing, theme, and translation language** you chose (saved to
  `%LOCALAPPDATA%\EslEpubReader\settings.json`) and restores everything
  automatically at the next launch. The position is stored as a scroll
  *fraction* of the chapter, so it still lands on the right paragraph after
  changing zoom, font, or window size.

## Requirements

- Windows 11 (or Windows 10 1809+ with the
  [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) —
  preinstalled on Windows 11).
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build.
- Internet connection for the dictionary lookups and translation (all three
  sources are online services).

## Build & run

```bash
dotnet build src/EslEpubReader/EslEpubReader.csproj -p:Platform=x64
```

Then run `src\EslEpubReader\bin\x64\Debug\net10.0-windows10.0.19041.0\EslEpubReader.exe`,
or open `ESL_ePub_Reader.sln` in Visual Studio 2022 (17.14+) and press F5.
The app runs **unpackaged** and **self-contained** (the Windows App SDK
runtime is bundled), so no MSIX installation or machine-wide runtime is
needed.

## Project layout

| Path | Purpose |
|---|---|
| `src/EslEpubReader/App.xaml(.cs)` | Application entry point |
| `src/EslEpubReader/MainWindow.xaml(.cs)` | UI + the selection→lookup pipeline |
| `src/EslEpubReader/Models/` | ePub & dictionary data models |
| `src/EslEpubReader/Services/EpubParserService.cs` | ZIP/OPF/TOC ePub parser |
| `src/EslEpubReader/Services/EnglishDictionaryService.cs` | English–English lookups (dictionaryapi.dev) |
| `src/EslEpubReader/Services/BingSession.cs` | Shared Bing web-translator session/token layer |
| `src/EslEpubReader/Services/BingDictionaryService.cs` | Bing Dict lookups (English→target language, ranked) |
| `src/EslEpubReader/Services/BingTranslateService.cs` | Whole-selection translation (words → sentences) |
| `src/EslEpubReader/Services/LanguageCatalog.cs` | All ~130 Microsoft-Translator target languages (codes + names) |
| `src/EslEpubReader/Services/SettingsService.cs` | Session persistence (book / chapter / position / theme / language) |
| `src/EslEpubReader/Controls/ColumnSplitter.cs` | Draggable pane-resize handle (WinUI 3 has no built-in GridSplitter) |

## How the selection lookup works (architecture)

```
user selects text in the chapter (WebView2)
  → injected JavaScript reads window.getSelection()
  → window.chrome.webview.postMessage({type:"selection", text})
  → CoreWebView2.WebMessageReceived (C#)
  → normalize the term, cancel any stale in-flight lookup
  → Task.WhenAll( English lookup (online),        ← skipped for
                  Chinese lookup (offline index), ← sentence selections
                  Bing Translator (online) )      ← always runs
  → side panel renders all three results
```

Every source file carries detailed comments explaining each step further.

## Licenses of data sources

- **dictionaryapi.dev**: free public API; queried at runtime.
- **Bing Translator / Bing Dict**: queried at runtime via the free
  endpoints of Bing's own web translator; nothing is redistributed in this
  repository.
