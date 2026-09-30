# CLAUDE.md — FanControl.AXB35 (developer notes)

Public repo. **Never commit personal or network details:** no hostnames, IP or MAC addresses, user names or
local paths. Internal deployment notes live in a separate private project.

## What this is

- A Fan Control plugin (`src/FanControl.Axb35`, net48 + net8.0-windows) for the Bosgame M5 / Sixunited AXB35-02 EC.
- `axb35ctl` (net48), a CLI that shares the EC code (`src/Axb35.Ec`, compiled into both via `<Compile Include>`).

## Facts that cost time to find

- **Prefer the BIOS WMI interface (since 1.1.0).** `root\wmi:PowerSwitchInterface`, GUID
  `99d89064-8d50-42bb-bea9-155b2e5d0fcd`, instance `ACPI\PNP0C14\IP3POWERSWITCH_0`, AML `\_SB.WMIB.WMAA`. Its AML
  touches the EC RAM through the memory window `OperationRegion ERAX, SystemMemory, 0xFEC40400` and never through
  the port handshake. Methods: 3 SetFanControl([fan 1|2, duty 0..101]) writes `0x80|duty` (0 = ok, 0xFF = refused);
  4 GetFanControl([1]) returns RPM with byte0 = 0x36, byte1 = 0x35, byte2 = 0x38, byte3 = 0x37; 11 GetHwTemp([1])
  returns byte0 = 0x70. There is **no release (0x00) and no fan 3**, so those stay on the ports. Called through
  advapi32 `WmiOpenBlock`/`WmiExecuteMethodW` (~0.07 ms per call); note PowerShell's `[Wmi]` accelerator shadows a
  class named `Wmi` in `Add-Type` tests.
- **Why: ACPI event 13.** Windows' EC driver answers the EC's events on 0x62/0x66 without any shareable lock. On the
  M5 the EC raises `_Q49` every ~10.15 s (seen with an ETW trace of `Microsoft-Windows-Kernel-Acpi`, AmlMethodTrace)
  and the driver's QR_EC query takes ~1 ms (status 20 2A 28 29 09). 1.0.0's 16-24 ms port burst every ~1 s caught about
  2 % of them. That meant ~3.3 event-13 timeouts an hour on the test unit, against 0 in the five days before. Event 15
  ("returned data when none was requested") is logged 5 times per boot when the plugin starts, then suppressed.
- **The DSDT has no `EmbeddedControl` OperationRegion.** All the BIOS's ACPI code reads the EC through the memory
  window, so collisions on the ports can only cost Windows an EC *event* (lost query), never an ACPI field read.
- **The EC's resting status is 0x00 / 0x08 (CMD)**; burst mode (0x10) and SMI_EVT (0x40) never showed in 12 M
  samples. `axb35ctl sniff` is the tool for this.
- **Fan 3 is real:** 0 rpm when cool, ~700-800 rpm under load. Do not hide it when it reads 0.
- **EC access:** PawnIO + the embedded signed `LpcACPIEC.bin`, ports 0x62/0x66 only, mutex `Global\Access_EC`.
  - Poll **without sleeping or yielding, at Highest thread priority.** Windows' own EC driver services the
    output-buffer-full interrupt and takes the answer byte if our thread is off-CPU.
  - Measured on the Bosgame M5: `Thread.Sleep(1)` failed intermittently, and `Thread.Yield()` failed on every read
    once all 32 CPU threads were busy. The tight spin at Highest priority never failed.
- **Register map (M5, BIOS 3.11):**
  - `0x33`/`0x34` fan 1/2 duty: `0x80|pct` holds the fan, `0x00` is firmware.
  - `0x35-0x38` RPM, high byte first; can tear, so read high, low, high.
  - `0x28-0x29` fan 3 RPM; 8000 means stopping, report 0.
  - `0x70` temperature. `0x31` power mode, displayed only and never written.
  - The cmetz 0x21/0x23/0x25 fan-mode registers read 0x00 on this firmware and are **not** used.
- **Fan Control identifiers** are `<plugin Name>/<sensor Id>`: `PluginSensor.Identifier` in
  FanControl.Library. `PairedFanSensorId` is used verbatim, so it must be the full identifier. The plugin `Name`
  is the constant `AXB35 EC`, and **renaming it orphans users' saved curves.**
- Release (`0x00`) returns a fan to the firmware curve on the M5. Measured: it decays back to firmware speed.

## Build and release

```powershell
dotnet build -c Release
powershell -ExecutionPolicy Bypass -File build-release.ps1   # zip + SHA256SUMS in dist/
```

## Session log

### 2026-09-30 — 1.1.0: BIOS WMI transport; port transactions give way
The test unit's System log showed ACPI event 13 about 3 times an hour from the day 1.0.0 went in, and none before.
The timestamps clustered at multiples of the EC's ~10.15 s event period. The fix: move temperature, fan 1-2 RPM
and duty to the BIOS's own WMI method, which reads and writes the memory-mapped EC RAM (see the facts above).
Ports remain only for the open-time checks, fan 3 every 30 s and release. Port transactions now require an idle EC
(200 µs quiet, no SCI_EVT), give way if an event appears between bytes, and no longer drain another user's byte.
A failed release now holds the fan at 80 % (MaxDuty). Measured after the swap: a 60 s sniff showed only Windows'
event queries plus one fan-3 read every 30 s. ACPI event 13 went from 3.25/h in the previous 24 h to 0 in the
first 3 h after the swap. The plugin logged 73 port transactions in its first 10 min, against ~8,400 for 1.0.0.
Merged as #1 (fast-forward, main `e43850e`) and released as **v1.1.0**, tagged at `e43850e`. Zip
`FanControl.AXB35-1.1.0.zip` SHA-256 `944b7706…d06d0ed` (GitHub's asset digest matches), net48 DLL `ec1ea792…`,
net8.0-windows DLL `d6699a10…`. The test unit runs that release DLL, with 0 event 13 after 4.3 h.

### 2026-09-27 — v1.0.0, first public release
Written for, and measured on, a Bosgame M5, alongside the Hardware Busters article. The high-priority spin fix
came from a full CPU+GPU load test, during which every EC read failed before it.
