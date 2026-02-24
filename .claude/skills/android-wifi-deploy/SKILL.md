---
name: android-wifi-deploy
description: >
  WiFi ADB deployment playbook for PolyPilot on macOS. Use when: (1) Deploying to a physical
  Android device over WiFi, (2) Troubleshooting ADB connection issues, (3) Building APKs for
  wireless install, (4) Managing the TCP proxy workaround for macOS firewall, (5) Recovering
  from broken ADB connections. Covers: proxy setup, port discovery, Fast Deployment gotchas,
  pairing, and the complete deploy checklist.
---

# Android WiFi ADB Deployment (macOS)

Deploy PolyPilot to a physical Android device over WiFi from macOS. This documents the
required TCP proxy workaround and the mistakes to avoid.

## Why a Proxy Is Needed

macOS firewall blocks ADB daemon's outbound TLS connections to the phone's wireless debug
port — even when `adb` is in the firewall allow list (the forked daemon child doesn't inherit
permissions). Symptom: `adb connect <phone-ip>:<port>` hangs or gets "connection refused."

**Workaround**: A Python TCP proxy on localhost relays traffic to the phone. The firewall
allows localhost connections, and the proxy's outbound connection to the phone succeeds
because it's a direct Python socket (not the ADB daemon).

## The Proxy Script

Save as `/tmp/adb_proxy.py` (or any convenient location):

```python
import socket, threading, sys

LOCAL_PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 39838
PHONE_HOST = sys.argv[2] if len(sys.argv) > 2 else '192.168.50.247'
PHONE_PORT = int(sys.argv[3]) if len(sys.argv) > 3 else 45451

def relay(src, dst):
    try:
        while True:
            data = src.recv(65536)
            if not data:
                break
            dst.sendall(data)
    except:
        pass
    finally:
        src.close()
        dst.close()

def handle(client):
    try:
        remote = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        remote.connect((PHONE_HOST, PHONE_PORT))
        t1 = threading.Thread(target=relay, args=(client, remote), daemon=True)
        t2 = threading.Thread(target=relay, args=(remote, client), daemon=True)
        t1.start(); t2.start()
        t1.join(); t2.join()
    except Exception as e:
        print(f"Connection error: {e}", file=sys.stderr)
        client.close()

server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
server.bind(('127.0.0.1', LOCAL_PORT))
server.listen(5)
print(f"Proxy listening on 127.0.0.1:{LOCAL_PORT} -> {PHONE_HOST}:{PHONE_PORT}", flush=True)

while True:
    client, addr = server.accept()
    threading.Thread(target=handle, args=(client,), daemon=True).start()
```

Usage: `python3 /tmp/adb_proxy.py <local-port> <phone-ip> <phone-port>`

## Step-by-Step Deploy

### 1. Discover the phone's current wireless debug port

The port changes when the phone reconnects to WiFi, wakes from sleep, or wireless debugging
is toggled. **Always check before connecting.**

```bash
dns-sd -L "<device-mdns-name>" _adb-tls-connect._tcp local
# Example: dns-sd -L "adb-RFCX10FDJHM-ds0CyQ" _adb-tls-connect._tcp local
# Output includes: port=45451
# Cancel with Ctrl-C after the result appears
```

To find the device mDNS name if unknown:
```bash
dns-sd -B _adb-tls-connect._tcp local
```

### 2. Start the proxy (if not running)

```bash
# Check if proxy is already running
ps aux | grep "[a]db_proxy"

# If not running, start it with the current phone port
python3 /tmp/adb_proxy.py 39838 192.168.50.247 <phone-port> > /tmp/adb_proxy.log 2>&1 &
sleep 2
```

If the phone port has changed since the proxy was started, you must stop the old proxy and
start a new one with the updated port.

### 3. Connect ADB through the proxy

```bash
adb connect 127.0.0.1:39838
adb devices   # Should show: 127.0.0.1:39838  device
```

### 4. Build and install

