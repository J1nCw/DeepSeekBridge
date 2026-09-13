$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkDir 'csc.exe'
$references = @('System.dll','System.Core.dll','System.Windows.Forms.dll','System.Drawing.dll', (Join-Path $frameworkDir 'WPF\UIAutomationClient.dll'), (Join-Path $frameworkDir 'WPF\UIAutomationTypes.dll'), (Join-Path $frameworkDir 'WPF\WindowsBase.dll'))
$candidatePath = Join-Path $taskRoot 'DeepSeekBridge.candidate.exe'
$finalPath = Join-Path $taskRoot 'DeepSeekBridge.exe'
$compilerArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',('/out:' + $candidatePath),('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')))
foreach ($referencePath in $references) { $compilerArgs += '/reference:' + $referencePath }
$compilerArgs += Join-Path $PSScriptRoot 'Program.cs'
$compilerArgs += Join-Path $PSScriptRoot 'Maintenance.cs'
& $compilerPath @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$selfTestProcess = Start-Process -FilePath $candidatePath -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
if ($selfTestProcess.ExitCode -ne 0) { throw 'Offline tests failed; existing executable was not replaced.' }
Move-Item -LiteralPath (Join-Path $taskRoot 'self-test-results.txt') -Destination (Join-Path $PSScriptRoot 'self-test-results.txt') -Force
if (Test-Path -LiteralPath $finalPath) {
    [System.IO.File]::Replace($candidatePath, $finalPath, [NullString]::Value)
} else {
    [System.IO.File]::Move($candidatePath, $finalPath)
}
Write-Output $finalPath
