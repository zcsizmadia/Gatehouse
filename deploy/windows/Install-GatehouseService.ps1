<#
.SYNOPSIS
    Installs Gatehouse as a Windows Service.

.DESCRIPTION
    Registers the Gatehouse binary as a Windows Service using the built-in service
    control manager. No wrapper, no NSSM: the binary references
    Microsoft.Extensions.Hosting.WindowsServices, so it speaks the service control
    protocol itself and reports Running only once it is genuinely ready.

    By default the service runs as the low-privilege 'NT SERVICE\gatehouse' virtual
    account, which the SCM creates on registration. A virtual account has no
    password to rotate or leak, and gets its own SID for file ACLs — which is what
    lets the SQLite database be readable by the service and nothing else.

.PARAMETER BinaryPath
    Path to gatehouse.exe. Defaults to gatehouse.exe beside this script.

.PARAMETER ConfigPath
    Path to the JSON configuration file the service should load.

.PARAMETER DataDirectory
    Directory for the SQLite store. Created and ACLed to the service account.

.PARAMETER ServiceName
    Service name. Defaults to 'Gatehouse'.

.PARAMETER ListenUrl
    The address Kestrel binds. Defaults to loopback: terminate TLS in front of the
    gateway rather than exposing it directly.

.EXAMPLE
    .\Install-GatehouseService.ps1 -ConfigPath C:\ProgramData\Gatehouse\gatehouse.json

.NOTES
    Must be run elevated. Uninstall with Uninstall-GatehouseService.ps1.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $BinaryPath = (Join-Path $PSScriptRoot 'gatehouse.exe'),

    [Parameter(Mandatory)]
    [string] $ConfigPath,

    [string] $DataDirectory = 'C:\ProgramData\Gatehouse',

    [string] $ServiceName = 'Gatehouse',

    [string] $ListenUrl = 'http://127.0.0.1:8080'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --- preconditions ------------------------------------------------------------

$identity = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must be run from an elevated PowerShell session.'
}

if (-not (Test-Path -LiteralPath $BinaryPath)) {
    throw "Gatehouse binary not found at '$BinaryPath'. Pass -BinaryPath explicitly."
}

if (-not (Test-Path -LiteralPath $ConfigPath)) {
    throw "Configuration file not found at '$ConfigPath'."
}

# Resolve to absolute paths. A service is started by the SCM with an unrelated
# working directory, so a relative path here would fail only at first start.
$BinaryPath   = (Resolve-Path -LiteralPath $BinaryPath).Path
$ConfigPath   = (Resolve-Path -LiteralPath $ConfigPath).Path

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "A service named '$ServiceName' already exists. Remove it first with Uninstall-GatehouseService.ps1."
}

# --- data directory -----------------------------------------------------------

if (-not (Test-Path -LiteralPath $DataDirectory)) {
    New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
}

$DataDirectory = (Resolve-Path -LiteralPath $DataDirectory).Path

# --- registration -------------------------------------------------------------

$serviceAccount = "NT SERVICE\$ServiceName"

# The SSPI quoting rules here are unforgiving: the binary path and each argument
# must be quoted individually, or a path containing a space silently becomes two
# arguments and the service fails to start with a message that names neither.
$binaryPathName = '"{0}" --config "{1}" --urls "{2}"' -f $BinaryPath, $ConfigPath, $ListenUrl

if ($PSCmdlet.ShouldProcess($ServiceName, 'Register Windows Service')) {

    # sc.exe rather than New-Service: New-Service cannot create the virtual
    # account, and running as LocalSystem to avoid the problem would give a
    # credential-bearing process far more privilege than it needs.
    & sc.exe create $ServiceName `
        binPath= $binaryPathName `
        DisplayName= 'Gatehouse AI control plane' `
        start= auto `
        obj= $serviceAccount | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe create failed with exit code $LASTEXITCODE."
    }

    & sc.exe description $ServiceName 'Routes, governs and meters LLM traffic. https://github.com/zcsizmadia/Gatehouse' | Out-Null

    # Restart on failure with a backoff, and reset the counter daily so a single
    # bad night does not leave the service permanently in a failed state.
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

    # --- ACLs -----------------------------------------------------------------
    # The virtual account SID exists only after registration, which is why this
    # comes last. The data directory holds the request log; nobody else needs it.
    $acl = Get-Acl -LiteralPath $DataDirectory
    $acl.SetAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $serviceAccount,
        'Modify',
        'ContainerInherit,ObjectInherit',
        'None',
        'Allow'))
    Set-Acl -LiteralPath $DataDirectory -AclObject $acl

    # Read-only on the configuration file: the service must never rewrite its own
    # config, and the Phase 2 admin UI writes it through a separate, audited path.
    $configAcl = Get-Acl -LiteralPath $ConfigPath
    $configAcl.SetAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $serviceAccount,
        'Read',
        'Allow'))
    Set-Acl -LiteralPath $ConfigPath -AclObject $configAcl

    Write-Host "Registered service '$ServiceName' running as '$serviceAccount'." -ForegroundColor Green
    Write-Host ''
    Write-Host 'Provider credentials should not live in the config file. Set them as' -ForegroundColor Yellow
    Write-Host 'machine environment variables and reference them with' -ForegroundColor Yellow
    Write-Host 'ApiKeyEnvironmentVariable, for example:' -ForegroundColor Yellow
    Write-Host '    [Environment]::SetEnvironmentVariable("OPENAI_API_KEY", "...", "Machine")' -ForegroundColor Yellow
    Write-Host ''
    Write-Host "Start it with:  Start-Service -Name $ServiceName"
    Write-Host "Check health:   Invoke-RestMethod $ListenUrl/health/ready"
}
