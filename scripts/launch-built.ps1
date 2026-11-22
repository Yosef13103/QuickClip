[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$work = Split-Path -Parent $PSScriptRoot
$workspace = Split-Path -Parent $work
$exe = Join-Path $work 'bin\quickclip-personal\dev\QuickClip.exe'
$dataDirectory = Join-Path $workspace '5. Data\Development'
$vars = @('QUICKCLIP_PERSONAL_DATA_DIR', 'QUICKCLIP_CI', 'QUICKCLIP_TEST_CAPTURE', 'DOTNET_ROOT')
$previous = @{}
foreach ($name in $vars) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    if (!(Test-Path -LiteralPath $exe -PathType Leaf))
    {
        throw 'No compiled Development app found. Use the regular QuickClip - Development shortcut once to build it.'
    }
    $env:QUICKCLIP_PERSONAL_DATA_DIR = $dataDirectory
    $env:QUICKCLIP_CI = '0'
    $env:QUICKCLIP_TEST_CAPTURE = '0'
    $env:DOTNET_ROOT = Join-Path $workspace '4. Shared\SDK'
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -ErrorAction Stop | Out-Null
}
catch {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show($_.Exception.Message, 'QuickClip Development - Fast Launch',
        [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Error) | Out-Null
    exit 1
}
finally {
    foreach ($name in $vars) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
