param([string]$Project = (Join-Path $PSScriptRoot '..\ExoLauncher.sln'))
$ErrorActionPreference = 'Stop'
$audit = & dotnet list $Project package --vulnerable --include-transitive --format json
if ($LASTEXITCODE -ne 0) { throw 'Dependency vulnerability query failed.' }
$report = ($audit -join "`n") | ConvertFrom-Json
$vulnerabilities = @($report.projects | ForEach-Object {
    $_.frameworks | ForEach-Object {
        @($_.topLevelPackages) + @($_.transitivePackages) | Where-Object { $_.vulnerabilities }
    }
})
if ($vulnerabilities.Count -gt 0) {
    $vulnerabilities | Select-Object id, resolvedVersion | Format-Table
    throw 'Known vulnerable dependencies require review.'
}
Write-Output 'No known vulnerable .NET dependencies were reported.'