```bash
# Full APK build (required for WiFi — Fast Deployment doesn't work reliably over wireless)
dotnet build -f net10.0-android -p:EmbedAssembliesIntoApk=true

# Install directly (adb install works through proxy)
adb -s 127.0.0.1:39838 install -r bin/Debug/net10.0-android/com.microsoft.PolyPilot-Signed.apk

# Launch
adb -s 127.0.0.1:39838 shell am start -n com.microsoft.PolyPilot/crc64ef8e1bf56c865459.MainActivity

# MauiDevFlow port forwarding (if using UI inspection)
adb -s 127.0.0.1:39838 reverse tcp:9223 tcp:9223
```

## Critical Invariants

| ❌ Never | ✅ Instead | Why |
|----------|-----------|-----|
| `adb kill-server` | `adb disconnect` | kill-server wipes TLS pairing keys AND kills the proxy's ADB connection. Requires re-pairing the phone. |
| Assume proxy is running | Check `ps aux \| grep adb_proxy` first | Proxy dies silently when ADB server restarts or phone disconnects. |
| Assume port is the same | Run `dns-sd -L` to check | Phone's wireless debug port changes on WiFi reconnect, sleep wake, or toggle. |
| `adb push` + `pm install` | `adb install -r` | `adb install` handles the transfer and install in one command. |
| Build without `EmbedAssembliesIntoApk` | Always pass `-p:EmbedAssembliesIntoApk=true` | Fast Deployment stores assemblies separately in `.__override__/`. The APK file stays stale (21MB vs 97MB). WiFi deploy via `adb install` only installs the APK — it won't update `.__override__/`. |
| Use `dotnet build -t:Install` over WiFi | Use `adb install -r` with full APK | `-t:Install` uses Fast Deployment which requires USB or a reliable multi-stream ADB connection. |

## Fast Deployment Explained

By default, Android Debug builds use **Fast Deployment**: assemblies are NOT embedded in the
APK. Instead, `dotnet build -t:Install` pushes them to `.__override__/` on the device via ADB.
This makes the APK small (~21MB) but means:

- The APK file on disk doesn't contain your latest code
- `adb install` installs the stale APK shell without updating assemblies
- The app runs OLD code even though the install "succeeded"

**For WiFi deploy, always use `-p:EmbedAssembliesIntoApk=true`** to force all assemblies into
the APK (~97MB). This ensures `adb install` deploys everything in one shot.

For USB-connected devices, `dotnet build -t:Install` is preferred (faster, uses Fast Deployment
correctly).

## Recovery Procedures

### Connection lost (proxy died or port changed)

1. Check proxy: `ps aux | grep "[a]db_proxy"`
2. If dead or port changed:
   ```bash
   dns-sd -L "<device-name>" _adb-tls-connect._tcp local   # Get current port
   # Update proxy script or restart with new port
   python3 /tmp/adb_proxy.py 39838 192.168.50.247 <new-port> > /tmp/adb_proxy.log 2>&1 &
   ```
3. Reconnect: `adb connect 127.0.0.1:39838`
4. Verify: `adb -s 127.0.0.1:39838 shell echo hello`

### After accidental `adb kill-server`

TLS pairing keys are wiped. Must re-pair:

1. On phone: **Settings → Developer Options → Wireless Debugging → Pair device with pairing code**
2. Note the pairing port and 6-digit code shown on screen
3. ```bash
   adb pair 192.168.50.247:<pairing-port> <pairing-code>
   ```
4. Then follow "Connection lost" steps above to restart proxy and connect

### Device shows "offline"

Usually means pairing keys are stale or proxy is targeting wrong port:

1. `adb disconnect 127.0.0.1:39838`
2. Restart proxy with freshly discovered port (step 1 above)
3. `adb connect 127.0.0.1:39838`
4. If still offline → re-pair (see above)

## Current Device Info

- **Phone**: Samsung Galaxy S24 (SM-S921U)
- **IP**: 192.168.50.247
- **mDNS name**: `adb-RFCX10FDJHM-ds0CyQ`
- **Package**: `com.microsoft.PolyPilot`
- **Activity**: `crc64ef8e1bf56c865459.MainActivity`
- **Proxy local port**: 39838
