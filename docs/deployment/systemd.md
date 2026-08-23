# Deploying Gatehouse as a systemd unit

Gatehouse ships as a self-contained NativeAOT executable. There is no runtime to
install, no virtualenv, and no wrapper process — the binary speaks the systemd notify
protocol itself, so systemd knows when it is genuinely ready rather than merely
running.

It is installed to `/opt/gatehouse` rather than `/usr/local/bin`, because the
executable is accompanied by the SQLite native library it loads from its own
directory. Keeping the pair together is the whole reason for the directory.

## Install

```bash
# 1. The executable and its native SQLite companion, kept together.
#
#    Extract the whole archive rather than picking out the executable. NativeAOT
#    does not statically link the SQLite native library, so gatehouse loads
#    libe_sqlite3.so from its own directory at first use. Installing only the
#    executable gives you a service that starts, reports healthy, and then fails
#    every request with DllNotFoundException.
sudo mkdir -p /opt/gatehouse
sudo tar -xzf gatehouse-linux-x64.tar.gz -C /opt/gatehouse
sudo chmod 0755 /opt/gatehouse/gatehouse

# 2. Configuration
sudo mkdir -p /etc/gatehouse
sudo install -m 0640 gatehouse.json /etc/gatehouse/gatehouse.json

# 3. The unit
sudo install -m 0644 gatehouse.service /etc/systemd/system/gatehouse.service

# 4. The service account and its state directory.
#
#    A static system user rather than systemd's DynamicUser. The `gatehouse keys`
#    command writes the same SQLite database the service reads, and a transient
#    uid gives an administrator no way to run it as the owning identity — the key
#    would land in a root-owned file the service then cannot write.
sudo useradd --system --no-create-home --shell /usr/sbin/nologin gatehouse || true
sudo install -d -o gatehouse -g gatehouse -m 0700 /var/lib/gatehouse

# 5. Issue a key, as the service user so the database ends up owned correctly.
#    Authentication is required by default and the unit will fail to start without
#    a key, rather than start and reject every request.
sudo -u gatehouse /opt/gatehouse/gatehouse keys create \
    --name my-app --org acme --team platform \
    --config /etc/gatehouse/gatehouse.json

sudo systemctl daemon-reload
sudo systemctl enable --now gatehouse
```

The secret is printed once. Only its hash is stored, so it cannot be recovered — if
it is lost, revoke the key and issue another.

Verify:

```bash
systemctl status gatehouse
curl -fsS http://127.0.0.1:8080/health/ready && echo
journalctl -u gatehouse -f
```

## Credentials

**Do not put API keys in `gatehouse.json`.** A key in a config file is a key in your
configuration management, your backups, and often your git history. Gatehouse logs a
warning at startup if it finds one.

Use an environment file that only root can read. The unit already references it:

```bash
sudo install -m 0600 -o root -g root /dev/null /etc/gatehouse/gatehouse.env
sudo tee /etc/gatehouse/gatehouse.env >/dev/null <<'EOF'
OPENAI_API_KEY=sk-...
EOF
sudo systemctl restart gatehouse
```

And reference it from the config:

```json
"Providers": {
  "openai": {
    "Kind": "openai-compatible",
    "BaseUrl": "https://api.openai.com/v1",
    "ApiKeyEnvironmentVariable": "OPENAI_API_KEY"
  }
}
```

On Azure, prefer managed identity — it needs no stored credential at all. That
lands with the Azure OpenAI provider in Phase 1.

## What the unit does for you

The shipped [`gatehouse.service`](../../deploy/systemd/gatehouse.service) is
hardened by default. The parts worth understanding:

**`Type=notify`.** Gatehouse tells systemd when it has validated its configuration
and opened its listener. A dependent unit ordered `After=gatehouse.service` can
therefore rely on it. With `Type=simple` you would get a unit that is "active" while
still rejecting requests.

**A static `gatehouse` user, not `DynamicUser=yes`.** A transient uid would be the
stronger hardening choice, and it was the original one. It loses to a practical
concern: `gatehouse keys create` writes the same SQLite database the service reads,
and with a transient identity there is nothing for an administrator to run that
command as. The key would land in a root-owned file the service then cannot write,
and the symptom would look like a corrupt database rather than a permissions
mistake. `StateDirectory=gatehouse` still creates `/var/lib/gatehouse` at mode 0700
owned by that user.

**Sandboxing.** `ProtectSystem=strict`, `MemoryDenyWriteExecute`, an empty
`CapabilityBoundingSet`, and `RestrictAddressFamilies=AF_INET AF_INET6`. A process
that holds provider credentials and sits in the request path is worth constraining
to exactly what it needs, and Gatehouse needs remarkably little: two socket families
and one writable directory.

**`LimitNOFILE=65535`.** A gateway holds one upstream connection per in-flight
stream. The default of 1024 is reached earlier than people expect under concurrent
streaming.

Check the sandbox with:

```bash
systemd-analyze security gatehouse.service
```

## Listening address

The unit binds `127.0.0.1:8080` deliberately. Terminate TLS in front of the gateway
— nginx, HAProxy, Envoy — rather than exposing it directly.

If you do terminate at nginx, `proxy_buffering off;` is required on the location
that proxies Gatehouse. nginx buffers proxied responses by default, which collects
each streamed completion and delivers it in one burst: the gateway streams
correctly, the user sees nothing for twenty seconds, and every test still passes.
Gatehouse sends `X-Accel-Buffering: no` to switch it off, which nginx honours, but
setting it explicitly costs nothing and survives a config refactor.

To bind elsewhere, override rather than editing the shipped unit:

```bash
sudo systemctl edit gatehouse
```

```ini
[Service]
Environment=ASPNETCORE_URLS=http://10.0.0.5:8080
```

## Upgrading

```bash
sudo systemctl stop gatehouse
sudo tar -xzf gatehouse-linux-x64.tar.gz -C /opt/gatehouse
sudo systemctl start gatehouse
```

Schema migrations run at startup, inside `StartAsync`, before the unit reports
ready. An unwritable or unmigratable database therefore fails the restart instead
of producing a gateway that serves traffic without recording it. In a
least-privilege or air-gapped deployment, set `Store.AutoMigrate` to `false` and
apply migrations out of band.

Stopping is graceful: in-flight streams are allowed to finish and queued request-log
records are flushed before the process exits.

## Uninstall

```bash
sudo systemctl disable --now gatehouse
sudo rm /etc/systemd/system/gatehouse.service
sudo systemctl daemon-reload
sudo rm -rf /opt/gatehouse
```

`/var/lib/gatehouse` is left alone on purpose: it holds usage and audit history.
Remove it deliberately, not as a side effect.
