$ErrorActionPreference = 'Stop'

$repositoryRoot = $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src/flowboost/flowboost.csproj'
$publishPath = Join-Path $repositoryRoot 'publish'

$publishOutput = & dotnet publish $projectPath -c Release -o $publishPath 2>&1
$publishExitCode = $LASTEXITCODE
$publishOutput | ForEach-Object { Write-Host $_ }

if ($publishExitCode -ne 0) {
    $outputText = $publishOutput -join "`n"
    if ($outputText -match '(?i)(MSB3021|MSB3027|UnauthorizedAccessException|access to the path .*flowboost\.exe.* is denied|being used by another process|used by another process|file is locked)') {
        Write-Error 'Publish output appears to be locked. Close any running publish/flowboost.exe instance, then run .\build.ps1 again. The running process was not stopped.'
    }

    exit $publishExitCode
}

Write-Host "Published executable: $(Join-Path $publishPath 'flowboost.exe')"
