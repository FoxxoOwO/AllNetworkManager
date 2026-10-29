# AllNetworkManager

[![build](https://github.com/FoxxoOwO/AllNetworkManager/actions/workflows/build.yml/badge.svg)](https://github.com/FoxxoOwO/AllNetworkManager/actions/workflows/build.yml)

Unified network device manager for Windows. Goal: one app that covers what Bosch Configuration Manager,
Hanwha Wisenet Device Manager, Axis Device Manager, MikroTik WinBox and Microsens MsSwitchIpConfig do today.

Vendor reference binaries live next to the repository on the development machine (`ConfigManager/`,
`Wisenet Device Manager/`, `winbox/`, `microsens/`, `WiseDetector/`) and are used only as a protocol reference —
they are not committed and are not a runtime dependency. The app talks to devices over public APIs
(ONVIF / WS-Discovery / SSDP / MNDP / ISAPI / SUNAPI / VAPIX / RouterOS API) first, vendor SDKs only where
essential.

## Status

| Area | State |
|---|---|
| Discovery: WS-Discovery (ONVIF) | working |
| Discovery: SSDP/UPnP | working |
| Discovery: MNDP (MikroTik) | working |
| Generic ONVIF vendor layer (info, network, users, reboot, factory reset) | working |
| MikroTik vendor layer (RouterOS API 8728/8729: info, network, users, logs, backup snapshot, reboot, factory reset) | working, verified against a fake API server |
| Hanwha/Samsung Wisenet vendor layer (SUNAPI: info, network, users, logs, reboot, factory reset) | working, verified against a fake SUNAPI server |
| MICROSENS switch layer (proprietary UDP: discovery + IP configuration) | working, protocol reverse engineered and verified |
| Axis vendor layer (VAPIX: info, network, users, logs, firmware, backup, reboot, factory reset) | working, verified against a fake VAPIX server |
| Wake-on-LAN (vendor-independent magic packet, ports 9 + 7) | working |
| Per-device notes (editable, autosaved, survive restart and rescan) | working, verified |
| Per-device tags (chips with add/remove, searchable, in CSV export) | working, verified |
| IP + hostname in the device list and the detail view | working, verified |
| Bosch vendor layer (ONVIF + ISAPI + VideoSDK wrapper) | working, needs hardware testing |
| Inventory persistence (`%LOCALAPPDATA%\AllNetworkManager\devices.json`) | working |
| Credential store (DPAPI, per Windows user) | working |
| Background auto-rescan with merge | working |
| CSV inventory export | working |
| UI: network write, user add/delete, firmware upload, backup/restore, reboot, factory reset | wired to vendor layer |
| PTZ / live view / recording | ONVIF contract exists, no UI yet |

## Solution layout

```
AllNetworkManager.Core           domain model, IDiscoveryService, IVendorDeviceManager, IVendorRegistry, ONVIF contracts
AllNetworkManager.Discovery      WS-Discovery, SSDP, MNDP protocols + DiscoveryService orchestrator
AllNetworkManager.Vendors.Onvif  concrete ONVIF SOAP client + OnvifDeviceManager (works for any ONVIF device)
AllNetworkManager.Vendors.MikroTik RouterOsApiClient (binary API) + MikroTikDeviceManager
AllNetworkManager.Vendors.Wisenet SunapiClient (HTTP CGI) + WisenetDeviceManager
AllNetworkManager.Vendors.Microsens MicrosensFrame (UDP protocol codec) + discovery + device manager
AllNetworkManager.Vendors.Axis VapixClient (HTTP/Digest) + AxisDeviceManager
AllNetworkManager.Vendors.Bosch  BoschDeviceManager (vendor extras, ISAPI CGI), BoschVideoSdkWrapper
AllNetworkManager.App            WPF (net10.0-windows), MVVM via CommunityToolkit.Mvvm, DI via Generic Host
```

Dependency direction: `App -> Discovery -> Core`, `App -> Vendors.X -> Discovery/Core`.
`Vendors.Bosch -> Vendors.Onvif` only to reuse the SOAP client.

Vendor resolution (`VendorRegistry`): manufacturer name match → model list match → `CanManageAsync` probe →
generic ONVIF fallback. Register specific vendors before the ONVIF layer in `App.xaml.cs` (order matters).

## Build / run

```
dotnet build AllNetworkManager.slnx
dotnet run --project AllNetworkManager.App
```

`AllNetworkManager.App/appsettings.json` controls discovery (auto subnet detection, which protocols are
enabled, port-scan fallback) and the Bosch SDK path (`Bosch:SdkPath`, default points at
`ConfigManager/bin` so `Bosch.VideoSDK5.*.dll` can be loaded reflectively).

## Adding a vendor

1. New project `AllNetworkManager.Vendors.<Vendor>` referencing `Core` (+ `Discovery` if it needs its own
   discovery protocol).
2. Implement `IVendorDeviceManager` (and `IVendorCapabilities`), plus an `Add<Vendor>Support()` DI extension
   that registers the concrete manager and maps `IVendorDeviceManager`/`IVendorCapabilities` to the same
   singleton instance.
3. Register it in `App.xaml.cs` **before** `AddOnvifSupport()`; the UI picks vendors through
   `IVendorRegistry`, no UI changes needed.
4. Set capability flags honestly — the detail view hides the Users / Logy / Firmware / Konfigurace tabs based
   on them.

## Notes

- Bosch cameras: ONVIF SOAP on `/onvif/device_service`, ISAPI-ish CGI endpoints for hostname/backup/restore,
  RCP command IDs are documented in `ConfigManager/conf/rcp_cmd_list.txt` and `supported_devices.txt`
  lists the model families the vendor tool covers. Those CGI endpoints are derived from the vendor tool's
  config, **not yet confirmed against hardware**.
- Generic ONVIF cannot do firmware update or config backup (not standardized). `OnvifDeviceManager` throws
  `UnsupportedOperation` and reports the capability as false instead of pretending.
- MikroTik: `RouterOsApiClient` speaks the binary API — length-prefixed words, `!re`/`!done`/`!trap` sentences,
  plain login for RouterOS 6.43+ with MD5 challenge/response fallback for older versions. Backup is a JSON
  snapshot of config sections (documentation/diff), **not** a restorable `.backup` file, so
  `SupportsConfigRestore` is false and the UI hides the restore button. Firmware install over the API is not
  implemented (`/system/package/update` or a `.npk` upload through WinBox remains the way).
- `IVendorCapabilities.SupportsConfigRestore` exists because "backup works, restore does not" is a real state
  (MikroTik). Do not fold the two into one flag.
- Wisenet/SUNAPI endpoints are `<service>.cgi?msubmenu=<menu>&action=<view|set|add|remove|control>`, XML in,
  `statusCode == 0` means OK. Schema varies by firmware, so `Xml.FindValue` matches by local name against a
  list of candidate names and prefers leaf elements (a container named like its child otherwise returns the
  whole subtree's text). Auth is HTTP Digest; the handler negotiates it from `NetworkCredential`.
- Device notes are free text per device, stored in `devices.json` next to credentials. Edits are written after
  a 600 ms debounce and flushed when the window closes; `UpsertRange` never touches `Notes`, so a rescan cannot
  wipe them. A note icon appears in the device list and the text is the row tooltip.
- Hostnames come from whatever the protocol reports (`identity` on MikroTik, `hostname` on Wisenet/Axis,
  `microsens.hostname` on MICROSENS); Bosch and plain ONVIF do not expose one, so `HostnameResolver` falls back
  to reverse DNS with a 2 s timeout. Resolved names are stored in `devices.json` and a rescan never overwrites
  them with an empty value.

## Discovery protocols

Multicast discovery (WS-Discovery, SSDP, MNDP, MICROSENS) is complemented by vendor broadcast protocols, one
class each so a vendor answering with something unexpected cannot take down the rest of the scan:

| Protocol | Request | Reply | Parsed fields |
|---|---|---|---|
| Hikvision SADP | XML probe → 239.255.255.250:37020 | XML `ProbeMatch` | model, serial, MAC, firmware, HTTP port |
| Dahua DHIP | 32-byte DHIP header + JSON → 239.255.255.251:37810 | header + JSON `client.notifyDevInfo` | model, serial, MAC, IP, mask, gateway, channels |
| Dahua (legacy) | 32-byte `0xa3` probe → broadcast:5050 | `0xb3` three-section frame | model, serial, MAC, IP, mask, gateway |
| Vivotek | 5-byte header (magic `0x4a5d8f1c`) → broadcast:10000 | type/length/value records | model, long name, MAC, IP, HTTP port |
| Ubiquiti | 4-byte probe → 233.89.188.1 and broadcast:10001 | type/length/value records | model, brand, MAC, IP, firmware, hardware |
| Hanwha Techwin | 16-byte probe → broadcast:7701 | fixed-layout reply on 7711 | model, MAC, IP, mask, gateway, web URL |
| MSSQL Browser | `0x02` byte → broadcast:1434 | CSV `key;value;…` | instance name, server name, version |
| Microchip / GCE | `Discover GCE Devices` → broadcast:30303 | two text lines | name, MAC |
| 360Vision | `DISCOVER\n` → broadcast:3600 | `DISCOVER TYPE=… KEY='mac' ID=…` | type, MAC, serial |
| Sony | `0x02 ENQ:allinfo 0xff` → broadcast:2380 | `0xff`-separated `KEY:value` records | model, serial, MAC, IP, web port, firmware |
| Eaton UPS | `<SCAN_REQUEST/>` → broadcast:4679 | XML `UPS.PowerSummary.*` objects | product, model, serial, version |
| Eden Optima | `DETECT BOX` → broadcast:8088 | `BOX\|` + XML | serial, IP, mask, gateway, DNS, web port |
| Lantronix | `00 00 00 f6` → broadcast:30718 | 30-byte fixed layout | MAC, gateway, hardware, version |
| Advantech | 60-byte `MADA` probe → broadcast:5048 | header + product block | MAC, product name, hardware |
| Vstarcam | `44 48 01 01` → broadcast:8600 | 256-byte fixed layout | IP, mask, gateway, DNS, MAC, serial, name, firmware |
| NiceVision | `NICE` request with answer port → broadcast:2007 | 590-byte fixed layout | MAC, IP, mask, gateway, name, version |
| Axis mDNS | PTR query for `_axis-video` / `_axis-nvr` / `_axis-disc` → 224.0.0.251:5353 | PTR + SRV + TXT + A | model, serial, MAC, firmware, port, camera vs. recorder |
| Arecont mDNS | PTR query for `_arec._tcp` | PTR + SRV + TXT + A | model, serial, MAC, stream port |
| Google Cast mDNS | PTR query for `_googlecast._tcp` | PTR + SRV + TXT + A | friendly name, model (`md`), device id, version (`ve`), cast port |

The three mDNS protocols use a small DNS-SD engine (`Protocols/Mdns/`): a PTR query per service type, answers
joined through SRV/TXT/A records (with real name compression), and a second round for hosts whose address was
missing. Port 5353 is bound with `ReuseAddress` so the browse survives the Windows responder; if the bind fails
the query falls back to an ephemeral port with the unicast-response bit set. Only instances of the services a
protocol asked for are turned into devices — in a shared reply the other services' records are present too.

The port/address matrix and the wire formats come from the LGPL-3.0
[UniversalScanner](https://github.com/julienblitte/UniversalScanner) project, which documents the protocols it
reverse engineered. Only the protocol behaviour is reused — the implementation here is written against the
captured packets, and every parser is validated against the vendor's own `.selftest` captures (Hikvision,
Dahua v1/v2, Vivotek, Ubiquiti, Hanwha) plus a live UDP round trip.

## Scan targets

The device panel has a target selector above the list:

| Choice | What happens |
|--------|--------------|
| **Všechna rozhraní (auto)** | every IPv4 subnet the discovery service detects (the original behaviour) |
| **<card> · 192.168.0.0/24** | only that network card's subnet; the list is rebuilt with `↻` when a cable is plugged in |
| **Vlastní rozsah…** | a typed target: `192.168.0.10-192.168.0.60`, the shorthand `192.168.0.10-60`, a CIDR such as `10.0.0.0/24` or a single address |

`Projít každou adresu (důkladné)` adds an active TCP sweep on top of multicast discovery (ports 80, 443, 554,
8000, 8080, 8728, 8291), which is what finds devices that never answer SSDP/WS-Discovery. Addresses are probed
with bounded concurrency, so one dead host cannot stall the scan. Ranges are capped at 1024 addresses
(a /22) and validated before the scan starts — a typo like `10.0.0.0/8` is rejected with an explanation.

The auto-rescan timer repeats whatever target was scanned last.

## Build & CI

GitHub Actions (`.github/workflows/build.yml`) builds on every push to `main`, on every pull request and on
manual dispatch, and publishes a downloadable artifact:

| Artifact | Contents |
|----------|----------|
| `AllNetworkManager-win-x64` | framework-dependent build of the app (needs the .NET 10 Desktop Runtime) |

Pushing a tag like `v0.1.0` additionally creates a GitHub release with a self-contained, runnable zip:

```bash
git tag v0.1.0 && git push origin v0.1.0
```

Locally the same steps are:

```bash
dotnet restore AllNetworkManager.slnx
dotnet build   AllNetworkManager.slnx -c Release
dotnet publish AllNetworkManager.App/AllNetworkManager.App.csproj -c Release -r win-x64 --self-contained false -o publish
```

The vendor reference tools (`ConfigManager/`, `winbox/`, `WiseDetector/`, `Wisenet Device Manager/`, `microsens/`)
are deliberately not in the repository — they are proprietary binaries and only served as a protocol reference
while the vendor layers were written.

## Storage: SQLite inventory

The inventory lives in `%LOCALAPPDATA%\AllNetworkManager\inventory.db` (SQLite, WAL journal, schema v1),
not in JSON any more:

| Table | Contents |
|-------|----------|
| `Devices` | one row per device — address, port, vendor/model, MAC, firmwares, serial, hostname, discovery protocol, URLs, username, `ProtectedPassword` (DPAPI), notes, tags, `PropertiesJson`, first/last seen |

- Indexes on `Manufacturer`, `MacAddress` and `LastSeen`; `PRAGMA user_version` carries the schema version.
- Writes are applied immediately (no whole-file rewrite), the connection is serialized, and `Snapshot()` returns
  a copy that is safe to enumerate while a background rescan writes.
- An old `devices.json` is imported automatically on the first start and then renamed to
  `devices.json.imported` — credentials, notes, tags and timestamps are preserved.
- Passwords are still stored only as DPAPI blobs; the database file never contains plaintext.

## Bugs found in review (fixed)

- Repeated scans returned **zero devices**: the discovery dedupe dictionary lived across runs and was never
  cleared, so every scan after the first returned nothing and never raised `DeviceDiscovered`. It is now
  per-run.
- Refreshing a non-ONVIF device (MikroTik, MICROSENS) **failed**: the network tab was always read over ONVIF,
  which those devices do not answer. The read is now limited to ONVIF/Bosch and is best-effort.
- Searching by a **tag or a note matched nothing** until the search box was touched again — the filtered view
  was never refreshed when a tag, note or hostname changed.
- Auto-rescan could run **at the same time** as a manual scan, so both competed for the same UDP ports.
- Discovery events used a blocking `Dispatcher.Invoke` from worker threads (UI stall / shutdown crash);
  they now post asynchronously.
- The inventory file was written **in place**, so a crash mid-write truncated it. Writes now go to a temp file
  and are moved over the original; `Snapshot()` gives a copy safe to enumerate while a scan writes.
- A wrong IP/mask/gateway was sent to the device without any check; `NetworkConfigValidator` now rejects
  invalid addresses, non-contiguous masks, network/broadcast addresses, out-of-subnet gateways and bad DNS.
- `SunapiClient` hard-coded `http`, so Wisenet devices on 443 could not be managed.
- MikroTik reported an unreachable device as a login failure.
- Protocol scans ran one after another (818 ms for two 400 ms protocols); they now run in parallel (417 ms).
- Tags are normalized in `DeviceStore.NormalizeTags` (trim, drop blanks, case-insensitive dedupe, max 32 chars,
  max 20 per device) so the UI and the file agree. They survive rescans, are matched by the search box and are
  exported as a CSV column.
- WPF `StaticResource` must be defined **before** its first use in the same dictionary: a `ControlTemplate`
  placed below the style that references it only explodes when the template is first instantiated, i.e. when a
  device dialog opens — not at startup.
- MICROSENS has no public API for IP configuration: `MicrosensFrame` was reconstructed from
  `microsens/MsSwitchIpConfig.jar` (decompiled with CFR). Frame 0x04 F2 D7 5C = legacy 92-byte layout on
  UDP 8341 without auth; 0x59/0x79 sections on UDP 1025 with a trailing MD5 (frame version < 4) or SHA-512
  (>= 4) checksum and a credential section = digest of `mac_random_<hash>`, where `<hash>` is MD5-crypt with
  the fixed salt `MSG60` (< v4, v4 sends the password raw) or SHA-512-crypt with the 40-byte salt from the
  announce header (>= v5). Discovery is passive: switches announce on 8340 (plus a BOOTP-style packet on 67);
  the vendor tool sends no query either. Setting an IP needs the original announce frame, which the discovery
  layer keeps in `Properties["microsens.frame"]` and `DeviceStore` persists.
- Axis/VAPIX: `basicdeviceinfo.cgi` (JSON) for identity, `param.cgi` for the MAC and exact firmware build,
  `param.cgi?action=update` for network (BootProto=none|DHCP), `usergroup.cgi` (JSON POST get/add/update/delete)
  for users, `admin/systemlog.cgi` for logs, `admin/restart.cgi`, `factorydefault.cgi`, `firmwareupgrade.cgi`
  (multipart, 10 min timeout). DNS is written in a separate request because one rejected parameter aborts the
  whole Axis update. Backup is a `param.cgi?action=list` dump (`axis-param-dump`), restore is not offered.
  HTTPS certificate validation is disabled — Axis ships self-signed certificates and devices are reached by IP.
  Model comes from `Brand.ProdFullName` (short) rather than `basicdeviceinfo`'s marketing name.
- **Untested on hardware**: the SUNAPI paths used here (`system.cgi` version/systeminfo/log/reboot/factorydefault,
  `network.cgi` ipconfig, `websetting.cgi` users) come from the published SUNAPI reference, not from a live
  camera. They are exercised only against a fake server. Firmware upload (`upgrade.cgi`) is implemented but
  `SupportsFirmwareUpdate` stays `false` until it is confirmed on a real device — flip the flag then.
- Device passwords are encrypted with DPAPI (CurrentUser) before hitting the JSON store; plaintext never
  reaches disk.
- Discovery protocols are multicast/broadcast based, so they do not need the subnet argument; the subnet
  list is only used by the optional port-scan fallback (capped to /24 per interface).
