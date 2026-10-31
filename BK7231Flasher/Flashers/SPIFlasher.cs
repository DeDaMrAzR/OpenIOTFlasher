using System;
using System.Drawing;
using System.IO;
using System.Threading;

namespace BK7231Flasher
{
    public class SPIFlasher : BaseFlasher
    {
        protected CH341DEV hd;
        private bool ch341Opened;
		bool readFailed;



        public byte[] ReadJEDECID()
        {
            byte[] cmd = new byte[4] { 0x9F, 0x00, 0x00, 0x00 };
            byte[] resp = hd.Ch341SPI4Stream(cmd);
            return resp;
        }

        public int GetFlashSize(byte[] jedec)
        {
            if (jedec == null || jedec.Length < 4)
                return 0;
            int size = 1 << jedec[3]; // common formula: 2^N bytes
            return size;
        }

        int GetFlashMIDFromJEDEC(byte[] jedec)
        {
            if (jedec == null || jedec.Length < 4)
                return 0;
            return jedec[1] | (jedec[2] << 8) | (jedec[3] << 16);
        }
        
        public bool CheckFlashEmpty(uint address, int size)
        {
            byte[] d = ReadFlash(address, size , true);
            if (d == null)
                return false;
            for (int i = 0; i < size; i++)
            {
                if (d[i] != 0xff)
                    return false;
            }
            return true;
        }
        public bool CheckFlash(uint address, int size, byte[] data) {
            byte[] d = ReadFlash(address, size);
            for(int i = 0; i < data.Length; i++)
            {
                if (d[i] != data[i])
                    return false;
            }
            return true;
        }
        public byte[] ReadFlash(uint address, int size, bool bSilent = false)
        {
            const int pageSize = 256;
            byte[] result = new byte[size];
            byte[] cmd = new byte[4 + pageSize];
			int lastPercent = -1;
            if (bSilent == false)
            {
				addLogLine($"Read range: 0x{address:X6}-0x{address + size - 1:X6} ({size} bytes).");

                logger.setProgress(0, size);
                logger.setState("Reading", Color.White);
            }
            for (uint addr = address; addr < address + size; addr += pageSize)
            {;
                int offset = (int)(addr - address);
                if (bSilent == false)
                {
                    logger.setProgress(offset, size);
                }
                int readSize = Math.Min(pageSize, size - offset);

                cmd[0] = 0x03; // Read Data
                cmd[1] = (byte)((addr >> 16) & 0xFF);
                cmd[2] = (byte)((addr >> 8) & 0xFF);
                cmd[3] = (byte)(addr & 0xFF);

                byte[] resp = hd.Ch341SPI4Stream(cmd);
                if (resp != null)
                {
                    //if(resp[0] != 0x03)
                    //{
                    //    addLogLine($" Failed to read 0x{addr:X6}");
                    //    return null;
                    //}
                    //Console.Write($"0x{addr:X6}...");
                    Array.Copy(resp, 4, result, offset, readSize);
					if (!bSilent)
					{
						int completed = offset + readSize;
						int percent = (int)((long)completed * 100 / size);
						if (percent != lastPercent)
						{
							SetOperationProgress($"Reading: ({percent,3}%) 0x{address + completed:X6}", Color.Black);
							lastPercent = percent;
						}
						logger.setProgress(completed, size);
					}
                }
                else
                {
                    if (bSilent == false)
                    {
                        addLogLine($" Failed to read 0x{addr:X6}");
                    }
                    return null;
                }
            }

            if (bSilent == false)
            {
				FinishOperationProgress();
            }
            return result;
        }
        public byte ReadStatus()
        {
            return ReadStatusRegister(0x05);
        }

        public byte ReadStatusRegister(byte opcode)
        {
            byte[] cmd = new byte[2] { opcode, 0x00 }; // Read Status Register
            byte[] resp = hd.Ch341SPI4Stream(cmd);
            return resp != null ? resp[1] : (byte)0xFF;
        }

