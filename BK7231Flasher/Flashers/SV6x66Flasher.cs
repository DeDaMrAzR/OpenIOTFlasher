using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace BK7231Flasher
{
	public sealed class SV6x66Flasher : BaseFlasher
	{
		const int FactoryBaud = 9600;
		const int FastBaud = 2000000;
		const int FlashBytes = 0x200000;
		const int BlockBytes = 0x10000;
		const int SectorBytes = 0x1000;
		const int ChunkBytes = 0x1000;
		const int OtaPayloadBytes = 508;
		const byte Ack = 0x06;
		const string StockVersion = "FWSWES-SV04MATE-3.7.1";
		const string LoaderSha256 = "5f8c9d2c350ac684ac33d06b9ddca408fae25ec106ab8e9349fe9da1d92ce18e";
		const string AgentEnvelopeSha256 = "94cb70e9f77df2048c302438e1874250469b37906a150614c760ea85130c9f65";

		MemoryStream readResult;
		public bool LastOperationSucceeded { get; private set; }

		public SV6x66Flasher(CancellationToken cancellationToken) : base(cancellationToken)
		{
		}

		static uint ReadLe32(byte[] data, int offset)
		{
			return (uint)(data[offset] | data[offset + 1] << 8 |
				data[offset + 2] << 16 | data[offset + 3] << 24);
		}

		static ushort ReadLe16(byte[] data, int offset)
		{
			return (ushort)(data[offset] | data[offset + 1] << 8);
		}

		static void WriteLe32(byte[] data, int offset, uint value)
		{
			data[offset] = (byte)value;
			data[offset + 1] = (byte)(value >> 8);
			data[offset + 2] = (byte)(value >> 16);
			data[offset + 3] = (byte)(value >> 24);
		}

		static uint Crc32(byte[] data, int offset, int length, uint initial = 0)
		{
			uint crc = initial ^ 0xFFFFFFFFu;
			for (int i = 0; i < length; i++) {
				crc ^= data[offset + i];
				for (int bit = 0; bit < 8; bit++)
					crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1u));
			}
			return crc ^ 0xFFFFFFFFu;
		}

		static byte Crc8(byte[] data, int offset, int length)
		{
			byte value = 0;
			for (int i = 0; i < length; i++) {
				value ^= data[offset + i];
				for (int bit = 0; bit < 8; bit++)
					value = (byte)((value & 0x80) != 0 ? (value << 1) ^ 0x07 : value << 1);
			}
			return value;
		}

		static string HexDigest(HashAlgorithm hash, byte[] data)
		{
			return string.Concat(hash.ComputeHash(data).Select(value => value.ToString("x2")));
		}

		void OpenPort()
		{
			closePort();
			serial = new SerialPort(serialName, FactoryBaud, Parity.None, 8, StopBits.One) {
				ReadTimeout = 50,
				WriteTimeout = 5000,
				DtrEnable = false,
				RtsEnable = false,
				Handshake = Handshake.None,
			};
			serial.Open();
		}

		void CheckCancelled()
		{
			if (isCancelled || cancellationToken.IsCancellationRequested)
				throw new OperationCanceledException();
		}

		void WriteAll(byte[] data)
		{
			CheckCancelled();
			serial.Write(data, 0, data.Length);
		}

		void WriteText(string text)
		{
			WriteAll(Encoding.ASCII.GetBytes(text));
		}

		byte[] ReadExact(int count, int timeoutMs, string label)
		{
			byte[] result = new byte[count];
			int offset = 0;
			Stopwatch timer = Stopwatch.StartNew();
			while (offset < count && timer.ElapsedMilliseconds < timeoutMs) {
				CheckCancelled();
				try
				{
					offset += serial.Read(result, offset, count - offset);
				}
				catch (TimeoutException)
				{
				}
			}
			if (offset != count)
				throw new IOException($"{label} timeout after {offset}/{count} bytes");
			return result;
		}

		byte[] ReadUntil(byte[] needle, int timeoutMs, string label, int maximumBytes = 4096)
		{
			List<byte> result = new List<byte>();
			Stopwatch timer = Stopwatch.StartNew();
			while (timer.ElapsedMilliseconds < timeoutMs) {
				CheckCancelled();
				try
				{
					int value = serial.ReadByte();
					if (value >= 0)
						result.Add((byte)value);
				}
				catch (TimeoutException)
				{
				}
				if (result.Count > maximumBytes)
					throw new IOException(label + " exceeded the bounded response length");
				if (result.Count >= needle.Length) {
					bool match = true;
					for (int i = 0; i < needle.Length; i++)
						match &= result[result.Count - needle.Length + i] == needle[i];
					if (match)
						return result.ToArray();
				}
			}
			throw new IOException($"{label} timeout");
		}

		string ReadLine(int timeoutMs, string label)
		{
			return Encoding.ASCII.GetString(ReadUntil(new byte[] { 13, 10 }, timeoutMs, label));
		}

		void EnterFactory()
		{
			serial.BaudRate = FactoryBaud;
			serial.DiscardInBuffer();
			logger.setState("Waiting for CKW04 reset", Color.DarkOrange);
			addLogLine("Manually reset the SV6x66 board now; waiting for factory mode.\n");
			ReadUntil(Encoding.ASCII.GetBytes("factory?\r\n"), 60000, "factory prompt");
			WriteText("factory!\r\n");
			ReadUntil(Encoding.ASCII.GetBytes("factory mode\r\n"), 4000, "factory handshake");
			addSuccess("Connection established." + Environment.NewLine);
			WriteText("AT+VER\r\n");
			string version = ReadLine(2500, "AT+VER");
			if (!version.Contains(StockVersion))
				throw new IOException("Unsupported stock firmware: " + version.Trim());
			addSuccess("SV6x66 stock firmware: " + version.Trim() + Environment.NewLine);
		}

		void SwitchToFastBaud()
		{
			WriteText("AT+BAUD=" + FastBaud + "\r\n");
			ReadUntil(Encoding.ASCII.GetBytes("OK\r\n"), 2500, "AT+BAUD");
			serial.BaudRate = FastBaud;
			serial.DiscardInBuffer();
			Thread.Sleep(250);
			WriteText("AT+VER\r\n");
			string version = ReadLine(2500, "AT+VER after baud change");
			if (!version.Contains(StockVersion))
				throw new IOException("2 Mbaud verification failed");
			addLogLine("Factory UART switched to 2000000 baud.");
		}

		static byte[] BuildOtaStream(byte[] loader)
		{
			byte[] header = new byte[0x54];
			WriteLe32(header, 0, 0x73737600u);
			WriteLe32(header, 8, (uint)loader.Length);
			WriteLe32(header, 12, 1u);
			using (MD5 md5 = MD5.Create()) {
				Encoding.ASCII.GetBytes(HexDigest(md5, Array.Empty<byte>())).CopyTo(header, 20);
			}
			using (MD5 md5 = MD5.Create()) {
				Encoding.ASCII.GetBytes(HexDigest(md5, loader)).CopyTo(header, 52);
			}
			byte[] stream = new byte[header.Length + loader.Length];
			Buffer.BlockCopy(header, 0, stream, 0, header.Length);
			Buffer.BlockCopy(loader, 0, stream, header.Length, loader.Length);
			return stream;
		}

		void StageLoader(byte[] loader)
		{
			if (loader.Length == 0 || loader.Length > 0x9B000 ||
				loader[0] != 0x48 || loader[1] != 0 || loader[2] != 0 || loader[3] != 0x02)
				throw new InvalidDataException("Embedded SV6x66 loader failed its artifact gate");
			byte[] stream = BuildOtaStream(loader);
			logger.setState("Staging CKW04 loader", Color.DarkOrange);
			WriteText("AT+UPGRADE_START\r\n");
			addWarningLine("Stock OTA is erasing and programming inactive Bank B.");
			ReadUntil(Encoding.ASCII.GetBytes("OK\r\n"), 35000, "AT+UPGRADE_START");
			addLogLine("Uploading loader...");
			int sequence = 0;
			for (int offset = 0; offset < stream.Length; offset += OtaPayloadBytes, sequence++) {
				int length = Math.Min(OtaPayloadBytes, stream.Length - offset);
				byte[] packet = new byte[4 + length];
				packet[0] = 0x55;
				packet[1] = 0xAA;
				packet[2] = (byte)(sequence >> 8);
				packet[3] = (byte)sequence;
				Buffer.BlockCopy(stream, offset, packet, 4, length);
				WriteAll(packet);
				byte[] reply = ReadExact(5, 6000, "OTA frame ACK");
				if (reply[0] != 0x55 || reply[1] != 0xAA || reply[2] != packet[2] ||
					reply[3] != packet[3] || reply[4] != Crc8(stream, offset, length))
					throw new IOException("OTA frame acknowledgement mismatch at frame " + sequence);
				logger.setProgress(Math.Min(offset + length, stream.Length), stream.Length);
			}
			string emptyMd5;
			string imageMd5;
			using (MD5 md5 = MD5.Create())
				emptyMd5 = HexDigest(md5, Array.Empty<byte>());
			using (MD5 md5 = MD5.Create())
				imageMd5 = HexDigest(md5, loader);
			WriteText("AT+VERIFT=" + emptyMd5 + imageMd5 + "\r\n");
			ReadUntil(Encoding.ASCII.GetBytes("OK\r\n"), 4000, "AT+VERIFT");
			WriteText("AT+UPGRADE_END\r\n");
			ReadUntil(Encoding.ASCII.GetBytes("OK\r\n"), 35000, "AT+UPGRADE_END");
			serial.BaudRate = FastBaud;
			addLogLine("SV6x66 loader staged and activated.");
		}

		void StartLoader(out byte[] loader, out byte[] stage)
		{
			loader = FLoaders.GetBinaryFromAssembly("SV6x66_Loader");
			stage = FLoaders.GetBinaryFromAssembly("SV6x66_FlashAgent");
			using (SHA256 sha = SHA256.Create()) {
				if (HexDigest(sha, loader) != LoaderSha256)
					throw new InvalidDataException("Embedded SV6x66 loader SHA-256 mismatch");
			}
			using (SHA256 sha = SHA256.Create()) {
				if (HexDigest(sha, stage) != AgentEnvelopeSha256)
					throw new InvalidDataException("Embedded SV6x66 RAM-agent envelope SHA-256 mismatch");
			}
			OpenPort();
			EnterFactory();
			SwitchToFastBaud();
			StageLoader(loader);
			string banner = ReadLine(120000, "SV6x66 loader banner");
			if (!banner.Contains("SV6LDR02 READY A_ARMED STAGE BAUD=2000000"))
				throw new IOException("Unexpected loader banner: " + banner.Trim());
		}

		byte[] ReadFullFlash()
		{
			addWarningLine("This CKW04 full read erases and replaces inactive Bank B before reading the 2 MiB flash.");
			byte[] loader;
			byte[] stage;
			StartLoader(out loader, out stage);
			string jedec;
			int capacity;
			StageRamAgent(stage, out jedec, out capacity);
			logger.setState("Reading CKW04 flash", Color.Green);
			addLogLine("Reading full 2 MiB flash...");
			WriteText("D");
			string header = ReadLine(10000, "flash read header");
			if (header != "SV6FWR02 READ BYTES=2097152\r\n")
				throw new IOException("Unexpected flash read header");
			byte[] image = new byte[FlashBytes];
			for (int offset = 0; offset < FlashBytes; offset += ChunkBytes) {
				byte[] chunk = ReadExact(ChunkBytes, 10000, "flash data");
				Buffer.BlockCopy(chunk, 0, image, offset, chunk.Length);
				logger.setProgress(offset + chunk.Length, FlashBytes);
			}
			uint wireCrc = ReadLe32(ReadExact(4, 5000, "flash read CRC"), 0);
			string complete = ReadLine(5000, "flash read completion");
			uint imageCrc = Crc32(image, 0, image.Length);
			if (complete != "SV6FWR02 READ COMPLETE\r\n" || wireCrc != imageCrc)
				throw new IOException("Full-flash CRC verification failed");
			addSuccess($"Full flash read passed CRC32 {imageCrc:X8}." + Environment.NewLine);
			return image;
		}

		void StageRamAgent(byte[] envelope, out string jedec, out int capacity)
		{
			if (envelope.Length < 80 || Encoding.ASCII.GetString(envelope, 0, 8) != "SV6STG01")
				throw new InvalidDataException("Embedded RAM-agent envelope is invalid");
			int packedLength = (int)ReadLe32(envelope, 28);
			if (packedLength <= 0 || envelope.Length != 80 + packedLength)
				throw new InvalidDataException("Embedded RAM-agent envelope length is invalid");
			WriteText("S");
			logger.setState("Uploading CKW04 flash agent", Color.DarkOrange);
			addLogLine("Uploading RAM flash agent...");
			string request = ReadLine(5000, "RAM-agent request");
			if (request != "SV6LDR02 STAGE SEND HEADER=80 PACKED_MAX=65536\r\n")
				throw new IOException("Unexpected RAM-agent request: " + request.Trim());
			WriteAll(envelope.Take(80).ToArray());
			if (ReadExact(1, 5000, "RAM-agent header acknowledgement")[0] != Ack)
				throw new IOException("RAM-agent header rejected");
			WriteAll(envelope.Skip(80).ToArray());
			for (int lineCount = 0; lineCount < 32; lineCount++) {
				string line = ReadLine(20000, "RAM-agent validation");
				if (line == "SV6LDR02 STAGE VALIDATED JUMP\r\n")
					goto stageValidated;
				if (line.Contains(" STAGE FAIL_"))
					throw new IOException("RAM-agent rejected: " + line.Trim());
			}
			throw new IOException("RAM-agent validation produced too many status lines");
		stageValidated:
			string info = ReadLine(10000, "RAM-agent information");
			Match match = Regex.Match(info,
				@"^SV6FWR02 INFO JEDEC=([0-9A-F]{6}) FLASH=([0-9]+) SECTOR=4096 BLOCK=65536 XIP=OFF\r\n$");
			if (!match.Success)
				throw new IOException("Invalid RAM-agent information response");
			jedec = match.Groups[1].Value;
			capacity = int.Parse(match.Groups[2].Value);
			if (jedec != "C84015" || capacity != FlashBytes)
				throw new IOException("This release requires CKW04 flash JEDEC C84015 with exactly 2 MiB capacity");
			int flashMid = Convert.ToInt32(jedec.Substring(0, 2), 16)
				| Convert.ToInt32(jedec.Substring(2, 2), 16) << 8
				| Convert.ToInt32(jedec.Substring(4, 2), 16) << 16;
			SPIFlashInfo flashInfo = SPIFlashInfoList.Singleton.findFlashForMID(flashMid);
			if (flashInfo == null)
			{
				addWarningLine("Flash JEDEC ID " + jedec + " is supported but has no chip-name definition.");
			}
			else
			{
				string jedecDisplay = jedec.Substring(0, 2) + "-" + jedec.Substring(2, 2)
					+ "-" + jedec.Substring(4, 2);
				string sizeDisplay = (capacity / (1024 * 1024)) + " MB";
				addSuccess("Flash info: JEDEC ID " + jedecDisplay + ", Manufacturer " + flashInfo.manufacturer
					+ ", chip " + flashInfo.icName + ", size " + sizeDisplay + " (0x"
					+ capacity.ToString("X") + " bytes)." + Environment.NewLine);
			}
		}

		// The full-write protocol is retained for continued R&D, but it has not been
		// validated in this OpenIOT integration and must not be compiled or exposed.
