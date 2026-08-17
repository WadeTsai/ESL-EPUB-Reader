# =============================================================================
# tools/build-portable.ps1
# =============================================================================
# Builds the PORTABLE single-file binaries published on the GitHub Releases
# page, for both architectures:
#
#     powershell -File tools/build-portable.ps1
#
# Output: store/portable/ESL-EPUB-Reader-win-x64.exe
#         store/portable/ESL-EPUB-Reader-win-arm64.exe
#
# ── WHY THE AssemblyName OVERRIDE IS LOAD-BEARING ────────────────────────────
# A WinUI 3 single-file exe embeds its XAML resources (resources.pri) under
# its ASSEMBLY name. If the exe file is later RENAMED, the XAML framework
# cannot find those resources and the app dies at startup with a stowed
# exception (0xC000027B in Microsoft.UI.Xaml.dll) — no window, no message.
#
# This bit us once: v1.0.0 assets were built as "EslEpubReader.exe" and then
# renamed for upload; every downloaded copy crashed. The fix: build each
# architecture WITH the final file name as its AssemblyName, so the binary
# is born under the name it will be distributed as. Corollary for users:
# the downloaded exe must keep its file name (documented in the release
# notes).
# =============================================================================

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$project = Join-Path $repoRoot "src\EslEpubReader\EslEpubReader.csproj"
$outDir = Join-Path $repoRoot "store\portable"
New-Item -ItemType Directory -Force $outDir | Out-Null

# arch -> (msbuild Platform, runtime identifier)
$targets = @(
    @{ Platform = "x64";   Rid = "win-x64" },
    @{ Platform = "ARM64"; Rid = "win-arm64" }
)

foreach ($t in $targets) {
    $name = "ESL-EPUB-Reader-$($t.Rid)"   # final asset/file name = assembly name
    Write-Host "`n=== Publishing portable $($t.Rid) ===" -ForegroundColor Cyan

    dotnet publish $project -c Release -r $t.Rid -p:Platform=$($t.Platform) `
        --self-contained `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:AssemblyName=$name `
        -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { Write-Error "Publish failed for $($t.Rid)." }

    $published = Join-Path $repoRoot ("src\EslEpubReader\bin\{0}\Release\net10.0-windows10.0.19041.0\{1}\publish\{2}.exe" -f $t.Platform, $t.Rid, $name)
    Copy-Item $published (Join-Path $outDir "$name.exe") -Force
}

Write-Host "`n=== Portable binaries ===" -ForegroundColor Green
Get-ChildItem $outDir -Filter *.exe | ForEach-Object {
    Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
}
Write-Host "`nUpload with: gh release upload vX.Y.Z store/portable/*.exe --clobber"