        public bool WaitWriteComplete(int timeoutMs = 5000)
        {
            int elapsed = 0;
            const int pollInterval = 1; // ms
            while (elapsed < timeoutMs)
            {
                if ((ReadStatus() & 0x01) == 0)
                    return true; // WIP=0
                Thread.Sleep(pollInterval);
                elapsed += pollInterval;
            }
            addLogLine("Error: Flash write timed out.");
            return false;
        }

        public bool WriteEnable()
        {
            byte[] cmd = new byte[1] { 0x06 }; // Write Enable
            hd.Ch341SPI4Stream(cmd);
            return (ReadStatus() & 0x02) != 0; // WEL=1
        }

        public bool WriteStatusRegister(byte opcode, byte value)
        {
            // Enable writes
            if (!WriteEnable())
                return false;

            byte[] cmd = new byte[2] { opcode, value };
            hd.Ch341SPI4Stream(cmd);

            return WaitWriteComplete(5000);
        }

        public bool WriteStatusRegisters(byte status1, byte status2)
        {
            // Enable writes
            if (!WriteEnable())
                return false;

            byte[] cmd = new byte[3] { 0x01, status1, status2 };
            hd.Ch341SPI4Stream(cmd);

            return WaitWriteComplete(5000);
        }

        public bool ChipErase()
        {
            addLogLine("Full chip erase - enabling chip...");
            if (!WriteEnable())
                return false;

            addLogLine("Full chip erase - sending erase...");
            byte[] cmd = new byte[1] { 0xC7 };
            hd.Ch341SPI4Stream(cmd);

            addLogLine("Full chip erase - awaiting erase complete...");
            // Poll WIP bit until erase completes
            if (!WaitWriteComplete(120_000)) // typical full chip erase can take up to 120s
            {
                addLogLine("Error: Full chip erase timed out.");
                return false;
            }
            addLogLine("Full chip erase done - checking is first sector empty (just to be sure)");

            if (CheckFlashEmpty(0, 4096) == false)
            {
                addLogLine("Full chip erase failed");
                logger.setState("Erase fail", Color.Red);
                return false;
            }
            addLogLine("Full chip erase completed successfully.");
            return true;
        }
        bool eraseSector(uint addr, bool bVerify)
        {
            const int sectorSize = 4096;
            if (!WriteEnable())
                return false;

            byte[] cmd = new byte[4];
            cmd[0] = 0x20; // Sector Erase
            cmd[1] = (byte)((addr >> 16) & 0xFF);
            cmd[2] = (byte)((addr >> 8) & 0xFF);
            cmd[3] = (byte)(addr & 0xFF);

            hd.Ch341SPI4Stream(cmd);
            if (!WaitWriteComplete(5000))
                return false;
            if (CheckFlashEmpty(addr, sectorSize) == false)
            {
                return false;
            }
            return true;
        }
        bool isValidFlashRange(uint address, long length)
        {
            return length > 0 && flashSize > 0
                && (ulong)address + (ulong)length <= (ulong)flashSize
                && (ulong)address + (ulong)length <= 0x1000000UL;
        }
        public bool EraseFlash(uint ofs, int len)
        {
            const int sectorSize = 4096;
            long eraseLength = ((long)len + sectorSize - 1) / sectorSize * sectorSize;
            if (ofs % sectorSize != 0 || !isValidFlashRange(ofs, eraseLength))
            {
                addErrorLine("SPI erase range is unaligned or outside the flash/address bounds.");
                return false;
            }
            byte[] cmd = new byte[4];

            addLogLine("Starting flash erase, ofs 0x" + ofs.ToString("X") + ", len 0x" + len.ToString("X"));

            logger.setProgress(0, len);
            logger.setState("Erasing", Color.White);
            for (uint addr = ofs; addr < ofs + len; addr += sectorSize)
            {
                logger.setProgress((int)addr - (int)ofs, len);
                int errors = 0;
                while (true)
                {
                    bool bOk = eraseSector(addr, true);
                    if(bOk)
                    {
                        break;
                    }
                    errors++;
                    if (errors > 10)
                    {
                        addLogLine("Erase verfication failed at " + addr);
                        logger.setState("Erase fail", Color.Red);
                        return false;
                    }
                    addLogLine("Error at " + addr + ", retry "+ errors);
                }
            }
            logger.setState("Erase done", Color.DarkGreen);
            return true;
        }

