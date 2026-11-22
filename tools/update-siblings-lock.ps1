<#
.SYNOPSIS
Writes siblings.lock from the commits checked out in the local arr411-android and arr411-chrome clones.

.DESCRIPTION
CI builds Arr411 Desktop against the two sibling repositories at the commits written in siblings.lock.
Run this at release time, once the siblings are committed and pushed, then commit the lock.

The script stops when a sibling has uncommitted changes, because the lock would not describe what you
built, unless -Force is given. It prints what changes, and writes nothing with -WhatIf.

.PARAMETER AndroidPath
The arr411-android clone. Default: $env:ARR411_ANDROID, else the arr411-android folder next to this repository.

.PARAMETER ChromePath
The arr411-chrome clone. Default: $env:ARR411_EXTENSION, else the arr411-chrome folder next to this repository.

.PARAMETER Force
Write the lock even when a sibling has uncommitted changes. The lock then holds the HEAD commit, without those changes.

.EXAMPLE
powershell -File tools\update-siblings-lock.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
  [string]$AndroidPath,
  [string]$ChromePath,
  [switch]$Force
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$parent = Split-Path -Parent $root
$lock = Join-Path $root 'siblings.lock'

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git not found in PATH' }
if (-not $AndroidPath) { $AndroidPath = if ($env:ARR411_ANDROID) { $env:ARR411_ANDROID } else { Join-Path $parent 'arr411-android' } }
if (-not $ChromePath) { $ChromePath = if ($env:ARR411_EXTENSION) { $env:ARR411_EXTENSION } else { Join-Path $parent 'arr411-chrome' } }
$paths = [ordered]@{ 'arr411-android' = $AndroidPath; 'arr411-chrome' = $ChromePath }

# Read-only git. --no-optional-locks keeps "git status" from rewriting the index of a clone that is being edited.
function Get-Git([string]$Path, [string[]]$GitArgs) {
  $out = & git --no-optional-locks -C $Path @GitArgs
  if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed in $Path (exit code $LASTEXITCODE)" }
  $out
}

function Get-SiblingState([string]$Name, [string]$Path) {
  if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
    throw "$Name not found in $Path (clone it next to this repository, or pass its folder)"
  }
  # A folder inside another repository would answer with the commit of that repository.
  if (@(Get-Git $Path 'rev-parse', '--show-cdup' | Where-Object { $_ }).Count -gt 0) {
    throw "$Path is not the top-level folder of a clone of $Name"
  }
  $head = @(Get-Git $Path 'rev-parse', 'HEAD')[0]
  if ($head -cnotmatch '^[0-9a-f]{40}$') { throw "Unexpected HEAD commit of ${Name}: $head" }
  [pscustomobject]@{
    Head    = $head
    Subject = [string]@(Get-Git $Path 'log', '-1', '--format=%s', $head)[0]
    Dirty   = @(Get-Git $Path 'status', '--porcelain')
    Pushed  = @(Get-Git $Path 'branch', '-r', '--contains', $head).Count -gt 0
  }
}

$state = [ordered]@{}
foreach ($name in $paths.Keys) { $state[$name] = Get-SiblingState $name $paths[$name] }

$unclean = @($state.Keys | Where-Object { $state[$_].Dirty.Count -gt 0 })
foreach ($name in $unclean) {
  Write-Host "$name has uncommitted changes:"
  $state[$name].Dirty | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" }
}
if ($unclean.Count -gt 0 -and -not $Force) {
  throw 'Commit and push these changes first, or use -Force to lock the HEAD commits without them.'
}
foreach ($name in $state.Keys) {
  if (-not $state[$name].Pushed) {
    Write-Warning "$name HEAD is on no remote branch: push it first, CI fetches the commit from GitHub."
  }
}

$old = @{}
if (Test-Path -LiteralPath $lock) {
  foreach ($line in Get-Content -LiteralPath $lock) {
    if ($line -cmatch '^\s*(arr411-android|arr411-chrome)=([0-9a-f]{40})\s*$') { $old[$Matches[1]] = $Matches[2] }
  }
}
$changed = $false
foreach ($name in $state.Keys) {
  $new = $state[$name].Head
  if ($old[$name] -ceq $new) {
    Write-Host "$name unchanged: $new"
  } else {
    $changed = $true
    $was = if ($old.ContainsKey($name)) { $old[$name] } else { '(none)' }
    Write-Host "$name changed:"
    Write-Host "  was $was"
    Write-Host "  now $new  $($state[$name].Subject)"
  }
}
if (-not $changed) {
  Write-Host 'siblings.lock is already up to date.'
  return
}

$text = "# Commits of the sibling repositories that CI builds Arr411 Desktop against.`n" +
  "# Written by tools\update-siblings-lock.ps1 at release time: do not edit by hand.`n" +
  "arr411-android=$($state['arr411-android'].Head)`n" +
  "arr411-chrome=$($state['arr411-chrome'].Head)`n"
if ($PSCmdlet.ShouldProcess($lock, 'Write the commits of the siblings')) {
  [IO.File]::WriteAllText($lock, $text, (New-Object Text.UTF8Encoding($false)))
  Write-Host "Wrote $lock"
  Write-Host 'Commit it once the siblings are pushed.'
}
