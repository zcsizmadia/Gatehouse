# Deploying Gatehouse as a Windows Service

Gatehouse ships as a single NativeAOT executable. It references
`Microsoft.Extensions.Hosting.WindowsServices`, so it speaks the service control
protocol directly — no NSSM, no wrapper, no scheduled task pretending to be a
service. It reports `Running` only once configuration has validated and the
listener is open.

This is the same binary that runs as a systemd unit and in a container. That is the
Phase 0 gate.

## Install

From an **elevated** PowerShell session, with `gatehouse.exe` and the install
script in the same directory:

```powershell
New-Item -ItemType Directory -Force C:\ProgramData\Gatehouse | Out-Null
Copy-Item .\gatehouse.json C:\ProgramData\Gatehouse\gatehouse.json

.\Install-GatehouseService.ps1 -ConfigPath C:\ProgramData\Gatehouse\gatehouse.json
Start-Service -Name Gatehouse
```

Verify:

```powershell
Get-Service -Name Gatehouse
Invoke-RestMethod http://127.0.0.1:8080/health/ready
Invoke-RestMethod http://127.0.0.1:8080/v1/models
```

Options:

| Parameter         | Default                        | Notes                                        |
| ----------------- | ------------------------------ | -------------------------------------------- |
| `-BinaryPath`     | `gatehouse.exe` beside script  | Resolved to an absolute path                 |
| `-ConfigPath`     | *(required)*                   | JSON configuration file                      |
| `-DataDirectory`  | `C:\ProgramData\Gatehouse`     | SQLite store; ACLed to the service account   |
| `-ServiceName`    | `Gatehouse`                    | Also names the virtual account               |
| `-ListenUrl`      | `http://127.0.0.1:8080`        | Loopback by default, deliberately            |

## The service account

The service runs as **`NT SERVICE\Gatehouse`**, a virtual account the service
control manager creates during registration. This is the right choice for something
holding provider credentials:

- No password exists, so there is nothing to rotate, store, or leak.
- It gets its own SID, so the data directory and config file can be ACLed to this
  service and nothing else — which the install script does.
- It is unprivileged. Running as `LocalSystem` to avoid thinking about this would
  give a credential-bearing process far more authority than it needs.

The install script grants `Modify` on the data directory and **`Read` only** on the
configuration file. The service must never rewrite its own config; the Phase 2 admin
UI writes it through a separate, audited path.

## Credentials

**Do not put API keys in the configuration file.** Set them as machine-scoped
environment variables and reference them by name:

```powershell
[Environment]::SetEnvironmentVariable('OPENAI_API_KEY', 'sk-...', 'Machine')
Restart-Service -Name Gatehouse
```

```json
"Providers": {
  "openai": {
    "Kind": "openai-compatible",
    "BaseUrl": "https://api.openai.com/v1",
    "ApiKeyEnvironmentVariable": "OPENAI_API_KEY"
  }
}
```

A machine environment variable is readable by any process running as an
administrator, which is a real limitation. For anything handling regulated
workloads, use Azure managed identity (Phase 1) so that no credential is stored at
all, or place the gateway on a host whose administrators are already trusted with
the provider account.

Gatehouse logs a warning at startup if it finds a literal `ApiKey` in the config.

## Logging

The service writes to stdout, which Windows does not capture on its own. The
practical options:

- **OTLP** — set `Gatehouse:Telemetry:OtlpEndpoint` and send traces, metrics and
  logs to a collector. This is the intended path.
- **A file** — redirect by adding `--` arguments in the service `binPath`, or
  configure a file logging provider.

An Event Log sink is not wired up: `CreateSlimBuilder` omits the Event Log provider,
and adding it back would pull in reflection that the NativeAOT build is specifically
built to avoid. If you need Event Log integration, please open an issue describing
the constraint — it is a reasonable ask, just not a Phase 0 one.

## Recovery

The install script configures restart-on-failure with a 5s / 15s / 60s backoff and a
daily counter reset, so a single bad night does not leave the service permanently
failed. Review with:

```powershell
sc.exe qfailure Gatehouse
```

## Upgrading

```powershell
Stop-Service -Name Gatehouse
Copy-Item .\gatehouse.exe 'C:\Program Files\Gatehouse\gatehouse.exe' -Force
Start-Service -Name Gatehouse
```

Schema migrations run before the service reports `Running`, so an unwritable
database fails the start rather than producing a service that runs without
recording anything.

Stopping is graceful: in-flight streams finish and queued request-log records are
flushed before the process exits. If a long generation is in progress, expect the
stop to take a few seconds.

## Uninstall

```powershell
.\Uninstall-GatehouseService.ps1
```

The data directory is left in place — it holds usage and audit history. Remove it
deliberately.

## Firewall

The default `-ListenUrl` is loopback, so nothing needs opening. Terminate TLS in
front of the gateway — IIS with ARR, YARP, or a hardware load balancer — rather than
exposing Kestrel directly. If you must expose it, add a rule scoped to the
application subnet rather than to `Any`:

```powershell
New-NetFirewallRule -DisplayName 'Gatehouse inference' `
  -Direction Inbound -Protocol TCP -LocalPort 8080 `
  -RemoteAddress 10.0.0.0/8 -Action Allow
```
