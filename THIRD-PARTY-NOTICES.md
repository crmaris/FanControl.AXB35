# Third-party notices

## PawnIO module `LpcACPIEC.bin`

`resources/LpcACPIEC.bin` is embedded, unmodified, in `FanControl.Axb35.dll` and `axb35ctl.exe`. It is the signed
`LpcACPIEC` module from PawnIO.Modules release 0.2.11 (SHA-256
`c38fd116e7aff4d1fdb0a494e296be0a6708e5a22fc72f14587442fb7f8f7906`).

- Copyright (C) 2023 namazso
- License: GNU Lesser General Public License v2.1 or later
- Source: https://github.com/namazso/PawnIO.Modules (`LpcACPIEC.p`)

The module lets the signed PawnIO driver (https://pawnio.eu/) read and write I/O ports 0x62 and 0x66, the standard
ACPI embedded-controller ports, and nothing else. You may replace the embedded copy with any build of the same
module from its source.

## FanControl plugin interface

`lib/net48/FanControl.Plugins.dll` and `lib/net10.0/FanControl.Plugins.dll` are the plugin-interface assemblies
shipped with Fan Control V281 by Rémi Mercier (https://github.com/Rem0o/FanControl.Releases). They are used only to
compile against, and are not included in the release packages. Fan Control supplies its own copy at runtime.

## Prior work this plugin relies on (facts, no code)

- EC register layout of the Bosgame M5 / Sixunited AXB35-02: https://github.com/nathanmarlor/strix-halo-fan-control
  (derived from the board's ACPI DSDT) and https://github.com/cmetz/ec-su_axb35-linux
- The ACPI EC access pattern over PawnIO follows LibreHardwareMonitor
  (https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
