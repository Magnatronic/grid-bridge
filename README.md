# Grid Bridge (working name)

One app and one dongle on an AAC device: independent access to the user's own PC or Xbox (Grid Bridge), and later a voice in shared class activities on the board (Roost).

Spec: [Grid Bridge and Roost Spec](https://claude.ai/artifact/FeVuEiPemRL3xESZMPto8o)

| Folder | What it holds | State |
|---|---|---|
| `firmware/dongle/` | One dongle firmware, with the role saved as a setting | `GameDongle` copied in, unchanged |
| `bridge-app/` | C# tray app for the AAC device | Empty |
| `web/` | Setup page, later Roost, on its own GitHub Pages URL | Empty |
| `docs/` | Phase pass criteria, hardware test results, later the protocol | Started |

**First:** the phase 0 test app in `bridge-app/`. It checks that the app can be installed on a real Grid Pad, starts at login, and reliably catches Grid 3's hotkeys and clipboard.

Related: [ChatterSwitch](https://github.com/Magnatronic/talking-switch), where the XAC and wireless work was done (`MOUSE-PLAN.md`, steps 6 and 7).
