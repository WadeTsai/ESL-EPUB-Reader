// ============================================================================
// Services/LanguageCatalog.cs
// ============================================================================
// The list of TARGET LANGUAGES the reader can translate into.
//
// Both Google services in this app (GoogleTranslateService for whole
// selections, GoogleDictionaryService for word lookups) accept any language
// code that Google Translate supports, so this catalog is simply Google
// Translate's public language list. The reader picks one in the dictionary
// panel; the choice is persisted in settings.json (default: Traditional
// Chinese, the app's original audience).
//
// CODE QUIRKS worth knowing (inherited from the gtx endpoint):
//   * Hebrew is "iw" (the pre-1989 ISO code) — "he" is NOT accepted;
//   * Javanese is "jw" for the same historical reason;
//   * Chinese needs a script suffix: "zh-TW" (Traditional) / "zh-CN"
//     (Simplified) — bare "zh" is ambiguous;
//   * a few languages use longer tags, e.g. "mni-Mtei" (Meiteilon).
//
// DISPLAY NAMES: each language shows its NATIVE name first (what a native
// speaker instantly recognizes) with the English name in parentheses —
// "繁體中文 (Chinese, Traditional)". Languages whose native spelling equals
// the English one (or where the native form is uncommon in UI lists) just
// show the English name.
// ============================================================================

namespace EslEpubReader.Services;

/// <summary>
/// One selectable target language. A C# record: value-equality and a
/// compact declaration for what is effectively a data row.
/// </summary>
/// <param name="Code">The language code the gtx endpoint expects ("zh-TW").</param>
/// <param name="EnglishName">English display name ("Chinese, Traditional").</param>
/// <param name="NativeName">Native display name ("繁體中文"), or null when it
/// adds nothing over the English name.</param>
public sealed record TranslationLanguage(string Code, string EnglishName, string? NativeName = null)
{
    /// <summary>Full name for the ComboBox list: "繁體中文 (Chinese, Traditional)".</summary>
    public string DisplayName => NativeName is null ? EnglishName : $"{NativeName} ({EnglishName})";

    /// <summary>Compact name for section headers: prefers the native form.</summary>
    public string ShortName => NativeName ?? EnglishName;

    /// <summary>ComboBox fallback display (when no DisplayMemberPath is set).</summary>
    public override string ToString() => DisplayName;
}

/// <summary>Static catalog of every Google-Translate-supported language.</summary>
public static class LanguageCatalog
{
    /// <summary>The app's default target language: Traditional Chinese.</summary>
    public const string DefaultCode = "zh-TW";

