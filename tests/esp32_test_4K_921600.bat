@echo off
..\BK7231Flasher\bin\Release\OpenIOT_flasher.exe --chip ESP32 -b 921600 test --addr 0x10000 --size 0x1000
pause
