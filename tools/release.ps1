# release.ps1 - cut a VRChat Archive Mod release from this machine, end to end.
#
#   1. Guard  - clean tree, branch main, and the tag / .csproj / src/Plugin.cs versions all agreeing,
#               with a matching CHANGELOG.md section. Offers to fix the two version strings for you.
#   2. Build  - dotnet build -c Release, prove bin\Release\VRChatArchiveMod.dll was written by THIS
#               run, and print its size and SHA256 so you can check what you are about to publish.
#   3. Tag    - create and push the annotated tag v<Version>, which fires .github/workflows/release.yml
#               and leaves a DRAFT release carrying that version's changelog section.
#   4. Attach - wait for the draft, upload the DLL into it with gh, print the URL. You press Publish.
#
# The DLL is built here, not in CI, and that is permanent: the project compiles against BepInEx 6
# (IL2CPP) and Il2CppInterop assemblies from a local VRChat install and embeds media that is not
# ours to redistribute. Neither libs\ nor ressources\ is in the repository, so no runner can build
# this project. That is the whole reason this script exists.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Version 3.9.21
#   powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Version 3.9.21 -DryRun
#   powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Version 3.9.21 -SkipBuild
#
# Targets Windows PowerShell 5.1, so nothing here uses &&, ||, ?:, ?? or any other 7+ syntax.

[CmdletBinding()]
param(
	# The version being released, without the leading v. The tag becomes v<Version>.
	[Parameter(Mandatory = $true, Position = 0, HelpMessage = 'Version to release, as x.y.z (for example 3.9.21)')]
	[ValidatePattern('^\d+\.\d+\.\d+$')]
	[string] $Version,

	# Run every check and print every action, touching nothing: no build, no file edit, no tag,
	# no push, no upload. Never prompts, so it is safe in a pipeline or a scheduled check.
	[switch] $DryRun,

	# Skip the build and use the bin\Release\VRChatArchiveMod.dll that is already there. For a
	# re-run after a build that already succeeded and an upload that did not.
	[switch] $SkipBuild,

	# I know what I am doing: release from a dirty tree or off main, move a tag that already
	# points somewhere else, and take every confirmation prompt as a yes.
	[switch] $Force
)

$ErrorActionPreference = 'Stop'

# Exit codes, so a wrapper can tell what went wrong without reading the text.
$EXIT_OK        = 0
$EXIT_GUARD     = 1   # dirty tree, wrong branch, wrong directory
$EXIT_VERSION   = 2   # .csproj / Plugin.cs disagree with -Version and were not fixed
$EXIT_CHANGELOG = 3   # no CHANGELOG.md section for this version
$EXIT_BUILD     = 4   # dotnet build failed, or produced no fresh DLL
$EXIT_TAG       = 5   # tag already exists elsewhere, or the push failed
$EXIT_RELEASE   = 6   # the draft never appeared, or the upload failed
$EXIT_TOOLING   = 7   # git, gh, dotnet missing or gh not signed in


# ---------------------------------------------------------------------------------------------
# Output
# ---------------------------------------------------------------------------------------------

function Write-Step {
	param([string] $Text)
	Write-Host ''
	Write-Host "==> $Text" -ForegroundColor Cyan
}

function Write-Detail {
	param([string] $Text)
	Write-Host "    $Text" -ForegroundColor Gray
}

function Write-Good {
	param([string] $Text)
	Write-Host "    $Text" -ForegroundColor Green
}

function Write-Note {
	param([string] $Text)
	Write-Host "    $Text" -ForegroundColor Yellow
}

function Write-Planned {
	param([string] $Text)
	Write-Host "    [dry run] $Text" -ForegroundColor DarkCyan
}

function Stop-Release {
	param(
		[string] $Text,
		[int]    $Code,
		[string[]] $Advice = @()
	)
	Write-Host ''
	Write-Host "RELEASE STOPPED: $Text" -ForegroundColor Red
	foreach ($line in $Advice) {
		Write-Host "  $line" -ForegroundColor Yellow
	}
	Write-Host ''
	exit $Code
}


# ---------------------------------------------------------------------------------------------
# Small utilities
# ---------------------------------------------------------------------------------------------

