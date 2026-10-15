[CmdletBinding()]
param([string]$SuiteFile = (Join-Path $PSScriptRoot 'change-test-suites.txt'))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
# Each batch declares related classes or exact methods. Never fall back to an unfiltered run.
$suites = @(Get-Content -LiteralPath $SuiteFile | ForEach-Object { $_.Trim() } | Where-Object { $_ -and !$_.StartsWith('#') })
if (!$suites.Count) { throw 'The change test suite list is empty.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$selections = @(foreach ($suite in $suites) {
    if ($suite -cnotmatch '^(Axiom\.(Domain|Engine|Providers\.Windows)\.Tests)\.([A-Za-z_][A-Za-z0-9_]*)(?:\.([A-Za-z_][A-Za-z0-9_]*))?$') { throw "Invalid test selection: $suite" }
    if (!$seen.Add($suite)) { throw "Duplicate test selection: $suite" }
    [pscustomobject]@{ Name = $suite; Project = $Matches[1]; Class = "$($Matches[1]).$($Matches[3])"; Method = $Matches[4] }
})
function Assert-SuiteCoverage([string]$Report, [object[]]$Expected) {
    [xml]$trx = Get-Content -LiteralPath $Report -Raw
    $definitions = @{}
    foreach ($test in $trx.TestRun.TestDefinitions.UnitTest) {
        $class = $test.TestMethod.className.Split(',')[0].Trim()
        $definitions[$test.id] = @($class, "$class.$($test.TestMethod.name)")
    }
    $executed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($result in $trx.TestRun.Results.UnitTestResult) {
        if ($result.outcome -in @('Passed', 'Failed') -and $definitions.ContainsKey($result.testId)) {
            foreach ($name in $definitions[$result.testId]) { [void]$executed.Add($name) }
        }
    }
    foreach ($selection in $Expected) { if (!$executed.Contains($selection.Name)) { throw "No executed tests for declared selection: $($selection.Name)" } }
}
$resultDirectory = Join-Path $repo 'artifacts/test-results/focused'
foreach ($group in ($selections | Group-Object Project)) {
    $project = $group.Name
    $filter = ($group.Group | ForEach-Object { if ($_.Method) { "FullyQualifiedName=$($_.Name)" } else { "FullyQualifiedName~$($_.Class)." } }) -join '|'
    $report = Join-Path $resultDirectory "$project.trx"
    if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
    Write-Host "Related tests: $($group.Group.Name -join ', ')"
    $arguments = @('test', (Join-Path $repo "tests/$project/$project.csproj"), '-c','Release',
        '-p:RestoreLockedMode=true','-p:NuGetAudit=false','/m:1','/nr:false', '--filter', $filter,
        '--logger', "trx;LogFileName=$project.trx", '--results-directory', $resultDirectory)
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Related tests failed: $project" }
    Assert-SuiteCoverage $report $group.Group
}
