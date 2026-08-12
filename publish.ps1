param(
    [string]$Configuration = "Release",
    [string]$HarmonyDll = "E:\steam\steamapps\common\Escape from Duckov\Duckov_Data\Mods\TotemStorageBag\0Harmony.dll"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "publish\TotemStorageBag"

# 1. Build
dotnet build (Join-Path $root "src\DuckovMod\DuckovMod\DuckovMod.csproj") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# 2. Assemble publish folder (full rebuild every time)
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null

Copy-Item -Path (Join-Path $root "assets\*") -Destination $out -Force
Copy-Item -LiteralPath (Join-Path $root "src\DuckovMod\DuckovMod\bin\$Configuration\netstandard2.1\TotemStorageBag.dll") -Destination $out -Force

# 3. Harmony runtime (third-party binary, not stored in repo; copy from local deploy dir)
if (Test-Path -LiteralPath $HarmonyDll) {
    Copy-Item -LiteralPath $HarmonyDll -Destination $out -Force
} else {
    Write-Warning "0Harmony.dll not found at $HarmonyDll - publish folder lacks Harmony runtime. Use -HarmonyDll to specify a source."
}

Write-Output "Publish folder ready: $out"
Get-ChildItem -LiteralPath $out -File | Select-Object Name, Length
