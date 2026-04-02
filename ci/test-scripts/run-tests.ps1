param (
    [string]$Category
)

New-Item -ItemType Directory -Force -Path "TestResults" | Out-Null
$reportPath = "TestResults\results_${Category}.xml"

$keywords = @("BVI-", "BackdropLocal", "missing frame","worldTransform", "0.00, 0.00", "chunk", "decoding stream")

& VSTest.Console.exe src\bin\e2e\ProtonVPN.UI.Tests.dll /Settings:.testsettings.xml /TestCaseFilter:"Category=$Category" /Logger:"junit;LogFilePath=$reportPath" |
ForEach-Object {
    $line = $_
    $shouldDrop = $false
    foreach ($keyword in $keywords) {
        if ($line -match $keyword) { 
            $shouldDrop = $true 
        }
    }
    if (-not $shouldDrop) {
        Write-Host $line 
    }
}

exit $LASTEXITCODE
