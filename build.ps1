param([switch]$NoRestore)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if (-not $NoRestore) {
    dotnet restore tests/PillManager.Checks.csproj
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
}
dotnet build tests/PillManager.Checks.csproj --no-restore -c Release -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
dotnet run --project tests/PillManager.Checks.csproj -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Protocol checks failed.' }
dotnet publish src/PillManager/PillManager.csproj -c Release --no-restore --self-contained false -o dist/PILL-Desktop-0.2.1-win-x64 -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Publish failed. Close the app before building.' }
Write-Output 'Ready: dist/PILL-Desktop-0.2.1-win-x64/PillManager.exe'
