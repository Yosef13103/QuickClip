param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
function Report([string]$line) {
    Write-Host $line
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $line }
}
if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
    Report 'No TRX produced. Check the restore, build and test steps.'
    throw 'Missing test evidence.'
}
[xml]$report = Get-Content -Raw -LiteralPath $Path
$counts = $report.TestRun.ResultSummary.Counters
if (!$counts -or [int]$counts.total -eq 0) { throw 'TRX contains no tests.' }
$skipped = @($report.TestRun.Results.UnitTestResult | Where-Object outcome -eq 'NotExecuted').Count
Report "TRX: $($counts.passed) passed, $($counts.failed) failed, $skipped skipped/not executed, $($counts.total) total."
if ([int]$counts.failed -ne 0 -or $report.TestRun.ResultSummary.outcome -ne 'Completed') {
    throw "Unsuccessful test run: $($report.TestRun.ResultSummary.outcome)"
}