#if false
		static byte[] BuildWriterHeader(byte[] image, uint imageCrc)
		{
			byte[] header = new byte[24];
			Encoding.ASCII.GetBytes("SV6FWR02").CopyTo(header, 0);
			WriteLe32(header, 8, 2);
			WriteLe32(header, 12, (uint)image.Length);
			WriteLe32(header, 16, imageCrc);
			WriteLe32(header, 20, Crc32(header, 0, 20));
			return header;
		}

		byte[] WriteFullFlash(byte[] image)
		{
			if (image == null || image.Length != FlashBytes)
				throw new InvalidDataException("SV6x66 full restore requires an exact 2 MiB BIN");
			byte[] loader;
			byte[] stage;
			StartLoader(out loader, out stage);
			string jedec;
			int capacity;
			StageRamAgent(stage, out jedec, out capacity);
			uint imageCrc = Crc32(image, 0, image.Length);
			WriteText("W");
			WriteAll(BuildWriterHeader(image, imageCrc));
			if (ReadExact(1, 5000, "writer session acknowledgement")[0] != Ack)
				throw new IOException("Writer session rejected");
			addWarningLine("Erasing and writing full flash. Do not remove power.");
			for (int expectedSequence = 0; expectedSequence < FlashBytes / SectorBytes; expectedSequence++) {
				byte[] request = ReadExact(8, 35000, "writer sector request");
				int sequence = ReadLe16(request, 1);
				int address = (int)ReadLe32(request, 3);
				char mode = (char)request[7];
				int expectedAddress;
				char expectedMode;
				if (expectedSequence < (FlashBytes - BlockBytes) / SectorBytes) {
					expectedAddress = BlockBytes + expectedSequence * SectorBytes;
					expectedMode = 'B';
				} else if (expectedSequence < FlashBytes / SectorBytes - 1) {
					expectedAddress = (expectedSequence - ((FlashBytes - BlockBytes) / SectorBytes) + 1) * SectorBytes;
					expectedMode = 'S';
				} else {
					expectedAddress = 0;
					expectedMode = 'S';
				}
				if (request[0] != (byte)'Q' || sequence != expectedSequence ||
					address != expectedAddress || mode != expectedMode)
					throw new IOException("Unsafe writer sector request");
				byte[] payload = new byte[4 + SectorBytes];
				WriteLe32(payload, 0, Crc32(image, address, SectorBytes));
				Buffer.BlockCopy(image, address, payload, 4, SectorBytes);
				WriteAll(payload);
				byte status = ReadExact(1, 35000, "writer sector result")[0];
				if (status != Ack)
					throw new IOException($"Sector 0x{address:X6} failed with status 0x{status:X2}");
				logger.setProgress(expectedSequence + 1, FlashBytes / SectorBytes);
			}
			string crcLine = ReadLine(60000, "whole-flash CRC");
			if (crcLine != $"SV6FWR02 FLASH_CRC={imageCrc:X8} PASS\r\n")
				throw new IOException("Whole-flash CRC failed: " + crcLine.Trim());
			string readbackHeader = ReadLine(10000, "readback header");
			if (readbackHeader != "SV6FWR02 READBACK BYTES=2097152\r\n")
				throw new IOException("Unexpected readback header");
			addLogLine("Verifying full flash by readback...");
			byte[] readback = new byte[FlashBytes];
			for (int offset = 0; offset < FlashBytes; offset += ChunkBytes) {
				byte[] chunk = ReadExact(ChunkBytes, 10000, "full flash readback");
				Buffer.BlockCopy(chunk, 0, readback, offset, chunk.Length);
				logger.setProgress(offset + chunk.Length, FlashBytes);
			}
			uint wireCrc = ReadLe32(ReadExact(4, 5000, "readback CRC"), 0);
			string complete = ReadLine(5000, "writer completion");
			if (complete != "SV6FWR02 COMPLETE XIP=RESTORED\r\n" ||
				wireCrc != imageCrc || Crc32(readback, 0, readback.Length) != imageCrc ||
				!readback.SequenceEqual(image))
				throw new IOException("Full-flash readback verification failed");
			addSuccess($"Full flash write and readback passed CRC32 {imageCrc:X8}." + Environment.NewLine);
			return readback;
		}
