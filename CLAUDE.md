# CLAUDE.md — FanControl.AXB35 (developer notes)

Public repo. **Never commit personal or network details:** no hostnames, IP or MAC addresses, user names or
local paths. Internal deployment notes live in a separate private project.

## What this is

- A Fan Control plugin (`src/FanControl.Axb35`, net48 + net8.0-windows) for the Bosgame M5 / Sixunited AXB35-02 EC.
- `axb35ctl` (net48), a CLI that shares the EC code (`src/Axb35.Ec`, compiled into both via `<Compile Include>`).

## Facts that cost time to find

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

### 2026-09-27 — v1.0.0, first public release
Written for, and measured on, a Bosgame M5, alongside the Hardware Busters article. The high-priority spin fix
came from a full CPU+GPU load test, during which every EC read failed before it.
