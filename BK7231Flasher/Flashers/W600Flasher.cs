using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BK7231Flasher
{
	public class W600Flasher : BaseFlasher
	{
		private const int FlashSize = 0x100000;
		private const int SecbootOffset = 0x2000;
		private const int SecbootSize = 0xE000;
		private const int HelperSectorOffset = SecbootOffset;
		private const int HelperSectorSize = 0x1000;
		private const int FlsHeaderSize = 56;
		private const int HelperTransferPayloadSize = HelperSectorSize - FlsHeaderSize;
		private const uint FlashBase = 0x08000000;
		private const uint HelperVectorAddress = 0x08002100;
		private const uint HelperResetAddress = 0x08002141;
		private const int HelperPayloadOffset = 0x1A0;
		private const uint HelperPayloadAddress = 0x080021A0;
		private const uint HelperRamAddress = 0x20030000;
		private const uint HelperApbClock = 40000000;
		private const int HelperMaxReadLength = 16 * 1024;
		private const int SerialReadBufferSize = 64 * 1024;
		private const int MinimumReadResponseTimeoutMs = 100;
		private const int ReadResponseTimeoutMarginMs = 50;
		private const int ReadPreparationPerKiBMs = 10;
		private const int RetryReceiveIdleMs = 20;
		private const int ProtocolRequestSize = 16;
		private const int ProtocolResponseSize = 20;
		private const byte ProtocolVersion = 2;
		private const byte ProtocolInfo = 0;
		private const byte ProtocolRead = 1;
		private const byte ProtocolReturnToRom = 2;
		private const byte ProtocolFlashId = 4;
		private static readonly byte[] ReaderProtocolMarker = System.Text.Encoding.ASCII.GetBytes("W600RD30");
		private const int SecbootEntryTimeoutMs = 90000;
		private const int HelperEntryTimeoutMs = 90000;
		private const int RomEntryTimeoutMs = 15000;

		private sealed class SecbootProfile
		{
			internal readonly string Banner;
			internal readonly string Version;
			internal readonly string Resource;
			internal readonly byte[] Image;

			internal SecbootProfile(string banner, string version, string resource, byte[] image)
			{
				Banner = banner;
				Version = version;
				Resource = resource;
				Image = image;
			}
		}

		byte[] flashID;
		MemoryStream ms;
		volatile bool secbootRecoveryRequired;
		string lastXmodemProgressText;

		public W600Flasher(CancellationToken ct) : base(ct)
		{
		}

		protected override bool KeepSerialPortOpenForCancellationRecovery()
		{
			return secbootRecoveryRequired;
		}

		private bool doGenericSetup()
		{
			addLog("Now is: " + DateTime.Now.ToLongDateString() + " " + DateTime.Now.ToLongTimeString() + "." + Environment.NewLine);
			addLog("Flasher mode: " + chipType + Environment.NewLine);
			addLog("Going to open port: " + serialName + "." + Environment.NewLine);
			try
			{
				serial = new SerialPort(serialName, 115200);
				serial.ReadBufferSize = SerialReadBufferSize;
				serial.Open();
				serial.DiscardInBuffer();
				serial.DiscardOutBuffer();
				serial.ReadTimeout = 2000;
				xm = new XMODEM(serial, XMODEM.Variants.XModem1K, 0xFF)
				{
					SendInactivityTimeoutMillisec = 5000,
					MaxSenderRetries = 5
				};
			}
			catch(Exception ex)
			{
				addLog("Port setup failed with " + ex.Message + "!" + Environment.NewLine);
				return false;
			}
			addLog("Port ready!" + Environment.NewLine);
			return true;
		}

		private byte[] ReadFlashId()
		{
			var res = ExecuteCommand(0x3C, null, 1, 10, isErrorExpected: true);
			if(res != null && res[0] == 'F' && res[1] == 'I' && res[2] == 'D')
			{
				flashID = new byte[] { Convert.ToByte($"{(char)res[4]}{(char)res[5]}", 16) };
				addLogLine($"Mask ROM flash manufacturer ID: 0x{flashID[0]:X2}.");
				return flashID;
			}
			addLogLine("No Mask-ROM flash-ID response; installed secboot is active.");
			addLogLine("Requesting secboot erase/reset to enter Mask ROM...");
			ExecuteCommand(0x3F, null, 1, 2);
			if(!Sync())
				return null;
			return ReadFlashId();
		}

		private bool Sync()
		{
			serial.DiscardInBuffer();
			int consecutiveC = 0;
			try
			{
				Stopwatch sw = Stopwatch.StartNew();
				long nextEsc = 0;
				long nextProgress = 1000;
				while(sw.ElapsedMilliseconds < RomEntryTimeoutMs && !isCancelled)
				{
					if(serial.BytesToRead > 0)
					{
						int value = serial.ReadByte();
						if(value == 'C')
						{
							consecutiveC++;
							if(consecutiveC >= 4)
							{
								logger.setLogProgress("Sync success!" + Environment.NewLine, Color.Green);
								return true;
							}
						}
						else if(value != 'P')
						{
							consecutiveC = 0;
						}
					}
					else
					{
						if(sw.ElapsedMilliseconds >= nextEsc)
						{
							serial.Write(new byte[] { 0x1B }, 0, 1);
							nextEsc = sw.ElapsedMilliseconds + 10;
						}
						Thread.Sleep(1);
					}
					if(sw.ElapsedMilliseconds >= nextProgress)
					{
						logger.setLogProgress($"Waiting for W600 sync ({sw.ElapsedMilliseconds / 1000}s)...", Color.Orange);
						nextProgress = sw.ElapsedMilliseconds + 1000;
					}
				}
			}
			catch(Exception ex)
			{
				logger.setLogProgress("Sync failed: " + ex.Message + Environment.NewLine, Color.Red);
				return false;
			}
			logger.setLogProgress($"Sync failed after {RomEntryTimeoutMs / 1000} seconds." + Environment.NewLine, Color.Red);
			return false;
		}

		private byte[] ReadFlashIdWithoutErase()
		{
			var res = ExecuteCommand(0x3C, null, 1, 10, isErrorExpected: true);
			if(res == null || res.Length < 6 || res[0] != 'F' || res[1] != 'I' ||
				res[2] != 'D' || res[3] != ':')
			{
				addErrorLine("W600 ROM flash-ID probe failed after the secboot transition; automatic recovery will be attempted.");
				return null;
			}
			try
			{
				flashID = new byte[] { Convert.ToByte($"{(char)res[4]}{(char)res[5]}", 16) };
			}
			catch(Exception)
			{
				addErrorLine("W600 ROM returned an invalid flash-ID response.");
				return null;
			}
			return flashID;
		}

		private static SecbootProfile[] LoadSecbootProfiles()
		{
			var definitions = new[]
			{
				new { Banner = "secboot(1MB) running V3.3...", Version = "V3.3", Resource = "W600_Secboot_1M_V3_3", Hash = "CD7038450AB26C568EEFA396299B231B53140C77299E78766E76F68C3814D8C4" },
				new { Banner = "secboot(1MB) running V3.9...", Version = "V3.9", Resource = "W600_Secboot_1M_V3_9", Hash = "F3FF0BB62C1B5F2AA72F65664DDE3E312C534CCA0F8DA4D4D5E9BD4126C0CD30" },
				new { Banner = "secboot(1MB) running V3.13...", Version = "V3.13", Resource = "W600_Secboot_1M_V3_13", Hash = "839F822ED973953559533474706EF02BE85AEF10971C7081772158C372753962" },
			};
			var profiles = new SecbootProfile[definitions.Length];
			for(int index = 0; index < definitions.Length; index++)
			{
				var definition = definitions[index];
				byte[] image = FLoaders.GetRawBinaryFromAssembly(definition.Resource);
				if(image == null || image.Length != SecbootSize)
					throw new InvalidDataException($"Embedded W600 secboot {definition.Version} has an unexpected size.");
				if(ReadUInt32(image, 0) != 0xA0FFFF9F || ReadUInt16(image, 4) != 2 ||
					ReadUInt32(image, 8) != 0x2100)
					throw new InvalidDataException($"Embedded W600 secboot {definition.Version} has an invalid image header.");
				if(!string.Equals(GetSha256(image), definition.Hash, StringComparison.Ordinal))
					throw new InvalidDataException($"Embedded W600 secboot {definition.Version} failed its SHA-256 catalog check.");
				for(int i = image.Length - FlsHeaderSize; i < image.Length; i++)
				{
					if(image[i] != 0xFF)
						throw new InvalidDataException($"Embedded W600 secboot {definition.Version} cannot be restored without XMODEM spill.");
				}
				profiles[index] = new SecbootProfile(definition.Banner, definition.Version,
					definition.Resource, image);
			}
			return profiles;
		}

		private SecbootProfile WaitForSupportedSecboot(SecbootProfile[] profiles,
			bool ignoreCancellation, int timeoutMs)
		{
			serial.BaudRate = 115200;
			serial.DiscardInBuffer();
			serial.DiscardOutBuffer();
			var capture = new StringBuilder();
			var sw = Stopwatch.StartNew();
			long nextPrompt = 0;
			while(sw.ElapsedMilliseconds < timeoutMs && (ignoreCancellation || !isCancelled))
			{
				serial.Write(new byte[] { 0x1B }, 0, 1);
				while(serial.BytesToRead > 0)
				{
					int value = serial.ReadByte();
					if(value >= 0)
						capture.Append((char)(byte)value);
				}
				if(capture.Length > 1024)
					capture.Remove(0, capture.Length - 512);

				string text = capture.ToString();
				foreach(SecbootProfile profile in profiles)
				{
					if(text.IndexOf(profile.Banner, StringComparison.Ordinal) >= 0)
					{
						addLogLine();
						return profile;
					}
				}
				int bannerStart = text.LastIndexOf("secboot(", StringComparison.Ordinal);
				if(bannerStart >= 0)
				{
					int bannerEnd = text.IndexOf('\n', bannerStart);
					if(bannerEnd >= 0)
					{
						string banner = text.Substring(bannerStart, bannerEnd - bannerStart).Trim();
						if(banner.IndexOf("V3.14", StringComparison.Ordinal) >= 0)
							addErrorLine($"Unsupported W600 secboot for the UART0 reader: {banner} " +
								"SDK V3.14 accepts host communication only on UART1. No flash command was sent.");
						else
							addErrorLine($"Unsupported W600 secboot: {banner}. No flash command was sent.");
						return null;
					}
				}
				if(sw.ElapsedMilliseconds >= nextPrompt)
				{
					logger.setLogProgress("Pull RESET pin to GND...", Color.DarkOrange);
					nextPrompt = sw.ElapsedMilliseconds + 5000;
				}
				Thread.Sleep(10);
			}
			if(!isCancelled || ignoreCancellation)
				addErrorLine("Timed out waiting for a supported W600 secboot banner.");
			return null;
		}

		public override void Xm_PacketSent(int sentBytes, int total, int sequence, uint offset)
		{
			if((sequence % 4) == 1)
			{
				lastXmodemProgressText = $"Writing 0x{offset:X6}...";
				logger.setLogProgress(lastXmodemProgressText, Color.Black);
			}
			logger.setProgress(sentBytes, total);
		}

		private bool InitialiseTarget()
		{
			return Sync() && ReadFlashId() != null;
		}

		private byte[] ExecuteCommand(int type, byte[] parms = null,
			float timeout = 0.1f, int expectedReplyLen = 0, int br = 115200, bool isErrorExpected = false)
		{
			parms = parms ?? new byte[0];
			var cmd = new List<byte>()
			{
				(byte)(type & 0xFF),
				(byte)((type >> 8) & 0xFF),
				(byte)((type >> 16) & 0xFF),
				(byte)((type >> 24) & 0xFF)
			};
			cmd.AddRange(parms);
			var raw = new List<byte>()
			{
				0x21,
				(byte)(cmd.Count + 2 & 0xFF),
				(byte)((cmd.Count + 2 >> 8) & 0xFF)
			};
			var crc = CRC16.Compute(CRC16Type.CCITT_FALSE, cmd.ToArray());
			raw.Add((byte)(crc & 0xFF));
			raw.Add((byte)((crc >> 8) & 0xFF));
			raw.AddRange(cmd);

			serial.DiscardInBuffer();
			serial.Write(raw.ToArray(), 0, raw.Count);
			if(type == 0x31)
			{
				Thread.Sleep(10);
				serial.BaudRate = br;
			}
			int timeoutMs = (int)(timeout * 1000);
			var sw = System.Diagnostics.Stopwatch.StartNew();
			while(sw.ElapsedMilliseconds < timeoutMs)
			{
				if(serial.BytesToRead >= expectedReplyLen)
					break;
			}
			if(serial.BytesToRead == 0)
			{
				if(!isErrorExpected) addErrorLine("Command response is empty!");
				return null;
			}
			var bytes = new byte[serial.BytesToRead];
			serial.Read(bytes, 0, bytes.Length);
			if(bytes.Length < expectedReplyLen)
			{
				if(!isErrorExpected) addErrorLine($"Command reply length {bytes.Length} < expected {expectedReplyLen}");
				return null;
			}
			var ret = new byte[expectedReplyLen];
			Array.Copy(bytes, ret, expectedReplyLen);
			return ret;
		}

		private bool SetBaud(int baud, bool noResync = false)
		{
			if(serial.BaudRate == baud)
				return true;
			addLogLine($"Changing baud to {baud}{(!noResync ? ", will resync..." : string.Empty)}");
			byte[] msg = BitConverter.GetBytes(baud);
			ExecuteCommand(0x31, msg, 1, 1, baud, noResync);
			return noResync || Sync();
		}

		private bool EraseAndWait(string label, byte[] parms, int timeoutSeconds)
		{
			addLogLine(label);
			var response = ExecuteCommand(0x32, parms, timeoutSeconds, 4);
			return response != null && response.Length >= 4 &&
				response[0] == 'C' && response[1] == 'C' && response[2] == 'C' && response[3] == 'C';
		}

		public override void doRead(int startSector = 0x000, int sectors = 10, bool fullRead = false)
		{
			ms = null;
			secbootRecoveryRequired = false;
			if(!TryCalculateHelperBaudRegister(baudrate, out uint helperBaudRegister))
			{
				addErrorLine($"W600 UART reader does not support the selected {baudrate} baud rate. " +
					"Select one of: 115200, 230400, 460800, 921600, 1500000, or 2000000.");
				return;
			}
			int offset = fullRead ? 0 : startSector;
			int length;
			try
			{
				length = fullRead ? FlashSize : checked(sectors * BK7231Flasher.SECTOR_SIZE);
			}
			catch(OverflowException)
			{
				addErrorLine("W600 read length is out of range.");
				return;
			}
			if(offset < 0 || length <= 0 || offset > FlashSize - length)
			{
				addErrorLine("W600 read range must stay within the 1 MiB flash.");
				return;
			}
			if(!doGenericSetup())
				return;

			SecbootProfile profile = null;
			SecbootProfile updateProfile = null;
			bool helperInstalled = false;
			bool helperReady = false;
			bool restorationCompleted = false;
			byte[] reconstructedFlash = null;
			try
			{
				xm.PacketSent += Xm_PacketSent;
				SecbootProfile[] profiles = LoadSecbootProfiles();
				updateProfile = Array.Find(profiles, candidate => candidate.Version == "V3.13");
				if(updateProfile == null)
					throw new InvalidDataException("The embedded W600 V3.13 secboot update image is unavailable.");
				// Construct and validate the embedded bootstrap-v3/protocol-v2 reader before asking
				// for a reset. A stale or oversized reader must fail with secboot intact.
				byte[] helperPayload = BuildHelperPayload(helperBaudRegister);
				byte[] fls = GenerateW600PseudoFLSFromData(helperPayload,
					(int)(FlashBase + (uint)HelperSectorOffset));
				if(fls.Length != HelperSectorSize)
					throw new InvalidDataException("The W600 helper FLS transfer must be exactly one 4 KiB XMODEM span.");

				profile = WaitForSupportedSecboot(profiles, false, SecbootEntryTimeoutMs);
				if(profile == null)
					return;

				secbootRecoveryRequired = true;
				addLogLine("Requesting Mask ROM entry...");
				ExecuteCommand(0x3F, null, 1, 0, isErrorExpected: true);
				serial.BaudRate = 115200;
				if(!WaitForRomAtCurrentBaud(RomEntryTimeoutMs, "after secboot 0x3F") || ReadFlashIdWithoutErase() == null)
					throw new IOException("W600 did not enter Mask ROM after the secboot erase command.");
				if(!SetBaud(baudrate))
				{
					logger.setState("Helper install failed", Color.Red);
					addErrorLine($"Failed to establish the W600 ROM link at {baudrate} baud.");
					return;
				}
				addLogLine("Staging loader...");
				int helperSent = xm.Send(fls, (uint)HelperSectorOffset);
				logger.setLogProgress("Writing loader" + Environment.NewLine, Color.Black);
				if(helperSent != fls.Length)
					throw new IOException("W600 UART secboot-reader write failed.");

				helperInstalled = true;
				logger.setLogProgress("W600 UART loader sector written." + Environment.NewLine, Color.Green);
				serial.BaudRate = baudrate;
				serial.DiscardInBuffer();
				logger.setState("Reset W600 to start reader", Color.DarkOrange);
				addLogLine("Temporary loader installed. Press and release RESET. Waiting for the reader...");
				if(!WaitForHelper(HelperEntryTimeoutMs, false, out int helperChunk))
					throw new IOException("Timed out waiting for the temporary W600 secboot reader.");
				helperReady = true;
				LogHelperFlashInfo();

				logger.setState("Reading W600 flash", Color.Green);
				int chunkSize = Math.Min(helperChunk, HelperMaxReadLength);
				addLogLine($"Reading W600 flash: {FlashSize} bytes at {baudrate} baud...");
				byte[] result = ReadHelperRange(FlashBase, FlashSize, chunkSize);
				if(result == null)
					throw new IOException(isCancelled ? "W600 flash read was cancelled." : "W600 flash read failed.");

				if(!SecbootRemainderMatches(result, profile.Image))
					throw new InvalidDataException($"The untouched secboot remainder does not match catalogued {profile.Version}; the dump cannot be reconstructed exactly.");

				reconstructedFlash = result;
				Array.Copy(profile.Image, 0, reconstructedFlash, SecbootOffset, HelperSectorSize);

				if(!RestoreSecboot(updateProfile, helperInstalled, helperReady, true))
					throw new IOException($"The flash was read, but the automatic {updateProfile.Version} secboot update failed.");
				restorationCompleted = true;
				secbootRecoveryRequired = false;
				byte[] requested = new byte[length];
				Array.Copy(reconstructedFlash, offset, requested, 0, length);
				ms = new MemoryStream(requested);
				logger.setState("Read complete", Color.DarkGreen);
			}
			catch(Exception ex)
			{
				if(reconstructedFlash != null)
				{
					byte[] requested = new byte[length];
					Array.Copy(reconstructedFlash, offset, requested, 0, length);
					ms = new MemoryStream(requested);
					addLogLine("The reconstructed read result is retained even though device recovery did not complete.");
				}
				logger.setState("Read error", Color.Red);
				addErrorLine("W600 read failed: " + ex.Message);
			}
			finally
			{
				if(secbootRecoveryRequired && profile != null && !restorationCompleted)
				{
					logger.setState("Restoring W600 secboot", Color.DarkOrange);
					addLogLine("Backup did not complete; attempting mandatory secboot recovery before closing COM.");
					if(RestoreSecboot(profile, helperInstalled, helperReady))
					{
						secbootRecoveryRequired = false;
						addLogLine($"Recovered {profile.Version} secboot after the interrupted backup.");
					}
					else
					{
						addErrorLine("Automatic secboot recovery failed. Keep the device powered; it remains recoverable through W600 Mask ROM on this UART.");
					}
				}
				xm.PacketSent -= Xm_PacketSent;
			}
		}

		public override byte[] getReadResult()
		{
			return ms?.ToArray();
		}

		public override bool saveReadResult(int startOffset)
		{
			if(ms == null)
				return false;
			string fileName = MiscUtils.formatDateNowFileName("readResult_" + chipType, backupName, "bin");
			string fullPath = "backups/" + fileName;
			byte[] data = ms.ToArray();
			File.WriteAllBytes(fullPath, data);
			addSuccess("Saved W600 flash backup: " + data.Length + " bytes to " + fullPath + "." + Environment.NewLine);
			logger.onReadResultQIOSaved(data, "", fullPath);
			return true;
		}

		public override void doWrite(int startSector, byte[] data)
		{
			return;
		}

		public override bool doErase(int startSector = 0x000, int sectors = 10, bool bAll = false)
		{
			if(!bAll)
			{
				addErrorLine("W600 range erase is not implemented.");
				return false;
			}
			if(!doGenericSetup() || !InitialiseTarget())
				return false;
			bool ok = EraseAndWait("Erasing W600 flash...", null, 24);
			if(ok)
			{
				logger.setState("Erase done", Color.DarkGreen);
				logger.setProgress(1, 1);
				addLogLine("Erase flash ok.");
			}
			else
			{
				logger.setState("Erase error!", Color.Red);
			}
			return ok;
		}

		public override void doReadAndWrite(int startSector, int sectors, string sourceFileName, WriteMode rwMode)
		{
			if(rwMode == WriteMode.ReadAndWrite)
			{
				addErrorLine("W600 Backup then Write is not supported because the UART backup helper requires a manual reset between the read and ROM write phases. Use Backup FW first.");
				return;
			}
			if(rwMode == WriteMode.OnlyOBKConfig)
			{
				addErrorLine("Writing only OBK config is disabled for W600, use \"Automatically configure OBK on flash write\".");
				return;
			}
			byte[] firmware = null;
			uint firmwareOffset = 0;
			string writeSummary = null;
			try
			{
				if(rwMode == WriteMode.OnlyWrite)
				{
					if(string.IsNullOrEmpty(sourceFileName))
					{
						addErrorLine("No filename given!");
						return;
					}
					addLogLine("Writing " + sourceFileName + "...");
					byte[] data = File.ReadAllBytes(sourceFileName);
					if(sourceFileName.EndsWith(".fls", StringComparison.OrdinalIgnoreCase))
					{
						firmware = data;
						writeSummary = $"Done W600 FLS write: {data.Length} bytes transferred.";
					}
					else if(data.Length >= 0x100000)
					{
						startSector = 0x2000;
						var secBootHeader = new byte[64];
						Array.Copy(data, startSector, secBootHeader, 0, secBootHeader.Length);
						if(secBootHeader[0] != 0x9F || secBootHeader[1] != 0xFF || secBootHeader[2] != 0xFF || secBootHeader[3] != 0xA0)
						{
							addErrorLine("Unknown file type, no firmware header at 0x2000!");
							return;
						}
						if(secBootHeader[60] != 0xFF || secBootHeader[61] != 0xFF || secBootHeader[62] != 0xFF || secBootHeader[63] != 0xFF)
						{
							addErrorLine("Not W600 backup!");
							return;
						}
						var cutData = new byte[data.Length - startSector];
						Array.Copy(data, startSector, cutData, 0, cutData.Length);
						firmware = GenerateW600PseudoFLSFromData(cutData, startSector | 0x08000000);
						firmwareOffset = (uint)startSector;
						writeSummary = $"Done W600 backup restore: programmed 0x{startSector:X6}-0x{data.Length - 1:X6} " +
							$"({cutData.Length} bytes); preserved 0x000000-0x{startSector - 1:X6}.";
					}
					else
					{
						addErrorLine("Unknown file type, skipping.");
						return;
					}
				}
			}
			catch(Exception ex)
			{
				addErrorLine(ex.Message);
				return;
			}

			if(!doGenericSetup() || !InitialiseTarget())
				return;
			try
			{
				xm.PacketSent += Xm_PacketSent;
				SetBaud(baudrate);
				OBKConfig cfg = logger.getConfigToWrite();
				if(rwMode == WriteMode.OnlyWrite)
				{
					if(xm.Send(firmware, firmwareOffset) != firmware.Length)
					{
						logger.setState("Write error!", Color.Red);
						logger.setLogProgress("Write error!" + Environment.NewLine, Color.Red);
						return;
					}
					logger.setState("Writing done", Color.DarkGreen);
					logger.setLogProgress(writeSummary + Environment.NewLine, Color.Green);
					logger.setProgress(1, 1);
				}

				if(cfg != null && !isCancelled)
				{
					int offset = OBKFlashLayout.getConfigLocation(chipType, out _) | 0x08000000;
					cfg.saveConfig(chipType);
					var data = MiscUtils.padArray(cfg.getData(), BK7231Flasher.SECTOR_SIZE);
					addLog("Now will also write OBK config..." + Environment.NewLine);
					addLog("Long name from CFG: " + cfg.longDeviceName + Environment.NewLine);
					addLog("Short name from CFG: " + cfg.shortDeviceName + Environment.NewLine);
					addLog("Web Root from CFG: " + cfg.webappRoot + Environment.NewLine);
					var fls = GenerateW600PseudoFLSFromData(data, offset);
					if(xm.Send(fls, (uint)(offset ^ 0x08000000)) == fls.Length)
					{
						logger.setState("OBK config write success!", Color.Green);
						logger.setLogProgress("OBK config write success!" + Environment.NewLine, Color.Green);
						logger.setProgress(1, 1);
					}
					else
					{
						logger.setState("OBK config write error!", Color.Red);
						logger.setLogProgress("OBK config write error!" + Environment.NewLine, Color.Red);
					}
				}
				else
				{
					addLog("NOTE: the OBK config writing is disabled, so not writing anything extra." + Environment.NewLine);
				}
			}
			catch(Exception ex)
			{
				addErrorLine(ex.Message);
			}
			finally
			{
				xm.PacketSent -= Xm_PacketSent;
				if(!isCancelled) SetBaud(115200, true);
			}
		}

		private static string GetSha256(byte[] data)
		{
			using(var sha256 = SHA256.Create())
				return HashToStr(sha256.ComputeHash(data));
		}

		private static bool SecbootRemainderMatches(byte[] flash, byte[] secboot)
		{
			if(flash == null || flash.Length < SecbootOffset + SecbootSize ||
				secboot == null || secboot.Length != SecbootSize)
				return false;
			for(int flashOffset = SecbootOffset + HelperSectorSize;
				flashOffset < SecbootOffset + SecbootSize; flashOffset++)
			{
				if(flash[flashOffset] != secboot[flashOffset - SecbootOffset])
					return false;
			}
			return true;
		}

		private void ResetXmodemForRecovery()
		{
			if(xm != null)
				xm.PacketSent -= Xm_PacketSent;
			xm = new XMODEM(serial, XMODEM.Variants.XModem1K, 0xFF)
			{
				SendInactivityTimeoutMillisec = 5000,
				MaxSenderRetries = 5
			};
			xm.PacketSent += Xm_PacketSent;
		}

		private bool WaitForRomAtCurrentBaud(int timeoutMs, string phase)
		{
			Stopwatch sw = Stopwatch.StartNew();
			int consecutiveC = 0;
			int totalC = 0;
			int totalP = 0;
			int totalOther = 0;
			addLogLine("Waiting for Mask ROM...");
			while(sw.ElapsedMilliseconds < timeoutMs)
			{
				if(serial.BytesToRead > 0)
				{
					int value = serial.ReadByte();
					if(value == 'C')
					{
						totalC++;
						consecutiveC++;
						if(consecutiveC >= 4)
						{
							addLogLine("Mask ROM ready.");
							return true;
						}
					}
					else if(value == 'P')
					{
						totalP++;
					}
					else
					{
						totalOther++;
						consecutiveC = 0;
					}
				}
				else
				{
					Thread.Sleep(5);
				}
			}
			addLogLine($"Mask ROM not detected within {timeoutMs} ms " +
				$"({phase}, {serial.BaudRate} baud; C={totalC}, P={totalP}, other={totalOther}).");
			return false;
		}

		private bool RestoreSecboot(SecbootProfile profile, bool helperMayBeInstalled,
			bool helperKnownResponsive, bool isUpdate = false)
		{
			try
			{
				if(serial == null || !serial.IsOpen)
				{
					addErrorLine("Cannot restore W600 secboot because the COM port is closed.");
					return false;
				}

				bool romReady = false;
				if(helperKnownResponsive)
				{
					serial.BaudRate = baudrate;
					serial.DiscardInBuffer();
					ushort sequence = 0x7F00;
					if(TryHelperCommand(ProtocolReturnToRom, sequence,
						FlashBase + SecbootOffset, 0, 3000, true, out _, out string error))
					{
						addLogLine("SRAM loader exiting...");
						serial.BaudRate = 115200;
						serial.DiscardInBuffer();
						romReady = WaitForRomAtCurrentBaud(RomEntryTimeoutMs, "after temporary-reader erase");
					}
					else
					{
						addLogLine("The SRAM reader did not acknowledge the return-to-ROM command (" + error + "); probing recovery paths.");
					}
				}

				if(!romReady && helperMayBeInstalled)
				{
					serial.BaudRate = baudrate;
					serial.DiscardInBuffer();
					if(WaitForRomAtCurrentBaud(1500, "recovery probe at selected baud"))
					{
						romReady = true;
					}
					else
					{
						// A previous recovery attempt may already have erased the helper
						// and reset UART0 to the Mask-ROM default baud.
						if(baudrate != 115200)
						{
							serial.BaudRate = 115200;
							serial.DiscardInBuffer();
							romReady = WaitForRomAtCurrentBaud(1500, "recovery probe at ROM default baud");
						}
						if(romReady)
						{
							// Continue below with the ROM/XMODEM restoration path.
						}
						else
						{
							serial.BaudRate = baudrate;
							serial.DiscardInBuffer();
							if(!WaitForHelper(HelperEntryTimeoutMs, true, out _))
							{
								serial.BaudRate = 115200;
								serial.DiscardInBuffer();
								if(!WaitForRomAtCurrentBaud(1500, "recovery fallback after reader timeout"))
								{
									addErrorLine("Temporary reader and Mask ROM were not reachable for secboot recovery. Press and release RESET, then retry recovery.");
									return false;
								}
								romReady = true;
							}
							else
							{
								ushort sequence = 0x7F00;
								addLogLine("Temporary SRAM reader responded; requesting bounded sector erase and watchdog return to Mask ROM.");
								if(!TryHelperCommand(ProtocolReturnToRom, sequence,
									FlashBase + SecbootOffset, 0, 3000, true, out _, out string error))
								{
									addErrorLine("Temporary reader could not return to Mask ROM: " + error);
									return false;
								}
								addLogLine("SRAM reader acknowledged return-to-ROM command; switching host UART to 115200.");
								serial.BaudRate = 115200;
								serial.DiscardInBuffer();
								romReady = WaitForRomAtCurrentBaud(RomEntryTimeoutMs, "after SRAM-reader sector erase");
							}
						}
					}
				}
				if(!romReady && !helperMayBeInstalled)
				{
					serial.BaudRate = baudrate;
					serial.DiscardInBuffer();
					romReady = WaitForRomAtCurrentBaud(1500, "recovery at selected baud without confirmed reader");
					if(!romReady)
					{
						serial.BaudRate = 115200;
						serial.DiscardInBuffer();
						romReady = WaitForRomAtCurrentBaud(RomEntryTimeoutMs, "recovery at ROM default baud without confirmed reader");
					}
				}

				if(!romReady)
				{
					addErrorLine("W600 Mask ROM did not synchronize for secboot restoration.");
					return false;
				}

				ResetXmodemForRecovery();
				byte[] restorePayload = new byte[SecbootSize - FlsHeaderSize];
				Array.Copy(profile.Image, restorePayload, restorePayload.Length);
				byte[] restoreFls = GenerateW600PseudoFLSFromData(restorePayload,
					(int)(FlashBase + SecbootOffset));
				if(restoreFls.Length != SecbootSize)
					throw new InvalidDataException("W600 secboot recovery transfer is not exactly 56 KiB.");

				if(!SetBaud(baudrate))
					return false;
				addLogLine(isUpdate
					? $"Updating secboot to {profile.Version}..."
					: $"Restoring {profile.Version} secboot...");
				lastXmodemProgressText = null;
				int restoreSent;
				restoreSent = xm.Send(restoreFls, SecbootOffset);
				if(lastXmodemProgressText != null)
					logger.setLogProgress(lastXmodemProgressText + Environment.NewLine,
						restoreSent == restoreFls.Length ? Color.Green : Color.Red);
				if(restoreSent != restoreFls.Length)
					return false;

				serial.BaudRate = 115200;
				logger.setLogProgress((isUpdate
					? "Read complete."
					: $"W600 {profile.Version} secboot restored.") + Environment.NewLine, Color.Green);
				return true;
			}
			catch(Exception ex)
			{
				addErrorLine("W600 secboot restoration failed: " + ex.Message);
				return false;
			}
		}

		private static bool TryCalculateHelperBaudRegister(int baud, out uint value)
		{
			value = 0;
			if(baud != 115200 && baud != 230400 && baud != 460800 &&
				baud != 921600 && baud != 1500000 && baud != 2000000)
				return false;

			ulong divisor = (ulong)(uint)baud * 16u;
			ulong integerPart = HelperApbClock / divisor;
			if(integerPart == 0)
				return false;
			ulong remainder = HelperApbClock % divisor;
			ulong fraction = (remainder * 16u) / divisor;
			value = (uint)((integerPart - 1u) | (fraction << 16));
			return true;
		}

		private byte[] BuildHelperPayload(uint helperBaudRegister)
		{
			byte[] ramLoader = FLoaders.GetRawBinaryFromAssembly("W600_Stub");
			if(ramLoader == null || ramLoader.Length == 0 || HelperPayloadOffset + ramLoader.Length > HelperTransferPayloadSize)
				throw new InvalidDataException("The embedded W600 UART reader has an unexpected size.");
			if(ramLoader.Length < ReaderProtocolMarker.Length)
				throw new InvalidDataException("The embedded W600 UART reader has no W600RD30 build marker. Build and install the reader before building OpenIOTflasher.");
			for(int i = 0; i < ReaderProtocolMarker.Length; i++)
			{
				if(ramLoader[ramLoader.Length - ReaderProtocolMarker.Length + i] != ReaderProtocolMarker[i])
					throw new InvalidDataException("The embedded W600 UART reader is not W600RD30. Build and install the reader before building OpenIOTflasher.");
			}

			byte[] sector = new byte[HelperTransferPayloadSize];
			for(int i = 0; i < sector.Length; i++)
				sector[i] = 0xFF;
			for(int i = 0; i < 56; i++)
				sector[i] = 0;

			// W600 secboot vector table. The reset handler reproduces V3.13's clock
			// transition while executing from flash, then copies the reader to SRAM.
			StoreUInt32(sector, 0x100, 0x20038000);
			StoreUInt32(sector, 0x104, HelperResetAddress);
			byte[] copyStub = new byte[]
			{
				0x72, 0xB6,             // cpsid i
				0x0B, 0x4C,             // ldr r4, [pc, #44] ; clock-divider register
				0x23, 0x68,             // ldr r3, [r4]
				0x0B, 0x4A,             // ldr r2, [pc, #44] ; divider-clear mask
				0x13, 0x40,             // ands r3, r2
				0x23, 0x60,             // str r3, [r4]
				0x23, 0x68,             // ldr r3, [r4]
				0x0A, 0x4A,             // ldr r2, [pc, #40] ; final divider bits
				0x13, 0x43,             // orrs r3, r2
				0x23, 0x60,             // str r3, [r4]
				0x09, 0x48,             // ldr r0, [pc, #36] ; source
				0x0A, 0x49,             // ldr r1, [pc, #40] ; destination
				0x0A, 0x4A,             // ldr r2, [pc, #40] ; length
				0x03, 0x78,             // ldrb r3, [r0]
				0x0B, 0x70,             // strb r3, [r1]
				0x01, 0x30,             // adds r0, #1
				0x01, 0x31,             // adds r1, #1
				0x01, 0x3A,             // subs r2, #1
				0xF9, 0xD1,             // bne copy loop
				0x08, 0x48,             // ldr r0, [pc, #32] ; selected baud divisor argument
				0x08, 0x49,             // ldr r1, [pc, #32] ; SRAM Thumb entry
				0x08, 0x47,             // bx r1
				0x00, 0xBF,             // nop
				0x00, 0xBF,             // nop; align literal pool
			};
			Array.Copy(copyStub, 0, sector, 0x140, copyStub.Length);
			StoreUInt32(sector, 0x170, 0x40000710u);
			StoreUInt32(sector, 0x174, 0xFFFFF0F0u);
			StoreUInt32(sector, 0x178, 0x80000212u);
			StoreUInt32(sector, 0x17C, HelperPayloadAddress);
			StoreUInt32(sector, 0x180, HelperRamAddress);
			StoreUInt32(sector, 0x184, (uint)ramLoader.Length);
			StoreUInt32(sector, 0x188, helperBaudRegister);
			StoreUInt32(sector, 0x18C, HelperRamAddress | 1u);
			Array.Copy(ramLoader, 0, sector, HelperPayloadOffset, ramLoader.Length);

			int runLength = 0xA0 + ramLoader.Length;
			StoreUInt32(sector, 0x00, 0xA0FFFF9F);
			StoreUInt16(sector, 0x04, 2); // secboot image
			StoreUInt16(sector, 0x06, 0); // uncompressed
			StoreUInt32(sector, 0x08, HelperVectorAddress & 0x00FFFFFFu);
			StoreUInt32(sector, 0x0C, (uint)runLength);
			StoreUInt32(sector, 0x10,
				CRC.crc32_ver2(0xFFFFFFFF, sector, runLength, 0x100));
			byte[] version = System.Text.Encoding.ASCII.GetBytes("W600-UART-RD");
			Array.Copy(version, 0, sector, 0x24, version.Length);
			StoreUInt32(sector, 0x34, CRC.crc32_ver2(0xFFFFFFFF, sector, 0x34));
			return sector;
		}

		private bool WaitForHelper(int timeoutMs, bool ignoreCancellation, out int chunkSize)
		{
			chunkSize = 0;
			ushort sequence = 1;
			int failedAttempts = 0;
			ushort lastFailedSequence = 0;
			string lastFailure = null;
			Stopwatch total = Stopwatch.StartNew();
			long nextProgress = 5000;
			while(total.ElapsedMilliseconds < timeoutMs && (ignoreCancellation || !isCancelled))
			{
				ushort requestSequence = sequence++;
				bool commandOk = TryHelperCommand(ProtocolInfo, requestSequence, 0, 0, 750,
					ignoreCancellation, out byte[] payload, out string error);
				if(!commandOk)
				{
					failedAttempts++;
					lastFailedSequence = requestSequence;
					lastFailure = error;
				}
				else if(payload.Length != 16)
				{
					failedAttempts++;
					lastFailedSequence = requestSequence;
					lastFailure = $"unexpected INFO payload length {payload.Length}/16 bytes";
				}
				else
				{
					uint reportedBase = ReadUInt32(payload, 0);
					uint reportedSize = ReadUInt32(payload, 4);
					int reportedChunkSize = ReadUInt16(payload, 8);
					uint reportedSectorSize = ReadUInt32(payload, 12);
					if(reportedBase == FlashBase && reportedSize == (uint)FlashSize &&
						reportedSectorSize == 0x00020000u && reportedChunkSize > 0 &&
						reportedChunkSize <= HelperMaxReadLength)
					{
						chunkSize = reportedChunkSize;
						logger.setLogProgress("W600 UART loader ready." + Environment.NewLine, Color.Green);
						return true;
					}
					failedAttempts++;
					lastFailedSequence = requestSequence;
					lastFailure = $"unexpected INFO geometry base=0x{reportedBase:X8}, size=0x{reportedSize:X}, " +
						$"chunk={reportedChunkSize}, sector=0x{reportedSectorSize:X}";
				}
				if(total.ElapsedMilliseconds >= nextProgress)
				{
					string failureSummary = lastFailure != null
						? $" Last INFO sequence {lastFailedSequence} failed: {lastFailure}; {failedAttempts} failed attempt(s)."
						: string.Empty;
					logger.setLogProgress($"Waiting for W600 UART loader ({total.ElapsedMilliseconds / 1000}s)...{failureSummary}", Color.DarkOrange);
					nextProgress = total.ElapsedMilliseconds + 5000;
				}
				Thread.Sleep(250);
			}
			return false;
		}

		private byte[] ReadHelperRange(uint address, int length, int chunkSize)
		{
			byte[] result = new byte[length];
			int completed = 0;
			string readProgressText = null;
			ushort sequence = 0x100;
			while(completed < length && !isCancelled)
			{
				int requestLength = Math.Min(chunkSize, length - completed);
				uint rangeStart = address + (uint)completed;
				readProgressText = $"Reading 0x{rangeStart - FlashBase:X6}...";
				logger.setLogProgress(readProgressText, Color.Black);
				byte[] payload = null;
				string lastError = "no response";
				bool ok = false;
				for(int attempt = 1; attempt <= 3 && !isCancelled; attempt++)
				{
					ok = TryHelperCommand(ProtocolRead, sequence, address + (uint)completed,
						(ushort)requestLength, CalculateReadResponseTimeoutMs(requestLength), false,
						out payload, out lastError);
					if(ok)
						break;
					addLogLine($"W600 UART read attempt {attempt}/3 failed at " +
						$"0x{address + (uint)completed:X8}: {lastError}");
					if(attempt < 3)
						DrainHelperResponseTail(CalculateReadResponseTimeoutMs(requestLength));
				}
				if(!ok)
				{
					logger.setLogProgress(
						$"W600 UART read failed at 0x{address + (uint)completed:X8}: {lastError}" + Environment.NewLine,
						Color.Red);
					return null;
				}
				Array.Copy(payload, 0, result, completed, payload.Length);
				completed += payload.Length;
				sequence++;
				logger.setProgress(completed, length);
			}
			if(isCancelled)
			{
				addLogLine("W600 flash read cancelled.");
				return null;
			}
			if(readProgressText != null)
				logger.setLogProgress(readProgressText + Environment.NewLine, Color.Green);
			return result;
		}

		private void LogHelperFlashInfo()
		{
			if(!TryHelperCommand(ProtocolFlashId, 0xFFE0, 0, 0, 750, false,
				out byte[] payload, out string error))
			{
				addWarningLine("W600 flash JEDEC ID unavailable: " + error + ".");
				return;
			}
			if(payload.Length != 4)
			{
				addWarningLine($"W600 flash JEDEC ID returned {payload.Length}/4 bytes.");
				return;
			}
			int jedecId = (int)(ReadUInt32(payload, 0) & 0x00FFFFFFu);
			if(jedecId == 0 || jedecId == 0x00FFFFFF)
			{
				addWarningLine($"W600 flash returned invalid JEDEC ID 0x{jedecId:X6}.");
				return;
			}

			int capacityExponent = (jedecId >> 16) & 0xff;
			int capacity = capacityExponent >= 10 && capacityExponent < 31
				? 1 << capacityExponent
				: 0;
			SPIFlashInfo flashInfo = SPIFlashInfoList.Singleton.findFlashForMID(jedecId);
			if(flashInfo != null)
			{
				int reportedSize = capacity != 0 ? capacity : flashInfo.szMem;
				addSuccess(FormatFlashInfo(jedecId, flashInfo.manufacturer, reportedSize) + Environment.NewLine);
				if(capacity != 0 && flashInfo.szMem != capacity)
				{
					addWarningLine("JEDEC flash size differs from flash list size: JEDEC=" +
						capacity.ToString("X") + ", list=" + flashInfo.szMem.ToString("X") + ".");
				}
			}
			else if(capacity != 0)
			{
				addWarningLine(FormatFlashInfo(jedecId, "unknown", capacity));
			}
			else
			{
				string jedecDisplay = string.Format("{0:X2}-{1:X2}-{2:X2}",
					jedecId & 0xff, (jedecId >> 8) & 0xff, (jedecId >> 16) & 0xff);
				addWarningLine("Flash info: JEDEC ID " + jedecDisplay +
					", Manufacturer unknown, size unknown.");
			}
			if(capacity != 0 && capacity != FlashSize)
			{
				addWarningLine($"W600 backup remains limited to 1 MiB; JEDEC reports 0x{capacity:X} bytes.");
			}
		}

		private int CalculateReadResponseTimeoutMs(int payloadLength)
		{
			long framedBytes = ProtocolResponseSize + (long)payloadLength;
			long wireMilliseconds = (framedBytes * 10L * 1000L + baudrate - 1L) / baudrate;
			long preparationMilliseconds = ((payloadLength + 1023L) / 1024L) *
				ReadPreparationPerKiBMs;
			return (int)Math.Max(MinimumReadResponseTimeoutMs,
				wireMilliseconds + preparationMilliseconds + ReadResponseTimeoutMarginMs);
		}

		private void DrainHelperResponseTail(int timeoutMs)
		{
			Stopwatch total = Stopwatch.StartNew();
			long idleSince = -1;
			while(total.ElapsedMilliseconds < timeoutMs && !isCancelled)
			{
				if(serial.BytesToRead > 0)
				{
					serial.DiscardInBuffer();
					idleSince = -1;
					continue;
				}
				if(idleSince < 0)
					idleSince = total.ElapsedMilliseconds;
				else if(total.ElapsedMilliseconds - idleSince >= RetryReceiveIdleMs)
					return;
				Thread.Sleep(1);
			}
		}

		private bool TryHelperCommand(byte opcode, ushort sequence, uint address,
			ushort length, int timeoutMs, bool ignoreCancellation,
			out byte[] payload, out string error)
		{
			payload = null;
			error = "response timeout";
			byte[] request = new byte[ProtocolRequestSize];
			request[0] = (byte)'W'; request[1] = (byte)'6';
			request[2] = (byte)'R'; request[3] = (byte)'Q';
			request[4] = ProtocolVersion;
			request[5] = opcode;
			StoreUInt16(request, 6, sequence);
			StoreUInt32(request, 8, address);
			StoreUInt16(request, 12, length);
			StoreUInt16(request, 14, CRC16.Compute(CRC16Type.CCITT_FALSE, request, 0, 14));

			byte[] header = new byte[ProtocolResponseSize];
			serial.DiscardInBuffer();
			serial.Write(request, 0, request.Length);
			Stopwatch sw = Stopwatch.StartNew();
			byte[] magic = new byte[] { (byte)'W', (byte)'6', (byte)'R', (byte)'S' };
			int matched = 0;
			while(sw.ElapsedMilliseconds < timeoutMs &&
				(ignoreCancellation || !isCancelled) && matched < magic.Length)
			{
				if(!TryReadSerialByte(sw, timeoutMs, ignoreCancellation, out byte value))
					break;
				if(value == magic[matched])
				{
					header[matched++] = value;
				}
				else
				{
					matched = value == magic[0] ? 1 : 0;
					if(matched == 1)
						header[0] = value;
				}
			}
			if(matched != magic.Length)
				return false;
			if(!ReadSerialExact(header, 4, header.Length - 4, sw, timeoutMs,
				ignoreCancellation, out int headerBytesRead))
			{
				error = $"response-header timeout after {headerBytesRead}/{header.Length - 4} bytes";
				return false;
			}
			if(CRC16.Compute(CRC16Type.CCITT_FALSE, header, 0, 18) != ReadUInt16(header, 18))
			{
				error = "response-header CRC mismatch";
				return false;
			}
			if(header[4] != ProtocolVersion)
			{
				error = "response version mismatch";
				return false;
			}
			if(ReadUInt16(header, 6) != sequence)
			{
				error = "response sequence mismatch";
				return false;
			}
			if(header[5] != 0)
			{
				error = "reader status " + header[5];
				return false;
			}
			ushort responseLength = ReadUInt16(header, 12);
			if(responseLength > HelperMaxReadLength || (opcode == ProtocolRead &&
				(responseLength != length || ReadUInt32(header, 8) != address)))
			{
				error = "response range mismatch";
				return false;
			}
			byte[] receivedPayload = new byte[responseLength];
			if(!ReadSerialExact(receivedPayload, 0, receivedPayload.Length, sw, timeoutMs,
				ignoreCancellation, out int payloadBytesRead))
			{
				error = $"response payload timeout after {payloadBytesRead}/{receivedPayload.Length} bytes";
				return false;
			}
			uint expectedPayloadCrc = ReadUInt32(header, 14);
			uint actualPayloadCrc = ~CRC.crc32_ver2(0xFFFFFFFF, receivedPayload);
			if(expectedPayloadCrc != actualPayloadCrc)
			{
				error = "response payload CRC mismatch";
				return false;
			}
			payload = receivedPayload;
			return true;
		}

		private bool TryReadSerialByte(Stopwatch sw, int timeoutMs, bool ignoreCancellation,
			out byte value)
		{
			while(sw.ElapsedMilliseconds < timeoutMs && (ignoreCancellation || !isCancelled))
			{
				if(serial.BytesToRead > 0)
				{
					value = (byte)serial.ReadByte();
					return true;
				}
				Thread.Sleep(1);
			}
			value = 0;
			return false;
		}

		private bool ReadSerialExact(byte[] destination, int offset, int length,
			Stopwatch sw, int timeoutMs, bool ignoreCancellation, out int bytesRead)
		{
			bytesRead = 0;
			while(bytesRead < length && sw.ElapsedMilliseconds < timeoutMs &&
				(ignoreCancellation || !isCancelled))
			{
				int available = serial.BytesToRead;
				if(available <= 0)
				{
					Thread.Sleep(1);
					continue;
				}
				int requested = Math.Min(available, length - bytesRead);
				int received = serial.Read(destination, offset + bytesRead, requested);
				if(received > 0)
					bytesRead += received;
			}
			return bytesRead == length;
		}

		private static ushort ReadUInt16(byte[] data, int offset)
		{
			return (ushort)(data[offset] | (data[offset + 1] << 8));
		}

		private static uint ReadUInt32(byte[] data, int offset)
		{
			return (uint)(data[offset] | (data[offset + 1] << 8) |
				(data[offset + 2] << 16) | (data[offset + 3] << 24));
		}

		private static void StoreUInt16(byte[] data, int offset, ushort value)
		{
			data[offset] = (byte)value;
			data[offset + 1] = (byte)(value >> 8);
		}

		private static void StoreUInt32(byte[] data, int offset, uint value)
		{
			data[offset] = (byte)value;
			data[offset + 1] = (byte)(value >> 8);
			data[offset + 2] = (byte)(value >> 16);
			data[offset + 3] = (byte)(value >> 24);
		}

		private byte[] GenerateW600PseudoFLSFromData(byte[] data, int startAddr)
		{
			var crc = CRC.crc32_ver2(0xFFFFFFFF, data);
			var fls = new List<byte>()
			{
				0x9F, 0xFF, 0xFF, 0xA0,
				0x00, 0x02, 0x00, 0x00,
				(byte)(startAddr & 0xFF), (byte)((startAddr >> 8) & 0xFF),
				(byte)((startAddr >> 16) & 0xFF), (byte)((startAddr >> 24) & 0xFF),
				(byte)(data.Length & 0xFF), (byte)((data.Length >> 8) & 0xFF),
				(byte)((data.Length >> 16) & 0xFF), (byte)((data.Length >> 24) & 0xFF),
				(byte)(crc & 0xFF), (byte)((crc >> 8) & 0xFF),
				(byte)((crc >> 16) & 0xFF), (byte)((crc >> 24) & 0xFF),
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x31, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
			};
			var headerCrc = CRC.crc32_ver2(0xFFFFFFFF, fls.ToArray());
			fls.Add((byte)(headerCrc & 0xFF));
			fls.Add((byte)((headerCrc >> 8) & 0xFF));
			fls.Add((byte)((headerCrc >> 16) & 0xFF));
			fls.Add((byte)((headerCrc >> 24) & 0xFF));
			fls.AddRange(data);
			return fls.ToArray();
		}
	}
}
