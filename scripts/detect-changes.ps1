#requires -Version 7.0
<#
.SYNOPSIS
    Works out which CI jobs a change actually needs, so a pull request only pays for what it touches.

.DESCRIPTION
    Takes the files changed between two commits (or an explicit list), maps each one to the project that owns it,
    follows the ProjectReference graph to find every project that depends on a changed one, and decides:

      * which test projects to build and run (a change in src/Core runs everything downstream of Core; a change in
        src/CliBridge runs only CliBridge.Tests),
      * whether the shipping binaries need building and the publish layout validating,
      * whether the third-party encoder tools need staging (only for test projects that use them),
      * whether the workflow/script linters need to run.

    The project graph is read from the .csproj files, so adding a project or a reference needs no change here.
    The script fails safe: a path it does not recognise, a global build input (Directory.*.props, global.json, the
    solution file, ci.yml itself) or any trouble working out the diff selects everything.

    Under GitHub Actions the results are written to $GITHUB_OUTPUT and a summary to $GITHUB_STEP_SUMMARY.

.EXAMPLE
    pwsh scripts/detect-changes.ps1 -Base origin/main                       # what would this branch run?
    pwsh scripts/detect-changes.ps1 -ChangedFile src/CliBridge/Program.cs   # what-if for a single file
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    # Commit to diff against. Empty, all zeros or unknown to git selects everything.
    [string]$Base,
    [string]$Head = 'HEAD',
    # Explicit list of changed files (repo-relative); skips git. Used by the self-tests and for what-if runs.
    [string[]]$ChangedFile,
    # Select everything (scheduled runs, manual runs, merge queue).
    [switch]$RunAll,
    # Print the plan as JSON and write nothing to the GitHub outputs.
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path (Split-Path -Parent $PSCommandPath) '..')).Path

function ConvertTo-RepoPath([string]$Path) {
    return ($Path -replace '\\', '/').TrimStart('/')
}

# ---------------------------------------------------------------------------------------------- project graph

function Get-ProjectGraph {
    $projects = @{}
    foreach ($root in 'src', 'tests') {
        $dir = Join-Path $RepoRoot $root
        if (-not (Test-Path $dir)) { continue }

        $files = Get-ChildItem -Path $dir -Recurse -Filter '*.csproj' -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }

        foreach ($file in $files) {
            [xml]$xml = Get-Content -LiteralPath $file.FullName -Raw
            $rel = ConvertTo-RepoPath ([IO.Path]::GetRelativePath($RepoRoot, $file.FullName))
            $dirRel = ConvertTo-RepoPath ([IO.Path]::GetRelativePath($RepoRoot, $file.DirectoryName))

            $references = @($xml.SelectNodes('//ProjectReference') | ForEach-Object {
                $target = [IO.Path]::GetFullPath((Join-Path $file.DirectoryName ($_.GetAttribute('Include') -replace '\\', '/')))
                ConvertTo-RepoPath ([IO.Path]::GetRelativePath($RepoRoot, $target))
            })

            $isTest = $null -ne $xml.SelectSingleNode("//PackageReference[@Include='Microsoft.NET.Test.Sdk']")

            # A test project needs the encoder tools staged when its sources look for them.
            $usesTools = $false
            if ($isTest) {
                $usesTools = $null -ne (Get-ChildItem -Path $file.DirectoryName -Recurse -Filter '*.cs' -File |
                    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
                    Select-String -Pattern 'ToolFact|SQUISH_TOOLS_DIR|ExternalToolResolver' -List |
                    Select-Object -First 1)
            }

            $projects[$rel] = [pscustomobject]@{
                Path       = $rel
                Dir        = $dirRel
                Name       = $file.BaseName
                References = $references
                IsTest     = $isTest
                UsesTools  = $usesTools
            }
        }
    }

    return $projects
}

function Get-Dependents([hashtable]$Projects, [string[]]$Seeds) {
    $affected = [System.Collections.Generic.HashSet[string]]::new($Seeds)
    do {
        $grew = $false
        foreach ($project in $Projects.Values) {
            if ($affected.Contains($project.Path)) { continue }
            if ($project.References | Where-Object { $affected.Contains($_) }) {
                $null = $affected.Add($project.Path)
                $grew = $true
            }
        }
    } while ($grew)

    # The comma stops PowerShell unrolling the set (an empty one would come back as $null).
    return , $affected
}

