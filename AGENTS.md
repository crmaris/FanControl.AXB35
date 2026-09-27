# AGENTS.md — FanControl.AXB35

Read **`CLAUDE.md`** first; it is the canonical developer notes for this public repo.

1. Public repo: never commit hostnames, IP or MAC addresses, user names or local paths.
2. EC transactions spin at Highest thread priority with no Sleep and no Yield, or Windows' EC driver steals the
   answer byte under load.
3. The plugin `Name` (`AXB35 EC`) and the sensor Ids are part of users' saved Fan Control configs. Never rename
   them.
4. Only EC registers 0x33 and 0x34 are ever written, and only after the board and layout checks pass.
