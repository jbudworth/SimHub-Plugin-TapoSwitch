# Tapo/Kasa Smart Switch Plugin for SimHub

Controls TP-Link wifi smart plugs from SimHub's **Controls and Events**
screen, so you can turn a switch on/off/toggle from a button box, keyboard
shortcut, wheel button, or any SimHub event/trigger.

Supports **two device generations**, selectable per switch in the settings
screen:

- **Legacy** - older Kasa/TP-Link plugs (HS100, HS103, HS105, HS110, HS200,
  HS210, KP1xx/KP3xx, etc). Plain TCP on port 9999, lightly obfuscated
  (not really "encrypted"), **no login required**. This is the same protocol
  used by [OctoPrint-TPLinkSmartplug](https://github.com/jneilliii/OctoPrint-TPLinkSmartplug)
  and Home Assistant's legacy Kasa integration.
- **Klap** - Tapo plugs (P100/P105/P110/P115) and any Kasa device updated to
  KLAP firmware (2021+). Encrypted HTTP on port 80, authenticated with the
  Tapo/TP-Link account email and password. See "A note on TPAP" below for a
  known gap even here.

## What this does

- Talks directly to each switch over your LAN (no cloud round-trip once
  configured).
- Implements TP-Link's **KLAP** protocol - the encrypted local protocol used
  by Tapo plugs (P100/P105/P110/P115/etc.) and by Kasa devices updated since
  the 2023 firmware change. Older Tapo firmware that still uses the original
  AES/RSA "securePassthrough" handshake, and legacy Kasa devices that use the
  old unencrypted port-9999 protocol, are **not** covered - see "Limitations"
  below.
- Adds three actions per configured switch to Controls and Events: **Turn
  On**, **Turn Off**, **Toggle**.
- Exposes a read-only `TapoSwitch.<Name>.State` property (true = on) that you
  can use elsewhere in SimHub (dash, formulas, other plugins).
- Polls each switch every 15 seconds (configurable) to keep that state
  current even if the switch was changed from the Tapo app.

## Project layout

```
SimHub.Plugin.TapoSwitch.csproj      Project file
TapoPlugin.cs                 Main plugin: IPlugin / IDataPlugin / IWPFSettingsV2
Tapo/ISmartSwitchClient.cs     Shared interface both device clients implement
Tapo/LegacyKasaClient.cs       Plain TCP/9999 client for older Kasa/TP-Link plugs
Tapo/KlapCipher.cs             KLAP AES-CBC session (key/iv/sig derivation, encrypt/decrypt)
Tapo/TapoClient.cs             HTTP handshake + get_device_info / set_device_info calls (Klap)
Settings/TapoDeviceConfig.cs   One switch's saved config (name, IP, protocol, email, password)
Settings/TapoPluginSettings.cs Root settings object (list of switches)
Settings/SettingsControl.xaml* WPF settings screen (add/remove/test/save)
Settings/PasswordDialog.xaml*  Standalone password entry dialog (see note in SettingsControl.xaml.cs)
```

## Building

1. Install **Visual Studio 2022** (Community is fine) with the ".NET desktop
   development" workload, and make sure the **.NET Framework 4.8 targeting
   pack** is installed (Visual Studio Installer → Individual components).
2. Open `SimHub.Plugin.TapoSwitch.csproj`.
3. Open the `.csproj` file and fix the four `HintPath` entries under
   `SimHub.Plugins`, `GameReaderCommon`, `SimHub.Logging`, and `log4net` so
   they point at the DLLs inside your actual SimHub install folder (default
   `C:\Program Files (x86)\SimHub\`). Note that `SimHub.Logging.dll` is a
   separate assembly from `SimHub.Plugins.dll` on current SimHub versions -
   if your install doesn't have it as a standalone file, the logging class is
   probably still inside `SimHub.Plugins.dll` instead; drop the
   `SimHub.Logging` reference from the `.csproj` in that case, since
   `SimHub.Plugins` will already expose it. If your SimHub.Plugins.dll
   happens to be built against a different framework version than 4.8,
   change `<TargetFramework>` in the `.csproj` to match (the error on build
   will tell you exactly which version it wants).
4. Build. The post-build step copies `SimHub.Plugin.TapoSwitch.dll` into that same
   SimHub folder automatically (remove the `CopyToSimHub` target in the
   `.csproj` if you'd rather copy it yourself).
5. Restart SimHub. The plugin appears under Settings as **Tapo Smart Switch**.

### A note on how far this has been verified

I don't have a Windows box or a physical Tapo device in this environment, so
I could not compile this against the real SimHub SDK or test it against
actual hardware. Two parts of this code rest on different footing:

- **The KLAP crypto and HTTP handshake** (`KlapCipher.cs`, `TapoClient.cs`)
  is ported line-for-line from the reference implementation in the
  [python-kasa](https://github.com/python-kasa/python-kasa) project's
  `klaptransport.py`, which is well-tested against real devices by that
  community. I'm confident in this part.
- **The exact SimHub SDK call shapes** - `this.AddAction(...)`,
  `this.AttachDelegate(...)`, `this.ReadCommonSettings/SaveCommonSettings`,
  and the `IWPFSettingsV2` members - follow the long-standing, commonly
  documented pattern used across community SimHub plugins, but SimHub has
  changed small details of these APIs between versions, and I can't check
  your installed version's exact signatures from here. If the build fails on
  one of these calls, it's almost always a same-named method with a slightly
  different overload, check `SimHub.Plugins.dll` (via Visual Studio's
  "Go to Definition"/Object Browser) and adjust the call to match. The core
  device logic in `Tapo/` doesn't depend on any of this and won't need
  changes.

## Setting up a switch

1. Register the switch in the **Tapo app** as usual (this is unavoidable,
   it's how the device gets its wifi credentials and how you set the account
   password it will authenticate with).
2. Find the switch's LAN IP address (check your router's device list, or the
   Tapo app's device info screen). A static DHCP reservation is strongly
   recommended so the IP doesn't change.
3. In SimHub, go to **Settings → Tapo Smart Switch**, click **Add**,
   and fill in:
   - **Name** – anything descriptive (used to build action names)
   - **IP Address** – the switch's LAN IP
   - **Protocol** – **Legacy** for older Kasa/TP-Link plugs (try this first if
     unsure - it needs no credentials), or **Klap** for Tapo plugs/updated
     Kasa devices (fill in the Tapo account email/password too)
4. Click that row's **Test** button to confirm it authenticates.
5. Click **Save settings**.
6. Go to **Controls and Events → Custom actions**, find
   `TapoSwitch.<YourSwitchName>.TurnOn` / `.TurnOff` / `.Toggle`, and bind
   each to whatever input or event you want.

## A note on TPAP (Klap-protocol devices only)

Some Tapo firmware updates rolled out from late 2025 onward moved certain
devices to an undocumented, unsupported encryption scheme TP-Link calls
"TPAP." A device on TPAP won't respond on port 80 at all (nothing listens
there in a way any current open-source tool understands - python-kasa, Home
Assistant, and this plugin are all affected identically). If a Klap-protocol
device won't connect and a plain browser can't reach port 80 either:

1. In the Tapo app, go to **Me → Third-Party Services** (or **Me → Tapo
   Lab → Third-Party Compatibility** on some versions) and toggle it off,
   then back on. This reportedly reverts most devices to the older,
   supported KLAP encryption.
2. If that doesn't help, the device may be on a firmware version that
   ignores the toggle. There's currently no known fix short of a firmware
   change from TP-Link or the community reverse-engineering TPAP - see
   [python-kasa#1590](https://github.com/python-kasa/python-kasa/issues/1590).

This doesn't affect Legacy-protocol devices, which have no comparable
authentication layer to begin with.

## Limitations / things to be aware of

- **Local network only, IPv4.** No cloud fallback if the switch's IP changes
  or your PC isn't on the same LAN.
- **Simple plugs/switches only** (single relay). Power strips with individual
  child sockets (e.g. Kasa KP303/EP40) need per-socket addressing this plugin
  doesn't implement.
- **Pre-2023 Tapo firmware** uses the older AES/RSA handshake instead of
  KLAP; this plugin will fail to authenticate against those until the device
  firmware is updated (Tapo app → device → firmware update).
- Passwords are encrypted at rest with Windows DPAPI (tied to the Windows
  user account SimHub runs as) before being written to SimHub's plugin
  settings file, rather than stored in plain text. This protects against
  someone reading the settings file directly (e.g. copying it off the disk),
  but not against something running under the same Windows account, since
  DPAPI decrypts transparently for that account. Consider creating a
  dedicated TP-Link account for your devices rather than reusing your main
  one, if that's a concern.
- Polling interval is fixed at construction time from settings; changing
  `PollingIntervalSeconds` in the settings file requires a SimHub restart to
  take effect (it's not exposed in the UI yet, though easy to add if wanted).

## Extending

- To support power-strip child sockets, add a `ChildId` to
  `TapoDeviceConfig` and include `"device_id": "<childId>"` /  the
  `control_child` wrapper method in `TapoClient` requests (the SMART protocol
  wraps child-socket calls in an extra `control_child` envelope).
- To support energy-monitoring plugs (P110/P115/KP125M) exposing current
  power draw, add a `get_energy_usage` call in `TapoClient` and an
  `AttachDelegate` for the wattage in `TapoPlugin`.
