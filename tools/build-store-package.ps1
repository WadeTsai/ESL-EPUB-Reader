# =============================================================================
# tools/build-store-package.ps1
# =============================================================================
# Builds the Microsoft Store upload packages (.msixupload) for BOTH
# architectures (x64 + ARM64) in one go:
#
#     powershell -File tools/build-store-package.ps1
#
# Prerequisite: the three PLACEHOLDER identity values in
# src/EslEpubReader/Package.appxmanifest must be replaced with the real ones
# from Partner Center (Product management -> Product identity) — the script
# REFUSES to build placeholder packages because the Store rejects them.
# Pass -AllowPlaceholders only to smoke-test the packaging pipeline itself.
#
# Output: store/packages/EslEpubReader_<version>_<arch>.msixupload
# Upload each file in the same Partner Center submission (x64 covers
# Intel/AMD machines, ARM64 covers Windows-on-ARM).
# =============================================================================

param(
    # Build even if the manifest still contains PLACEHOLDER identity values
    # (pipeline test only — Partner Center will reject such a package).
    [switch]$AllowPlaceholders
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$project = Join-Path $repoRoot "src\EslEpubReader\EslEpubReader.csproj"
$manifest = Join-Path $repoRoot "src\EslEpubReader\Package.appxmanifest"
$outDir = Join-Path $repoRoot "store\packages"

# ---- guard: no placeholder identities into a real submission ---------------
if ((Get-Content $manifest -Raw) -match "PLACEHOLDER") {
    if ($AllowPlaceholders) {
        Write-Warning "Manifest still contains PLACEHOLDER identity values - this package can NOT be submitted."
    } else {
        Write-Error ("Package.appxmanifest still contains PLACEHOLDER identity values.`n" +
            "Paste the real values from Partner Center (Product management -> Product identity) first,`n" +
            "or pass -AllowPlaceholders to test the packaging pipeline.")
    }
}

# ---- build both architectures -----------------------------------------------
foreach ($arch in @("x64", "ARM64")) {
    Write-Host "`n=== Building Store package ($arch) ===" -ForegroundColor Cyan
    # AppxSymbolPackageEnabled=false: the symbols (.appxsym) step requires
    # Visual Studio's C++ toolchain (mspdbcmf.exe) and crashes without it.
    # Symbols are OPTIONAL for Store submissions — crash analytics in
    # Partner Center are just nicer with them; build with VS installed if
    # you ever want that.
    dotnet build $project -c Release -p:Platform=$arch -nologo -v minimal `
        -p:BuildMsix=true `
        -p:GenerateAppxPackageOnBuild=true `
        -p:UapAppxPackageBuildMode=StoreUpload `
        -p:AppxSymbolPackageEnabled=false `
        -p:AppxPackageDir="$outDir\"
    if ($LASTEXITCODE -ne 0) { Write-Error "Build failed for $arch." }
}

# ---- report ------------------------------------------------------------------
Write-Host "`n=== Store upload packages ===" -ForegroundColor Green
Get-ChildItem $outDir -Filter *.msixupload | ForEach-Object {
    Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
}
Write-Host "`nNext: Partner Center -> your app -> submission -> Packages -> upload the .msixupload files."
Write-Host "Screenshots for the listing are in store/screenshots/ (1920x1080)."
