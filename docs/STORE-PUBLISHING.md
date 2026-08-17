# Publishing ESL EPUB Reader to the Microsoft Store

Everything below the "Your part" line is already prepared in this repo:
MSIX packaging (`Package.appxmanifest` + `-p:BuildMsix=true` build switch),
Store logo assets (`tools/make-store-assets.ps1`), and three ready
1920×1080 screenshots in `store/screenshots/`.

## Your part (requires the wadetsai@msn.com account — interactive sign-in)

### 1. Enroll in Partner Center (one-time)

1. Go to <https://partner.microsoft.com/dashboard/registration> and sign in
   with **wadetsai@msn.com**.
2. Register as an **Individual** developer (one-time fee, ~USD $19; needs a
   credit card and identity verification).

### 2. Reserve the app name

1. Partner Center → **Apps and games** → **New product** → **App**.
2. Reserve the name **ESL EPUB Reader** (fallbacks if taken:
   "ESL ePub Reader", "ESL EPUB Reader for English Learners").

### 3. Copy the product identity into the manifest

Partner Center → your app → **Product management** → **Product identity**
shows three values. Paste them into
`src/EslEpubReader/Package.appxmanifest`, replacing the `PLACEHOLDER`
strings:

| Partner Center value | Manifest location |
|---|---|
| `Package/Identity/Name` | `<Identity Name="…">` |
| `Package/Identity/Publisher` | `<Identity Publisher="CN=…">` |
| `Package/Properties/PublisherDisplayName` | `<PublisherDisplayName>` |

### 4. Build the Store upload package

```powershell
dotnet build src/EslEpubReader/EslEpubReader.csproj -c Release -p:Platform=x64 `
  -p:BuildMsix=true -p:GenerateAppxPackageOnBuild=true `
  -p:UapAppxPackageBuildMode=StoreUpload `
  -p:AppxPackageDir="$PWD/store/packages/"
```

This produces `store/packages/…​.msixupload` — unsigned by design; the
Store signs it with Microsoft's certificate during publication.

### 5. Create the submission

In Partner Center → your app → **Start your submission**:

- **Packages**: upload the `.msixupload`.
- **Store listing**: upload the three screenshots from
  `store/screenshots/` (they are exactly 1920×1080):
  - `1-lookup-1920x1080.png` — the triple dictionary lookup (hero shot)
  - `2-dual-page-1920x1080.png` — dual-page open-book view
  - `3-light-theme-1920x1080.png` — light theme
- Suggested description: see below.
- **Pricing**: Free. **Markets**: all (or your pick).
- **Age ratings**: complete the questionnaire (no objectionable content).
- Submit for certification (typically 24–72 hours).

### Suggested Store description

> ESL EPUB Reader is an ePub reader built for English learners. Select any
> word, phrase, or whole sentence while reading and instantly see an
> English–English dictionary entry, a bilingual dictionary entry, and a
> full translation — into Traditional Chinese by default, or any of 130+
> languages. The built-in Windows voice reads selections aloud so you hear
> the pronunciation as you learn. Day/night themes, single or dual-page
> layouts, adjustable fonts and line spacing, and automatic resume make
> long reading sessions comfortable. Supports ePub 2 and ePub 3.

## Notes & caveats

- **Version bumps**: every new submission needs a higher
  `<Identity Version>` in the manifest (e.g. 1.1.0.0 → 1.2.0.0).
- **Certification risk**: the translation/dictionary features use the free
  endpoints of Bing's own web translator (unofficial). Store certification
  does not normally inspect this, but if the endpoints ever change or
  rate-limit, the features degrade gracefully with in-app error messages.
  For a long-term Store product, consider swapping in the official Azure
  Translator API (`BingTranslateService.cs` / `BingDictionaryService.cs`
  are the drop-in points).
- **x64 only vs. bundle**: the command above builds x64. To also cover
  Windows-on-ARM, run it again with `-p:Platform=ARM64` and upload both
  packages to the same submission.
- The dev-loop unpackaged build (`dotnet run`, portable exe) is unaffected
  by any of this — packaging only activates with `-p:BuildMsix=true`.
