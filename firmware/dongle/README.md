# Dongle firmware

`dongle.ino` is `GameDongle/GameDongle.ino` from [ChatterSwitch](https://github.com/Magnatronic/talking-switch) at commit `db10139`, unchanged apart from the file name. Its comments still use the old names.

It keeps accepting that sketch's wireless `'G'` packets, so a ChatterSwitch stick can be the motion sender with no new stick firmware.

Board and USB settings are in `sketch.yaml`, and the sketch's own header lists them for the Arduino IDE.