    /// <summary>
    /// All supported target languages, sorted by English name so the
    /// ComboBox reads like an index. (Typing a letter while the ComboBox is
    /// open jumps to it — WinUI gives us that for free.)
    /// </summary>
    public static readonly IReadOnlyList<TranslationLanguage> All =
    [
        new("af", "Afrikaans"),
        new("sq", "Albanian", "Shqip"),
        new("am", "Amharic", "አማርኛ"),
        new("ar", "Arabic", "العربية"),
        new("hy", "Armenian", "Հայերեն"),
        new("as", "Assamese", "অসমীয়া"),
        new("ay", "Aymara"),
        new("az", "Azerbaijani", "Azərbaycan"),
        new("bm", "Bambara"),
        new("eu", "Basque", "Euskara"),
        new("be", "Belarusian", "Беларуская"),
        new("bn", "Bengali", "বাংলা"),
        new("bho", "Bhojpuri"),
        new("bs", "Bosnian", "Bosanski"),
        new("bg", "Bulgarian", "Български"),
        new("ca", "Catalan", "Català"),
        new("ceb", "Cebuano"),
        new("zh-CN", "Chinese, Simplified", "简体中文"),
        new("zh-TW", "Chinese, Traditional", "繁體中文"),
        new("co", "Corsican", "Corsu"),
        new("hr", "Croatian", "Hrvatski"),
        new("cs", "Czech", "Čeština"),
        new("da", "Danish", "Dansk"),
        new("dv", "Dhivehi", "ދިވެހި"),
        new("doi", "Dogri"),
        new("nl", "Dutch", "Nederlands"),
        new("en", "English"),
        new("eo", "Esperanto"),
        new("et", "Estonian", "Eesti"),
        new("ee", "Ewe"),
        new("tl", "Filipino", "Tagalog"),
        new("fi", "Finnish", "Suomi"),
        new("fr", "French", "Français"),
        new("fy", "Frisian", "Frysk"),
        new("gl", "Galician", "Galego"),
        new("ka", "Georgian", "ქართული"),
        new("de", "German", "Deutsch"),
        new("el", "Greek", "Ελληνικά"),
        new("gn", "Guarani"),
        new("gu", "Gujarati", "ગુજરાતી"),
        new("ht", "Haitian Creole", "Kreyòl Ayisyen"),
        new("ha", "Hausa"),
        new("haw", "Hawaiian", "ʻŌlelo Hawaiʻi"),
        new("iw", "Hebrew", "עברית"),                    // legacy code — NOT "he"
        new("hi", "Hindi", "हिन्दी"),
        new("hmn", "Hmong"),
        new("hu", "Hungarian", "Magyar"),
        new("is", "Icelandic", "Íslenska"),
        new("ig", "Igbo"),
        new("ilo", "Ilocano"),
        new("id", "Indonesian", "Bahasa Indonesia"),
        new("ga", "Irish", "Gaeilge"),
        new("it", "Italian", "Italiano"),
        new("ja", "Japanese", "日本語"),
        new("jw", "Javanese", "Basa Jawa"),              // legacy code — NOT "jv"
        new("kn", "Kannada", "ಕನ್ನಡ"),
        new("kk", "Kazakh", "Қазақ"),
        new("km", "Khmer", "ខ្មែរ"),
        new("rw", "Kinyarwanda"),
        new("gom", "Konkani", "कोंकणी"),
        new("ko", "Korean", "한국어"),
        new("kri", "Krio"),
        new("ku", "Kurdish, Kurmanji", "Kurdî"),
        new("ckb", "Kurdish, Sorani", "کوردیی ناوەندی"),
        new("ky", "Kyrgyz", "Кыргызча"),
        new("lo", "Lao", "ລາວ"),
        new("la", "Latin", "Latina"),
        new("lv", "Latvian", "Latviešu"),
        new("ln", "Lingala"),
        new("lt", "Lithuanian", "Lietuvių"),
        new("lg", "Luganda"),
        new("lb", "Luxembourgish", "Lëtzebuergesch"),
        new("mk", "Macedonian", "Македонски"),
        new("mai", "Maithili", "मैथिली"),
        new("mg", "Malagasy"),
        new("ms", "Malay", "Bahasa Melayu"),
        new("ml", "Malayalam", "മലയാളം"),
        new("mt", "Maltese", "Malti"),
        new("mi", "Maori", "Te Reo Māori"),
        new("mr", "Marathi", "मराठी"),
        new("mni-Mtei", "Meiteilon (Manipuri)"),
        new("lus", "Mizo"),
        new("mn", "Mongolian", "Монгол"),
        new("my", "Myanmar (Burmese)", "မြန်မာ"),
        new("ne", "Nepali", "नेपाली"),
        new("no", "Norwegian", "Norsk"),
        new("ny", "Nyanja (Chichewa)"),
        new("or", "Odia (Oriya)", "ଓଡ଼ିଆ"),
        new("om", "Oromo"),
        new("ps", "Pashto", "پښتو"),
        new("fa", "Persian", "فارسی"),
        new("pl", "Polish", "Polski"),
        new("pt", "Portuguese", "Português"),
        new("pa", "Punjabi", "ਪੰਜਾਬੀ"),
        new("qu", "Quechua"),
        new("ro", "Romanian", "Română"),
        new("ru", "Russian", "Русский"),
        new("sm", "Samoan", "Gagana Samoa"),
        new("sa", "Sanskrit", "संस्कृतम्"),
        new("gd", "Scots Gaelic", "Gàidhlig"),
        new("nso", "Sepedi"),
        new("sr", "Serbian", "Српски"),
        new("st", "Sesotho"),
        new("sn", "Shona"),
        new("sd", "Sindhi", "سنڌي"),
        new("si", "Sinhala", "සිංහල"),
        new("sk", "Slovak", "Slovenčina"),
        new("sl", "Slovenian", "Slovenščina"),
        new("so", "Somali", "Soomaali"),
        new("es", "Spanish", "Español"),
        new("su", "Sundanese", "Basa Sunda"),
        new("sw", "Swahili", "Kiswahili"),
        new("sv", "Swedish", "Svenska"),
        new("tg", "Tajik", "Тоҷикӣ"),
        new("ta", "Tamil", "தமிழ்"),
        new("tt", "Tatar", "Татарча"),
        new("te", "Telugu", "తెలుగు"),
        new("th", "Thai", "ไทย"),
        new("ti", "Tigrinya", "ትግርኛ"),
        new("ts", "Tsonga"),
        new("tr", "Turkish", "Türkçe"),
        new("tk", "Turkmen", "Türkmen"),
        new("ak", "Twi (Akan)"),
        new("uk", "Ukrainian", "Українська"),
        new("ur", "Urdu", "اردو"),
        new("ug", "Uyghur", "ئۇيغۇرچە"),
        new("uz", "Uzbek", "Oʻzbek"),
        new("vi", "Vietnamese", "Tiếng Việt"),
        new("cy", "Welsh", "Cymraeg"),
        new("xh", "Xhosa", "isiXhosa"),
        new("yi", "Yiddish", "ייִדיש"),
        new("yo", "Yoruba", "Yorùbá"),
        new("zu", "Zulu", "isiZulu"),
    ];

    /// <summary>
    /// Resolve a persisted language code back to its catalog entry.
    /// Unknown/garbage codes (hand-edited settings file, a code Google
    /// retired) fall back to the default instead of crashing.
    /// </summary>
    public static TranslationLanguage FromCode(string code) =>
        All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))
        ?? All.First(l => l.Code == DefaultCode);
}
