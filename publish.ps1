# publish.ps1 - runs the test suite, then publishes a self-contained Release binary.
#
# Output: bin\Release\net9.0-windows\win-x64\publish\Vt2ModManager.exe

[CmdletBinding()]
param(
    [switch]$SkipOpen,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root

try {
    if (-not $SkipTests) {
        Write-Host '[publish] Running tests...' -ForegroundColor Cyan
        dotnet test vt2-mod-manager.sln --nologo --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit $LASTEXITCODE)" }
    }

    Write-Host '[publish] Building Release...' -ForegroundColor Cyan
    dotnet publish Vt2ModManager.csproj -c Release --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)" }

    $out = Join-Path $root 'bin\Release\net9.0-windows\win-x64\publish'
    $exe = Join-Path $out 'Vt2ModManager.exe'
    if (-not (Test-Path $exe)) { throw "Expected exe not found at $exe" }

    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "[publish] OK -- $exe ($size MB)" -ForegroundColor Green

    if (-not $SkipOpen) {
        explorer.exe $out
    }
}
finally {
    Pop-Location
}
