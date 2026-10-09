<#
.SYNOPSIS
  Registers the agent as an hourly scheduled task for the current user.

.DESCRIPTION
  The resident agent already runs its cycle every hour on its own, so this is
  OPTIONAL. It covers two cases:

    * making sure it starts again even if the panelist closes it with "Exit";
    * meeting "runs every hour" in an auditable way, with history in Task
      Scheduler.

  The task is registered for the CURRENT USER with RunLevel Limited. It neither
  asks for nor uses administrator rights. If the agent is already running, the
  new instance exits silently (single-instance mutex).

  It is an alternative to the Run key, not a complement: using both does not
  break anything, it just duplicates start attempts.

.PARAMETER ExePath
  Path to PersonicoreDeviceAgent.exe, already copied to its final location.

.PARAMETER Remove
  Removes the task instead of creating it.

.NOTES
  Windows blocks downloaded .ps1 files by default. Run it like this:
    powershell -ExecutionPolicy Bypass -File .\install-scheduled-task.ps1 -ExePath ...

.EXAMPLE
  .\install-scheduled-task.ps1 -ExePath "$env:LOCALAPPDATA\Personicore\PersonicoreDeviceAgent.exe"
  .\install-scheduled-task.ps1 -Remove
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$taskName = 'Personicore Device Agent'

if ($Remove) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Task '$taskName' removed."
    return
}

if (-not $ExePath) { throw "Missing -ExePath." }
if (-not (Test-Path $ExePath)) { throw "Not found: $ExePath" }

$ExePath = (Resolve-Path $ExePath).Path

# --autostart: if the agent is already running, the new instance exits silently
# instead of showing the "already running" message every hour.
$action = New-ScheduledTaskAction -Execute $ExePath -Argument '--autostart' `
    -WorkingDirectory (Split-Path -Parent $ExePath)

# At logon and then every hour, indefinitely. The agent lives in the tray: outside
# an interactive session it would have nowhere to draw and nobody to ask.
# Without -RepetitionDuration the repetition is indefinite. Do not use
# [TimeSpan]::MaxValue: Register-ScheduledTask rejects it on Windows 10/11.
$atLogon = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$hourly  = New-ScheduledTaskTrigger -Once -At (Get-Date) `
    -RepetitionInterval (New-TimeSpan -Hours 1)

# Limited = no elevation. The agent never needs administrator rights.
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType Interactive -RunLevel Limited

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask -TaskName $taskName -Force `
    -Action $action -Trigger @($atLogon, $hourly) `
    -Principal $principal -Settings $settings `
    -Description 'Registers this computer as a personal device of the Personicore household.' | Out-Null

Write-Host "Task '$taskName' registered for $env:USERNAME."
Write-Host "  Executable  $ExePath"
Write-Host "  Trigger     at logon, and every hour"
Write-Host "  Elevation   none (RunLevel Limited)"