        public bool WriteFlash(uint ofs, int len, byte[] buffer)
        {
            if (buffer == null || len > buffer.Length || !isValidFlashRange(ofs, len))
            {
                addErrorLine("SPI write buffer or range is invalid.");
                return false;
            }
            const int pageSize = 256;
			addLogLine($"Write range: 0x{ofs:X6}-0x{ofs + len - 1:X6} ({len} bytes).");
			int lastPercent = -1;

            logger.setProgress(0, len);
            logger.setState("Writing", Color.White);
            int loops = 0;
            for (int offset = 0; offset < len;)
            {
                logger.setProgress(offset, len);
                if (!WriteEnable())
                {
					FinishOperationProgress();
                    return false;
                }

                uint addr = ofs + (uint)offset;
                int pageRemaining = pageSize - (int)(addr % pageSize);
                int writeSize = Math.Min(pageRemaining, len - offset);
                byte[] cmd = new byte[4 + writeSize];
                cmd[0] = 0x02; // Page Program
                cmd[1] = (byte)((addr >> 16) & 0xFF);
                cmd[2] = (byte)((addr >> 8) & 0xFF);
                cmd[3] = (byte)(addr & 0xFF);
                Array.Copy(buffer, offset, cmd, 4, writeSize);

                hd.Ch341SPI4Stream(cmd);
                if (!WaitWriteComplete(5000))
                {
					FinishOperationProgress();
                    return false;
                }
                if (loops % 50 == 0)
                {
                    byte[] readBack = ReadFlash(addr, writeSize, true);
                    if (readBack == null)
                    {
                        logger.setState("Writing error", Color.Red);
						FinishOperationProgress();
						return false;
                    }
                    for (int i = 0; i < writeSize; i++)
                    {
                        if (readBack[i] != buffer[offset + i])
                        {
                            logger.setState("Writing error", Color.Red);
							FinishOperationProgress();
							return false;
                        }
                    }
                }
                loops++;
                offset += writeSize;
				logger.setProgress(offset, len);
				int percent = (int)((long)offset * 100 / len);
				if (percent != lastPercent)
				{
					SetOperationProgress($"Writing: ({percent,3}%) 0x{ofs + offset:X6}", Color.Black);
					lastPercent = percent;
				}

            }
			FinishOperationProgress();
            logger.setState("Writing done", Color.DarkGreen);
            return true;
        }

        public bool WriteFlashAndVerify(uint ofs, int len, byte[] buffer)
        {
            if (!WriteFlash(ofs, len, buffer)) return false;

			addLogLine("Verification: full readback comparison.");
            byte[] readBack = ReadFlash(ofs, len, true);
            if (readBack == null) return false;

            for (int i = 0; i < len; i++)
                if (readBack[i] != buffer[i])
                    return false;

			addSuccess("Full readback verification passed." + Environment.NewLine);
            return true;
        }

        public byte[] RandomPattern(int len)
        {
            byte[] pattern = new byte[len];
            Random rnd = new Random((int)DateTime.Now.Ticks);
            rnd.NextBytes(pattern);
            return pattern;
        }