# ---------------------------------------------------------------------------------------------- classification

# Returns one of: global | project | publish | tools | lint | ignore, plus the owning project for 'project'.
function Get-Classification([string]$File, [hashtable]$Projects) {
    $f = ConvertTo-RepoPath $File

    # The definition of CI itself: prove the whole pipeline still works.
    if ($f -ieq '.github/workflows/ci.yml') { return @{ Kind = 'global'; Why = 'ci.yml changed' } }

    # Global build inputs affect every project.
    if ($f -imatch '(^|/)Directory\.[^/]+\.(props|targets)$' -or
        $f -imatch '(^|/)(global\.json|nuget\.config|\.editorconfig|\.globalconfig)$' -or
        $f -imatch '\.(sln|slnx)$') {
        return @{ Kind = 'global'; Why = "$f is a global build input" }
    }

    # Workflow and script plumbing: lint it, nothing to build.
    if ($f -imatch '^\.github/workflows/[^/]+\.ya?ml$') { return @{ Kind = 'lint'; Why = "$f changed" } }
    if ($f -iin 'scripts/detect-changes.ps1', 'scripts/test-detect-changes.ps1') { return @{ Kind = 'lint'; Why = "$f changed" } }

    # Inputs of the publish layout and the encoder tools.
    if ($f -iin 'scripts/publish-layout.ps1', 'installer/SQUISH_LICENSE.txt', 'docs/windows-deps.md', 'docs/cli.md', 'docs/automation-samples.md') {
        return @{ Kind = 'publish'; Why = "$f is part of the publish layout" }
    }
    if ($f -iin 'tools/tools-manifest.json', 'scripts/fetch-tools.ps1') { return @{ Kind = 'tools'; Why = "$f changed" } }
    if ($f -imatch '^tools/') { return @{ Kind = 'publish'; Why = "$f is shipped with the tools" } }

    # Source: owned by the project whose directory contains it (deepest match wins).
    if ($f -imatch '^(src|tests)/') {
        $owner = $Projects.Values |
            Where-Object { $f.StartsWith($_.Dir + '/', [StringComparison]::OrdinalIgnoreCase) } |
            Sort-Object { $_.Dir.Length } -Descending |
            Select-Object -First 1

        if ($null -eq $owner) { return @{ Kind = 'global'; Why = "$f belongs to no known project" } }
        if ($f -imatch '\.md$') { return @{ Kind = 'ignore'; Why = "$f is documentation" } }
        return @{ Kind = 'project'; Project = $owner; Why = "$f belongs to $($owner.Name)" }
    }

    # Things that cannot affect a build or a test.
    if ($f -imatch '\.md$' -or
        $f -imatch '^docs/' -or
        $f -imatch '^installer/' -or
        $f -imatch '^\.github/' -or
        $f -imatch '^\.vscode/' -or
        $f -iin 'LICENSE', '.gitignore', '.gitattributes', 'scripts/check-tool-updates.ps1', 'scripts/update-release-integrity.ps1', 'scripts/build-icons.ps1') {
        return @{ Kind = 'ignore'; Why = "$f cannot affect a build" }
    }

    return @{ Kind = 'global'; Why = "$f is not recognised, so everything runs" }
}

# ---------------------------------------------------------------------------------------------- changed files

function Get-ChangedFiles {
    if ($ChangedFile) {
        # `pwsh -File` hands a list over as one comma-separated string, so split on the usual separators.
        return @($ChangedFile | ForEach-Object { $_ -split '[,;\r\n]' } | Where-Object { $_.Trim() } | ForEach-Object { ConvertTo-RepoPath $_.Trim() })
    }

    if ([string]::IsNullOrWhiteSpace($Base) -or $Base -match '^0+$') {
        throw 'No base commit to compare against.'
    }

    # --no-renames lists both sides of a rename, so a file moved out of a project still marks that project changed.
    $files = git -C $RepoRoot diff --name-only --no-renames $Base $Head
    if ($LASTEXITCODE -ne 0) { throw "git diff $Base $Head failed." }
    return @($files | Where-Object { $_ } | ForEach-Object { ConvertTo-RepoPath $_ })
}

# ---------------------------------------------------------------------------------------------- plan

