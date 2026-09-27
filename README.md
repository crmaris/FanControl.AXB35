# FanControl.AXB35

A [Fan Control](https://github.com/Rem0o/FanControl.Releases) plugin that controls the fans of the **Bosgame M5 /
BeyondMax** mini PC (AMD Ryzen AI Max+ 395 "Strix Halo", Sixunited **AXB35-02** board).

Out of the box Fan Control finds no fans on this machine. They are driven by an ITE IT5570E embedded controller (EC),
which LibreHardwareMonitor, and therefore Fan Control, does not support. The stock firmware keeps the fans slow under
sustained load, so the chip sits in the high 80s and 90s °C. This plugin gives Fan Control the EC's temperature
sensor, the RPM of all three fans, and full 0-100 % control of fans 1 and 2. You can then run any curve you like.

It talks to the EC through **PawnIO**, the signed driver Fan Control itself installs. **Secure Boot and Memory
Integrity (HVCI) stay on**, and there is no WinRing0 and no test-signing.

Background and measurements: *Hardware Busters article (link added on publication).*

## Compatibility

| | |
|---|---|
| Tested | Bosgame M5 / BeyondMax, board `AXB35-02`, BIOS 3.11, Windows 11 25H2, Performance mode |
| Probably | Other Sixunited AXB35 machines (GMKtec EVO-X2, FEVM FA-EX9 ...). **Untested.** See the safety checks below |
| Fan Control | V238 or newer (earlier versions do not ship PawnIO). Tested on V281, both the .NET 10 and .NET Framework 4.8 builds |

The plugin **refuses to write to the EC** unless all of these hold:
- Windows reports an `AXB35` baseboard;
- both fan duty registers hold values this layout allows;
- the temperature register reads a plausible value.

On any other machine it loads nothing. On an AXB35 variant with a different layout it reports why in Fan Control's
log and stays read-only.

## Install

1. **Install Fan Control V238 or newer** from its [releases page](https://github.com/Rem0o/FanControl.Releases/releases),
   using the installer or the zip. Start it once and let it install **PawnIO** when it asks. This step is required;
   the plugin cannot reach the EC without it.
2. **Download the plugin** zip from this repository's [Releases](../../releases) and pick the DLL that matches your
   Fan Control build:
   - if your Fan Control folder contains **`FanControl.runtimeconfig.json`**, you run the .NET build: use
     `net8.0-windows\FanControl.Axb35.dll`;
   - if it contains **`FanControl.exe.config`** instead, you run the .NET Framework 4.8 build: use
     `net48\FanControl.Axb35.dll`.
3. **Close Fan Control completely** (tray icon, then Exit).
4. **Copy the DLL** into the `Plugins` folder inside the Fan Control folder, e.g.
   `C:\Program Files (x86)\FanControl\Plugins\` for the installer, or the `Plugins` folder of the zip.
5. **Unblock it.** Right-click the DLL, choose Properties, tick **Unblock**, then OK. Or run this in PowerShell:
   ```powershell
   Unblock-File "C:\Program Files (x86)\FanControl\Plugins\FanControl.Axb35.dll"
   ```
6. **Start Fan Control.** The new cards are:
   - **Controls:** `AXB35 Fan 1`, `AXB35 Fan 2`
   - **Speeds:** `AXB35 Fan 1`, `AXB35 Fan 2`, `AXB35 Fan 3`
   - **Temperatures:** `AXB35 EC temperature`

   If they are missing, open Fan Control's `log.txt`. The plugin writes one line saying why it did not load.

## Set up a fan curve

1. In **Curves**, click **+** and choose **Graph**. Set its temperature source to **AXB35 EC temperature**. That
   register tracks the die within about 1 °C under load, and it is the sensor the firmware's own curve uses.
2. Enter points. This is the curve used for the article, aggressive enough to keep a sustained full load well below
   the throttle point:

   | °C | 40 | 55 | 65 | 72 | 78 | 83 |
   |---|---|---|---|---|---|---|
   | % | 35 | 50 | 65 | 80 | 90 | 100 |

   For a quieter desk machine, lower the first points (for example 45 °C → 30 %). Keep the top end: the point is to
   ramp *before* the high 80s.
3. In **Controls**, assign that curve to **AXB35 Fan 1** and **AXB35 Fan 2** and enable both cards.
4. Fan 3 (the small one) has no known control register. It is shown for monitoring only and stays on firmware
   control.

Duty is continuous from 0 to 100 %. At 100 % both main fans reach about 4,400-4,500 rpm; the stock firmware held
them at about 2,350-2,600 rpm with the chip at 72-78 °C.

**Know the limit.** In Performance mode, an all-core CPU load takes the chip to 97-98 °C in seconds whatever the
fans do. At that point the fans on the test unit lost speed, falling to roughly 1,400-2,600 rpm and swinging,
even while the plugin held them at 100 % and the EC's duty register read 100 %. The stock firmware does the same.
The plugin cannot override it. It helps most below the limit, which covers GPU-heavy work such as LLM inference.

## Run it without logging in (optional)

Fan Control V281+ can run as a Windows service at boot, with no user session. That suits a headless or server box.
The plugin loads inside the service the same way, with the curve saved in Fan Control's configuration.

## Check it from the command line: `axb35ctl`

The release also contains `axb35ctl.exe`, a small tool that shares the plugin's code. Run it from an **elevated**
prompt; PawnIO must be installed.

```text
axb35ctl status                  temperature, power-mode byte, fans 1-3 (RPM, duty)
axb35ctl watch 30                a status line every second for 30 s
axb35ctl set 1 100               set fan 1 to 100 % once (fans 1, 2 or all; 0-100; the EC may take it back)
axb35ctl hold all 100 60         hold fans 1-2 at 100 % for 60 s, re-writing every 2 s, then release
axb35ctl release all             hand fans 1 and 2 back to the firmware
axb35ctl log 600 temps.csv       one CSV line a second: EC and GPU temperature, RPM and duty
axb35ctl dump                    read-only dump of the EC's 256 registers
```

Do not run `set` or `release` while Fan Control is controlling the fans; the two will fight.

## How it works

The Bosgame M5 EC is reached through the standard ACPI EC ports (0x62 data, 0x66 command). The plugin loads PawnIO's
signed `LpcACPIEC` module, which allows exactly those two ports, and speaks the ACPI EC read/write protocol from user
mode. It takes the shared `Global\Access_EC` mutex that LibreHardwareMonitor and HWiNFO use.

| EC register | Meaning |
|---|---|
| `0x33` / `0x34` | Fan 1 / fan 2 duty. Write `0x80 \| duty` (0-100) to hold the fan; `0x00` returns it to the firmware |
| `0x35-0x36`, `0x37-0x38` | Fan 1 / fan 2 RPM, high byte first |
| `0x28-0x29` | Fan 3 RPM |
| `0x70` | Temperature, °C |
| `0x31` | Power-mode byte, displayed only and never written |

Details that matter:
- **Polling.** Windows' own EC driver shares these ports and services the EC's answer-ready interrupt, so a reader
  that is off-CPU at that moment loses the byte. The plugin polls at Highest thread priority, without sleeping or
  yielding, and retries every transaction up to five times. With `Thread.Yield()` every read failed once all 32 CPU
  threads were busy; the spin has not failed since.
- **Re-assert.** The EC quietly takes a held fan back after a while, without changing the duty register. The plugin
  re-writes the duty every 2 s, as the Linux tool does.
- **Verified writes.** Every write is read back.
- **Release.** When Fan Control stops or releases a fan, the plugin writes `0x00` and the fan goes back to the
  firmware's own curve. This was measured: after 100 % the fans decayed back to their firmware speed.

## Uninstall

Close Fan Control, delete `FanControl.Axb35.dll` from `Plugins`, and start Fan Control again. The fans are already back
on firmware control, because the plugin releases them on exit.

## Safety

This writes to your machine's embedded controller. The plugin only writes the two fan duty registers, only after
the checks above pass, and verifies every write. The realistic failure is a fan left at a fixed duty until you
release it or reboot. **No warranty. Use at your own risk.**

Never set a low duty on a loaded machine. The chip protects itself by throttling, but that is exactly what this plugin
is meant to avoid.

## Build from source

```powershell
dotnet build -c Release
```

Needs the .NET 8+ SDK. It produces `src/FanControl.Axb35/bin/Release/{net48,net8.0-windows}/FanControl.Axb35.dll`
and `src/axb35ctl/bin/Release/net48/axb35ctl.exe`.

## Credits

- **nathanmarlor/strix-halo-fan-control:** the Bosgame M5 register map (from the DSDT) and the first proof that the
  stock curve is the problem.
- **cmetz/ec-su_axb35-linux** and **deseven/ec-su_axb35-win:** earlier AXB35 EC work.
- **namazso/PawnIO:** the signed driver and the `LpcACPIEC` module.
- **Rem0o/FanControl:** the application and its plugin interface.

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Licensed under the [MIT License](LICENSE).