        public bool TestReadWrite(uint ofs, int len)
        {
            byte[] pattern = RandomPattern(len);

            //ChipErase();
            addLogLine("Erasing flash...");
            if (!EraseFlash(ofs, len))
                return false;
            byte[] cleared = ReadFlash(ofs, len);
            for (int i = 0; i < len; i++)
            {
                if (cleared[i] != 0xff)
                {
                    addLogLine("Clear error");
                    return false;
                }
            }

            addLogLine("Writing pattern...");
            if (!WriteFlashAndVerify(ofs, len, pattern))
            {
                addLogLine("Test Failed!");
                return false;
            }

            addLogLine("Test passed!");
            return true;
        }
        public virtual bool Sync()
        {
             return true;
        }
        bool doGenericSetup()
        {
			addLogLine("Connecting: CH341 SPI, " + chipType + ".");

            closePort();
            hd = new CH341DEV(0);
            if (hd.Ch341Open() == -1)
            {
                addError("CH341 error " + hd.getLastError() + Environment.NewLine);
                hd = null;
                return false;
            }
            ch341Opened = true;
            addLog(CH341DEV.GetDllDiagnostics() + Environment.NewLine);
            if (hd.Ch341SetI2CSpeed(3) < 0)
            {
                addError("CH341 setup error " + hd.getLastError() + Environment.NewLine);
                closePort();
                return false;
            }
            addLog("CH341 ready!" + Environment.NewLine);

            if (this.Sync() == false)
            {
                addErrorLine("CH341 sync failed.");
                closePort();
                return false;
            }
            if (this.getAndPrintInfo())
            {
                addErrorLine("Initial get info failed.");
                addErrorLine("This may happen if you don't reset between flash operations");
                addErrorLine("So, make sure that BOOT is connected, do reset (or power off/on) and try again");
                closePort();
                return false;
            }
            return true;
        }
        int flashSize;
        bool getAndPrintInfo()
        {
            byte[] jedec = this.ReadJEDECID();
            if(jedec == null)
            {
                addErrorLine("Failed to read JEDEC ID!");
                return true;
            }
            flashSize = this.GetFlashSize(jedec);
            if(flashSize <= 0 || flashSize >= 64 * 1024 * 1024)
            {
                addErrorLine("Failed to extract flash size!");
                return true;
            }
            int deviceMID = GetFlashMIDFromJEDEC(jedec);
            SPIFlashInfo flashInfo = SPIFlashInfoList.Singleton.findFlashForMID(deviceMID);
            if(flashInfo != null)
            {
				addSuccess(FormatFlashInfo(deviceMID, flashInfo.manufacturer, flashSize, flashInfo.icName) + Environment.NewLine);
                if(flashInfo.szMem != flashSize)
                {
                    addWarningLine("JEDEC flash size differs from flash list size: JEDEC="
                        + flashSize.ToString("X2") + ", list=" + flashInfo.szMem.ToString("X2"));
                }
            }
            else
            {
                addWarningLine("No flash definition found for MID " + deviceMID.ToString("X8") + ".");
            }
          
            return false;
        }
        public bool loadAndRunPreprocessedImage(string fname)
        {
            if (File.Exists(fname) == false)
            {
                addErrorLine("File " + fname + " not found!");
                return false;
            }
            byte[] loaderBinary = File.ReadAllBytes(fname);

            return false;
        }
        public bool doWrite(int startSector, int numSectors, byte[] data, WriteMode mode)
        {
            if (doGenericSetup() == false)
            {
                return true;
            }
            //if(upload_ram_loader("loaders/LN882H_RAM_BIN.bin"))
            //{
            //    return true;
            //}
            //flash_program(data,0,data.Length, "", false);
            return false;
        }



        public override void doRead(int startSector = 0x000, int sectors = 10, bool fullRead = false)
        {
			readFailed = true;
			ms?.Dispose();
			ms = null;
            if (doGenericSetup() == false)
            {
                return;
            }
            if(fullRead)
            {
                doReadInternal(0, flashSize);
            }
            else
            {
                // In CH341 SPI modes this inherited parameter is a byte offset.
                doReadInternal((uint)startSector, sectors * 4096);
            }
            ChipReset();
        }
        public virtual bool ChipReset()
        {
            return false;
        }

        bool openPort()
        {
            return false;
        }