#endif

		public override void doRead(int startSector = 0, int sectors = 10, bool fullRead = false)
		{
			LastOperationSucceeded = false;
			readResult?.Dispose();
			readResult = null;
			logger.setState("Starting CKW04 read", Color.DarkOrange);
			try
			{
				if (!fullRead || startSector != 0 || sectors != FlashBytes / SectorBytes) {
					logger.setState("CKW04 read parameters invalid", Color.Red);
					addErrorLine("SV6x66 currently supports complete 2 MiB reads only.");
					return;
				}
				readResult = new MemoryStream(ReadFullFlash(), false);
				LastOperationSucceeded = true;
				logger.setState("CKW04 read complete", Color.DarkGreen);
				addWarningLine("Read complete. Manually reset the CKW04 before the next connection.");
			}
			catch (OperationCanceledException)
			{
				logger.setState("CKW04 read cancelled", Color.Yellow);
				addWarningLine("SV6x66 read cancelled. Manually reset the CKW04 before retrying.");
			}
			catch (Exception exception)
			{
				logger.setState("CKW04 read failed", Color.Red);
				addErrorLine("SV6x66 read failed: " + exception.Message);
			}
			finally
			{
				closePort();
			}
		}

		public override void doReadAndWrite(int startSector, int sectors, string sourceFileName, WriteMode mode)
		{
			LastOperationSucceeded = false;
			addErrorLine("SV6x66 write support is retained as untested source only and is disabled.");
		}

		public override byte[] getReadResult()
		{
			return readResult?.ToArray();
		}

		public override bool saveReadResult(int startOffset)
		{
			if (readResult == null)
				return false;
			try
			{
				string fileName = MiscUtils.formatDateNowFileName("readResult_SV6x66", backupName, "bin");
				string backupDirectory = Path.Combine(Environment.CurrentDirectory, "backups");
				Directory.CreateDirectory(backupDirectory);
				string fullPath = Path.Combine(backupDirectory, fileName);
				File.WriteAllBytes(fullPath, readResult.ToArray());
				logger.setState("CKW04 backup saved", Color.DarkGreen);
				addSuccess("Saved complete SV6x66 flash to " + fullPath + Environment.NewLine);
				return true;
			}
			catch (Exception exception)
			{
				logger.setState("CKW04 backup save failed", Color.Red);
				addErrorLine("Save failed: " + exception.Message);
				return false;
			}
		}

		public override bool doErase(int startSector = 0, int sectors = 10, bool bAll = false)
		{
			addErrorLine("Standalone erase is not supported for CKW04/SV6x66.");
			return false;
		}
	}
}