# Runs a native command, swallows its stderr, and reports stdout plus the exit code.
#
# $ErrorActionPreference is dropped to Continue for the call on purpose: in Windows PowerShell
# 5.1, redirecting a native executable's stderr while the preference is 'Stop' turns each stderr
# line into a NativeCommandError and can abort the script even when the exe exited 0. Commands
# whose stderr is worth seeing (git push, gh upload, dotnet build) are run directly instead, and
# judged on $LASTEXITCODE.
function Invoke-Quiet {
	param(
		[Parameter(Mandatory = $true)] [string]   $File,
		[Parameter(Mandatory = $true)] [string[]] $Arguments
	)
	$previous = $ErrorActionPreference
	$ErrorActionPreference = 'Continue'
	try {
		$output = & $File @Arguments 2>$null
		$code = $LASTEXITCODE
	}
	finally {
		$ErrorActionPreference = $previous
	}
	if ($null -eq $output) {
		$text = ''
	}
	else {
		$text = ($output -join "`n")
	}
	return [pscustomobject]@{ Out = $text; Code = $code }
}

function Confirm-Yes {
	param([string] $Question)
	if ($DryRun) {
		Write-Planned "would ask: $Question"
		return $false
	}
	if ($Force) {
		Write-Note "-Force: answering yes to `"$Question`""
		return $true
	}
	$answer = Read-Host "    $Question [y/N]"
	return ($answer -match '^[Yy]')
}

# Reads a file as text, remembering whether it carried a UTF-8 BOM. Line endings are left alone
# because the text is never split: a version bump must not rewrite all of Plugin.cs into LF, and
# .cs files in this repository are inconsistent about the BOM on purpose (43 of them have one).
function Get-TextFile {
	param([string] $Path)
	$bytes  = [System.IO.File]::ReadAllBytes($Path)
	$hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
	return [pscustomobject]@{
		Text   = [System.IO.File]::ReadAllText($Path)
		HasBom = $hasBom
	}
}

function Set-TextFile {
	param(
		[string] $Path,
		[string] $Text,
		[bool]   $HasBom
	)
	$encoding = New-Object System.Text.UTF8Encoding($HasBom)
	[System.IO.File]::WriteAllText($Path, $Text, $encoding)
}

function Format-Size {
	param([long] $Bytes)
	return ('{0:N0} bytes ({1:N2} MiB)' -f $Bytes, ($Bytes / 1MB))
}


# ---------------------------------------------------------------------------------------------
# 0. Where are we, and is everything we need installed
# ---------------------------------------------------------------------------------------------

$tag = "v$Version"

if ([string]::IsNullOrEmpty($PSScriptRoot)) {
	Write-Host 'Run this as a script file, not through -Command: it locates the repository from its own path.' -ForegroundColor Red
	exit 7
}
$repoRoot = Split-Path -Parent $PSScriptRoot

# Set when a version bump is committed here, which means the branch has to be pushed alongside
# the tag: a tag whose commit is on no branch is a confusing thing to leave on a remote.
$branchNeedsPush = $false

$csprojPath    = Join-Path $repoRoot 'VRChatArchiveMod.csproj'
$pluginPath    = Join-Path $repoRoot 'src\Plugin.cs'
$changelogPath = Join-Path $repoRoot 'CHANGELOG.md'
$dllPath       = Join-Path $repoRoot 'bin\Release\VRChatArchiveMod.dll'

Write-Host ''
Write-Host "VRChat Archive Mod - release $tag" -ForegroundColor White
if ($DryRun) {
	Write-Note 'DRY RUN: nothing will be built, edited, committed, tagged, pushed or uploaded.'
}

Write-Step 'Checking the working directory and the tooling'

if (-not (Test-Path -LiteralPath $csprojPath)) {
	Stop-Release "VRChatArchiveMod.csproj was not found in $repoRoot." $EXIT_GUARD @(
		'This script expects to live in tools\ inside the repository.'
	)
}
Set-Location -LiteralPath $repoRoot
Write-Detail "repository   $repoRoot"

$required = @('git', 'gh')
if (-not $SkipBuild -and -not $DryRun) {
	$required += 'dotnet'
}
foreach ($exe in $required) {
	if (-not (Get-Command $exe -ErrorAction SilentlyContinue)) {
		Stop-Release "'$exe' is not on PATH." $EXIT_TOOLING @(
			'git and gh (the GitHub CLI) are always needed; dotnet is needed unless -SkipBuild is given.'
		)
	}
}

# gh authentication is checked here rather than at the upload step. Finding out that gh is not
# signed in is cheap now and expensive after the tag has been pushed.
$auth = Invoke-Quiet 'gh' @('auth', 'status')
if ($auth.Code -ne 0) {
	Stop-Release 'The GitHub CLI is not signed in.' $EXIT_TOOLING @(
		'Run:  gh auth login'
	)
}
Write-Good 'git, gh (signed in) and the project file are all present.'

$originUrl = (Invoke-Quiet 'git' @('remote', 'get-url', 'origin')).Out.Trim()
if ($originUrl -eq '') {
	Stop-Release "This clone has no 'origin' remote, so there is nowhere to push a tag." $EXIT_GUARD
}
Write-Detail "origin       $originUrl"


# ---------------------------------------------------------------------------------------------
# 1. Guards: clean tree, on main
# ---------------------------------------------------------------------------------------------

Write-Step 'Checking the branch and the working tree'

$branch = "$(& git rev-parse --abbrev-ref HEAD)".Trim()
if ($LASTEXITCODE -ne 0) {
	Stop-Release 'git could not read the current branch.' $EXIT_GUARD
}
Write-Detail "branch       $branch"

if ($branch -ne 'main') {
	if ($Force) {
		Write-Note "Not on main (on '$branch'), continuing because -Force was given."
	}
	else {
		Stop-Release "Releases are cut from main; this clone is on '$branch'." $EXIT_GUARD @(
			'Switch to main, or re-run with -Force if you really mean to tag this branch.'
		)
	}
}

$dirty = @(& git status --porcelain)
if ($dirty.Count -gt 0) {
	Write-Detail 'uncommitted changes:'
	foreach ($line in $dirty) {
		Write-Detail "  $line"
	}
	if ($Force) {
		Write-Note 'Working tree is dirty, continuing because -Force was given.'
		Write-Note 'The tag will point at the last commit, NOT at what is on disk.'
	}
	else {
		Stop-Release 'The working tree is dirty.' $EXIT_GUARD @(
			'A tag names a commit, so anything uncommitted would not be in the release.',
			'Commit or stash first, or re-run with -Force.'
		)
	}
}
else {
	Write-Good 'Working tree is clean.'
}


# ---------------------------------------------------------------------------------------------
# 2. The three version strings must agree
# ---------------------------------------------------------------------------------------------

Write-Step "Checking that the project says $Version"

# .csproj <Version> stamps the assembly. src/Plugin.cs PluginInfo.Version is what [BepInPlugin]
# registers and what the mod prints into the BepInEx log, so it is the number a user quotes back
# to you in a bug report. These two have disagreed before: at the 3.5.0 release the csproj said
# 3.5.0 while Plugin.cs still said 3.4.0, and the in-game banner was wrong for that whole release.
# The workflow refuses to draft a release unless the tag and both files agree, so it is worth
# catching here, before anything is pushed.
$csprojPattern = '(<Version>\s*)(\d+\.\d+\.\d+)(\s*</Version>)'
$pluginPattern = '(public\s+const\s+string\s+Version\s*=\s*")(\d+\.\d+\.\d+)(")'

$targets = @(
	[pscustomobject]@{ Label = 'VRChatArchiveMod.csproj'; Path = $csprojPath; Pattern = $csprojPattern },
	[pscustomobject]@{ Label = 'src\Plugin.cs';           Path = $pluginPath; Pattern = $pluginPattern }
)

$stale = @()
foreach ($target in $targets) {
	if (-not (Test-Path -LiteralPath $target.Path)) {
		Stop-Release "$($target.Label) is missing." $EXIT_VERSION
	}
	$file  = Get-TextFile -Path $target.Path
	$match = [regex]::Match($file.Text, $target.Pattern)
	if (-not $match.Success) {
		Stop-Release "No version string could be found in $($target.Label)." $EXIT_VERSION @(
			'The script looks for <Version>x.y.z</Version> and for',
			'public const string Version = "x.y.z"; - fix the file or the pattern in this script.'
		)
	}
	$found = $match.Groups[2].Value
	if ($found -eq $Version) {
		Write-Good "$($target.Label) says $found"
	}
	else {
		Write-Note "$($target.Label) says $found, expected $Version"
		$stale += [pscustomobject]@{
			Label   = $target.Label
			Path    = $target.Path
			Pattern = $target.Pattern
			Found   = $found
			HasBom  = $file.HasBom
			Text    = $file.Text
		}
	}
}

if ($stale.Count -gt 0) {
	if ($DryRun) {
		Stop-Release "$($stale.Count) file(s) still carry an older version." $EXIT_VERSION @(
			"A real run would offer to rewrite them to $Version and to commit that change."
		)
	}

	Write-Host ''
	if (-not (Confirm-Yes "Rewrite those $($stale.Count) file(s) to $Version now?")) {
		Stop-Release 'The version strings do not match the release.' $EXIT_VERSION @(
			"Set the version to $Version in the files listed above, commit, then run this script again."
		)
	}

	foreach ($item in $stale) {
		# The instance Replace is the one that takes a count. The static [regex]::Replace has no
		# count overload at all: a 4th argument of 1 there binds to RegexOptions and quietly means
		# IgnoreCase, replacing every match instead of the first.
		$expression = New-Object System.Text.RegularExpressions.Regex($item.Pattern)
		$updated = $expression.Replace($item.Text, ('${1}' + $Version + '${3}'), 1)
		Set-TextFile -Path $item.Path -Text $updated -HasBom $item.HasBom
		Write-Good "$($item.Label): $($item.Found) -> $Version"
	}

	# The tag has to name a commit that already contains the new version, because the workflow
	# checks out the tag and reads these two files back. Leaving the bump uncommitted would make
	# the release fail its own version check.
	Write-Host ''
	if (-not (Confirm-Yes 'Commit that version bump?')) {
		Stop-Release 'The version bump is written but not committed.' $EXIT_VERSION @(
			'The tag would point at a commit that still carries the old version, and the release',
			'workflow would refuse it. Commit the two files, then run this script again.'
		)
	}

	& git add -- $csprojPath $pluginPath
	if ($LASTEXITCODE -ne 0) {
		Stop-Release 'git add failed.' $EXIT_VERSION
	}
	& git commit -m "Release: set the version to $Version in the csproj and the plugin banner"
	if ($LASTEXITCODE -ne 0) {
		Stop-Release 'git commit failed.' $EXIT_VERSION
	}
	Write-Good 'Version bump committed.'
	$branchNeedsPush = $true
}


# ---------------------------------------------------------------------------------------------
# 3. CHANGELOG.md must have a section for this version
# ---------------------------------------------------------------------------------------------

Write-Step "Reading the CHANGELOG.md section for $Version"

if (-not (Test-Path -LiteralPath $changelogPath)) {
	Stop-Release 'CHANGELOG.md is missing.' $EXIT_CHANGELOG @(
		'The release notes are taken from it, so there would be nothing to publish.'
	)
}

# Exactly what .github/workflows/release.yml does, so a section that passes here passes there:
# everything between "## [x.y.z]" and the next "## " heading, matched as literal text.
$changelogLines = ([System.IO.File]::ReadAllText($changelogPath)) -split "`r?`n"
$heading = "## [$Version]"
$startIndex = -1
for ($i = 0; $i -lt $changelogLines.Count; $i++) {
	if ($changelogLines[$i].StartsWith($heading, [System.StringComparison]::Ordinal)) {
		$startIndex = $i
		break
	}
}

if ($startIndex -lt 0) {
	$existing = @()
	foreach ($line in $changelogLines) {
		if ($line.StartsWith('## ', [System.StringComparison]::Ordinal)) {
			$existing += "    $line"
		}
	}
	Write-Host ''
	Write-Note 'Headings that do exist:'
	foreach ($line in $existing) {
		Write-Host $line -ForegroundColor Yellow
	}
	Stop-Release "CHANGELOG.md has no '## [$Version]' section." $EXIT_CHANGELOG @(
		"Add a section headed '## [$Version]' (a date may follow it), then run this script again."
	)
}

$sectionLines = @()
for ($i = $startIndex + 1; $i -lt $changelogLines.Count; $i++) {
	if ($changelogLines[$i].StartsWith('## ', [System.StringComparison]::Ordinal)) {
		break
	}
	$sectionLines += $changelogLines[$i]
}
$sectionText = ($sectionLines -join "`n").Trim()

if ($sectionText -eq '') {
	Stop-Release "The '## [$Version]' section of CHANGELOG.md is empty." $EXIT_CHANGELOG @(
		'It would be published as the release notes exactly as it is.'
	)
}

Write-Good "Found $($sectionLines.Count) line(s) of notes. They will be the release body:"
Write-Host ''
$preview = $sectionText -split "`n"
$previewCount = [Math]::Min(12, $preview.Count)
for ($i = 0; $i -lt $previewCount; $i++) {
	Write-Host "    | $($preview[$i])" -ForegroundColor DarkGray
}
if ($preview.Count -gt $previewCount) {
	Write-Host "    | ... $($preview.Count - $previewCount) more line(s)" -ForegroundColor DarkGray
}


# ---------------------------------------------------------------------------------------------
# 4. Build, and prove the DLL came from this run
# ---------------------------------------------------------------------------------------------

Write-Step 'Building bin\Release\VRChatArchiveMod.dll'

if ($DryRun) {
	Write-Planned "would run: dotnet build `"$csprojPath`" -c Release --nologo"
	if (Test-Path -LiteralPath $dllPath) {
		$present = Get-Item -LiteralPath $dllPath
		Write-Detail "a DLL is already there, from $($present.LastWriteTime): $(Format-Size $present.Length)"
	}
	else {
		Write-Detail 'no DLL is present yet.'
	}
}
elseif ($SkipBuild) {
	Write-Note '-SkipBuild: using the DLL that is already in bin\Release.'
	if (-not (Test-Path -LiteralPath $dllPath)) {
		Stop-Release 'bin\Release\VRChatArchiveMod.dll does not exist.' $EXIT_BUILD @(
			'-SkipBuild only makes sense when a build has already produced one. Drop the switch.'
		)
	}
	$dllInfo = Get-Item -LiteralPath $dllPath
	Write-Note "Its freshness is NOT verified: it was written $($dllInfo.LastWriteTime)."
}
else {
	# The existing DLL is deleted first, deliberately. MSBuild is incremental: with no source
	# change it leaves the old file untouched, and then "is the DLL newer than the build" would
	# fail on a perfectly good build. Removing the output forces the target to run, which makes
	# the freshness check below mean something instead of being a coin toss.
	if (Test-Path -LiteralPath $dllPath) {
		Remove-Item -LiteralPath $dllPath -Force
		Write-Detail 'removed the previous DLL so the build has to write a new one.'
	}

	$buildStart = Get-Date
	& dotnet build $csprojPath -c Release --nologo
	if ($LASTEXITCODE -ne 0) {
		Stop-Release "dotnet build failed with exit code $LASTEXITCODE." $EXIT_BUILD @(
			'This project only builds where libs\ (BepInEx + interop assemblies) and ressources\',
			'(the embedded media) exist. Neither is in the repository, which is also exactly why',
			'no CI runner can build it.'
		)
	}
	if (-not (Test-Path -LiteralPath $dllPath)) {
		Stop-Release 'The build reported success but bin\Release\VRChatArchiveMod.dll is not there.' $EXIT_BUILD
	}
	$dllInfo = Get-Item -LiteralPath $dllPath
	if ($dllInfo.LastWriteTime -lt $buildStart) {
		Stop-Release 'The DLL is older than the build that just ran.' $EXIT_BUILD @(
			"Written $($dllInfo.LastWriteTime), build started $buildStart.",
			'Something served a stale output; do not ship it.'
		)
	}
	Write-Good 'Build succeeded and wrote a fresh DLL.'
}

$dllSize   = 0
$dllHash   = ''
$dllWhen   = ''
if (Test-Path -LiteralPath $dllPath) {
	$dllInfo = Get-Item -LiteralPath $dllPath
	$dllSize = $dllInfo.Length
	$dllWhen = $dllInfo.LastWriteTime
	$dllHash = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash
	Write-Detail "path         $dllPath"
	Write-Detail "written      $dllWhen"
	Write-Detail "size         $(Format-Size $dllSize)"
	Write-Detail "sha256       $dllHash"
}
elseif (-not $DryRun) {
	Stop-Release 'No DLL to attach.' $EXIT_BUILD
}


# ---------------------------------------------------------------------------------------------
# 5. Tag
# ---------------------------------------------------------------------------------------------

Write-Step "Tagging $tag"

$headSha = (Invoke-Quiet 'git' @('rev-parse', 'HEAD')).Out.Trim()
$localTagResult = Invoke-Quiet 'git' @('rev-parse', '-q', '--verify', "refs/tags/$tag")
if ($localTagResult.Code -eq 0) {
	$localTagSha = $localTagResult.Out.Trim()
}
else {
	$localTagSha = ''
}

# What the tag resolves to, following the annotation, so an annotated tag can be compared with a
# commit sha.
$localTagCommit = ''
if ($localTagSha -ne '') {
	$localTagCommit = (Invoke-Quiet 'git' @('rev-list', '-n', '1', $tag)).Out.Trim()
}

$needTagCreate = $true
if ($localTagCommit -ne '') {
	if ($localTagCommit -eq $headSha) {
		Write-Detail "$tag already exists here and points at HEAD; reusing it."
		$needTagCreate = $false
	}
	elseif ($Force) {
		Write-Note "$tag exists and points at $($localTagCommit.Substring(0, 7)), not HEAD. -Force: moving it."
	}
	else {
		Stop-Release "$tag already exists locally and points somewhere other than HEAD." $EXIT_TAG @(
			"tag  $localTagCommit",
			"HEAD $headSha",
			"Delete it with 'git tag -d $tag' if it was never pushed, or re-run with -Force."
		)
	}
}

if ($needTagCreate) {
	if ($DryRun) {
		Write-Planned "would run: git tag -a $tag -m `"VRChat Archive Mod $tag`""
	}
	else {
		if ($localTagCommit -ne '') {
			& git tag -d $tag | Out-Null
		}
		& git tag -a $tag -m "VRChat Archive Mod $tag"
		if ($LASTEXITCODE -ne 0) {
			Stop-Release 'git tag failed.' $EXIT_TAG
		}
		Write-Good "Created annotated tag $tag on $($headSha.Substring(0, 7))."
	}
}


# ---------------------------------------------------------------------------------------------
# 6. Push - the first step anyone else can see
# ---------------------------------------------------------------------------------------------

Write-Step 'Pushing to origin'

$remoteTagLine = (Invoke-Quiet 'git' @('ls-remote', '--tags', 'origin', "refs/tags/$tag")).Out.Trim()
$tagAlreadyPushed = $false
if ($remoteTagLine -ne '') {
	Write-Detail "origin already has $tag."
	if ($Force) {
		Write-Note '-Force: it will be overwritten.'
	}
	else {
		$tagAlreadyPushed = $true
		Write-Detail 'Nothing to push; continuing to the release itself.'
	}
}

if ($DryRun) {
	if ($branchNeedsPush) {
		Write-Planned "would run: git push origin $branch"
	}
	if (-not $tagAlreadyPushed) {
		Write-Planned "would run: git push origin $tag"
		Write-Planned 'that push is what starts .github/workflows/release.yml and drafts the release.'
	}
	Write-Planned "would then wait for the draft and run: gh release upload $tag `"$dllPath`" --clobber"
	Write-Host ''
	Write-Host 'Dry run finished. Every check above passed; nothing was changed.' -ForegroundColor Cyan
	Write-Host ''
	exit $EXIT_OK
}

if (-not $tagAlreadyPushed) {
	Write-Host ''
	Write-Detail 'This is the first thing other people can see. It pushes:'
	if ($branchNeedsPush) {
		Write-Detail "  - the version bump commit, to $branch"
	}
	Write-Detail "  - the tag $tag, which starts the release workflow"
	if (-not (Confirm-Yes 'Push now?')) {
		Stop-Release 'Nothing was pushed.' $EXIT_TAG @(
			"The tag $tag exists locally and nothing left this machine.",
			"Push it yourself with:  git push origin $tag",
			"Or drop it with:        git tag -d $tag"
		)
	}

	if ($branchNeedsPush) {
		& git push origin $branch
		if ($LASTEXITCODE -ne 0) {
			Stop-Release "Pushing $branch failed." $EXIT_TAG @(
				'The tag was not pushed. Resolve the branch push first, then run this script again.'
			)
		}
		Write-Good "Pushed $branch."
	}

	if ($Force -and $remoteTagLine -ne '') {
		& git push --force origin "refs/tags/$tag"
	}
	else {
		& git push origin "refs/tags/$tag"
	}
	if ($LASTEXITCODE -ne 0) {
		Stop-Release "Pushing $tag failed." $EXIT_TAG
	}
	Write-Good "Pushed $tag. The release workflow is starting."
}


# ---------------------------------------------------------------------------------------------
# 7. Wait for the draft, then attach the DLL
# ---------------------------------------------------------------------------------------------

Write-Step 'Waiting for the workflow to draft the release'

Write-Detail 'The workflow checks the tag against the two version files, cuts the notes out of'
Write-Detail 'CHANGELOG.md and opens a draft release. It never builds anything.'

# gh finds a draft by its tag name: the tag lookup 404s for drafts, and gh then falls back to
# listing releases and matching on tag name. That is what makes "draft first, upload second" work.
$deadline = (Get-Date).AddMinutes(5)
$release  = $null
$attempt  = 0
while ((Get-Date) -lt $deadline) {
	$attempt++
	$view = Invoke-Quiet 'gh' @('release', 'view', $tag, '--json', 'url,isDraft,name')
	if ($view.Code -eq 0 -and $view.Out -ne '') {
		$release = $view.Out | ConvertFrom-Json
		break
	}
	if ($attempt -eq 1) {
		Write-Detail 'not there yet; checking every 10 seconds.'
	}
	Start-Sleep -Seconds 10
}

if ($null -eq $release) {
	Stop-Release "No release for $tag appeared within 5 minutes." $EXIT_RELEASE @(
		'Check the run:  gh run list --workflow=release.yml',
		'A version mismatch or a missing changelog section makes the workflow refuse to draft one;',
		'the job summary says which. Once the draft exists, re-run this script with -SkipBuild and',
		'it will go straight to the upload.'
	)
}

if ($release.isDraft) {
	Write-Good "Draft release found: $($release.name)"
}
else {
	Write-Note "A release for $tag exists and is already published."
}

Write-Step 'Uploading the DLL'

& gh release upload $tag $dllPath --clobber
if ($LASTEXITCODE -ne 0) {
	Stop-Release "gh release upload failed with exit code $LASTEXITCODE." $EXIT_RELEASE @(
		"The draft is still there. Retry with:  gh release upload $tag `"$dllPath`" --clobber"
	)
}
Write-Good 'VRChatArchiveMod.dll attached.'

