# Builds, tests and packages Arr411 Desktop: a self-contained single-file Arr411.exe in dist\Arr411-Desktop-<version>-win-x64.zip.
# Usage: powershell -File build.ps1 [-Task build|test|e2e|publish|zip] [-Configuration Release]
#   build    compile the solution
#   test     build + run the unit tests
#   e2e      start the real Arr411.exe (Debug build) and drive it: needs an unlocked interactive Windows session and the WebView2 runtime
#   publish  single-file self-contained Arr411.exe in dist\publish
#   zip      test + publish + zip (default)
param(
  [ValidateSet('build', 'test', 'e2e', 'publish', 'zip')] [string]$Task = 'zip',
  [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$parent = Split-Path $root

# .NET SDK 10: from PATH, otherwise the per-user install in %LOCALAPPDATA%\Microsoft\dotnet.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
  $local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
  if (Test-Path $local) { $dotnet = $local; $env:DOTNET_ROOT = Split-Path $local }
}
if (-not $dotnet) { throw '.NET SDK 10 not found (install it from https://dotnet.microsoft.com/download/dotnet/10.0)' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# Sources embedded as is: arr411-android (web app + shims) and arr411-chrome (engine and page scripts).
$android = if ($env:ARR411_ANDROID) { $env:ARR411_ANDROID } else { Join-Path $parent 'arr411-android' }
foreach ($f in 'app\index.html', 'assets-extra\shim-engine.js', 'assets-extra\shim-page.js', 'assets-extra\engine.html') {
  if (-not (Test-Path "$android\$f")) { throw "arr411-android not found in $android (clone https://github.com/HugoMeyer/arr411-android next to this repository, or set ARR411_ANDROID)" }
}
$ext = if ($env:ARR411_EXTENSION) { $env:ARR411_EXTENSION } else { Join-Path $parent 'arr411-chrome' }
foreach ($f in 'background.js', 'content.js', 'page-hook.js') {
  if (-not (Test-Path "$ext\$f")) { throw "arr411-chrome not found in $ext (clone https://github.com/HugoMeyer/arr411-chrome next to this repository, or set ARR411_EXTENSION)" }
}

function Invoke-Dotnet {
  & $dotnet @args
  if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) failed (exit code $LASTEXITCODE)" }
}

$sln = "$root\Arr411.Desktop.sln"
$proj = "$root\src\Arr411.Desktop\Arr411.Desktop.csproj"

if ($Task -in 'build', 'test') {
  Invoke-Dotnet build $sln -c $Configuration --nologo
}
if ($Task -in 'test', 'zip') {
  Invoke-Dotnet test $sln -c $Configuration --nologo --filter 'Category!=E2E'
}
if ($Task -eq 'e2e') {
  # Always Debug: only that build opens the browser debug port the tests drive.
  Invoke-Dotnet test "$root\tests\Arr411.E2E\Arr411.E2E.csproj" -c Debug --nologo --filter 'Category=E2E'
}
if ($Task -in 'build', 'test', 'e2e') { return }

$dist = "$root\dist"
$pub = "$dist\publish"
Remove-Item $pub -Recurse -Force -ErrorAction SilentlyContinue
# The -p: arguments are quoted: unquoted, PowerShell 7 splits them in two when they go through $args, and dotnet then takes the value for a second project.
Invoke-Dotnet publish $proj -c Release -r win-x64 --self-contained true `
  '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' `
  '-p:DebugType=none' '-p:DebugSymbols=false' -o $pub --nologo
Get-ChildItem "$pub\*" -Include *.pdb, *.xml | Remove-Item -Force
$exe = "$pub\Arr411.exe"
if (-not (Test-Path $exe)) { throw "Arr411.exe not found in $pub" }
"EXE: $exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)"
if ($Task -eq 'publish') { return }

$version = (& $dotnet msbuild $proj -getProperty:Version -nologo | Out-String).Trim()
if (-not $version) { throw 'Could not read the Version property' }
$stage = "$dist\stage"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item $exe $stage
foreach ($f in 'LICENSE', 'THIRD_PARTY_NOTICES.md') {
  if (Test-Path "$root\$f") { Copy-Item "$root\$f" $stage }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = "$dist\Arr411-Desktop-$version-win-x64.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item $stage -Recurse -Force
"ZIP: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
"SHA-256: $((Get-FileHash $zip -Algorithm SHA256).Hash)"
