[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedBranch = 'agent/port-smart-routing-presentation'
if ($env:GITHUB_EVENT_NAME -ne 'pull_request' -or $env:GITHUB_HEAD_REF -ne $expectedBranch -or $env:GITHUB_JOB -ne 'pipeline') {
    Write-Host 'Smart Routing localization applicator skipped outside the guarded PR pipeline.'
    return
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$stringsRoot = Join-Path $repositoryRoot 'src\Client\Localization\ProtonVPN.Client.Localization\Strings'
$key = 'Countries_SmartRouting_RoutedThrough'
$comment = 'Label used when server is virtual. {0} is a place holder for the name of the country the server is routed through.'

$translations = [ordered]@{
    'ar-SA' = 'Smart routed through {0}'
    'be-BY' = 'Разумная маршрутызацыя праз краіну {0}'
    'ca-ES' = 'Smart ha encaminat a través de {0}'
    'cs-CZ' = 'Smart routed through {0}'
    'da-DK' = 'Smart routet gennem {0}'
    'de-DE' = 'Intelligent weitergeleitet über {0}'
    'el-GR' = 'Έξυπνη δρομολόγηση μέσω {0}'
    'en-US' = 'Smart routed through {0}'
    'es-419' = 'Enrutamiento inteligente a través de {0}'
    'es-ES' = 'Smart routed through {0}'
    'fa-IR' = 'Smart routed through {0}'
    'fi-FI' = 'Älykäs reititys {0}:n kautta'
    'fil-PH' = 'Smart routed through {0}'
    'fil-Tglg' = 'Smart routed through {0}'
    'fr-FR' = 'Routé de manière intelligente à travers {0}'
    'hu-HU' = 'Smart routed through {0}'
    'id-ID' = 'Smart routed through {0}'
    'it-IT' = 'Smart routed through {0}'
    'ja-JP' = 'Smart routed through {0}'
    'ka-GE' = 'Smart routed through {0}'
    'ko-KR' = 'Smart routed through {0}'
    'nb-NO' = 'Smart routed through {0}'
    'nl-NL' = 'Slimme routering via {0}'
    'pl-PL' = 'Smart routed through {0}'
    'pt-BR' = 'Smart routed through {0}'
    'pt-PT' = 'Smart routed through {0}'
    'ro-RO' = 'Rutare inteligentă prin {0}'
    'ru-RU' = 'Smart routed through {0}'
    'sk-SK' = 'Inteligentné smerovanie cez {0}'
    'sl-SI' = 'Pametno usmerjanje prek {0}'
    'sv-SE' = 'Smart routed through {0}'
    'th-TH' = 'Smart routed through {0}'
    'tr-TR' = '{0}, akıllı yönlendirme üzerinden yönlendirildi'
    'uk-UA' = 'Smart-спрямування через {0}'
    'vi-VN' = 'Smart routed through {0}'
    'zh-CN' = '通过 {0} 智能路由'
    'zh-TW' = 'Smart routed through {0}'
}

if ($translations.Count -ne 37) {
    throw "Expected 37 recovered locales, found $($translations.Count)."
}

foreach ($entry in $translations.GetEnumerator()) {
    $locale = $entry.Key
    $resourcePath = Join-Path $stringsRoot "$locale\Resources.resw"
    if (-not (Test-Path -LiteralPath $resourcePath -PathType Leaf)) {
        throw "Missing localization resource: $resourcePath"
    }

    $text = [System.IO.File]::ReadAllText($resourcePath)
    if ($text.Contains("name=\"$key\"")) {
        continue
    }

    $newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $smartRoutingPattern = '(?s)(  <data name="Countries_SmartRouting"[^>]*>.*?</data>\r?\n)'
    $match = [regex]::Match($text, $smartRoutingPattern)
    if (-not $match.Success) {
        throw "Could not locate Countries_SmartRouting in $resourcePath"
    }

    $spaceAttribute = if ($locale -eq 'en-US') { ' xml:space="preserve"' } else { '' }
    $block = "  <data name=\"$key\"$spaceAttribute>$newline" +
             "    <value>$($entry.Value)</value>$newline" +
             "    <comment>$comment</comment>$newline" +
             "  </data>$newline"

    $updated = $text.Insert($match.Index + $match.Length, $block)
    [System.IO.File]::WriteAllText($resourcePath, $updated, [System.Text.UTF8Encoding]::new($true))
}

$viewModelPath = Join-Path $repositoryRoot 'src\Client\ProtonVPN.Client\UI\Main\Home\Card\ConnectionCardComponentViewModel.cs'
$viewModel = [System.IO.File]::ReadAllText($viewModelPath)
$oldLabel = '    public string SmartRoutingLabel => $"{Localizer.Get(""Countries_SmartRouting"")}: {Localizer.GetCountryName(HostCountry)}";'
$newLabel = @'
    public string SmartRoutingLabel =>
        Localizer.GetFormat(
            "Countries_SmartRouting_RoutedThrough",
            Localizer.GetCountryName(HostCountry));
'@ -replace "`n", "`r`n"
if ($viewModel.Contains('Countries_SmartRouting_RoutedThrough')) {
    Write-Host 'ViewModel already uses the recovered Smart Routing format resource.'
} elseif ($viewModel.Contains($oldLabel)) {
    $viewModel = $viewModel.Replace($oldLabel, $newLabel.TrimEnd("`r", "`n"))
    [System.IO.File]::WriteAllText($viewModelPath, $viewModel, [System.Text.UTF8Encoding]::new($true))
} else {
    throw 'Could not locate the approximated SmartRoutingLabel implementation.'
}

$testPath = Join-Path $repositoryRoot 'src\Client\Localization\ProtonVPN.Client.Localization.Tests\SmartRoutingPresentationTests.cs'
$test = [System.IO.File]::ReadAllText($testPath)
if (-not $test.Contains('LocalizedResources_ShouldContainRecoveredSmartRoutingFormat')) {
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
'@ -replace "`n", "`r`n"
    $lastBrace = $test.LastIndexOf('}')
    if ($lastBrace -lt 0) { throw 'Could not locate test class closing brace.' }
    $test = $test.Insert($lastBrace, $method)
    [System.IO.File]::WriteAllText($testPath, $test, [System.Text.UTF8Encoding]::new($false))
}

$changedResources = @(git -C $repositoryRoot status --short -- 'src/Client/Localization/ProtonVPN.Client.Localization/Strings/*/Resources.resw')
if ($LASTEXITCODE -ne 0) { throw 'git status failed.' }
if ($changedResources.Count -notin @(0, 37)) {
    throw "Expected either 0 or 37 changed localization resources, found $($changedResources.Count)."
}

$allChanges = @(git -C $repositoryRoot status --short)
if ($LASTEXITCODE -ne 0) { throw 'git status failed.' }
if ($allChanges.Count -eq 0) {
    Write-Host 'Smart Routing localization parity is already applied.'
    return
}

Write-Host "Applying exact recovered Smart Routing localization to $($changedResources.Count) locale resources."
git -C $repositoryRoot config user.name 'github-actions[bot]'
git -C $repositoryRoot config user.email '41898282+github-actions[bot]@users.noreply.github.com'
git -C $repositoryRoot add -- `
    'src/Client/Localization/ProtonVPN.Client.Localization/Strings' `
    'src/Client/ProtonVPN.Client/UI/Main/Home/Card/ConnectionCardComponentViewModel.cs' `
    'src/Client/Localization/ProtonVPN.Client.Localization.Tests/SmartRoutingPresentationTests.cs'
if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }

git -C $repositoryRoot commit -m 'Port recovered Smart Routing localization'
if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }

git -C $repositoryRoot push origin "HEAD:$expectedBranch"
if ($LASTEXITCODE -ne 0) { throw 'git push failed.' }
