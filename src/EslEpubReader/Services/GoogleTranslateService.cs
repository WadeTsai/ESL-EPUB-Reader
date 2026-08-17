// ============================================================================
// Services/GoogleTranslateService.cs
// ============================================================================
// The THIRD lookup source: machine translation of the selected text into
// the user-selected TARGET LANGUAGE via Google Translate (default:
// Traditional Chinese; any language in LanguageCatalog can be chosen).
//
// Why this exists in addition to the two dictionaries:
//   * Dictionaries explain WORDS and short PHRASES.
//   * Google Translate can handle WHOLE SENTENCES, giving the ESL reader a
//     fluent rendering of an entire passage when word-by-word decoding is
//     not enough (idioms, complex grammar, long clauses).
//
// ENDPOINT NOTE (important):
//   This service uses the free, key-less "gtx" endpoint
//
//     https://translate.googleapis.com/translate_a/single
//         ?client=gtx&sl=en&tl=zh-TW&dt=t&q=<text>
//
//   which is the same endpoint Google's own browser extension uses. It is
//   UNOFFICIAL: fine for personal / educational use with light traffic, but
//   it has no SLA and Google may rate-limit or change it at any time.
//   For production-scale usage, swap this class for the official
//   Google Cloud Translation API (needs an API key) — the rest of the app
//   only depends on TranslateAsync's signature, so it is a drop-in change.
//
// RESPONSE FORMAT (abridged):
//   The endpoint returns a nested JSON array, not an object:
//
//     [ [ ["翻譯好的句子","original sentence",null,null,10], ... ], null, "en", ... ]
//       └──── index 0: list of translated SEGMENTS ────┘         └ detected language
//
//   Long inputs are split into several segments; the full translation is the
//   concatenation of segment[0] over all segments — that is what we parse.
// ============================================================================

using System.Net.Http;
using System.Text;
using System.Text.Json;
using EslEpubReader.Models;

namespace EslEpubReader.Services;

public sealed class GoogleTranslateService
{
    /// <summary>
    /// Target language code, settable at runtime from the language picker in
    /// the dictionary panel (any code in LanguageCatalog). The default,
    /// "zh-TW", is Chinese as written in Taiwan — Traditional characters.
    /// </summary>
    public string TargetLanguage { get; set; } = LanguageCatalog.DefaultCode;

    /// <summary>
    /// Shared HttpClient (see EnglishDictionaryService for why one static
    /// instance is used instead of new-per-request). A browser-like
    /// User-Agent is attached because the endpoint occasionally rejects
    /// clients with no User-Agent at all.
    /// </summary>
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return client;
    }

    /// <summary>Session cache keyed by "languageCode|text" — the language is
    /// part of the key so switching languages and back reuses earlier
    /// answers instead of returning the wrong language's translation.</summary>
    private readonly Dictionary<string, TranslationResult> _cache =
        new(StringComparer.Ordinal);   // translations ARE case-sensitive ("May" vs "may")

    /// <summary>
    /// Translate an English word, phrase, or whole sentence into the current
    /// TargetLanguage. NEVER throws for network/parse problems — failures
    /// come back in StatusMessage so the panel can always render something.
    /// </summary>
    /// <param name="text">The reader's (already normalized) selection.</param>
    /// <param name="ct">Cancelled when a newer selection supersedes this one.</param>
    public async Task<TranslationResult> TranslateAsync(string text, CancellationToken ct)
    {
        string cacheKey = $"{TargetLanguage}|{text}";
        if (_cache.TryGetValue(cacheKey, out TranslationResult? cached)) return cached;

        try
        {
            string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=en" +
                         $"&tl={TargetLanguage}&dt=t&q={Uri.EscapeDataString(text)}";
            using HttpResponseMessage response = await Http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            // ---- Concatenate all translated segments (see class comment). --
            var translated = new StringBuilder();
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 &&
                root[0].ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement segment in root[0].EnumerateArray())
                {
                    // Each segment is itself an array; element [0] holds the
                    // translated text. Guard every level — the format is
                    // undocumented and occasionally contains nulls.
                    if (segment.ValueKind == JsonValueKind.Array &&
                        segment.GetArrayLength() > 0 &&
                        segment[0].ValueKind == JsonValueKind.String)
                    {
                        translated.Append(segment[0].GetString());
                    }
                }
            }

            string resultText = translated.ToString().Trim();
            TranslationResult result = resultText.Length > 0
                ? new TranslationResult { Term = text, TranslatedText = resultText }
                : new TranslationResult
                {
                    Term = text,
                    StatusMessage = "Google Translate returned no translation.",
                };

            _cache[cacheKey] = result;
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded by a newer selection — let the caller drop it.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Network failure / rate limit / format change: explain, do NOT
            // cache (so the next selection retries automatically).
            return new TranslationResult
            {
                Term = text,
                StatusMessage = "Google Translate is unreachable (offline, or the service is rate-limiting).",
            };
        }
    }
}