		bool VerifyFinalReadback(uint address, byte[] expected, string operation)
		{
			if (!finalReadbackVerification.HasValue)
				return true;
			if (!finalReadbackVerification.Value)
			{
				addLogLine(operation + " final readback skipped.");
				return true;
			}
			try
			{
				if (expected == null || expected.Length == 0 || flashSize <= 0
					|| (ulong)address + (ulong)expected.Length > (ulong)flashSize
					|| (ulong)address + (ulong)expected.Length > 0x1000000UL)
					throw new IOException("Final readback range is outside the SPI flash/address bounds.");
				addLogLine(operation + " final readback...");
				for (int offset = 0; offset < expected.Length; offset += 4096)
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (isCancelled)
						throw new OperationCanceledException();
					int length = Math.Min(4096, expected.Length - offset);
					byte[] actual = ReadFlash(address + (uint)offset, length, true);
					if (actual == null || actual.Length != length)
						throw new IOException("Final SPI readback failed or returned an unexpected length.");
					for (int index = 0; index < length; index++)
						if (actual[index] != expected[offset + index])
							throw new IOException("Final SPI readback differs at 0x" + (address + (uint)(offset + index)).ToString("X") + ".");
					logger.setProgress(offset + length, expected.Length);
				}
				cancellationToken.ThrowIfCancellationRequested();
				if (isCancelled)
					throw new OperationCanceledException();
				addSuccess(operation + " final readback passed." + Environment.NewLine);
				return true;
			}
			catch (Exception exception)
			{
				addErrorLine(operation + " final readback failed: " + exception.Message);
				return false;
			}
		}

        public bool doReadInternal(uint addr, int len)
        {
			readFailed = true;
			ms?.Dispose();
			ms = null;
            byte[] res = ReadFlash(addr, len);
			if (res != null && VerifyFinalReadback(addr, res, "Backup"))
            {
                ms = new MemoryStream(res);
				readFailed = false;
            }
            else
            {
                ms = null;
            }
            return false;
        }
        MemoryStream ms;

        public SPIFlasher(CancellationToken ct) : base(ct)
        {
        }

        public override byte[] getReadResult()
        {
			if (readFailed || ms == null)
                return null;
            return ms.ToArray();
        }
        public override bool doErase(int startSector, int sectors, bool bAll)
        {
            if (doGenericSetup() == false)
            {
                return false;
            }
            if (UnprotectFlash() == false)
            {
                return false;
            }
            if (bAll)
            {
                if (!ChipErase())
                {
                    return false;
                }
                return true;
            }
            return false;
        }
        public bool UnprotectFlash()
        {
            // Read out status registers before changing protection bits.
            byte sr1Before = ReadStatusRegister(0x05);
            byte sr2Before = ReadStatusRegister(0x35);
            byte sr3Before = ReadStatusRegister(0x15);
            addLogLine("Status before unprotect: SR1=0x" + sr1Before.ToString("X2")
                + ", SR2(35h)=0x" + sr2Before.ToString("X2")
                + ", SR3(15h)=0x" + sr3Before.ToString("X2"));

            // Write 0x00 to Status Register 1 (clear BP bits and common TB/SEC/SRP bits)
            if (!WriteStatusRegister(0x01, 0x00))
                return false;

            byte sr1After = ReadStatusRegister(0x05);
            byte sr2After = ReadStatusRegister(0x35);

            if ((sr2After & 0x40) != 0)
            {
                if (sr2After == 0xFF)
                {
                    addWarningLine("SR2 read as 0xFF; leaving SR2 bit 6 untouched because the SR2 opcode may be unsupported.");
                }
                else
                {
                    // Clear SR2 CMP/TB only, preserving other bits such as QE.
                    byte newSr2 = (byte)(sr2After & ~0x40);
                    addLogLine("SR2 bit 6 (CMP/TB) is set; clearing it while preserving other SR2 bits.");
                    if (!WriteStatusRegister(0x31, newSr2))
                        return false;

                    sr2After = ReadStatusRegister(0x35);
                    if ((sr2After & 0x40) != 0)
                    {
                        addWarningLine("SR2 bit 6 remained set after WRSR2(31h); trying two-byte WRSR(01h SR1 SR2).");
                        if (!WriteStatusRegisters(sr1After, newSr2))
                            return false;
                        sr2After = ReadStatusRegister(0x35);
                    }
                }
            }

            // Verify unprotected: check status register has BP=0 and SR2 CMP/TB is clear.
            sr1After = ReadStatusRegister(0x05);
            sr2After = ReadStatusRegister(0x35);
            addLogLine("Unprotect done, SR1=0x" + sr1After.ToString("X2")
                + ", SR2(35h)=0x" + sr2After.ToString("X2"));

            return (sr1After & 0x1C) == 0 && (sr2After == 0xFF || (sr2After & 0x40) == 0); // BP0..BP3 and SR2 CMP/TB
        }
        public override void closePort()
        {
            if (hd != null)
            {
                if (ch341Opened)
                    hd.Ch341Close();
                hd = null;
                ch341Opened = false;
            }
        }
        public override void Dispose()
        {
            closePort();
            base.Dispose();
        }
        public override void doTestReadWrite(int startSector = 0x000, int sectors = 10)
        {
        }

