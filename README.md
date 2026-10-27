<Original Read Me>
# Kronos CLI

Kronos CLI is an OGFN launcher made by helix.67. It supports Fortnite builds from 1.7.2 through the latest version.

Starting with Fortnite 30.10, the game added Easy Anti-Cheat (EAC) protection, and launching `FortniteClient-Win64-Shipping.exe` directly may no longer work as expected. Recent builds include the EAC-enabled `FortniteClient-Win64-Shipping_EAC_EOS.exe`; Kronos uses it when you launch a client with the proxy, so the game starts through EAC while its supported Epic traffic is redirected to your backend. The built-in proxy is configured for the game process and does not change Windows' system proxy settings or require a redirect DLL.

EAC restricts some launch options: only one EAC game instance can run at a time, and a proxy client cannot use a client DLL or a server dll because EAC doesn't let you for it to be injected. If you launch without the proxy, Kronos uses `FortniteClient-Win64-Shipping.exe` and can inject a configured redirect DLL instead. This can be useful for older builds or setups that rely on DLL-based redirection; compatibility depends on the Fortnite build and your backend.

## Features

- **Version checker** - reads your build's version so the proxy is only offered on 30.10 and newer; older builds always launch without it.
- **Built-in redirect proxy** - only touches Fortnite's traffic, rewriting supported Epic domains to your backend while leaving game content CDNs and EAC's own servers alone. No system-wide proxy and no redirect DLL needed on modern builds.
- **Client, server, or both** - launch an OGFN client, a dedicated gameserver, or both from one menu.
- **Live dashboard** - a black-and-grey terminal UI with scrollable panes for the launcher/game logs and the proxy, status badges (build, version, backend, proxy), and a command bar. It only shows the useful lines; the noisy ones (full command lines, splash backups, internal steps) go to `launcher.log` only. It falls back to plain text output when piped or run with `--mode`.
- **Single exe** - published as one self-contained `Kronos CLI.exe`; no extra `.dll` or `.json` files and no .NET install needed to run it.
- **DLL injection** - for no-proxy launches, injects configured client/server lobby DLLs as soon as the game reaches the lobby (it watches the game log for the Frontend load).
- **Saved settings** - everything you change in Options is written to a config file, so you don't have to set it again next launch.


## Getting started

1. Start your private backend.
2. Run Kronos CLI. On first launch, open **Options** and set **Build** to your Fortnite build directory (or the game's executable), and set the **Backend** URL.
3. Configure the login and any redirect or lobby DLLs your setup requires.
4. Choose **Run Client**, **Run Server**, or **Run Both**. The launcher reports whether the backend and proxy are reachable.

The default build path is `C:\Program Files\Epic Games\Fortnite`, the backend is `http://127.0.0.1:3551`, and the proxy listens on `127.0.0.1:8888`.

If no Fortnite exe is found at the build path, Kronos still opens and tells you, so you can set the right one in **Options > Build**.

## Version detection

Kronos reads the build version from:

```
<Build>\Cloud\cloudcontent.json
```

using its `BuildVersion` field (for example `++Fortnite+Release-33.30-CL-39768313` is shown as `33.30-CL-39768313`). The version is shown at startup and in the dashboard header, and its number (`33.30`) decides how clients launch:

| Build version | Run Client | Proxy option |
|---------------|------------|--------------|
| 30.10 and newer | Asks "Launch with proxy?" | Shown |
| Older than 30.10 | Always without the proxy (redirect DLL), no question | Hidden, and the proxy isn't started |
| Unknown (file missing or unreadable) | Asks "Launch with proxy?" | Shown |

On 30.10 and newer, **Run Server** and **Run Both** also ask, but they can't actually use the proxy: answering yes explains why (EAC blocks DLL injection and only allows one instance), and answering no launches them normally. With a patched exe, a gameserver and a client work without the proxy.

## Menu

```
1  Run Client        Launch a client (asks about the proxy on 30.10+)
2  Run Server        Launch a dedicated gameserver (never uses the proxy)
3  Run Both          Launch a client and a server together (client without the proxy)
4  Kill Everything   Stop every launched process and clear the log panes
5  Options           Open the settings menu
6  Exit              Quit the launcher
```

Type `?` to reprint the menu.

## Options

Open **5 Options** to change these. Each one is saved as soon as you change it, and an example is shown while you type:

- **Build** - path to your Fortnite build (the build root, its `Win64` folder, or the game exe)
- **DLLs** - redirect DLL, client lobby DLL, and server lobby DLL (enter `-` to clear a slot)
- **Login** - username and password your backend accepts
- **Backend** - the backend URL Epic traffic is redirected to
- **Proxy** - the `ip:port` the built-in proxy listens on (hidden for builds older than 30.10)

## Tested

I've tested 33.30, 37.51, 41.30, 42.30, and 42.40 and they all work fine, but if you want to run server with the client (with a patched exe and your own or open sourced gameserver) launching it with proxy will not work. Since this launcher has been mostly tested on higher versions we don't know if it works fine on lower versions which I need you guys to do if you can. the only things that need to be tested are just Server, Client, Redirect dll's get injected into the Fortnite process fine. Make an issue if you have any problems on versions below 30.10.

## Requirements

- Windows 10 or newer, 64-bit
- A Fortnite build installed or extracted on this PC
- A compatible private backend running and reachable at the configured backend URL
- For proxy launches (30.10 and newer): a trusted Fiddler root certificate with its private key in the current Windows user's certificate store (`CurrentUser\My`). The proxy uses this certificate to handle HTTPS traffic; if it is missing or untrusted, HTTPS redirection will not work.

## Issues

- Open an issue if you have a problem with the launcher or find a bug.
