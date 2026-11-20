param([switch]$AllowGameLoad)
$ErrorActionPreference='Stop'
$branch=Split-Path -Parent $PSScriptRoot
$root=Split-Path -Parent (Split-Path -Parent $branch)
$profile=Join-Path $root '5. Data\Fixtures\discord-companion-owner'
$bin=Join-Path $branch 'src\QuickClip\bin\Release\net10.0-windows'
$exe=Join-Path $bin 'QuickClip.exe'
$dll=Join-Path $bin 'QuickClip.dll'
$native=Join-Path $bin 'QuickClipCapture.dll'
$expectedExe='555B02B209F1E15A5B5D28861242634F5970CBC8C1471DC9763F3C507CCC4117'
$expectedDll='0972B73F27D407CB4BBE4F5EA809C9F4B3038BF7F6AE9039ECEA68AFCF84F3EA'
$expectedNative='B004FF7FF908C09D1B5B310DE8B13F48C5CF8AB463B6B2D7E28D02022788659F'
if((Get-Process -Name QuickClip -ErrorAction SilentlyContinue)) {
    throw 'Quit every other QuickClip instance normally before starting this isolated candidate.'
}
if(-not $AllowGameLoad -and (Get-Process -Name Marvel-Win64-Shipping,cs2 -ErrorAction SilentlyContinue)) {
    throw 'A game is running. For the initial Discord GPU acceptance, close the game first. Use -AllowGameLoad only for an intentional priority/performance comparison.'
}
foreach($record in @(@($exe,$expectedExe),@($dll,$expectedDll),@($native,$expectedNative))) {
    $path=$record[0]; $hash=$record[1]
    if(-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-FileHash -LiteralPath $path).Hash -ne $hash) {
        throw "Test candidate has changed or is missing: $path"
    }
}
$settings=Join-Path $profile 'settings\settings.json'
$clips=Join-Path $profile 'clips'
foreach($p in @($profile,(Join-Path $profile 'settings'),$clips)) {
    $item=Get-Item -LiteralPath $p -ErrorAction Stop
    if(-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Isolated data directory is missing or redirected.'
    }
}
$s=Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json
$actual=[IO.Path]::GetFullPath($s.ClipsFolder).TrimEnd('\')
$expected=[IO.Path]::GetFullPath($clips).TrimEnd('\')
if(-not [string]::Equals($actual,$expected,[StringComparison]::OrdinalIgnoreCase) -or
    @($s.AdditionalClipSources).Count -ne 0) {
    throw 'The Discord test profile must use its own empty clip root without additional media sources.'
}
$env:DOTNET_ROOT=Join-Path $root '4. Shared\SDK'
$env:DOTNET_ROOT_X64=$env:DOTNET_ROOT
$env:QUICKCLIP_PERSONAL_DATA_DIR=$profile
$env:QUICKCLIP_DEV_DIAGNOSTICS='1'
$env:QUICKCLIP_CI='0'
Write-Host 'Starting isolated Discord companion acceptance. Production/development remain unchanged.'
Write-Host "Clips: $clips"
Write-Host 'Discord recording starts disabled. Enable it explicitly under Settings > Companions.'
Start-Process -FilePath $exe -WorkingDirectory $bin
