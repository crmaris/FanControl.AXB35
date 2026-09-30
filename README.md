# FanControl.AXB35

A [Fan Control](https://github.com/Rem0o/FanControl.Releases) plugin that controls the fans of the **Bosgame M5 /
BeyondMax** mini PC (AMD Ryzen AI Max+ 395 "Strix Halo", Sixunited **AXB35-02** board).

Out of the box Fan Control finds no fans on this machine. They are driven by an ITE IT5570E embedded controller (EC),
which LibreHardwareMonitor, and therefore Fan Control, does not support. The stock firmware keeps the fans slow under
sustained load, so the chip sits in the high 80s and 90s °C. This plugin gives Fan Control the EC's temperature
sensor, the RPM of all three fans, and full 0-100 % control of fans 1 and 2. You can then run any curve you like.

It reads and holds the fans through the **BIOS's own WMI fan interface**, and uses **PawnIO**, the signed driver Fan
Control itself installs, for the few things that interface cannot do. **Secure Boot and Memory Integrity (HVCI) stay
on**, and there is no WinRing0 and no test-signing.

> **Upgrade from 1.0.0.** Version 1.0.0 talked to the EC only through its ports 0x62/0x66. Windows' own EC driver uses
> the same ports, so about three times an hour the two collided and Windows logged *ACPI event 13, "The embedded
> controller (EC) did not respond within the specified timeout period"* in the System log. 1.1.0 moves the regular
> traffic to the BIOS interface, which cannot collide. See [How it works](#how-it-works).

Background and measurements: [Bosgame M5 Fan Control: Broken as Shipped, So I Fixed It With a Free Plugin](https://hwbusters.com/systems/bosgame-m5-fan-control-broken-as-shipped-so-i-fixed-it-with-a-free-plugin/) (Hardware Busters).

## Compatibility

| | |
|---|---|
| Tested | Bosgame M5 / BeyondMax, board `AXB35-02`, BIOS 3.11, Windows 11 25H2, Performance mode |
| Probably | Other Sixunited AXB35 machines (GMKtec EVO-X2, FEVM FA-EX9 ...). **Untested.** See the safety checks below |
| Fan Control | V238 or newer (earlier versions do not ship PawnIO). Tested on V281, both the .NET 10 and .NET Framework 4.8 builds |
| BIOS WMI | The M5's BIOS 3.11 has it (`root\wmi:PowerSwitchInterface`). Without it the plugin falls back to the EC ports, and says so in the log |

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

Duty is continuous from 0 to 80 %. **The plugin caps the EC at 80 %; Fan Control's 81-100 % all mean 80 %.**
Measured on the test unit with the chip at 98 °C and all 32 threads loaded:

| Command | Fan 1 | Fan 2 |
|---|---|---|
| Stock firmware | ~3,000 rpm | ~3,180 rpm |
| 100 % (re-written every 2 s) | ~4,540 rpm | **~2,080 rpm, dipping to ~1,420** |
| **80 % (re-written every 2 s)** | **~4,000 rpm, steady** | **~4,010 rpm, steady** |

**At 100 % the board cannot keep both fans spinning once the chip is hot.** One of them, and it is not always the
same one, repeatedly collapses to 1,400-2,000 rpm, even though the EC's duty register still reads 100 %. At 80 %
both hold about 4,000 rpm. That is more combined airflow than 100 % gives, and far more than the stock firmware
delivers. When cool, 100 % reaches about 4,400-4,500 rpm per fan.

In Performance mode, an all-core CPU load still takes the chip into the high 90s °C whatever the fans do. The
plugin buys headroom and airflow; it cannot beat the chip's power limit.

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
axb35ctl sniff 60 ec.csv         read-only: watch the EC status port for 60 s and list who else talks to the EC
axb35ctl --ports status          any command, forced onto the EC ports even where the BIOS WMI interface exists
```

Do not run `set` or `release` while Fan Control is controlling the fans; the two will fight.

## How it works

There are two ways into this EC, and the plugin uses both:

1. **The BIOS's WMI fan interface** (preferred). The M5's BIOS has an ACPI-WMI device (`\_SB.WMIB`, WMI class
   `root\wmi:PowerSwitchInterface`, GUID `99D89064-8D50-42BB-BEA9-155B2E5D0FCD`) whose methods set a fan's duty, read
   both fan speeds and read the EC temperature. Its ACPI code reaches the EC's RAM through the EC's **memory window**
   (`0xFEC40400`, same offsets as the registers below), not through the port handshake. The plugin calls it through
   Windows' WMI data-block API (`WmiExecuteMethod`), well under a millisecond per call, with no WMI service or COM in
   between. It writes only what the BIOS's own method writes: `0x80 | duty` into the fan's duty register.
2. **The EC ports** 0x62 (data) and 0x66 (command/status), through PawnIO's signed `LpcACPIEC` module, which allows
   exactly those two ports. The plugin speaks the ACPI EC read/write protocol from user mode, under the shared
   `Global\Access_EC` mutex that LibreHardwareMonitor and HWiNFO use. With the WMI interface present, the ports are
   used only for:
   - the checks when the plugin opens, including that the WMI interface and the ports see the same temperature and
     fan speed;
   - fan 3's speed, every 30 s (the WMI interface has no reading for it);
   - handing a fan back to the firmware (`0x00`), which the WMI interface cannot do.

**Why not just the ports.** Windows' own ACPI EC driver uses the same two ports to answer the EC's events, and it does
not take `Global\Access_EC` or any other lock a program can share. On the M5 the EC raises an event every ~10.2 s
(it runs the BIOS method `_Q49`), and Windows answers it with a ~1 ms query on the ports. Version 1.0.0 did all its
reading and writing on the ports: a 16-24 ms burst of transactions every Fan Control cycle (~1 s). About 2 % of the
EC's events landed inside a burst. The two conversations then interleaved, and about half of those times Windows'
side gave up after a second and logged ACPI event 13. Measured on the test unit:

| | ACPI event 13 per hour |
|---|---|
| The five days before the plugin was installed | 0 |
| 1.0.0 (ports only), three days | 3.3 |
| 1.1.0 (BIOS WMI), first 3 h | 0 (about 10 expected at the 1.0.0 rate) |

The timestamps give it away: within each cluster, the gaps between those events are whole multiples of the EC's
~10.2 s event period. Nothing important was lost on the M5. None of the BIOS's ACPI code reads the EC through the
ports, and the periodic `_Q49` event only raises a vendor WMI event that nothing listens to. But the same collision
could swallow a rarer event: the power button (`_Q54`), the power-mode key (`_Q46`), or a thermal-table change (`_Q74`).

`axb35ctl sniff` shows all of this. It samples the status port read-only and lists who else is talking to the EC.

| EC register | Meaning |
|---|---|
| `0x33` / `0x34` | Fan 1 / fan 2 duty. Write `0x80 \| duty` (0-100) to hold the fan; `0x00` returns it to the firmware |
| `0x35-0x36`, `0x37-0x38` | Fan 1 / fan 2 RPM, high byte first |
| `0x28-0x29` | Fan 3 RPM |
| `0x70` | Temperature, °C |
| `0x31` | Power-mode byte, displayed only and never written |

Details that matter:
- **Port transactions give way.** A port transaction starts only when the status port shows no one else mid-transaction:
  both buffers empty, no event pending, and still so 200 µs later. If an event appears between two of its bytes, it
  stops and retries rather than finishing across Windows' query. A byte already waiting in the output buffer is left
  for its owner. 1.0.0 read it out of the way, which guaranteed the other side's timeout.
- **Polling.** Windows' own EC driver shares these ports and services the EC's answer-ready interrupt, so a reader
  that is off-CPU at that moment loses the byte. The plugin polls at Highest thread priority, without sleeping or
  yielding, and retries every transaction up to five times. With `Thread.Yield()` every read failed once all 32 CPU
  threads were busy; the spin has not failed since.
- **Re-assert.** The EC quietly takes a held fan back after a while, without changing the duty register. The plugin
  re-writes the duty every 2 s, as the Linux tool does.
- **Lock order.** Thread priority is raised *before* the shared EC mutex is taken, so a holder is never preempted
  mid-transaction on a saturated CPU while other EC users wait for it.
- **80 % cap.** See the table above.
- **Checked writes.** A port write is read back. A WMI write is acknowledged by the BIOS method, which refuses
  anything above 101 %; the WMI interface has no way to read the duty back.
- **Release.** When Fan Control stops or releases a fan, the plugin writes `0x00` over the ports and the fan goes back
  to the firmware's own curve. This was measured: after 100 % the fans decayed back to their firmware speed. If that
  write ever fails, the plugin holds the fan at 80 % instead, so an unattended fan fails loud rather than hot.

## Uninstall

Close Fan Control, delete `FanControl.Axb35.dll` from `Plugins`, and start Fan Control again. The fans are already back
on firmware control, because the plugin releases them on exit.

## Safety

This writes to your machine's embedded controller. The plugin only writes the two fan duty registers, only after
the checks above pass, and checks every write. The realistic failure is a fan left at a fixed duty until you
release it or reboot. **No warranty. Use at your own risk.**

Never set a low duty on a loaded machine. The chip protects itself by throttling, but that is exactly what this plugin
is meant to avoid.

## Build from source

```powershell
dotnet build -c Release
```

Needs the .NET 8+ SDK. It produces `src/FanControl.Axb35/bin/Release/{net48,net8.0-windows}/FanControl.Axb35.dll`
and `src/axb35ctl/bin/Release/net48/axb35ctl.exe`.

## Changelog

- **1.1.0**
  - Fans 1-2 and the temperature now go through the BIOS's WMI fan interface. That stops the ACPI event 13 timeouts
    1.0.0 caused in the System log: about 3 an hour on the test unit, and none in the first 3 hours after the update.
  - Port transactions now wait for an idle EC, give way to Windows' event queries, and no longer take someone else's
    byte.
  - Fan 3 is read every 30 s instead of every second while the WMI interface is in use.
  - If a release fails, the fan is held at 80 %.
  - New in `axb35ctl`: `sniff`, `--ports`, and a transport line in `status`. Fan Control's log gets a traffic summary
    10 minutes after start and then daily.
  - Sensor and control identifiers are unchanged, so saved curves keep working.
- **1.0.0** First release.

## Credits

- **nathanmarlor/strix-halo-fan-control:** the Bosgame M5 register map (from the DSDT) and the first proof that the
  stock curve is the problem.
- **cmetz/ec-su_axb35-linux** and **deseven/ec-su_axb35-win:** earlier AXB35 EC work.
- **namazso/PawnIO:** the signed driver and the `LpcACPIEC` module.
- **Rem0o/FanControl:** the application and its plugin interface.

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Licensed under the [MIT License](LICENSE).
