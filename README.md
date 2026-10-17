<img width="1362" height="705" alt="image" src="https://github.com/user-attachments/assets/b3596685-3b73-493c-b4df-b6f21e5afb13" />


# OpenIOTFlasher

OpenIOTFlasher is a simple Windows application that allows you to back up and flash OpenBK/OpenBeken and related Open\* firmware projects to supported IoT chips without extensive programming knowledge. The tool originally focused on Beken BK7231T/BK7231N devices, but the current version supports a wider set of chip and platform modes.

This project is maintained as [OpenIOTFlasher](https://github.com/DeDaMrAzR/OpenIOTFlasher), a fork of the original [BK7231 GUI Flash Tool](https://github.com/openshwprojects/BK7231GUIFlashTool) and all credits belongs to the owner of that repo!!!

Supported GUI-selectable chip/platform modes:
- Beken UART:
  - BK7231M
  - BK7231N (T2, T34, BL2028N)
  - BK7231T
  - BK7231U
  - BK7236 (T3)
  - BK7238 (T1)
  - BK7239N
  - BK7252
  - BK7252N (T4)
  - BK7258 (T5)
- Beken SPI CH341
- Bouffalo Lab:
  - BL602
  - BL616/BL618
  - BL702
- Espressif:
  - ESP32
  - ESP32-C2/C3/C5/C6/C61/S2/S3
  - ESP8285/ESP8266
- ESWIN/Transa Semi:
  - ECR6600
  - TR6260
- GigaDevice:
  - GD32VW553
- Generic SPI CH341
- iComm Semi:
  - SV6x66 (backup and guarded full-image restore)
- Lightning Semi:
  - LN882H
  - LN8825
- RDA Micro:
  - RDA5981
- Realtek:
  - RTL8710B (AmebaZ)
  - RTL8720DN (AmebaD)
  - RTL87X0C (AmebaZ2)
  - RTL8721DA (AmebaDp)
  - RTL8720E (AmebaLite)
- WinnerMicro:
  - W600
  - W800/W803
- XRadio:
  - XR806
  - XR809
  - XR872 (XF16)

Furthermore, it can automatically create an original firmware backup before flashing, attempt Tuya GPIO/config extraction from backups, and read/write OBK configuration where the selected platform supports it.

Other built-in tools include:
- Tuya config extraction from a binary dump
- OBK config and flash-dump download from an OBK device on LAN
- LAN scanner and mass OBK CFG backup
- OTA tool for OBK devices
- BK7231N decryption/encryption helpers

❗ NOTE: The flash dump may contain your SSID and password if the device was paired at the time of the backup.

[See also Russian guide for this tool and BK7231N.](https://www.v-elite.ru/t34)

# [Youtube Tutorial for example usage - CB2S flashing](https://youtu.be/YQdR7r6lXRY?list=PLzbXEc2ebpH0CZDbczAXT94BuSGrd_GoM)
See also the secondary example: [WB3S flashing](https://youtu.be/-a5hV1y5aIU?list=PLzbXEc2ebpH0CZDbczAXT94BuSGrd_GoM).

Per device flashing guides (NOTE: they may use obsolete flash tools, so always prefer to use the new tool from this repo):
- [BK7231T/WB3S flashing guide - 2g Tuya wall switch - with SOIC8 chip desoldering - Home Assistant](https://www.youtube.com/watch?v=Yb3zXtBdSnE)
- [Tuya Relay CB2S/BK7231N control without Local Tuya - 100% free from cloud with Home Assistant guide](https://www.youtube.com/watch?v=PKkiqDNFIx8)
- [How to add IR receiver and extra buttons to any Tuya BK7231T/BK7231N LED strip controller, 100% DIY](https://www.youtube.com/watch?v=KU0tDwtjfjw)
- [RGBCW Tuya bulb flashing guide - BK7231N (WB2L_M1) - Tasmota/ESPHome multiplatform replacement](https://www.youtube.com/watch?v=2e1SUQNMrgY)
- [TreatLife Intertek teardown & programming tutorial - WB3S/BK7231 - 100% local Home Assistant control](https://www.youtube.com/watch?v=-a5hV1y5aIU)
- [Firmware change process for RGB+CCT Tuya ceiling lamp, OpenBeken, WiFi module desoldering, BK7231N](https://www.youtube.com/watch?v=YQdR7r6lXRY)

See also our [youtube channel](https://www.youtube.com/@elektrodacom) and [forum](https://www.elektroda.com/)

# Compiling and Running on Linux

It should be possible to compile and run this tool on Linux by using [Mono](https://www.mono-project.com/). Mono is an open-source implementation of the .NET Framework which is also sponsored by Microsoft.

Once it's installed, you can compile this software by executing `msbuild` on the project directory. To execute the program, you can simply execute the following command:

`mono BK7231Flasher/bin/debug/OpenIOT_flasher.exe`

Alternatively, you can use a prebuilt OpenIOTFlasher release. The release executable is `OpenIOTFlasher.exe`, packaged in `OpenIOTFlasher-v<version>.zip`.

# Brief usage instructions (BK72xx)

1. Connect a 3.3 V USB-to-UART converter to the selected Beken chip's flash-download UART; use the in-app guide for the exact TX/RX pins
2. Start flasher tool
3. Select the correct platform for your chip
4. Click "Download latest release" to get firmware binary
5. Click "Backup then Write FW"
6. Reset/repower Beken
7. Tool will do both read and flash in one go.
8. Done!

No command line and no strange arguments required.

# Detailed usage instructions (BK72xx)

1. Download and unpack executable from Releases tab on the right
2. Prepare flashing circuit for BK72xx

    - Get a USB to UART bridge with 3.3V voltage signals
    - Connect Bridge RX to the module's flash-download TX and Bridge TX to its flash-download RX; use the in-app guide for the exact pin names
    - If necessary, solder a wire to the CEN pad (if you want to RESET through shorting CEN to ground)
    - Power the device from either the bridge or an external power source. All Beken based modules require 3.3v.  
      ⚠️ **NEVER try hacking devices while connected to mains power!** 

3. Open our flasher
4. Select your COM port of USB to UART converter
5. Select proper platform
6. Click "Download latest release" to get the proper binary file, or place a matching firmware file manually in the `firmwares` directory
7. Wait for download to finish

![image](https://user-images.githubusercontent.com/85486843/210281125-a3e25ab2-3144-4e02-a30c-6e135ecefd24.png)

8. Click "Backup then Write FW".
9. When the log displays `Getting bus...`, reset or power-cycle the module, or briefly short CEN to ground.
10. Wait while the tool backs up the existing firmware and writes the selected firmware.
11. Restart the module and connect to its access point, named `Open<chip_name>_<partial_mac>`. Open `192.168.4.1` to access the configuration page.
12. The original firmware backup is saved in the `backups` directory.

# CRC Mismatch?
CRC/key checks are chip-type dependent. If you get a CRC mismatch, you are most likely selecting a wrong chip type or trying to use firmware intended for another platform.

# OBK Configuration via UART
See this tutorial:
https://www.elektroda.com/rtvforum/viewtopic.php?p=20733610#20733610

# Can't auto download firmware?
Firmware download will not work on systems without newer TLS version required by GitHub. You can always manually download release from here:
https://github.com/openshwprojects/OpenBK7231T_App
and put the matching file into the `firmwares` dir, then restart flasher.

# Other problems?
You can also try changing the baudrate for flashing. Remember - sometimes higher baud rate might work better than lower one!

If you still need help, you can ask on our forums: https://www.elektroda.com/
