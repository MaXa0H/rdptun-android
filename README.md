# RDP Tunnel for Windows

Windows x64 MVP for tunnelling IPv4 packets through a real Microsoft RDP Dynamic Virtual Channel named `rdptun`.

## Architecture

- `Rdptun.exe` — WinForms UI targeting **.NET Framework 4.7.2**.
- `Rdptun.Plugin.exe` — out-of-process COM LocalServer DVC plug-in for the Microsoft Remote Desktop client (`mstsc.exe`).
- `wintun.dll` — Wintun adapter used as the local layer-3 interface.
- Server side remains xrdp + `rdptund` with `rdptun0` (`10.77.0.1/24`) and NAT.

The client plug-in uses the Microsoft `IWTSPlugin` / `IWTSVirtualChannel*` DVC interfaces. The COM LocalServer pattern is based on Microsoft's official RDP DVC .NET Framework sample.

## Wire protocol

The RDP DVC payload is unchanged from the original MVP:

```
[2-byte big-endian packet length][raw IPv4 packet]
```

Client tunnel address: `10.77.0.2/24`  
Server tunnel address: `10.77.0.1/24`  
MTU: `1200`

## Requirements

- Windows 10/11 x64.
- .NET Framework 4.7.2 or newer installed.
- Run `Rdptun.exe` as Administrator. The manifest requests elevation automatically.
- Server reachable over RDP/TCP (default `3389`).
- `rdptund` running in the target xrdp session and listening for DVC `rdptun`.

## Usage

1. Download the `rdptun-windows-x64` artifact from GitHub Actions and extract it.
2. Run `Rdptun.exe`.
3. Enter server, port, username and password.
4. Click **Connect**.
5. The app registers `Rdptun.Plugin.exe` per-user, stores the RDP credential temporarily with `cmdkey`, and launches Microsoft's `mstsc.exe`.
6. When the server opens DVC `rdptun`, the app creates/configures Wintun, adds a host route that keeps the outer RDP connection on the physical interface, and adds the IPv4 default route through `10.77.0.1`.
7. Click **Disconnect** to remove routes/firewall rules, stop Wintun and close the RDP client.

The RDP window is intentionally visible in this MVP so authentication/certificate problems are easy to diagnose.

## IPv6

This MVP tunnels IPv4 only. While the tunnel is active it installs a temporary Windows Firewall outbound rule blocking IPv6 (`::/0`) to avoid IPv6 bypass. The rule is removed on normal disconnect.

## Build

Open `Rdptun.sln` in Visual Studio 2019/2022 with .NET desktop development installed, or run:

```powershell
msbuild Rdptun.sln /m /p:Configuration=Release /p:Platform=x64
```

GitHub Actions downloads the official Wintun 0.14.1 distribution, builds all projects, and packages:

```
Rdptun.exe
Rdptun.exe.config
Rdptun.Plugin.exe
Rdptun.Plugin.exe.config
Rdptun.Common.dll
wintun.dll
```

## Security notes

- The password is not written into this repository or an app settings file. For RDP launch, Windows `cmdkey.exe` is used and the credential is deleted on disconnect/process exit.
- `authentication level:i:0` is used in the generated RDP file for MVP testing. For production, use certificate validation/pinning appropriate for your deployment.
- Wintun is a third-party component downloaded from its official distribution during CI. Review its upstream licensing before redistribution.

## Third-party reference

DVC COM interop and LocalServer structure are adapted from Microsoft's `rdp-dvc-plugin-samples` project (MIT licensed):

https://github.com/microsoft/rdp-dvc-plugin-samples