# Read the assets back rather than trusting the upload's own exit code.
$assetView = Invoke-Quiet 'gh' @('release', 'view', $tag, '--json', 'assets')
if ($assetView.Code -eq 0 -and $assetView.Out -ne '') {
	$assets = ($assetView.Out | ConvertFrom-Json).assets
	foreach ($asset in $assets) {
		Write-Detail "asset        $($asset.name)  $(Format-Size $asset.size)"
	}
}


# ---------------------------------------------------------------------------------------------
# 8. Summary. Publishing is left to a human on purpose.
# ---------------------------------------------------------------------------------------------

Write-Host ''
Write-Host '---------------------------------------------------------------' -ForegroundColor White
Write-Host " $tag is drafted and carries its DLL" -ForegroundColor White
Write-Host '---------------------------------------------------------------' -ForegroundColor White
Write-Host ''
Write-Host "  release   $($release.url)"
Write-Host "  commit    $headSha"
Write-Host "  dll       $(Format-Size $dllSize)"
Write-Host "  sha256    $dllHash"
Write-Host ''
Write-Host '  Read the draft, then publish it yourself:' -ForegroundColor Yellow
Write-Host ''
Write-Host "    gh release view $tag --web"
Write-Host "    gh release edit $tag --draft=false"
Write-Host ''
Write-Host '  This script does not publish. A draft can be fixed or deleted quietly; a published' -ForegroundColor DarkGray
Write-Host '  release has already notified every watcher and become the download people get.' -ForegroundColor DarkGray
Write-Host ''

exit $EXIT_OK
