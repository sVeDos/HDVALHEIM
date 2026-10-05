param(
    [string]$ValheimDir = "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
)
$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$pluginDir = Join-Path $ValheimDir "BepInEx\plugins\HDValheimUpdated"
$textureDir = Join-Path $pluginDir "Textures"

if (!(Test-Path (Join-Path $ValheimDir "BepInEx\core\BepInEx.dll"))) {
    throw "BepInEx не найден: $ValheimDir"
}

$env:VALHEIM_DIR = $ValheimDir
dotnet build $projectDir -c Release

$dll = Join-Path $projectDir "bin\Release\netstandard2.1\HDValheimUpdated.dll"
if (!(Test-Path $dll)) { throw "DLL не найдена после сборки: $dll" }

New-Item -ItemType Directory -Force -Path $textureDir | Out-Null
Copy-Item $dll (Join-Path $pluginDir "HDValheimUpdated.dll") -Force

Write-Host "Готово."
Write-Host "DLL: $pluginDir\HDValheimUpdated.dll"
Write-Host "Текстуры: $textureDir"
Write-Host "F8 = перезагрузить текстуры."
