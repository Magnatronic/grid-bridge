# Bridge app

C# tray app for the AAC device. The real app isn't built yet. `TestApp/` is the phase 0 test app.

## Phase 0 test app

It logs hotkeys, clipboard changes, live keys and a writing-area check to `%LOCALAPPDATA%\GridBridgeTest\log.csv`. It installs nothing else and doesn't need .NET on the device.

Build it from this folder:

`dotnet publish TestApp -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o TestApp/bin/publish`

To run the test:

1. Copy `GridBridgeTest.exe` to the Grid Pad and run it. Right-click the tray icon and tick **Start at login**.
2. Make Grid 3 cells that send Ctrl+Alt+F1 to F4, plus a cell that copies the chat writing area.
3. Live mode: a cell sending Ctrl+Alt+F5 turns it on and off. While it's on, every key is logged, so use test text only. It turns off after 10 minutes. The last 3 keys logged are the off command.
4. Writing-area check: type text starting with `GBTEST` in the writing area, then send Ctrl+Alt+F6.
5. Text that doesn't start with `GBTEST` is logged by length only. Reboot and test, then fill in `docs/hardware-tests/phase-0.md`.
