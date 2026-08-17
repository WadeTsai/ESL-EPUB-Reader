// ============================================================================
// Services/GoogleDictionaryService.cs
// ============================================================================
// English → target-language dictionary lookups (default: Traditional
// Chinese, user-selectable from LanguageCatalog), powered by GOOGLE
// DICTIONARY — the bilingual dictionary data behind the word cards that
// Google Translate shows for single words.
//
// (This service REPLACED the previous offline CC-CEDICT implementation: no
// dictionary download/installation step is needed anymore, and the results
// are ranked by Google's own usage statistics instead of a hand-rolled
// relevance score. The trade-off: lookups now require an internet
// connection, like the English–English dictionary already did.)
//
// ENDPOINT:
//   The same free key-less "gtx" endpoint used by GoogleTranslateService,
//   but with the "dt=bd" parameter (bd = Bilingual Dictionary):
//
//     https://translate.googleapis.com/translate_a/single
//         ?client=gtx&sl=en&tl=zh-TW&dt=bd&q=<word>
//
//   Same caveat as the translator: unofficial, fine for light personal use,
//   swap for the official Cloud Translation API in production.
//
// RESPONSE FORMAT (abridged — the endpoint returns nested JSON ARRAYS):
//
//   [ <translation part, unused here>,
//     [                                        <- index 1: THE DICTIONARY
//       [ "noun",                              <- part of speech
//         ["傳統","慣例", ...],                 <- quick term list (unused;
//                                                 superseded by next array)
//         [                                    <- detailed entries:
//           ["傳統", ["tradition","heritage"], null, 0.4],
//            ^term    ^back-translations             ^frequency score 0..1
//           ...
//         ],
//         "tradition"                          <- the base word
//       ],
//       [ "verb", ... ]                        <- one block per part of speech
//     ],
//     "en", ... ]
//
//   Google only returns index 1 for SINGLE WORDS and very common short
//   phrases; for anything longer it is null — the UI then points the reader
//   at the full-sentence translation section instead.
// ============================================================================

using System.Net.Http;
using System.Text.Json;
using EslEpubReader.Models;

namespace EslEpubReader.Services;

public sealed class GoogleDictionaryService
{
    /// <summary>
    /// Target language code, settable at runtime from the language picker in
    /// the dictionary panel (any code in LanguageCatalog). The default,
    /// "zh-TW", returns Traditional Chinese terms; every other Google
    /// Translate language works the same way through dt=bd.
    /// </summary>
    public string TargetLanguage { get; set; } = LanguageCatalog.DefaultCode;

    /// <summary>Cap on total entries per lookup so a very common word
    /// ("run") cannot flood the side panel across its many parts of speech.</summary>
    private const int MaxEntries = 15;

    /// <summary>Shared HttpClient — one instance per process to reuse
    /// connections (see EnglishDictionaryService for the full rationale).</summary>
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // The endpoint occasionally rejects clients without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return client;
    }

    /// <summary>Session cache keyed by "languageCode|word" — the language is
    /// part of the key so switching languages and back reuses earlier
    /// answers instead of showing the wrong language's entries.</summary>
    private readonly Dictionary<string, ChineseLookupResult> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Look up an English word/short phrase in Google Dictionary and return
    /// ranked translations in the current TargetLanguage. NEVER throws for
    /// network/parse problems — failures come back in StatusMessage.
    /// </summary>
    /// <param name="term">Normalized selection text (the caller already
    /// filters out sentence-length selections).</param>
    /// <param name="ct">Cancelled when a newer selection supersedes this one.</param>
    public async Task<ChineseLookupResult> LookupAsync(string term, CancellationToken ct)
    {
        string cacheKey = $"{TargetLanguage}|{term}";
        if (_cache.TryGetValue(cacheKey, out ChineseLookupResult? cached)) return cached;

        try
        {
            string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=en" +
                         $"&tl={TargetLanguage}&dt=bd&q={Uri.EscapeDataString(term)}";
            using HttpResponseMessage response = await Http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var entries = new List<GoogleDictionaryEntry>();

            // ---- Walk the dictionary payload at root index 1. --------------
            // Every level is defensively type-checked: the format is
            // undocumented and contains nulls in unpredictable places.
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 1 &&
                root[1].ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement posBlock in root[1].EnumerateArray())
                {
                    // posBlock = [ partOfSpeech, [terms], [detailedEntries], baseWord ]
                    if (posBlock.ValueKind != JsonValueKind.Array ||
                        posBlock.GetArrayLength() < 3) continue;

                    string partOfSpeech =
                        posBlock[0].ValueKind == JsonValueKind.String
                            ? posBlock[0].GetString() ?? "" : "";

                    if (posBlock[2].ValueKind != JsonValueKind.Array) continue;

                    foreach (JsonElement detail in posBlock[2].EnumerateArray())
                    {
                        if (entries.Count >= MaxEntries) break;

                        // detail = [ chineseTerm, [backTranslations], null, score ]
                        if (detail.ValueKind != JsonValueKind.Array ||
                            detail.GetArrayLength() < 1 ||
                            detail[0].ValueKind != JsonValueKind.String) continue;

                        string chineseTerm = detail[0].GetString() ?? "";
                        if (chineseTerm.Length == 0) continue;

                        // Back-translations: the English words this Chinese
                        // term maps back to — lets the learner confirm the
                        // right sense was picked. Take the first few.
                        string backTranslations = "";
                        if (detail.GetArrayLength() > 1 &&
                            detail[1].ValueKind == JsonValueKind.Array)
                        {
                            backTranslations = string.Join(", ",
                                detail[1].EnumerateArray()
                                         .Where(x => x.ValueKind == JsonValueKind.String)
                                         .Select(x => x.GetString())
                                         .Take(6));
                        }

                        entries.Add(new GoogleDictionaryEntry
                        {
                            PartOfSpeech = partOfSpeech,
                            Term = chineseTerm,
                            BackTranslations = backTranslations,
                        });
                    }
                    // NOTE: entries stay in Google's own order — it already
                    // ranks by real-world usage frequency, which beats any
                    // heuristic we could compute locally.
                }
            }

            ChineseLookupResult result = entries.Count > 0
                ? new ChineseLookupResult { Term = term, Entries = entries }
                : new ChineseLookupResult
                {
                    Term = term,
                    Entries = [],
                    StatusMessage = "No dictionary entry — see the Google Translate section above.",
                };

            _cache[cacheKey] = result;   // cache "not found" too: it won't change
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // superseded by a newer selection — caller drops it
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Offline / rate-limited / format change: explain, do NOT cache
            // (the next selection retries automatically).
            return new ChineseLookupResult
            {
                Term = term,
                Entries = [],
                StatusMessage = "Google Dictionary is unreachable (check your internet connection).",
            };
        }
    }
}