function New-Plan {
    $projects = Get-ProjectGraph
    $reasons = [System.Collections.Generic.List[string]]::new()
    $runAll = [bool]$RunAll
    $publish = $false
    $tools = $false
    $lint = $false
    $changedProjects = [System.Collections.Generic.HashSet[string]]::new()
    $files = @()

    if ($RunAll) {
        $reasons.Add('full run requested')
    } else {
        try {
            # @() keeps a single changed file an array (a function's output is unrolled to a bare string).
            $files = @(Get-ChangedFiles)
        } catch {
            $runAll = $true
            $reasons.Add("could not work out the diff ($($_.Exception.Message)); running everything")
        }
    }

    if (-not $runAll) {
        if ($files.Count -eq 0) { $reasons.Add('no changed files') }

        foreach ($file in $files) {
            $c = Get-Classification $file $projects
            switch ($c.Kind) {
                'global'  { $runAll = $true; $reasons.Add($c.Why) }
                'project' {
                    $null = $changedProjects.Add($c.Project.Path)
                    if ($c.Project.Path -like 'src/*') { $publish = $true }
                }
                'publish' { $publish = $true; $reasons.Add($c.Why) }
                'tools'   { $tools = $true; $publish = $true; $reasons.Add($c.Why) }
                'lint'    { $lint = $true; $reasons.Add($c.Why) }
                default   { }
            }
        }
    }

    $testProjects = @($projects.Values | Where-Object { $_.IsTest })
    $selected = if ($runAll) {
        $testProjects
    } else {
        $affected = Get-Dependents $projects @($changedProjects)
        # A change to the tool staging re-runs the tests that exercise the real encoders.
        $testProjects | Where-Object { $affected.Contains($_.Path) -or ($tools -and $_.UsesTools) }
    }

    if ($runAll) {
        $publish = $true
        $lint = $true
    }

    $selected = @($selected | Sort-Object Name)
    $matrix = @($selected | ForEach-Object {
        [ordered]@{ name = $_.Name; project = $_.Path; tools = [bool]$_.UsesTools }
    })

    foreach ($project in ($changedProjects | Sort-Object)) {
        $reasons.Add("$($projects[$project].Name) changed")
    }

    return [pscustomobject][ordered]@{
        run_all     = $runAll
        has_tests   = $matrix.Count -gt 0
        test_matrix = @{ include = $matrix }
        publish     = $publish
        lint        = $lint
        reasons     = @($reasons | Select-Object -Unique)
        files       = $files
    }
}

# ---------------------------------------------------------------------------------------------- output

$plan = New-Plan

if ($AsJson) {
    $plan | ConvertTo-Json -Depth 6
    return
}

$lowerBool = { param($b) if ($b) { 'true' } else { 'false' } }

if ($env:GITHUB_OUTPUT) {
    @(
        "run_all=$(& $lowerBool $plan.run_all)"
        "has_tests=$(& $lowerBool $plan.has_tests)"
        "test_matrix=$($plan.test_matrix | ConvertTo-Json -Depth 5 -Compress)"
        "publish=$(& $lowerBool $plan.publish)"
        "lint=$(& $lowerBool $plan.lint)"
    ) | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
}

$summary = [System.Collections.Generic.List[string]]::new()
$summary.Add('### What this change runs')
$summary.Add('')
$summary.Add('| Job | Runs |')
$summary.Add('| --- | --- |')
$summary.Add("| Tests | $(if ($plan.has_tests) { ($plan.test_matrix.include | ForEach-Object { $_.name }) -join ', ' } else { 'none' }) |")
$summary.Add("| Publish and validate layout | $(& $lowerBool $plan.publish) |")
$summary.Add("| Workflow and script lint | $(& $lowerBool $plan.lint) |")
$summary.Add('')
if ($plan.run_all) { $summary.Add('**Everything runs.**') }
if ($plan.reasons.Count -gt 0) {
    $summary.Add('')
    $summary.Add('Why:')
    foreach ($reason in $plan.reasons) { $summary.Add("- $reason") }
}
$summary.Add('')
$summary.Add("<details><summary>$($plan.files.Count) changed file(s)</summary>")
$summary.Add('')
foreach ($file in $plan.files) { $summary.Add("- ``$file``") }
$summary.Add('')
$summary.Add('</details>')

$summary | ForEach-Object { Write-Host $_ }
if ($env:GITHUB_STEP_SUMMARY) {
    $summary | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
}
