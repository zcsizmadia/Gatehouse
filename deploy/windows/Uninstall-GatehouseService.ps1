<#
.SYNOPSIS
    Removes the Gatehouse Windows Service.

.DESCRIPTION
    Stops and deregisters the service. The data directory is left in place: it
    holds the request log, which is usage and audit history, and deleting that as
    a side effect of an uninstall would be the wrong default. Remove it yourself
    when you are sure.

.PARAMETER ServiceName
    Service name. Defaults to 'Gatehouse'.

.EXAMPLE
    .\Uninstall-GatehouseService.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $ServiceName = 'Gatehouse'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$identity = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must be run from an elevated PowerShell session.'
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "No service named '$ServiceName' is registered. Nothing to do."
    return
}

if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop and remove Windows Service')) {

    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force

        # Wait for a genuine stop rather than assuming one. An in-flight streamed
        # completion can hold the process open for a few seconds, and deleting a
        # service that is still stopping leaves it marked for deletion until reboot.
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }

    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe delete failed with exit code $LASTEXITCODE."
    }

    Write-Host "Removed service '$ServiceName'." -ForegroundColor Green
    Write-Host 'The data directory was left in place; it contains the request log.' -ForegroundColor Yellow
}
