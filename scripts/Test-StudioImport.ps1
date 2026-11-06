[CmdletBinding()]
param(
    [switch]$NoBuild,
    [string]$Dotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$project = 'SubTerra.Diagnostics/SubTerra.Diagnostics.csproj'

function Test-Sample($File, $Axis, $Layout, $Triangles, $ExpectedSize) {
    $arguments = @('run', '--project', $project, '-c', 'Release', '--no-build', '--no-restore',
        '--', '--check-studio-export', "samples/scan-import/$File", 'mm', $Axis)
    if ($Layout) { $arguments += $Layout }
    $json = & $Dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Import failed for $File (exit $LASTEXITCODE)." }
    $report = ($json -join [Environment]::NewLine) | ConvertFrom-Json
    if ($report.ProjectRoundTrip -ne $true -or $report.Synthetic -ne $true -or $report.Points -ne 8 -or
        $report.ColoredPoints -ne 8 -or $report.Triangles -ne $Triangles) {
        throw "Unexpected geometry, colors or project round trip for $File."
    }
    foreach ($axisName in 'X','Y','Z') {
        $actual = $report.SizeMeters.$axisName
        if ($null -eq $actual -or [Math]::Abs($actual - $ExpectedSize[$axisName]) -gt 0.000001) {
            throw "Unexpected $axisName extent for $File ($Axis up): $actual m."
        }
    }
    Write-Output "PASS: $File ($Axis up), 8 colored points, $Triangles triangles, size and project round trip verified."
}

Push-Location (Join-Path $PSScriptRoot '..')
try {
    if (-not $NoBuild) {
        & $Dotnet restore $project --locked-mode
        if ($LASTEXITCODE -ne 0) { throw "Restore failed (exit $LASTEXITCODE)." }
        & $Dotnet build $project -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
    }
    $zUp = @{ X = 0.10; Y = 0.06; Z = 0.04 }
    Test-Sample 'block-100x60x40-mm.ply' 'z' '' 12 $zUp
    Test-Sample 'points-with-normals-and-rgb.asc' 'z' 'xyz-normal-rgb' 0 $zUp
    Test-Sample 'block-100x60x40-mm.ply' 'y' '' 12 @{ X = 0.10; Y = 0.04; Z = 0.06 }
}
finally { Pop-Location }
