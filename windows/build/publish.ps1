<#
.SYNOPSIS
  Publishes the Personicore device agent for Windows.

.DESCRIPTION
  By default it produces one self-contained .exe per architecture, in dist\win-x64
  and dist\win-arm64: the panelist's computer does not need .NET installed. With
  -FrameworkDependent the executable is small but requires .NET Desktop Runtime 9
  on the target machine.

.EXAMPLE
  .\publish.ps1
  .\publish.ps1 -FrameworkDependent
#>
[CmdletBinding()]
param(
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'

$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $here '..\src\Personicore.DeviceAgent\Personicore.DeviceAgent.csproj'
$output  = Join-Path $here '..\dist'

# The icon is regenerated if Python with Pillow is available. The generated resources
# are already in the repo, so without Python it publishes with the existing ones. On
# Windows "python3" is often the alias that opens the Microsoft Store: try "py" first.
$python = $null
foreach ($candidate in @('py -3', 'python', 'python3')) {
    $exe, $rest = $candidate -split ' '
    $pyArgs = @($rest | Where-Object { $_ })
    $command = Get-Command $exe -ErrorAction SilentlyContinue
    if (-not $command -or $command.Source -like '*WindowsApps*') { continue }

    # In Windows PowerShell 5.1, redirecting a native executable's stderr with
    # ErrorActionPreference=Stop throws: relax it just for the probe.
    $ErrorActionPreference = 'Continue'
    & $exe @pyArgs -c 'import PIL' 2>&1 | Out-Null
    $found = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = 'Stop'

    if ($found) { $python = @{ Exe = $exe; Args = $pyArgs }; break }
}

if ($python) {
    $pyArgs = $python.Args
    & $python.Exe @pyArgs (Join-Path $here 'make-icon.py')
    if ($LASTEXITCODE -ne 0) { throw 'make-icon.py failed.' }
} else {
    Write-Warning 'No Python + Pillow: publishing with the icons already generated in Resources\.'
}

# x64 for the vast majority; native arm64 for Windows on ARM (Snapdragon, Surface
# Pro X). Windows 10 on ARM does not emulate x64, so the x64 .exe does not start there.
foreach ($rid in @('win-x64', 'win-arm64')) {
    $out = Join-Path $output $rid

    if ($FrameworkDependent) {
        dotnet publish $project -c Release -r $rid -o $out -p:SelfContained=false -p:PublishSingleFile=true
    } else {
        dotnet publish $project -c Release -r $rid -o $out
    }
    # $ErrorActionPreference does not cover native executables: without this, a build
    # failure would carry on and leave the previous .exe in dist\.
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $rid failed (exit code $LASTEXITCODE)." }

    # Template and install guide travel next to the .exe: the agent looks for .env there first.
    Copy-Item (Join-Path $here '..\.env.example') (Join-Path $out '.env.example') -Force
    Copy-Item (Join-Path $here '..\INSTALL.txt') (Join-Path $out 'INSTALL.txt') -Force
    Remove-Item (Join-Path $out '*.pdb') -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "Published to: $output"
Get-ChildItem $output -Recurse -File | Format-Table FullName, @{ Name = 'MB'; Expression = { [math]::Round($_.Length / 1MB, 1) } }
