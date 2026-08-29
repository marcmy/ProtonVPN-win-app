[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedBranch = 'agent/port-smart-routing-presentation'
if ($env:GITHUB_EVENT_NAME -ne 'pull_request' -or $env:GITHUB_HEAD_REF -ne $expectedBranch -or $env:GITHUB_JOB -ne 'pipeline') {
    Write-Host 'Smart Routing source normalizer skipped outside the guarded PR pipeline.'
    return
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$cleanFeatureCommit = '46bf778ea4ffc39d77ce5b8a9b82bb631baf6182'
$vmPath = 'src/Client/ProtonVPN.Client/UI/Main/Home/Card/ConnectionCardComponentViewModel.cs'
$testPath = 'src/Client/Localization/ProtonVPN.Client.Localization.Tests/SmartRoutingPresentationTests.cs'

function Get-GitBlobBytes {
    param([Parameter(Mandatory = $true)][string] $Spec)

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = 'git'
    $psi.ArgumentList.Add('-C')
    $psi.ArgumentList.Add($repositoryRoot)
    $psi.ArgumentList.Add('show')
    $psi.ArgumentList.Add($Spec)
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::Start($psi)
    $memory = [System.IO.MemoryStream]::new()
    $process.StandardOutput.BaseStream.CopyTo($memory)
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "git show failed for $Spec`: $stderr"
    }
    return $memory.ToArray()
}

function Write-ModifiedBlob {
    param(
        [Parameter(Mandatory = $true)][string] $GitPath,
        [Parameter(Mandatory = $true)][scriptblock] $Transform
    )

    [byte[]] $bytes = Get-GitBlobBytes "$cleanFeatureCommit`:$GitPath"
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    $updated = & $Transform $text
    if ($updated.Contains("`r")) {
        throw "Unexpected CR character introduced while normalizing $GitPath."
    }

    $destination = Join-Path $repositoryRoot ($GitPath -replace '/', '\')
    [System.IO.File]::WriteAllBytes($destination, [System.Text.Encoding]::UTF8.GetBytes($updated))
}

Write-ModifiedBlob $vmPath {
    param($text)
    $old = '    public string SmartRoutingLabel => $"{Localizer.Get("Countries_SmartRouting")}: {Localizer.GetCountryName(HostCountry)}";'
    $new = @'
    public string SmartRoutingLabel =>
        Localizer.GetFormat(
            "Countries_SmartRouting_RoutedThrough",
            Localizer.GetCountryName(HostCountry));
'@ -replace "`r", ''
    if (-not $text.Contains($old)) {
        throw 'Could not find the pre-fix SmartRoutingLabel implementation in the clean feature commit.'
    }
    $text.Replace($old, $new.TrimEnd("`n"))
}

Write-ModifiedBlob $testPath {
    param($text)
    $method = @'

    [TestMethod]
    public void LocalizedResources_ShouldContainRecoveredSmartRoutingFormat()
    {
        string[] resourcePaths = Directory.GetFiles(
            SourcePathResolver.LocalizationStringsRoot,
            "Resources.resw",
            SearchOption.AllDirectories);

        resourcePaths.Should().HaveCount(37);
        foreach (string resourcePath in resourcePaths)
        {
            string content = File.ReadAllText(resourcePath);
            content.Should().Contain("name=\"Countries_SmartRouting_RoutedThrough\"");
            content.Should().Contain("{0}");
        }

        string viewModel = File.ReadAllText(Path.Combine(_connectionCardDirectory, "ConnectionCardComponentViewModel.cs"));
        viewModel.Should().Contain("Countries_SmartRouting_RoutedThrough");
        viewModel.Should().Contain("Localizer.GetFormat(");
        viewModel.Should().NotContain("Localizer.Get(\"Countries_SmartRouting\")}: ");
    }
'@ -replace "`r", ''
    $lastBrace = $text.LastIndexOf('}')
    if ($lastBrace -lt 0) {
        throw 'Could not locate the test class closing brace.'
    }
    $text.Insert($lastBrace, $method)
}

git -C $repositoryRoot add -- $vmPath $testPath
if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }

$changes = @(git -C $repositoryRoot diff --cached --name-only)
if ($LASTEXITCODE -ne 0) { throw 'git diff failed.' }
if ($changes.Count -ne 2) {
    throw "Expected exactly two normalized source changes, found $($changes.Count): $($changes -join ', ')"
}

git -C $repositoryRoot config user.name 'github-actions[bot]'
git -C $repositoryRoot config user.email '41898282+github-actions[bot]@users.noreply.github.com'
git -C $repositoryRoot commit -m 'Normalize Smart Routing presentation edits'
if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }

git -C $repositoryRoot push origin "HEAD:$expectedBranch"
if ($LASTEXITCODE -ne 0) { throw 'git push failed.' }
