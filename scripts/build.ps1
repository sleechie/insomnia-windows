<#
.SYNOPSIS
  Builds a self-contained Insomnia.exe (no .NET install needed to run it) into dist\release.
  Needs the .NET 8 SDK. -LidSimulation builds the test variant that accepts
  `Insomnia.exe --simulate-lid closed|open`.
#>
param([switch]$LidSimulation)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
$out = if ($LidSimulation) { "$root\dist\sim" } else { "$root\dist\release" }
$extra = if ($LidSimulation) { @('-p:LidSimulation=true') } else { @() }
dotnet publish "$root\src\Insomnia\Insomnia.csproj" -c Release -o $out @extra
Write-Host "Built $out\Insomnia.exe"
