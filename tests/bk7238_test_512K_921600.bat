@echo off
..\BK7231Flasher\bin\Release\OpenIOT_flasher.exe --chip BK7238 -b 921600 test --addr 0x11000 --size 0x80000
pause