        public override void doReadAndWrite(int startSector, int sectors, string sourceFileName, WriteMode rwMode)
        {
            if (doGenericSetup() == false)
            {
                return;
            }
            if(string.IsNullOrEmpty(sourceFileName))
            {
                addLogLine("No filename given!");
                return;
            }
            addLogLine("Reading " + sourceFileName + "...");
            byte[] x = File.ReadAllBytes(sourceFileName);
            if (x.Length == 0)
            {
                addErrorLine("Source file is empty.");
                return;
            }
            // In CH341 SPI modes this inherited parameter is a byte offset.
            uint writeOffset = (uint)startSector;
            int eraseLen = x.Length;
            if(bCustomWriteMode)
            {
                long requestedLen = (long)sectors * BK7231Flasher.SECTOR_SIZE;
                if(requestedLen <= 0 || requestedLen > int.MaxValue || x.Length > requestedLen)
                {
                    addErrorLine("Custom write length is invalid or smaller than the source file.");
                    return;
                }
                eraseLen = (int)requestedLen;
            }
            long roundedEraseLength = ((long)eraseLen + 4095) / 4096 * 4096;
            if (writeOffset % 4096 != 0 || !isValidFlashRange(writeOffset, roundedEraseLength))
            {
                addErrorLine("SPI write/erase range is unaligned or outside the flash/address bounds.");
                return;
            }
            if (rwMode == WriteMode.ReadAndWrite)
            {
                if (!isValidFlashRange(0, flashSize))
                {
                    addErrorLine("Full SPI backup exceeds the supported address range.");
                    return;
                }
                try
                {
                    doReadInternal(0, flashSize);
                    if (ms == null || ms.Length != flashSize)
                    {
                        addErrorLine("Full SPI backup failed or was incomplete; flash was not modified.");
                        return;
                    }
                    if (!saveReadResult(0))
                    {
                        addErrorLine("SPI backup could not be saved; flash was not modified.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    addErrorLine("SPI backup failed: " + ex.Message);
                    return;
                }
            }
            if (UnprotectFlash() == false)
            {
                return;
            }
            addLogLine("Starting SPI write at offset 0x" + writeOffset.ToString("X")
                + ", erase length 0x" + eraseLen.ToString("X") + ".");
            if(EraseFlash(writeOffset, eraseLen)==false)
            {

                return;
            }
            if(WriteFlash(writeOffset, x.Length,x) == false)
            {
                addErrorLine("SPI write failed.");
                return;
            }
			if (!VerifyFinalReadback(writeOffset, x, "Write"))
				return;
            ChipReset();
			addSuccess("Write complete." + Environment.NewLine);
        }
        bool saveReadResult(string fileName)
        {
			if (readFailed)
				return false;
            if (ms == null)
            {
                addError("There was no result to save." + Environment.NewLine);
                return false;
            }
            byte[] dat = ms.ToArray();
            string fullPath = "backups/" + fileName;
            File.WriteAllBytes(fullPath, dat);
			addLogLine("Saved: " + dat.Length + " bytes to " + fullPath + ".");
            logger.onReadResultQIOSaved(dat, "", fullPath);
			addSuccess("Read complete." + Environment.NewLine);
            return true;
        }
        public override bool saveReadResult(int startOffset)
        {
            string fileName = MiscUtils.formatDateNowFileName("readResult_" + chipType, backupName, "bin");
            return saveReadResult(fileName);
        }
    }
}

