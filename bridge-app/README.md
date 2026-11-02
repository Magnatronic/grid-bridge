# Bridge app

C# tray app for the AAC device. The real app isn't built yet. `TestApp/` is the phase 0 test app.

## Phase 0 test app

It catches Ctrl+Alt+F1 to F4 and clipboard changes, then logs each one to `%LOCALAPPDATA%\GridBridgeTest\log.csv`. It installs nothing else and doesn't need .NET on the device.

Build it from this folder:

`dotnet publish TestApp -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o TestApp/bin/publish`

To run the test:

1. Copy `GridBridgeTest.exe` to a folder on the Grid Pad and run it. A tray icon appears.
2. Right-click the icon and tick **Start at login**.
3. In Grid 3, make cells that send each hotkey, and a cell that copies the chat writing area.
4. For clipboard checks, start the text with `GBTEST`. Other text is logged by length only, so real conversations stay out of the log.
5. Reboot and test, then count the log rows and fill in `docs/hardware-tests/phase-0.md`.
