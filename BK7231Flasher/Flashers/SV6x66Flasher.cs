using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BK7231Flasher
{
	internal sealed class SvRestoreTransaction
	{
		internal readonly ulong Nonce;
		internal readonly byte[] SourceSha;
		internal uint Attempt { get; private set; }
		internal byte Region { get; private set; }
		internal int Address { get; private set; }
		internal int Length { get; private set; }
		internal uint ExpectedCrc { get; private set; }
		internal bool Pending { get; private set; }
		Result terminal;
		internal sealed class Result
		{
			internal byte State, Status;
			internal uint ProgramCompleted, AckIntent, RangeCrc, Detail, EraseCompleted, Flags;
		}
		internal SvRestoreTransaction(ulong nonce, byte[] sourceSha)
		{
			if (nonce == 0 || sourceSha == null || sourceSha.Length != 32) throw new ArgumentException("Invalid SV6x66 session identity.");
			Nonce = nonce;
			SourceSha = (byte[])sourceSha.Clone();
		}
		static void Put32(byte[] data, int offset, uint value)
		{
			for (int index = 0; index < 4; index++) data[offset + index] = (byte)(value >> (index * 8));
		}
		static uint Get32(byte[] data, int offset)
		{
			return (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);
		}
		void PutNonce(byte[] data, int offset)
		{
			for (int index = 0; index < 8; index++) data[offset + index] = (byte)(Nonce >> (index * 8));
		}
		internal byte[] Control(byte operation)
		{
			if (operation != 1 && (operation < 5 || operation > 7)) throw new ArgumentOutOfRangeException(nameof(operation));
			byte[] data = new byte[operation == 1 ? 41 : 13];
			data[0] = operation;
			PutNonce(data, 1);
			if (operation == 1) Buffer.BlockCopy(SourceSha, 0, data, 9, 32);
			else Put32(data, 9, Attempt);
			return data;
		}
		internal byte[] Prepare(byte region, int address, byte[] image)
		{
			int[] starts = { 0x141000, 0x0A6000, 0x00C000, 0x00B000, 0x005000, 0x001000, 0, 0x004000 };
			int[] lengths = { 191 * 4096, 155 * 4096, 154 * 4096, 4096, 6 * 4096, 3 * 4096, 4096, 4096 };
			if (Pending || region >= 8 || Attempt != region || address != starts[region] || image == null || image.Length != lengths[region])
				throw new InvalidDataException("SV6x66 region differs from the approved schedule or prior region is unresolved.");
			Attempt++;
			Region = region;
			Address = address;
			Length = image.Length;
			ExpectedCrc = CRC.crc32_ver2(0xFFFFFFFF, image) ^ 0xFFFFFFFF;
			Pending = true;
			terminal = null;
			return PreparePayload();
		}
		internal byte[] PreparePayload()
		{
			byte[] data = new byte[26];
			data[0] = 2;
			PutNonce(data, 1);
			Put32(data, 9, Attempt);
			data[13] = Region;
			Put32(data, 14, (uint)Address);
			Put32(data, 18, (uint)Length);
			Put32(data, 22, ExpectedCrc);
			return data;
		}
		internal Result Parse(byte[] data, bool session = false)
		{
			if (data == null || data.Length != 84 || data[0] != 3 || (data[1] != 0 && data[1] != 1 && data[1] != 3 && data[1] != 4))
				throw new InvalidDataException("SV6x66 v3 metadata version/state/length mismatch.");
			ulong nonce = 0;
			for (int index = 0; index < 8; index++) nonce |= (ulong)data[4 + index] << (index * 8);
			if (nonce != Nonce || !data.Skip(12).Take(32).SequenceEqual(SourceSha))
				throw new InvalidDataException("SV6x66 session/source identity changed; automatic rebind is forbidden.");
			Result result = new Result { State = data[1], Status = data[2], ProgramCompleted = Get32(data, 60), AckIntent = Get32(data, 64), RangeCrc = Get32(data, 68), Detail = Get32(data, 72), EraseCompleted = Get32(data, 76), Flags = Get32(data, 80) };
			if (session)
			{
				if (result.State != 0 || result.Status != 0 || data[3] != 255 || data.Skip(44).Any(value => value != 0))
					throw new InvalidDataException("SV6x66 initial v3 session is not idle.");
				return result;
			}
			if (!Pending || data[3] != Region || Get32(data, 44) != Attempt || Get32(data, 48) != Address
				|| Get32(data, 52) != Length || Get32(data, 56) != ExpectedCrc
				|| result.ProgramCompleted > Length || result.ProgramCompleted % 1024 != 0
				|| result.AckIntent > result.ProgramCompleted || result.AckIntent % 1024 != 0 || result.ProgramCompleted - result.AckIntent > 1024
				|| result.EraseCompleted > Length || result.EraseCompleted % 4096 != 0 || (result.Flags & ~3u) != 0
				|| (result.Status != 0 && result.Status != 1 && result.Status != 0x13 && result.Status != 0x14 && result.Status != 0x15)
				|| (result.Flags == 0 && (result.ProgramCompleted != 0 || result.AckIntent != 0 || result.EraseCompleted != 0))
				|| (result.ProgramCompleted != 0 && result.EraseCompleted != Length)
				|| (result.ProgramCompleted < Length && result.RangeCrc != 0)
				|| (result.Status == 0x14 && (result.ProgramCompleted != Length || result.AckIntent != Length || result.EraseCompleted != Length || result.Flags != 1 || result.RangeCrc == ExpectedCrc))
				|| ((result.Flags & 2) != 0 && (result.Flags != 3 || result.Status != 0 || result.ProgramCompleted != Length || result.AckIntent != Length || result.EraseCompleted != Length || result.RangeCrc != ExpectedCrc)))
				throw new InvalidDataException("SV6x66 v3 checkpoint identity or completion counters contradict the region.");
			if (result.State == 0 || (result.State == 1 && (result.Status != 0 || result.ProgramCompleted != 0 || result.AckIntent != 0 || result.EraseCompleted != 0 || result.Flags != 0 || result.RangeCrc != 0)))
				throw new InvalidDataException("SV6x66 prepared region already claims destructive work.");
			if (result.State >= 3)
			{
				if (result.Status == 0 && (result.Flags != 3 || result.ProgramCompleted != Length || result.AckIntent != Length || result.EraseCompleted != Length || result.RangeCrc != ExpectedCrc))
					throw new InvalidDataException("SV6x66 successful terminal result contradicts complete physical range CRC proof.");
				if (result.State == 4 && result.Status != 0) throw new InvalidDataException("SV6x66 unsuccessful region must never be resolved or advanced.");
				if (terminal != null && (terminal.Status != result.Status || terminal.ProgramCompleted != result.ProgramCompleted || terminal.AckIntent != result.AckIntent || terminal.RangeCrc != result.RangeCrc || terminal.Detail != result.Detail || terminal.EraseCompleted != result.EraseCompleted || terminal.Flags != result.Flags))
					throw new InvalidDataException("SV6x66 retained terminal result changed.");
				terminal = result;
			}
			return result;
		}
		internal Result ResolveReadOnly(byte operation, Func<byte[], byte[]> exchange, Action synchronize, Action<byte, Exception> observeRecovery = null)
		{
			if (operation != 6 && operation != 7) throw new ArgumentOutOfRangeException(nameof(operation));
			if (operation == 7 && (terminal == null || terminal.Status != 0)) throw new InvalidDataException("Unknown or failed region cannot be resolved.");
			for (int round = 0; ; round++)
			{
				try
				{
					Result result = Parse(exchange(Control(operation)));
					if ((operation == 6 && result.State < 3) || (operation == 7 && result.State != 4)) throw new InvalidDataException("SV6x66 terminal/resolved state not proven.");
					return result;
				}
				catch (IOException exception) when (round < 2)
				{
					try { observeRecovery?.Invoke(operation, exception); } catch { }
					try { synchronize(); } catch (Exception barrierFailure) { throw new IOException("SV6x66 read-only recovery failed after original I/O error.", new AggregateException(exception, barrierFailure)); }
				}
			}
		}
		internal void Resolved(Result result)
		{
			if (!Pending || result.State != 4 || result.Status != 0) throw new InvalidDataException("SV6x66 region is unresolved.");
			Pending = false;
		}
	}
	internal sealed class SvApplicationImage
	{
		internal const int MaximumCarrierBytes = 12 * 1024;
		internal static bool HasEmbeddedLoaders
		{
			get
			{
				string[] resources = typeof(SvApplicationImage).Assembly.GetManifestResourceNames();
				return resources.Count(name => name.EndsWith(".SV6x66_AppBankA_Carrier.bin", StringComparison.Ordinal)) == 1
					&& resources.Count(name => name.EndsWith(".SV6x66_AppBankB_Carrier.bin", StringComparison.Ordinal)) == 1;
			}
		}
		readonly byte[][] banks;
		readonly byte[][] carriers;
		internal readonly string SourcePath;
		internal readonly string SourceSha256;
		SvApplicationImage(string path, byte[] envelope, byte[][] bankImages, byte[][] bankCarriers)
		{
			SourcePath = path;
			SourceSha256 = Digest(SHA256.Create(), envelope);
			banks = bankImages;
			carriers = bankCarriers;
		}
		internal static string Digest(HashAlgorithm algorithm, byte[] bytes)
		{
			using (algorithm) return string.Concat(algorithm.ComputeHash(bytes).Select(value => value.ToString("x2")));
		}
		static void RequireMagic(byte[] bytes, int offset, string magic)
		{
			if (!bytes.Skip(offset).Take(magic.Length).SequenceEqual(Encoding.ASCII.GetBytes(magic)))
				throw new InvalidDataException("SV6x66 artifact identity marker is missing.");
		}
		static void RequireEntry(byte[] bytes)
		{
			if (bytes.Length < 4 || bytes[0] != 0x48 || bytes[1] != 0 || bytes[2] != 0 || bytes[3] != 2)
				throw new InvalidDataException("SV6x66 artifact does not have the required raw bank entry.");
		}
		static void ValidateBank(byte[] raw, byte bank)
		{
			if (raw.Length < 68 || raw.Length > SvApplicationTransaction.BankLength)
				throw new InvalidDataException("SV6x66 raw application length is outside its bank.");
			RequireEntry(raw);
			int record = raw.Length - 64;
			RequireMagic(raw, record, "OBKSV6X1");
			if (SvApplicationTransaction.Get32(raw, record + 8) != SvApplicationTransaction.AddressFor(bank)
				|| SvApplicationTransaction.Get32(raw, record + 12) != SvApplicationTransaction.BankLength
				|| SvApplicationTransaction.Get32(raw, record + 16) != raw.Length
				|| SvApplicationTransaction.Get32(raw, record + 20) != 1
				|| raw.Skip(record + 24).Any(value => value != 0))
				throw new InvalidDataException("SV6x66 application bank link identity, size, or format is unsupported.");
		}
		internal static byte[] LoadCarrier(byte bank)
		{
			if (bank > 1) throw new ArgumentOutOfRangeException(nameof(bank));
			string resource = bank == 0 ? "SV6x66_AppBankA_Carrier" : "SV6x66_AppBankB_Carrier";
			byte[] carrier;
			try { carrier = FLoaders.GetRawBinaryFromAssembly(resource); }
			catch (InvalidOperationException exception)
			{
				throw new InvalidDataException("This flasher build is missing its SV6x66 loader. Use a complete OpenIOTFlasher build.", exception);
			}
			if (carrier.Length < 36 || carrier.Length > MaximumCarrierBytes)
				throw new InvalidDataException("Embedded SV6x66 loader is outside its 12 KiB bound: " + resource);
			int expectedLength = bank == 0 ? 8680 : 8548;
			string expectedSha256 = bank == 0
				? "0a81c14f07d73a23f4374b387902d3b332c7df45ce96070a9501b72464e20c33"
				: "71d055df13d3d7a3d8538b7d8ae1923466b1ccef8b07b049befc8dac699ada89";
			if (carrier.Length != expectedLength || Digest(SHA256.Create(), carrier) != expectedSha256)
				throw new InvalidDataException("Embedded SV6x66 loader differs from the reviewed unified build: " + resource);
			RequireEntry(carrier);
			int record = carrier.Length - 32;
			RequireMagic(carrier, record, "SV6CARR1");
			byte[] body = carrier.Take(record).ToArray();
			uint bodyCrc = CRC.crc32_ver2(0xFFFFFFFFu, body) ^ 0xFFFFFFFFu;
			if (SvApplicationTransaction.Get32(carrier, record + 8) != bank
				|| SvApplicationTransaction.Get32(carrier, record + 12) != SvApplicationTransaction.AddressFor(bank)
				|| SvApplicationTransaction.Get32(carrier, record + 16) != carrier.Length
				|| SvApplicationTransaction.Get32(carrier, record + 20) != bodyCrc
				|| carrier.Skip(record + 24).Any(value => value != 0))
				throw new InvalidDataException("Embedded SV6x66 loader bank/link/length/CRC identity is inconsistent: " + resource);
			return carrier;
		}
		internal static bool IsApplicationFileName(string path)
		{
			if (string.IsNullOrEmpty(path)) return false;
			string name = Path.GetFileName(path);
			return !name.StartsWith("readResult_SV6X66_", StringComparison.OrdinalIgnoreCase)
				&& !name.EndsWith(".bank.bin", StringComparison.OrdinalIgnoreCase)
				&& (name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
					|| name.EndsWith(".sv6ota", StringComparison.OrdinalIgnoreCase));
		}
		internal static SvApplicationImage Load(string path)
		{
			path = Path.GetFullPath(path ?? "");
			FileInfo info = new FileInfo(path);
			if (!IsApplicationFileName(path) || !info.Exists
				|| info.Length < 84 + 136 || info.Length > 84 + 2 * SvApplicationTransaction.BankLength)
				throw new InvalidDataException("Select an SV6x66 application bundle (.bin or .sv6ota); raw banks and full backups are different inputs.");
			byte[] envelope = File.ReadAllBytes(path);
			if (envelope.Length < 84 + 136 || envelope.Length > 84 + 2 * SvApplicationTransaction.BankLength)
				throw new InvalidDataException("SV6x66 application package changed size while being read.");
			if (SvApplicationTransaction.Get32(envelope, 0) != 0x73737600
				|| SvApplicationTransaction.Get32(envelope, 12) != 1 || SvApplicationTransaction.Get32(envelope, 16) != 0)
				throw new InvalidDataException("SV6x66 application envelope magic or version is unsupported.");
			uint aLength = SvApplicationTransaction.Get32(envelope, 4);
			uint bLength = SvApplicationTransaction.Get32(envelope, 8);
			if (aLength < 68 || bLength < 68 || aLength > SvApplicationTransaction.BankLength
				|| bLength > SvApplicationTransaction.BankLength || 84UL + aLength + bLength != (ulong)envelope.Length)
				throw new InvalidDataException("SV6x66 application package has invalid lengths or trailing bytes.");
			byte[][] banks = { new byte[(int)aLength], new byte[(int)bLength] };
			Buffer.BlockCopy(envelope, 84, banks[0], 0, banks[0].Length);
			Buffer.BlockCopy(envelope, 84 + banks[0].Length, banks[1], 0, banks[1].Length);
			for (byte bank = 0; bank < 2; bank++)
			{
				ValidateBank(banks[bank], bank);
				string expected = Encoding.ASCII.GetString(envelope, 20 + bank * 32, 32);
				if (!string.Equals(expected, Digest(MD5.Create(), banks[bank]), StringComparison.Ordinal))
					throw new InvalidDataException("SV6x66 raw application MD5 does not match the dual-bank envelope.");
			}
			return new SvApplicationImage(path, envelope, banks,
				new[] { LoadCarrier(0), LoadCarrier(1) });
		}
		internal byte[] PaddedBank(byte bank)
		{
			if (bank > 1) throw new ArgumentOutOfRangeException(nameof(bank));
			byte[] padded = Enumerable.Repeat((byte)0xFF, SvApplicationTransaction.BankLength).ToArray();
			Buffer.BlockCopy(banks[bank], 0, padded, 0, banks[bank].Length);
			return padded;
		}
		internal byte[] Carrier(byte bank)
		{
			if (bank > 1) throw new ArgumentOutOfRangeException(nameof(bank));
			return (byte[])carriers[bank].Clone();
		}
		internal int RawLength(byte bank) => banks[bank].Length;
	}
	internal sealed class SvApplicationTransaction
	{
		internal const int BankLength = 0x9B000;
		internal readonly byte TargetBank;
		internal readonly byte RollbackBank;
		internal readonly ulong Nonce;
		internal readonly byte[] SourceSha;
		internal readonly uint ExpectedCrc;
		internal readonly int RawLength;
		internal sealed class Result
		{
			internal byte State, Status;
			internal uint ActualCrc, Selector, Programmed, Acknowledged, Erased, Flags;
		}
		internal SvApplicationTransaction(byte targetBank, byte rollbackBank, ulong nonce, byte[] sourceSha, uint expectedCrc, int rawLength)
		{
			if (targetBank > 1 || rollbackBank != 1 - targetBank || nonce == 0 || sourceSha == null || sourceSha.Length != 32
				|| rawLength < 68 || rawLength > BankLength)
				throw new ArgumentException("Invalid SV6x66 application transaction identity.");
			TargetBank = targetBank;
			RollbackBank = rollbackBank;
			Nonce = nonce;
			SourceSha = (byte[])sourceSha.Clone();
			ExpectedCrc = expectedCrc;
			RawLength = rawLength;
		}
		internal static uint Get32(byte[] data, int offset)
		{
			return (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);
		}
		internal static void Put32(byte[] data, int offset, uint value)
		{
			for (int index = 0; index < 4; index++) data[offset + index] = (byte)(value >> (8 * index));
		}
		internal static uint SelectorFor(byte bank) => bank == 0 ? uint.MaxValue : 0;
		internal static int AddressFor(byte bank) => bank == 0 ? 0x00B000 : 0x0A6000;
		internal byte[] Control(byte operation)
		{
			if (operation < 1 || operation > 4) throw new ArgumentOutOfRangeException(nameof(operation));
			byte[] data = new byte[operation == 1 ? 53 : 9];
			data[0] = operation;
			for (int index = 0; index < 8; index++) data[1 + index] = (byte)(Nonce >> (8 * index));
			if (operation == 1)
			{
				Buffer.BlockCopy(SourceSha, 0, data, 9, 32);
				Put32(data, 41, ExpectedCrc);
				Put32(data, 45, BankLength);
				Put32(data, 49, (uint)RawLength);
			}
			return data;
		}
		internal static void ValidateIdle(byte[] data)
		{
			ValidateProfile(data);
			if (data[1] != 0 || data[2] != 0 || data.Skip(8).Take(40).Any(value => value != 0)
				|| Get32(data, 48) != BankLength || Get32(data, 52) != 0 || Get32(data, 56) != 0
				|| Get32(data, 60) != SelectorFor(data[4]) || data.Skip(64).Any(value => value != 0))
				throw new InvalidDataException("SV6x66 application loader is not idle with its original bank selected; no rebind or replay.");
		}
		static void ValidateProfile(byte[] data)
		{
			if (data == null || data.Length != 84 || data[0] != 1 || data[3] > 1
				|| data[4] != 1 - data[3] || data[5] != 1 || data[6] != 0 || data[7] != 0)
				throw new InvalidDataException("SV6x66 application loader profile or bank identity is unsupported.");
		}
		internal Result Parse(byte[] data)
		{
			ValidateProfile(data);
			ulong nonce = 0;
			for (int index = 0; index < 8; index++) nonce |= (ulong)data[8 + index] << (8 * index);
			if (data[3] != TargetBank || data[4] != RollbackBank || nonce != Nonce
				|| !data.Skip(16).Take(32).SequenceEqual(SourceSha) || Get32(data, 48) != BankLength
				|| Get32(data, 52) != ExpectedCrc || Get32(data, 80) != RawLength || data[1] < 1 || data[1] > 4)
				throw new InvalidDataException("SV6x66 application session, source, or fixed bank identity changed; no rebind or replay.");
			Result result = new Result {
				State = data[1], Status = data[2], ActualCrc = Get32(data, 56), Selector = Get32(data, 60),
				Programmed = Get32(data, 64), Acknowledged = Get32(data, 68), Erased = Get32(data, 72), Flags = Get32(data, 76)
			};
			if (result.Programmed > BankLength || result.Programmed % 1024 != 0 || result.Acknowledged > result.Programmed
				|| result.Acknowledged % 1024 != 0 || result.Programmed - result.Acknowledged > 1024
				|| result.Erased > BankLength || result.Erased % 4096 != 0 || (result.Flags & ~3u) != 0
				|| (result.Programmed != 0 && result.Erased != BankLength)
				|| (result.State == 1 && (result.Status != 0 || result.Programmed != 0 || result.Acknowledged != 0
					|| result.Erased != 0 || result.Flags != 0 || result.ActualCrc != 0 || result.Selector != SelectorFor(RollbackBank)))
				|| ((result.Flags & 2) != 0 && (result.Programmed != BankLength || result.Acknowledged != BankLength
					|| result.Erased != BankLength || result.ActualCrc != ExpectedCrc || result.Flags != 3))
				|| (result.State == 4 && (result.Status != 0 || result.Flags != 3 || result.Selector != SelectorFor(TargetBank))))
				throw new InvalidDataException("SV6x66 application completion counters or selector contradict the fixed-bank transaction.");
			return result;
		}
		internal void RequireReceived(Result result)
		{
			if (result.State != 3 || result.Status != 0 || result.Flags != 3 || result.ActualCrc != ExpectedCrc
				|| result.Selector != SelectorFor(RollbackBank))
				throw new InvalidDataException("SV6x66 inactive image is not fully verified with the original bank selected; selector commit blocked.");
		}
	}
	public sealed class SV6x66Flasher : BaseFlasher
	{
		const byte CMD_SYN = 0x00;
		const byte CMD_BAUD = 0x07;
		const byte CMD_CUSTOM_CHIP_INFO = 0x20;
		const byte CMD_CUSTOM_CRC32 = 0x8F;
		const byte CMD_CUSTOM_FLASH_ID = 0x90;
		const byte CMD_CUSTOM_XMODEM_WRITE = 0x91;
		const byte CMD_CUSTOM_XMODEM_READ = 0x92;
		const byte CMD_RESTORE_TRANSACTION = 0x9D;
		const byte CMD_APPLICATION_TRANSACTION = 0x9E;
		const byte CMD_AVAILABLE_CMDS = 0xFF;
		const int FactoryBaud = 9600;
		const int StubInitialBaud = 115200;
		const int FlashBytes = 0x200000;
		const int SectorBytes = 0x1000;
		const int OtaPayloadBytes = 508;
		const int OtaTransmitDrainTimeoutMs = 1000;
		const int StockOtaSetupExpectedMilliseconds = 18500;
		const int StubBaudSettleMilliseconds = 250;
		const int RestoreAbortSyncMaximumRounds = 5;
		const int RestoreAbortTransmitDrainTimeoutMs = 1000;
		const int RestoreSyncResponseTimeoutMs = 2000;
		const int RestoreAbortSyncRetryDelayMs = 100;
		MemoryStream readResult;
		Stopwatch backupTimer;
		Stopwatch restoreTimer;
		Dictionary<string, RestorePhaseStatistics> restorePhaseTimings;
		long restoreTimingOrigin;
		XMODEM sharedStubXmodem;
		bool restoreTransportMustRemainOpen;
		bool deferRestoreCancellation;
		bool selectedBaudApplied;
		string restoreProgressLine;
		string restoreProgressDiagnosticLine;
		bool restoreProgressLinePending;
		SvRestoreTransaction restoreTransaction;
		string serialOperation;
		bool portOpenFailed;
		public bool LastOperationSucceeded { get; private set; }
		public bool RestoreDiagnosticsEnabled { get; set; }
		public bool TechnicalLoggingEnabled { get; set; }
		sealed class RestorePhaseStatistics
		{
			internal int Count;
			internal int Failed;
			internal long Total;
			internal long Minimum = long.MaxValue;
			internal long Maximum;
			internal long FirstStart;
			internal long LastEnd;
		}

		T MeasureRestorePhase<T>(string phase, Func<T> operation)
		{
			if (restorePhaseTimings == null)
				return operation();
			long start = Stopwatch.GetTimestamp();
			bool failed = true;
			try
			{
				T result = operation();
				failed = false;
				return result;
			}
			finally
			{
				long end = Stopwatch.GetTimestamp();
				try
				{
					if (!restorePhaseTimings.TryGetValue(phase, out RestorePhaseStatistics timing))
					{
						timing = new RestorePhaseStatistics { FirstStart = start - restoreTimingOrigin };
						restorePhaseTimings.Add(phase, timing);
					}
					long elapsed = end - start;
					timing.Count++;
					if (failed)
						timing.Failed++;
					timing.Total += elapsed;
					timing.Minimum = Math.Min(timing.Minimum, elapsed);
					timing.Maximum = Math.Max(timing.Maximum, elapsed);
					timing.LastEnd = end - restoreTimingOrigin;
				}
				catch
				{
					// Optional timing must not replace the operation's result or exception.
				}
			}
		}

		void MergeSenderTiming(string phase, XMODEM.SenderTimingStatistics source)
		{
			try
			{
				if (restorePhaseTimings == null || source == null || source.Count == 0) return;
				if (!restorePhaseTimings.TryGetValue(phase, out RestorePhaseStatistics timing))
				{
					timing = new RestorePhaseStatistics { FirstStart = source.FirstStart - restoreTimingOrigin };
					restorePhaseTimings.Add(phase, timing);
				}
				timing.Count += source.Count;
				timing.Failed += source.Failed;
				timing.Total += source.Total;
				timing.Minimum = Math.Min(timing.Minimum, source.Minimum);
				timing.Maximum = Math.Max(timing.Maximum, source.Maximum);
				timing.LastEnd = source.LastEnd - restoreTimingOrigin;
			}
			catch { }
		}

		void EmitRestorePhaseTimings()
		{
			if (!RestoreDiagnosticsEnabled) return;
			if (restorePhaseTimings == null)
				return;
			try
			{
				addLogLine($"SV6x66 timing: clock=Stopwatch; frequency={Stopwatch.Frequency}; phases_are_nested=true; flash_time_includes_transport=true");
				addLogLine("SV6x66 timing: xmodem_ack_wait=host_wait_including_wakeup; unacknowledged_waits_count_as_failed=true; packet_and_eot_are_separate=true");
				foreach (var entry in restorePhaseTimings.OrderBy(item => item.Key, StringComparer.Ordinal))
				{
					RestorePhaseStatistics timing = entry.Value;
					addLogLine(FormattableString.Invariant($"SV6x66 timing: phase={entry.Key}; calls={timing.Count}; failed_calls={timing.Failed}; total_ms={timing.Total * 1000.0 / Stopwatch.Frequency:F3}; min_ms={timing.Minimum * 1000.0 / Stopwatch.Frequency:F3}; max_ms={timing.Maximum * 1000.0 / Stopwatch.Frequency:F3}; first_start_ticks={timing.FirstStart}; last_end_ticks={timing.LastEnd}"));
				}
			}
			catch
			{
				// Keep the primary restore result even if the diagnostic sink is unavailable.
			}
			finally
			{
				restorePhaseTimings = null;
			}
		}

		sealed class RetryableRangeVerificationException : IOException
		{
			internal RetryableRangeVerificationException(string message) : base(message)
			{
			}
		}

		sealed class RetryableRangeTransferException : IOException
		{
			internal uint AcknowledgedBoundary { get; }

			internal RetryableRangeTransferException(string message,
				uint acknowledgedBoundary) : base(message)
			{
				AcknowledgedBoundary = acknowledgedBoundary;
			}
		}

		sealed class StubResponseEnvelope
		{
			internal byte Status { get; }
			internal byte[] Data { get; }

			internal StubResponseEnvelope(byte status, byte[] data)
			{
				Status = status;
				Data = data;
			}
		}

		public SV6x66Flasher(CancellationToken cancellationToken) : base(cancellationToken)
		{
		}

		static uint ReadLe32(byte[] data, int offset)
		{
			return (uint)(data[offset] | data[offset + 1] << 8 |
				data[offset + 2] << 16 | data[offset + 3] << 24);
		}

		static void WriteLe32(byte[] data, int offset, uint value)
		{
			data[offset] = (byte)value;
			data[offset + 1] = (byte)(value >> 8);
			data[offset + 2] = (byte)(value >> 16);
			data[offset + 3] = (byte)(value >> 24);
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

		static string FormatDiagnosticBytes(byte[] data)
		{
			StringBuilder text = new StringBuilder();
			foreach (byte value in data) {
				switch (value)
				{
					case 9: text.Append("\\t"); break;
					case 10: text.Append("\\n"); break;
					case 13: text.Append("\\r"); break;
					case 34: text.Append("\\\""); break;
					case 92: text.Append("\\\\"); break;
					default:
						if (value >= 0x20 && value <= 0x7E)
							text.Append((char)value);
						else
							text.Append("\\x" + value.ToString("X2"));
						break;
				}
			}
			return text.ToString();
		}

		static string FormatExceptionDetail(Exception exception)
		{
			StringBuilder detail = new StringBuilder();
			for (Exception current = exception; current != null; current = current.InnerException) {
				if (detail.Length > 0)
					detail.Append(" -> ");
				detail.Append(current.GetType().Name);
				detail.Append($" [HResult=0x{current.HResult:X8}");
				if (current is System.ComponentModel.Win32Exception native)
					detail.Append($", NativeErrorCode={native.NativeErrorCode}");
				detail.Append("]");
				detail.Append(": ");
				detail.Append(current.Message.Replace("\r", "\\r").Replace("\n", "\\n"));
				if (!string.IsNullOrEmpty(current.StackTrace))
					detail.Append("; stack=" + current.StackTrace.Replace("\r", "\\r").Replace("\n", "\\n"));
				if (current is AggregateException)
					detail.Append("; aggregate=" + current.ToString().Replace("\r", "\\r").Replace("\n", "\\n"));
			}
			return detail.ToString();
		}

		static string FormatHexBytes(byte[] data)
		{
			return data == null ? "<none>" : BitConverter.ToString(data).Replace("-", " ");
		}

		void OpenPort()
		{
			portOpenFailed = false;
			closePort();
			serial = new SerialPort(serialName, FactoryBaud, Parity.None, 8, StopBits.One) {
				ReadTimeout = 50,
				WriteTimeout = 5000,
				DtrEnable = false,
				RtsEnable = false,
				Handshake = Handshake.None,
			};
			serialOperation = "open " + serialName;
			try { serial.Open(); }
			catch { portOpenFailed = true; throw; }
		}
		void WriteSvWarning(string message)
		{
			logger.addLog(message + Environment.NewLine, Color.Orange);
		}
		void WriteSvError(string message)
		{
			logger.addLog(message + Environment.NewLine, Color.Red);
		}
		bool IsExplicitReadCancellation(Exception exception)
		{
			if (portOpenFailed || (!isCancelled && !cancellationToken.IsCancellationRequested)) return false;
			for (Exception current = exception; current != null; current = current.InnerException)
			{
				if (current is AggregateException) return false;
				if (current.InnerException == null)
					return current is IOException && current.HResult == unchecked((int)0x800703E3);
			}
			return false;
		}
		static string BriefFailureMessage(Exception exception)
		{
			string text = (exception.Message ?? "Operation failed.").Replace("\r", " ").Replace("\n", " ");
			if (text.StartsWith("SV6x66 stub returned invalid baud diagnostics", StringComparison.Ordinal)) return "SV6x66 baud verification failed.";
			if (text.StartsWith("SV6x66 stub returned an unexpected platform identity", StringComparison.Ordinal)) return "SV6x66 platform identity mismatch.";
			if (text.StartsWith("SV6x66 stub returned an unexpected capability set", StringComparison.Ordinal)) return "SV6x66 loader capability mismatch.";
			foreach (string marker in new[] { "; final failure:", "; stack=", "; raw=", "; expected=", "; partial=", "; aggregate=" })
			{
				int at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
				if (at >= 0) text = text.Substring(0, at);
			}
			return text.Trim();
		}
		void ReportOperationFailure(Exception exception, bool restoring)
		{
			bool serialFailure = IsSerialConnectionLostException(exception);
			string classification = portOpenFailed ? "PORT_OPEN_FAILURE" : serialFailure ? "SERIAL_IO_FAILURE" : "OPERATION_FAILURE";
			logger.setState(portOpenFailed ? "Unable to open COM port" : serialFailure ? "SV6x66 serial I/O failed" : restoring ? "SV6x66 restore failed" : "SV6x66 read failed", Color.Red);
			if (TechnicalLoggingEnabled)
			{
				WriteSvError("FailureClass: " + classification + "; USB_removal=unverified; no automatic full-run resume.");
				WriteSvError("SV6x66 serial failure detail: operation=" + serialOperation + "; " + FormatExceptionDetail(exception));
			}
			else if (portOpenFailed)
			{
				string detail = exception.HResult == unchecked((int)0x8007001F)
					? "A device attached to the system is not functioning." : BriefFailureMessage(exception);
				WriteSvError("Unable to open COM port: " + detail);
			}
			else if (serialFailure)
				WriteSvError("Serial I/O failed on " + serialName + ": " + BriefFailureMessage(exception));
			else
				WriteSvError((restoring ? "SV6x66 restore stopped: " : "SV6x66 backup stopped: ") + BriefFailureMessage(exception));
			if (!restoring)
				WriteSvWarning("No backup file was saved. The inactive bank may have changed; preserve this log before resetting or retrying.");
			if (restoring)
				WriteSvWarning("Full recovery stopped before completion. Restoring chip functionality requires SPI recovery; do not retry through UART.");
		}
		void CheckCancelled()
		{
			if (!deferRestoreCancellation
				&& (isCancelled || cancellationToken.IsCancellationRequested))
				throw new OperationCanceledException();
		}

		protected override bool KeepSerialPortOpenForCancellationRecovery()
		{
			return restoreTransportMustRemainOpen;
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

		void WaitForTransmitDrain(int timeoutMs, out int initialBytesToWrite,
			out int finalBytesToWrite, out double drainMilliseconds, string context)
		{
			initialBytesToWrite = -1;
			finalBytesToWrite = -1;
			drainMilliseconds = -1;
			CheckCancelled();
			if (serial == null || !serial.IsOpen)
				throw new IOException("Serial port closed while waiting for " + context + " transmit drain");
			Stopwatch timer = Stopwatch.StartNew();
			try
			{
				initialBytesToWrite = serial.BytesToWrite;
				finalBytesToWrite = initialBytesToWrite;
				while (finalBytesToWrite > 0) {
					CheckCancelled();
					if (serial == null || !serial.IsOpen)
						throw new IOException("Serial port closed while waiting for " + context + " transmit drain");
					if (timer.ElapsedMilliseconds >= timeoutMs)
						throw new IOException($"{context} transmit drain timed out after {timer.Elapsed.TotalMilliseconds:F3} ms with {finalBytesToWrite} byte(s) still queued");
					Thread.Sleep(1);
					finalBytesToWrite = serial.BytesToWrite;
				}
			}
			finally
			{
				drainMilliseconds = timer.Elapsed.TotalMilliseconds;
			}
		}

		byte[] ReadOtaAck(int timeoutMs, out double firstReadMilliseconds,
			out double totalMilliseconds)
		{
			byte[] result = new byte[5];
			int offset = 0;
			firstReadMilliseconds = -1;
			Stopwatch timer = Stopwatch.StartNew();
			while (offset < result.Length && timer.ElapsedMilliseconds < timeoutMs) {
				CheckCancelled();
				try
				{
					int read = serial.Read(result, offset, result.Length - offset);
					if (read > 0 && offset == 0)
						firstReadMilliseconds = timer.Elapsed.TotalMilliseconds;
					offset += read;
				}
				catch (TimeoutException)
				{
				}
			}
			totalMilliseconds = timer.Elapsed.TotalMilliseconds;
			if (offset != result.Length)
				throw new IOException($"OTA frame ACK timeout after {offset}/{result.Length} bytes; partial={FormatHexBytes(result.Take(offset).ToArray())}");
			return result;
		}

		byte[] ReadUntil(byte[] needle, int timeoutMs, string label, int maximumBytes = 4096,
			int expectedProgressMilliseconds = 0)
		{
			serialOperation = label + " response read";
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
				if (expectedProgressMilliseconds > 0)
					logger.setProgress(Math.Min((int)timer.ElapsedMilliseconds,
						expectedProgressMilliseconds - 1), expectedProgressMilliseconds);
				if (result.Count > maximumBytes)
					throw new IOException(label + " exceeded the bounded response length");
				if (result.Count >= needle.Length) {
					bool match = true;
					for (int i = 0; i < needle.Length; i++)
						match &= result[result.Count - needle.Length + i] == needle[i];
					if (match)
					{
						if (expectedProgressMilliseconds > 0)
							logger.setProgress(expectedProgressMilliseconds,
								expectedProgressMilliseconds);
						return result.ToArray();
					}
				}
			}
			throw new IOException($"{label} timeout");
		}

		string ReadLine(int timeoutMs, string label)
		{
			return Encoding.ASCII.GetString(ReadUntil(new byte[] { 13, 10 }, timeoutMs, label));
		}

		void ConfigureSharedXmodem()
		{
			sharedStubXmodem = new XMODEM(serial, XMODEM.Variants.XModem1K, 0xFF) {
				WorkerOwnedSenderCleanup = true,
				BatchSenderPackets = true,
				PreserveSenderInitiation = true,
				ReceiverFallBackAllowed = false,
				ReceiverRequireAcceptedDataBeforeEndOfFile = true,
			};
			xm = sharedStubXmodem;
		}

		byte[] ApplicationCommand(byte[] request, float timeoutSeconds = 3f)
		{
			return ExecuteStubCommand(CMD_APPLICATION_TRANSACTION, request, timeoutSeconds, 84);
		}

		byte[] ConnectApplicationLoader()
		{
			serial.BaudRate = StubInitialBaud;
			SynchronizeStub("Synchronizing with bank-aware SV6x66 loader", null, 100, 2, 6);
			ValidateStubIdentityAndCapabilities();
			byte[] query = ApplicationCommand(new byte[] { 0 });
			SvApplicationTransaction.ValidateIdle(query);
			addSuccess("Loader ready." + Environment.NewLine);
			byte[] flashId;
			ValidateFlashIdentity(out flashId);
			ConfigureSharedXmodem();
			return query;
		}

		void StageBankAwareCarrier(byte[][] carriers, byte? targetBank)
		{
			addLogLine($"Staging the bank-aware loader at {StubInitialBaud} baud...");
			addLogLine();
			byte[] a = targetBank == 1 ? Array.Empty<byte>() : carriers[0];
			byte[] b = targetBank == 0 ? Array.Empty<byte>() : carriers[1];
			string aMd5 = SvApplicationImage.Digest(MD5.Create(), a);
			string bMd5 = SvApplicationImage.Digest(MD5.Create(), b);
			byte[] stream = new byte[84 + a.Length + b.Length];
			WriteLe32(stream, 0, 0x73737600);
			WriteLe32(stream, 4, (uint)a.Length);
			WriteLe32(stream, 8, (uint)b.Length);
			WriteLe32(stream, 12, 1);
			Encoding.ASCII.GetBytes(aMd5 + bMd5).CopyTo(stream, 20);
			Buffer.BlockCopy(a, 0, stream, 84, a.Length);
			Buffer.BlockCopy(b, 0, stream, 84 + a.Length, b.Length);
			logger.setState("Preparing SV6x66 loader", Color.DarkOrange);
			logger.setProgress(0, StockOtaSetupExpectedMilliseconds);
			WriteText("AT+UPGRADE_START\r\n");
			ReadUntil(Encoding.ASCII.GetBytes("OK\r\n"), 35000, "bank-aware carrier staging start",
				4096, StockOtaSetupExpectedMilliseconds);
			addLogLine("Staging start accepted. Sending loader...");
			logger.setState("Sending SV6x66 loader", Color.DarkOrange);
			logger.setProgress(0, stream.Length);
			for (int offset = 0, sequence = 0; offset < stream.Length; offset += OtaPayloadBytes, sequence++)
			{
				int length = Math.Min(OtaPayloadBytes, stream.Length - offset);
				byte[] packet = new byte[4 + length];
				packet[0] = 0x55;
				packet[1] = 0xAA;
				packet[2] = (byte)(sequence >> 8);
				packet[3] = (byte)sequence;
				Buffer.BlockCopy(stream, offset, packet, 4, length);
				byte[] expectedReply = { 0x55, 0xAA, packet[2], packet[3], Crc8(stream, offset, length) };
				byte[] reply = null;
				double writeMilliseconds = -1, drainMilliseconds = -1;
				double firstAckReadMilliseconds = -1, totalAckReadMilliseconds = -1;
				int initialBytesToWrite = -1, finalBytesToWrite = -1;
				try
				{
					Stopwatch writeTimer = Stopwatch.StartNew();
					WriteAll(packet);
					writeMilliseconds = writeTimer.Elapsed.TotalMilliseconds;
					WaitForTransmitDrain(OtaTransmitDrainTimeoutMs, out initialBytesToWrite,
						out finalBytesToWrite, out drainMilliseconds, "bank-aware carrier frame");
					reply = ReadOtaAck(6000, out firstAckReadMilliseconds, out totalAckReadMilliseconds);
					if (!reply.SequenceEqual(expectedReply))
						throw new IOException("Bank-aware carrier frame acknowledgement mismatch; no frame replay or automatic restart.");
				}
				catch (OperationCanceledException) { throw; }
				catch (Exception exception)
				{
					// Always retain protocol metadata on failure; no payload, replay or resynchronization.
					WriteSvError($"Carrier frame failure: frame={sequence}, offset=0x{offset:X}, payload={length}, expected={FormatHexBytes(expectedReply)}, received={(reply == null ? "incomplete (see failure detail)" : FormatHexBytes(reply))}, Write()={writeMilliseconds:F3} ms, TX queued initial={initialBytesToWrite}, TX drain={drainMilliseconds:F3} ms, TX queued final={finalBytesToWrite}, ACK first-read={firstAckReadMilliseconds:F3} ms, ACK total={totalAckReadMilliseconds:F3} ms; {BriefFailureMessage(exception)}");
					WriteSvWarning("Carrier verification and activation were not requested. Preserve the log; do not retry or reset until the staging outcome is reviewed.");
					throw new IOException($"Carrier frame {sequence} at stream offset 0x{offset:X} failed: {exception.Message}", exception);
				}
				logger.setProgress(Math.Min(offset + length, stream.Length), stream.Length);
			}
			addLogLine("Loader transfer acknowledged. Verifying loader...");
			logger.setState("Verifying SV6x66 loader", Color.DarkOrange);
			WriteText("AT+VERIFT=" + aMd5 + bMd5 + "\r\n");
			ReadUntil(Encoding.ASCII.GetBytes("OK\r\n"), 4000, "bank-aware carrier verification");
			addLogLine("Loader verification accepted. Activating loader...");
			logger.setState("Starting SV6x66 loader", Color.DarkOrange);
			WriteText("AT+UPGRADE_END\r\n");
			ReadUntil(Encoding.ASCII.GetBytes("OK\r\n"), 35000, "bank-aware carrier activation");
			serial.BaudRate = StubInitialBaud;
		}

		byte[] PrepareBankAwareLoader(byte[][] carriers)
		{
			selectedBaudApplied = false;
			OpenPort();
			serial.BaudRate = StubInitialBaud;
			bool directLoader = false;
			try
			{
				StubResponseEnvelope sync = ExecuteStubCommandEnvelope(CMD_SYN, null, 0.5f, 0, true);
				directLoader = sync.Status == 0;
			}
			catch (IOException) { }
			if (directLoader)
			{
				addLogLine("Connecting to the running bank-aware loader...");
				return ConnectApplicationLoader();
			}
			serial.BaudRate = FactoryBaud;
			serial.DiscardInBuffer();
			logger.setState("Reset SV6x66 to begin loader staging", Color.DarkOrange);
			WriteSvWarning("Reset the SV6x66 to enter factory mode.");
			ReadUntil(Encoding.ASCII.GetBytes("factory?\r\n"), 60000, "application factory prompt");
			WriteText("factory!\r\n");
			ReadUntil(Encoding.ASCII.GetBytes("factory mode\r\n"), 4000, "application factory handshake");
			WriteText("AT+VER\r\n");
			string version = ReadLine(2500, "application factory version");
			byte? targetBank = null;
			if (version == "FWSWES-SV04MATE-3.7.1\r\n")
			{
				addLogLine("Exact CKW04 stock profile identified; its receiver selects the inactive carrier from the dual-bank stream.");
				WriteText("AT+BAUD=115200\r\n");
				ReadUntil(Encoding.ASCII.GetBytes("OK\r\n"), 2500, "stock application staging baud");
				serial.BaudRate = StubInitialBaud;
				Thread.Sleep(250);
				WriteText("AT+VER\r\n");
				if (ReadLine(2500, "stock profile at 115200") != version)
					throw new InvalidDataException("Stock 115200 compatibility or profile verification failed; staging blocked, no faster-rate fallback.");
			}
			else if (version == "OpenSV6X66/1 CKW04\r\n")
			{
				WriteText("AT+OBK_BANK?\r\n");
				string identity = ReadLine(2500, "OBK running bank identity");
				if (identity == "OBKSV6/1 CKW04 RUN=A SEL=FFFFFFFF BASE=0000B000 SIZE=0009B000 FLASH=00200000\r\n")
					targetBank = 1;
				else if (identity == "OBKSV6/1 CKW04 RUN=B SEL=00000000 BASE=000A6000 SIZE=0009B000 FLASH=00200000\r\n")
					targetBank = 0;
				else
					throw new InvalidDataException("Unknown SV6x66 firmware, running bank, selector, or layout; no carrier staging was started.");
				addLogLine($"Checking active bank: running Bank {(targetBank == 1 ? "A" : "B")}; inactive Bank {(targetBank == 0 ? "A" : "B")}.");
				WriteText("AT+BAUD=115200\r\n");
				ReadUntil(Encoding.ASCII.GetBytes("OK\r\n"), 2500, "OBK recovery baud");
				serial.BaudRate = StubInitialBaud;
				Thread.Sleep(250);
				WriteText("AT+OBK_BANK?\r\n");
				if (ReadLine(2500, "OBK bank identity after baud change") != identity)
					throw new InvalidDataException("OBK bank identity changed before staging; no write was started.");
			}
			else
				throw new InvalidDataException("Unknown SV6x66 factory firmware; no carrier staging was started.");
			StageBankAwareCarrier(carriers, targetBank);
			byte[] query = ConnectApplicationLoader();
			if (targetBank.HasValue && query[3] != targetBank.Value)
				throw new InvalidDataException("The running carrier differs from the queried inactive bank; further operations blocked.");
			return query;
		}

		void PrepareFullRestoreLoader()
		{
			byte[][] carriers = { SvApplicationImage.LoadCarrier(0), SvApplicationImage.LoadCarrier(1) };
			byte[] query = PrepareBankAwareLoader(carriers);
			SetStubBaud(baudrate);
			ValidateStubIdentityAndCapabilities();
			byte[] confirmed = ApplicationCommand(new byte[] { 0 });
			SvApplicationTransaction.ValidateIdle(confirmed);
			if (!confirmed.SequenceEqual(query))
				throw new InvalidDataException("SV6x66 loader bank/rollback identity changed before restore; no restore was started.");
		}

		SvApplicationTransaction.Result ApplicationStatus(SvApplicationTransaction transaction)
		{
			for (int attempt = 0; ; attempt++)
			{
				try { return transaction.Parse(ApplicationCommand(transaction.Control(3))); }
				catch (IOException) when (attempt < 2) { RestoreReadOnlyCommandBarrier(); }
			}
		}

		internal void doInstallApplication(SvApplicationImage image, bool backupFirst = false)
		{
			LastOperationSucceeded = false;
			portOpenFailed = false;
			bool selectorCommitAttempted = false;
			Stopwatch installationTimer = null;
			logger.setState("Starting SV6x66 firmware installation", Color.DarkOrange);
			try
			{
				if (image == null) throw new ArgumentNullException(nameof(image));
				if (backupFirst)
				{
					readResult?.Dispose();
					readResult = null;
					backupTimer?.Stop();
					backupTimer = Stopwatch.StartNew();
					logger.setState("Starting SV6x66 backup then firmware installation", Color.DarkOrange);
				}
				if (baudrate != 115200 && baudrate != 230400 && baudrate != 460800
					&& baudrate != 921600 && baudrate != 1500000 && baudrate != 2000000)
					throw new InvalidOperationException("Select a supported SV6x66 loader baud rate.");
				installationTimer = Stopwatch.StartNew();
				addLogLine("Source: " + Path.GetFileName(image.SourcePath) + ".");
				WriteSvWarning("Inactive-bank installation preserves the original application, bootloader, calibration and filesystem.");
				addLogLine("Connecting to " + serialName + "...");
				addLogLine($"Establishing communication at {baudrate} baud...");
				byte[] query = PrepareBankAwareLoader(new[] { image.Carrier(0), image.Carrier(1) });
				byte targetBank = query[3];
				byte rollbackBank = query[4];
				SetStubBaud(baudrate);
				if (backupFirst)
				{
					ValidateStubIdentityAndCapabilities();
					CheckCancelled();
					byte[] confirmed = ApplicationCommand(new byte[] { 0 });
					SvApplicationTransaction.ValidateIdle(confirmed);
					if (!confirmed.SequenceEqual(query))
						throw new InvalidDataException("SV6x66 loader bank/rollback identity changed before backup; application writing blocked.");
					addLogLine($"Read range: 0x000000-0x{FlashBytes - 1:X6} ({FlashBytes} bytes) at {baudrate} baud.");
					logger.setState("Reading SV6x66 flash", Color.Green);
					readResult = new MemoryStream(ReadXmodemFlash(targetBank, image.Carrier(targetBank), true), false);
					CheckCancelled();
					if (!saveReadResult(0))
						throw new IOException("SV6x66 backup could not be saved; application writing blocked.");
					CheckCancelled();
					confirmed = ApplicationCommand(new byte[] { 0 });
					SvApplicationTransaction.ValidateIdle(confirmed);
					if (!confirmed.SequenceEqual(query))
						throw new InvalidDataException("SV6x66 loader bank/rollback identity changed after backup; application writing blocked.");
					CheckCancelled();
					addSuccess("Backup saved. Continuing with firmware installation." + Environment.NewLine);
				}
				byte[] padded = image.PaddedBank(targetBank);
				byte[] sourceSha;
				using (SHA256 sha = SHA256.Create()) sourceSha = sha.ComputeHash(padded);
				byte[] random = new byte[8];
				using (RandomNumberGenerator generator = RandomNumberGenerator.Create()) generator.GetBytes(random);
				ulong nonce = BitConverter.ToUInt64(random, 0);
				SvApplicationTransaction transaction = new SvApplicationTransaction(targetBank, rollbackBank,
					nonce == 0 ? 1UL : nonce, sourceSha, CalculateCrc(padded), image.RawLength(targetBank));
				addLogLine($"Target: inactive Bank {(targetBank == 0 ? "A" : "B")}; original Bank {(rollbackBank == 0 ? "A" : "B")} remains selected.");
				if (backupFirst) CheckCancelled();
				SvApplicationTransaction.Result prepared = transaction.Parse(ApplicationCommand(transaction.Control(1)));
				if (prepared.State != 1) throw new InvalidDataException("Application transaction was not prepared without destructive work.");
				CheckCancelled();
				restoreTransportMustRemainOpen = true;
				deferRestoreCancellation = true;
				int address = SvApplicationTransaction.AddressFor(targetBank);
				logger.setState("Writing SV6x66 firmware", Color.DarkOrange);
				addLogLine($"Write range: 0x{address:X6}-0x{address + padded.Length - 1:X6} ({padded.Length} bytes).");
				logger.setProgress(0, padded.Length);
				StubResponseEnvelope ready = ExecuteStubCommandEnvelope(CMD_APPLICATION_TRANSACTION,
					transaction.Control(2), 35f, 0, false, false, true);
				if (ready.Status != 0) throw new IOException("Inactive-bank START rejected; no automatic retry.");
				int acknowledged = 0;
				void PacketSent(int sentBytes, int total, int sequence, uint offset)
				{
					if (total != padded.Length || sentBytes < 0 || sentBytes > padded.Length || offset != address + sentBytes)
						throw new InvalidDataException("SV6x66 application transfer accounting changed.");
					acknowledged = sentBytes;
					logger.setProgress(sentBytes, padded.Length);
					logger.setLogProgress($"Writing: ({sentBytes * 100 / padded.Length,3}%) 0x{offset:X6}", Color.Black);
				}
				Exception transferFailure = null;
				sharedStubXmodem.PacketSent += PacketSent;
				try { sharedStubXmodem.Send(padded, (uint)address); }
				catch (Exception exception) { transferFailure = exception; }
				finally { sharedStubXmodem.PacketSent -= PacketSent; }
				logger.setLogProgress($"Writing: ({acknowledged * 100 / padded.Length,3}%) 0x{address + acknowledged:X6}"
					+ Environment.NewLine, transferFailure == null && acknowledged == padded.Length ? Color.Black : Color.Red);
				if (transferFailure != null || sharedStubXmodem.TerminationReason != XMODEM.TerminationReasonEnum.EndOfFile)
				{
					serial.Write(new byte[] { 0x18, 0x18 }, 0, 2);
					RestoreReadOnlyCommandBarrier();
				}
				addLogLine();
				addLogLine("Verification...");
				logger.setState("Verifying SV6x66 firmware", Color.DarkOrange);
				SvApplicationTransaction.Result received = ApplicationStatus(transaction);
				transaction.RequireReceived(received);
				if (acknowledged != padded.Length || (transferFailure != null && !(transferFailure is IOException)))
					throw new IOException("Application transfer was not fully acknowledged; original bank retained, no replay or selector commit.", transferFailure);
				deferRestoreCancellation = false;
				CheckCancelled();
				if (finalReadbackVerification ?? true)
				{
					logger.setProgress(0, padded.Length);
					byte[] readback = ReadXmodemRange(address, padded.Length, count => logger.setProgress(count, padded.Length));
					if (!readback.SequenceEqual(padded)) throw new IOException("Inactive-bank final readback differs; original selector retained.");
				}
				addSuccess(((finalReadbackVerification ?? true) ? "CRC and final readback verification passed."
					: "CRC verification passed.") + Environment.NewLine);
				if (finalReadbackVerification == false)
					addLogLine("Write final readback skipped.");
				CheckCancelled();
				xm = null;
				deferRestoreCancellation = true;
				addLogLine("Activating verified firmware...");
				logger.setState("Verifying SV6x66 boot selector", Color.DarkOrange);
				selectorCommitAttempted = true;
				try { transaction.Parse(ApplicationCommand(transaction.Control(4), 5f)); }
				catch (IOException) { RestoreReadOnlyCommandBarrier(); }
				SvApplicationTransaction.Result committed = ApplicationStatus(transaction);
				if (committed.State != 4 || committed.Status != 0 || committed.Flags != 3
					|| committed.ActualCrc != transaction.ExpectedCrc || committed.Selector != SvApplicationTransaction.SelectorFor(targetBank))
					throw new IOException("Selector commit outcome is unproven; no commit replay or automatic restart.");
				LastOperationSucceeded = true;
				logger.setState("SV6x66 firmware installation complete", Color.DarkGreen);
				addSuccess("Boot selector verified." + Environment.NewLine);
				addLogLine();
				addSuccess("Firmware installation complete." + Environment.NewLine);
				installationTimer.Stop();
				addLogLine($"Completed in {installationTimer.Elapsed.TotalSeconds:F3} s.");
				WriteSvWarning("Reset manually to activate new firmware.");
			}
			catch (OperationCanceledException)
			{
				logger.setState("SV6x66 application installation cancelled", Color.DarkOrange);
				WriteSvWarning(selectorCommitAttempted ? "Cancellation arrived during selector commit; inspect retained outcome before reset."
					: "Installation cancelled before selector commit. The inactive bank may be incomplete; reset returns to the original application when rollback was verified.");
			}
			catch (Exception exception)
			{
				logger.setState("SV6x66 application installation stopped", Color.Red);
				WriteSvError("SV6x66 application installation stopped: " + BriefFailureMessage(exception));
				WriteSvWarning(selectorCommitAttempted ? "Selector outcome may be indeterminate. Do not replay commit; external recovery may be required."
					: "No application selector commit was requested. Carrier staging or rollback may still have changed the selector; preserve the log and use the documented recovery state.");
			}
			finally
			{
				installationTimer?.Stop();
				if (backupFirst) backupTimer?.Stop();
				deferRestoreCancellation = false;
				restoreTransportMustRemainOpen = false;
				xm = null;
				sharedStubXmodem = null;
				closePort();
			}
		}

		static byte StubChecksum(byte[] data, int length)
		{
			byte checksum = 0;
			unchecked
			{
				for (int i = 0; i < length; i++)
					checksum += data[i];
			}
			return checksum;
		}

		static string DescribeStubStatus(byte type, byte status)
		{
			if (type != CMD_CUSTOM_XMODEM_WRITE)
				return "status 0x" + status.ToString("X2");
			switch (status)
			{
				case 0x00: return "success";
				case 0x01: return "runtime failure";
				case 0x02: return "invalid range or profile";
				case 0x04: return "invalid length";
				case 0x12: return "no pending write result";
				case 0x13: return "retryable transfer abort";
				default: return "unknown write status 0x" + status.ToString("X2");
			}
		}

		bool ReadStubByte(Stopwatch timer, int timeoutMs, out byte value)
		{
			int originalReadTimeout = serial.ReadTimeout;
			bool timeoutChanged = false;
			bool readFailed = false;
			try
			{
				while (timer.ElapsedMilliseconds < timeoutMs
					&& (!isCancelled || deferRestoreCancellation))
				{
					int remainingMs = timeoutMs - (int)timer.ElapsedMilliseconds;
					if (remainingMs <= 0)
						break;
					int waitMs = Math.Min(50, remainingMs);
					if (serial.ReadTimeout != waitMs)
					{
						serial.ReadTimeout = waitMs;
						timeoutChanged = true;
					}
					try
					{
						int received = serial.ReadByte();
						if (timer.ElapsedMilliseconds >= timeoutMs)
							break;
						value = (byte)received;
						return true;
					}
					catch (TimeoutException)
					{
					}
				}
			}
			catch
			{
				readFailed = true;
				throw;
			}
			finally
			{
				if (timeoutChanged)
				{
					try { serial.ReadTimeout = originalReadTimeout; }
					catch when (readFailed) { }
				}
			}
			value = 0;
			return false;
		}

		static byte[] BuildStubRequest(byte type, byte[] parameters)
		{
			parameters ??= Array.Empty<byte>();
			List<byte> request = new List<byte>() {
				0xA5,
				type,
				(byte)(parameters.Length & 0xFF),
				(byte)(parameters.Length >> 8),
			};
			request.AddRange(parameters);
			request.Add(StubChecksum(request.ToArray(), request.Count));
			return request.ToArray();
		}

		StubResponseEnvelope ExecuteStubCommandEnvelope(byte type,
			byte[] parameters, float timeoutSeconds, int expectedReplyLength,
			bool discardInputBeforeWrite, bool strictXmodemControlPrefix = false,
			bool requireImmediateFrame = false)
		{
			byte[] request = BuildStubRequest(type, parameters);

			if (discardInputBeforeWrite)
				serial.DiscardInBuffer();
			MeasureRestorePhase($"command_0x{type:X2}_request", () => {
				serialOperation = $"command 0x{type:X2} request write";
				serial.Write(request, 0, request.Length);
				return 0;
			});
			return MeasureRestorePhase($"command_0x{type:X2}_response", () => {
				serialOperation = $"command 0x{type:X2} response read";
				return ReadStubResponseEnvelope(type, timeoutSeconds,
					expectedReplyLength, strictXmodemControlPrefix, requireImmediateFrame);
			});
		}

		StubResponseEnvelope ReadStubResponseEnvelope(byte type,
			float timeoutSeconds, int expectedReplyLength,
			bool strictXmodemControlPrefix, bool requireImmediateFrame)
		{
			int timeoutMs = Math.Max(1, (int)(timeoutSeconds * 1000));
			Stopwatch timer = Stopwatch.StartNew();
			byte value;
			do
			{
				if (!ReadStubByte(timer, timeoutMs, out value))
					throw new IOException("Shared-stub command response is empty");
				if (value != 0x5A && requireImmediateFrame)
					throw new IOException($"Shared-stub command 0x{type:X2} response did not begin with frame marker; received 0x{value:X2}");
				if (value != 0x5A && strictXmodemControlPrefix
					&& value != 0x18 && value != 0x15 && value != 0x43)
					throw new IOException($"Shared-stub sync barrier received invalid leading byte 0x{value:X2}");
			} while (value != 0x5A);

			List<byte> response = new List<byte>() { value };
			for (int i = 0; i < 3; i++)
			{
				if (!ReadStubByte(timer, timeoutMs, out value))
					throw new IOException("Shared-stub command response header is incomplete");
				response.Add(value);
			}
			int dataLength = response[2] | response[3] << 8;
			for (int i = 0; i < dataLength + 2; i++)
			{
				if (!ReadStubByte(timer, timeoutMs, out value))
					throw new IOException("Shared-stub command response is incomplete");
				response.Add(value);
			}

			byte[] raw = response.ToArray();
			if (raw[1] != type)
				throw new IOException($"Shared-stub response type 0x{raw[1]:X2} does not match 0x{type:X2}");
			if (StubChecksum(raw, raw.Length - 1) != raw[raw.Length - 1])
				throw new IOException("Shared-stub response checksum is incorrect");
			byte status = raw[raw.Length - 2];
			if (expectedReplyLength >= 0 && dataLength != expectedReplyLength)
				throw new IOException($"Shared-stub reply length {dataLength} != expected {expectedReplyLength}");

			byte[] data = new byte[dataLength];
			Buffer.BlockCopy(raw, 4, data, 0, dataLength);
			return new StubResponseEnvelope(status, data);
		}

		byte[] ExecuteStubCommand(byte type, byte[] parameters = null,
			float timeoutSeconds = 0.1f, int expectedReplyLength = -1,
			int baudAfterReply = 0)
		{
			StubResponseEnvelope response = ExecuteStubCommandEnvelope(type,
				parameters, timeoutSeconds, expectedReplyLength, true);
			if (response.Status != 0)
				throw new IOException($"Shared-stub command 0x{type:X2} failed with status 0x{response.Status:X2} ({DescribeStubStatus(type, response.Status)})");
			if (baudAfterReply > 0)
			{
				Thread.Sleep(50);
				serial.BaudRate = baudAfterReply;
				Thread.Sleep(StubBaudSettleMilliseconds);
			}
			return response.Data;
		}

		int RestoreWriteCommandModeAfterFailedXmodem()
		{
			IOException lastError = null;
			for (int round = 1; round <= RestoreAbortSyncMaximumRounds; round++)
			{
				try
				{
					if (round > 1)
						serial.DiscardInBuffer();
					byte[] abort = new byte[] { 0x18, 0x18 };
					serial.Write(abort, 0, abort.Length);
					int initialBytesToWrite;
					int finalBytesToWrite;
					double drainMilliseconds;
					WaitForTransmitDrain(RestoreAbortTransmitDrainTimeoutMs,
						out initialBytesToWrite, out finalBytesToWrite,
						out drainMilliseconds, "SV6x66 restore abort barrier");
					StubResponseEnvelope sync = ExecuteStubCommandEnvelope(CMD_SYN,
						null, RestoreSyncResponseTimeoutMs / 1000f, 0, false, true);
					if (sync.Status != 0)
						throw new IOException($"SV6x66 restore sync barrier returned status 0x{sync.Status:X2}");
					return round;
				}
				catch (IOException exception)
				{
					if (IsSerialConnectionLostException(exception))
						throw;
					lastError = exception;
					if (TechnicalLoggingEnabled)
						WriteSvWarning($"SV6x66 restore abort-and-sync round {round}/{RestoreAbortSyncMaximumRounds} failed: {FormatExceptionDetail(exception)}");
					if (round < RestoreAbortSyncMaximumRounds)
						Thread.Sleep(RestoreAbortSyncRetryDelayMs);
				}
			}
			throw new IOException($"SV6x66 restore transport did not return to command mode after {RestoreAbortSyncMaximumRounds} abort-and-sync rounds; the current session is not reusable", lastError);
		}

		byte[] ExchangeRestoreTransaction(byte[] request)
		{
			StubResponseEnvelope response = ExecuteStubCommandEnvelope(CMD_RESTORE_TRANSACTION, request, 3f, 84, false, false, true);
			if (response.Status != 0) throw new InvalidDataException($"SV6x66 v3 control rejected: status=0x{response.Status:X2}; no rebind or replay.");
			return response.Data;
		}
		void RestoreReadOnlyCommandBarrier()
		{
			SerialPort handle = serial;
			MeasureRestorePhase("transaction_readonly_recovery", () => {
				IOException last = null;
				for (int round = 0; round < 5; round++)
				{
					CheckCancelled();
					if (!ReferenceEquals(handle, serial) || !handle.IsOpen)
						throw new IOException("SV6x66 serial handle is no longer usable; no reopen or automatic resume.");
					try
					{
						for (int clean = 0; clean < 2; clean++)
						{
							StubResponseEnvelope response = ExecuteStubCommandEnvelope(CMD_SYN, null, 2f, 0,
								clean == 0, true, clean != 0);
							if (response.Status != 0) throw new IOException("SV6x66 read-only sync status is not success.");
						}
						return 0;
					}
					catch (IOException exception) { last = exception; }
					Thread.Sleep(100);
				}
				throw new IOException("SV6x66 same-handle command mode could not be proven; transaction remains unresolved.", last);
			});
		}

		SvRestoreTransaction.Result ReadRestoreTransaction(byte operation)
		{
			return restoreTransaction.ResolveReadOnly(operation, ExchangeRestoreTransaction, RestoreReadOnlyCommandBarrier,
				(code, exception) => RecordRecoverableSerialFailure($"transaction control {code}", exception));
		}

		void RecordRecoverableSerialFailure(string operation, Exception exception)
		{
			try
			{
				FinalizeRestoreProgress(Color.DarkOrange);
				if (TechnicalLoggingEnabled)
					WriteSvWarning($"SV6x66 bounded read-only recovery: operation={operation}; serial_operation={serialOperation}; "
					+ $"session=0x{restoreTransaction.Nonce:X16}; attempt={restoreTransaction.Attempt}; address=0x{restoreTransaction.Address:X6}; "
					+ FormatExceptionDetail(exception));
				else
					WriteSvWarning("Restoring communication to confirm the current write result.");
			}
			catch { }
		}

		void ResolveRestoreTransaction()
		{
			restoreTransaction.Resolved(ReadRestoreTransaction(7));
		}

		void EstablishRestoreTransactionSession(byte[] image)
		{
			byte[] nonce = new byte[8];
			using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(nonce);
			byte[] sourceSha;
			using (SHA256 sha = SHA256.Create()) sourceSha = sha.ComputeHash(image);
			ulong value = BitConverter.ToUInt64(nonce, 0);
			restoreTransaction = new SvRestoreTransaction(value == 0 ? 1UL : value, sourceSha);
			restoreTransaction.Parse(ExchangeRestoreTransaction(restoreTransaction.Control(1)), true);
		}
		int SynchronizeStub(string context, string successMessage, int retryDelayMs,
			int requiredConsecutiveSuccesses = 1, int maximumAttempts = 5,
			bool suppressRecoverableFailures = false, bool reportAttemptProgress = false)
		{
			IOException lastSyncError = null;
			string lastFailureDetail = null;
			int consecutiveSuccesses = 0;
			if (reportAttemptProgress)
				logger.setProgress(0, maximumAttempts);
			for (int attempt = 1; attempt <= maximumAttempts; attempt++) {
				if (reportAttemptProgress)
					logger.setProgress(attempt, maximumAttempts);
				int staleBytes = 0;
				try
				{
					staleBytes = serial.BytesToRead;
					byte[] response = ExecuteStubCommand(CMD_SYN, null, 2f);
					if (response == null || response.Length != 0)
						throw new IOException("SV6x66 stub sync returned no exact empty success response");
					consecutiveSuccesses++;
					if (consecutiveSuccesses >= requiredConsecutiveSuccesses)
					{
						if (!string.IsNullOrEmpty(successMessage))
							addSuccess(successMessage + Environment.NewLine);
						return attempt;
					}
					Thread.Sleep(retryDelayMs);
				}
				catch (IOException exception)
				{
					lastSyncError = exception;
					consecutiveSuccesses = 0;
					lastFailureDetail = $"{context} failed on attempt {attempt}/{maximumAttempts} after discarding {staleBytes} stale byte(s): {FormatExceptionDetail(exception)}";
					if (TechnicalLoggingEnabled && !suppressRecoverableFailures)
						WriteSvWarning(lastFailureDetail);
					if (attempt < maximumAttempts)
						Thread.Sleep(retryDelayMs);
				}
			}
			if (TechnicalLoggingEnabled && suppressRecoverableFailures && lastFailureDetail != null)
				WriteSvWarning(lastFailureDetail);
			throw new IOException($"SV6x66 stub sync failed to produce {requiredConsecutiveSuccesses} consecutive clean responses within {maximumAttempts} attempts; final failure: {FormatExceptionDetail(lastSyncError)}", lastSyncError);
		}

		bool SetStubBaud(int baud)
		{
			if (serial.BaudRate == baud && selectedBaudApplied)
				return true;
			byte[] payload = new byte[4];
			WriteLe32(payload, 0, (uint)baud);
			byte[] response = ExecuteStubCommand(CMD_BAUD, payload, 3f, 16, baud);
			if (response == null)
				throw new IOException("SV6x66 stub baud change failed");
			uint requestedBaud = ReadLe32(response, 0);
			uint achievedBaud = ReadLe32(response, 4);
			uint sourceClock = ReadLe32(response, 8);
			uint divisor = ReadLe32(response, 12);
			if (requestedBaud != (uint)baud || achievedBaud == 0 ||
				sourceClock == 0 || divisor == 0)
				throw new IOException("SV6x66 stub returned invalid baud diagnostics: "
					+ $"requested={requestedBaud}, achieved={achievedBaud}, source_clock={sourceClock}, divisor={divisor}");
			serial.DiscardInBuffer();
			int syncAttempts = SynchronizeStub("Re-synchronizing with shared stub at selected baud",
				null,
				100, 3, 8);
			if (syncAttempts > 3)
				WriteSvWarning(TechnicalLoggingEnabled ? $"Selected-baud synchronization recovered after {syncAttempts} attempts." : "Selected-baud synchronization recovered.");
			selectedBaudApplied = true;
			return true;
		}

		void ValidateStubIdentityAndCapabilities()
		{
			byte[] expectedIdentity = Encoding.ASCII.GetBytes("SV66");
			byte[] identity = ExecuteStubCommand(CMD_CUSTOM_CHIP_INFO, null, 2f, 4)
				?? throw new IOException("SV6x66 stub identity command failed");
			if (!identity.SequenceEqual(expectedIdentity))
				throw new IOException("SV6x66 stub returned an unexpected platform identity; expected="
					+ FormatHexBytes(expectedIdentity) + ", raw=" + FormatHexBytes(identity));

			byte[] expected = new byte[] { CMD_SYN, CMD_BAUD, CMD_CUSTOM_CHIP_INFO,
				CMD_CUSTOM_CRC32, CMD_CUSTOM_FLASH_ID, CMD_CUSTOM_XMODEM_WRITE,
				CMD_RESTORE_TRANSACTION, CMD_APPLICATION_TRANSACTION, CMD_CUSTOM_XMODEM_READ, CMD_AVAILABLE_CMDS };
			byte[] capabilities = ExecuteStubCommand(CMD_AVAILABLE_CMDS, null, 2f,
				expected.Length) ?? throw new IOException("SV6x66 stub capabilities command failed");
			if (!capabilities.SequenceEqual(expected))
				throw new IOException("SV6x66 stub returned an unexpected capability set; expected="
					+ FormatHexBytes(expected) + ", raw=" + FormatHexBytes(capabilities));
		}

		SPIFlashInfo ValidateFlashIdentity(out byte[] flashId)
		{
			flashId = ExecuteStubCommand(CMD_CUSTOM_FLASH_ID, null, 3f, 4)
				?? throw new IOException("SV6x66 flash identity command failed");
			byte[] expectedFlashId = new byte[] { 0xC8, 0x40, 0x15, 0x00 };
			if (!flashId.SequenceEqual(expectedFlashId))
				throw new IOException("Unsupported SV6x66 flash identity/layout; expected="
					+ FormatHexBytes(expectedFlashId) + ", raw=" + FormatHexBytes(flashId));
			int jedecId = flashId[0] | flashId[1] << 8 | flashId[2] << 16;
			SPIFlashInfo flashInfo = SPIFlashInfoList.Singleton.findFlashForMID(jedecId);
			if (flashInfo == null || flashInfo.szMem != FlashBytes)
				throw new IOException("Unsupported SV6x66 flash information/layout for JEDEC "
					+ FormatHexBytes(flashId.Take(3).ToArray()));
			addSuccess("Device identified: SV6x66." + Environment.NewLine);
			addLogLine($"Detected flash: {flashInfo.manufacturer} {flashInfo.icName}, JEDEC ID {FormatHexBytes(flashId.Take(3).ToArray())}, {flashInfo.szMem / (1024 * 1024)} MiB.");
			return flashInfo;
		}

		void ValidateFullFlashCrc(int address, byte[] data)
		{
			uint deviceCrc = ReadRangeCrc(address, data.Length);
			uint hostCrc = CalculateCrc(data);
			if (deviceCrc != hostCrc)
				throw new IOException($"Full-flash CRC32 mismatch: device {deviceCrc:X8}, host {hostCrc:X8}");
		}

		void SanitizeVerifiedBackupBank(byte[] image, byte bank, byte[] carrier, bool bankAware)
		{
			if (image == null || image.Length != FlashBytes)
				throw new InvalidDataException("SV6x66 backup sanitization requires an exact 2 MiB raw image");
			if (bank > 1 || carrier == null || carrier.Length < 36 || carrier.Length > SvApplicationImage.MaximumCarrierBytes)
				throw new InvalidDataException("SV6x66 backup carrier identity is invalid");
			int bankStart = SvApplicationTransaction.AddressFor(bank);
			int bankEnd = bankStart + SvApplicationTransaction.BankLength;
			int carrierEnd = bankStart + carrier.Length;
			if (carrierEnd > bankEnd || bankEnd > image.Length)
				throw new InvalidDataException("SV6x66 backup bank sanitization boundaries are invalid");
			if (bankAware && ReadLe32(image, 0x4000) != SvApplicationTransaction.SelectorFor((byte)(1 - bank)))
				throw new InvalidDataException("SV6x66 backup does not retain the original application's selector; refusing to sanitize or save");

			for (int offset = 0; offset < carrier.Length; offset++)
			{
				if ((offset & 0xFFFF) == 0)
					CheckCancelled();
				if (image[bankStart + offset] != carrier[offset])
					throw new InvalidDataException($"SV6x66 carrier mismatch at flash offset 0x{bankStart + offset:X6}; refusing to sanitize or save");
			}
			for (int offset = carrierEnd; offset < bankEnd; offset++)
			{
				if ((offset & 0xFFFF) == 0)
					CheckCancelled();
				if (image[offset] != 0xFF)
					throw new InvalidDataException($"SV6x66 inactive-bank padding is not erased at flash offset 0x{offset:X6}; refusing to sanitize or save");
			}
			CheckCancelled();
			for (int offset = bankStart; offset < carrierEnd; offset++)
				image[offset] = 0xFF;
			addLogLine($"Backup normalized: staged Bank {(bank == 0 ? "A" : "B")} is erased in the saved copy; its previous contents are not preserved!!");
		}

		static uint CalculateCrc(byte[] data)
		{
			return CRC.crc32_ver2(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;
		}

		uint ReadRangeCrc(int address, int length)
		{
			byte[] range = new byte[8];
			WriteLe32(range, 0, (uint)address);
			WriteLe32(range, 4, (uint)length);
			byte[] crcReply = ExecuteStubCommand(CMD_CUSTOM_CRC32, range, 30f, 4)
				?? throw new IOException("SV6x66 range CRC32 command failed");
			return ReadLe32(crcReply, 0);
		}

		byte[] ReadXmodemRange(int address, int length, Action<int> progress = null)
		{
			byte[] request = new byte[8];
			WriteLe32(request, 0, (uint)address);
			WriteLe32(request, 4, (uint)length);
			ExecuteStubCommand(CMD_CUSTOM_XMODEM_READ, request, 2f);
			using (MemoryStream stream = new MemoryStream())
			{
				int received = 0;
				void XmPacketReceived(XMODEM sender, byte[] packet, bool endOfFileDetected)
				{
					received += packet.Length;
					progress?.Invoke(Math.Min(received, length));
				}
				XMODEM.TerminationReasonEnum result;
				sharedStubXmodem.PacketReceived += XmPacketReceived;
				try
				{
					result = sharedStubXmodem.Receive(stream);
				}
				finally
				{
					sharedStubXmodem.PacketReceived -= XmPacketReceived;
				}
				if (result != XMODEM.TerminationReasonEnum.EndOfFile)
					throw new IOException("Shared-stub XMODEM read failed with " + result);
				byte[] data = stream.ToArray();
				if (data.Length != length)
					throw new IOException($"Flash range read returned {data.Length} bytes; expected {length} bytes");
				return data;
			}
		}

		void SetRestoreProgress(string regionName, int address, int rangeLength,
			int completedBeforeRange, int attempt, uint acknowledgedBoundary, string phase)
		{
			int acknowledgedBytes = (int)Math.Max(0L,
				Math.Min((long)acknowledgedBoundary - address, rangeLength));
			int completed = Math.Max(0,
				Math.Min(completedBeforeRange + acknowledgedBytes, FlashBytes));
			logger.setProgress(completed, FlashBytes);
			restoreProgressLine = $"Writing: ({completed * 100 / FlashBytes,3}%) 0x{acknowledgedBoundary:X6}";
			restoreProgressDiagnosticLine = $"Writing {regionName}: region 0x{address:X6}-0x{address + rangeLength - 1:X6}; "
				+ $"acknowledged boundary 0x{acknowledgedBoundary:X6} ({acknowledgedBytes}/{rangeLength} bytes); "
				+ "packet ACK means program completion only; full-region CRC pending; "
				+ $"cumulative {completed * 100 / FlashBytes,3}%; range attempt={attempt}; phase={phase}";
			restoreProgressLinePending = true;
			logger.setLogProgress(restoreProgressLine, Color.Black);
		}

		void FinalizeRestoreProgress(Color color, string finalText = null)
		{
			if (!restoreProgressLinePending || string.IsNullOrEmpty(restoreProgressLine))
				return;
			logger.setLogProgress((finalText ?? (RestoreDiagnosticsEnabled ? restoreProgressDiagnosticLine : restoreProgressLine) ?? restoreProgressLine)
				+ Environment.NewLine, color);
			restoreProgressLinePending = false;
		}

		void ProgramAndVerifyRegion(byte region, string regionName, int address, byte[] rangeData, int completedBeforeRange)
		{
			CheckCancelled();
			SvRestoreTransaction.Result prepared = restoreTransaction.Parse(ExchangeRestoreTransaction(restoreTransaction.Prepare(region, address, rangeData)));
			if (prepared.State != 1) throw new InvalidDataException("SV6x66 region PREP was not proven.");
			CheckCancelled();
			deferRestoreCancellation = true;
			uint acknowledgedBytes = 0;
			Exception transferFailure = null;
			int sent = 0;
			try
			{
				if (RestoreDiagnosticsEnabled)
				{
					FinalizeRestoreProgress(Color.Black, restoreProgressLine);
					addLogLine($"Erasing and writing {regionName}: 0x{address:X6}-0x{address + rangeData.Length - 1:X6}.");
				}
				StubResponseEnvelope ready = MeasureRestorePhase("region_ready", () => ExecuteStubCommandEnvelope(CMD_RESTORE_TRANSACTION,
					restoreTransaction.Control(5), 3f, 0, false, false, true));
				if (ready.Status != 0 || ready.Data.Length != 0) throw new InvalidDataException("SV6x66 BEGIN_RANGE did not return exact ready; entire current region may be erased; no replay.");
				void XmPacketSent(int sentBytes, int total, int sequence, uint offset)
				{
					if (sentBytes < 0 || sentBytes > rangeData.Length || offset != (uint)(address + sentBytes))
						throw new InvalidDataException("SV6x66 sender byte/absolute-offset accounting differs.");
					acknowledgedBytes = (uint)sentBytes;
					SetRestoreProgress(regionName, address, rangeData.Length, completedBeforeRange, (int)restoreTransaction.Attempt, offset,
						sentBytes == 0 ? "waiting for initial C after current-region erase" : "program-completion ACK observed; range CRC pending");
				}
				sharedStubXmodem.PacketSent += XmPacketSent;
				try { sent = MeasureRestorePhase("region_upload_with_target_program", () => sharedStubXmodem.Send(rangeData, (uint)address)); }
				catch (Exception exception) { transferFailure = exception; FinalizeRestoreProgress(Color.Red); RecordRecoverableSerialFailure("region XMODEM stopped; whole current region unverified; read-only result inspection only", exception); }
				finally
				{
					sharedStubXmodem.PacketSent -= XmPacketSent;
					MergeSenderTiming("xmodem_packet_write", sharedStubXmodem.PacketWriteTiming);
					MergeSenderTiming("xmodem_packet_ack_wait", sharedStubXmodem.PacketAckWaitTiming);
					MergeSenderTiming("xmodem_eot_write", sharedStubXmodem.EotWriteTiming);
					MergeSenderTiming("xmodem_eot_ack_wait", sharedStubXmodem.EotAckWaitTiming);
				}
				bool transferPassed = transferFailure == null && sent == rangeData.Length && sharedStubXmodem.TerminationReason == XMODEM.TerminationReasonEnum.EndOfFile;
				SvRestoreTransaction.Result result;
				try
				{
					if (!transferPassed)
					{
						serial.Write(new byte[] { 0x18, 0x18 }, 0, 2);
						RestoreReadOnlyCommandBarrier();
					}
					result = MeasureRestorePhase("region_retained_result", () => ReadRestoreTransaction(6));
				}
				catch (Exception checkpointFailure) when (transferFailure != null)
				{
					throw new IOException("SV6x66 region I/O failed and result inspection failed; whole current region unverified; no resume/replay.", new AggregateException(transferFailure, checkpointFailure));
				}
				if (RestoreDiagnosticsEnabled)
				{
					FinalizeRestoreProgress(Color.Black, restoreProgressLine);
					addLogLine($"SV6x66 region result: region={region}; attempt={restoreTransaction.Attempt}; status=0x{result.Status:X2}; program_completed={result.ProgramCompleted}; ack_intent={result.AckIntent}; host_ack_observed={acknowledgedBytes}; erase_completed={result.EraseCompleted}; flags=0x{result.Flags:X8}; range_crc=0x{result.RangeCrc:X8}; detail=0x{result.Detail:X8}");
				}
				bool knownFullOutcome = result.State == 3 && result.Status == 0 && acknowledgedBytes == rangeData.Length
					&& result.ProgramCompleted == rangeData.Length && result.AckIntent == rangeData.Length && result.EraseCompleted == rangeData.Length
					&& result.Flags == 3 && result.RangeCrc == restoreTransaction.ExpectedCrc;
				if (transferFailure != null && (!(transferFailure is IOException) || !knownFullOutcome))
					throw new IOException("SV6x66 stopped after identified result inspection; whole current region unverified; original transfer failure retained; no region replay.", transferFailure);
				if (!knownFullOutcome)
					throw new IOException($"SV6x66 region stopped: sent={sent}, host_ack={acknowledgedBytes}, status=0x{result.Status:X2}, flags=0x{result.Flags:X8}; whole current region unverified; no destructive replay.");
				if (!transferPassed)
				{
					FinalizeRestoreProgress(Color.DarkOrange, restoreProgressLine);
					WriteSvWarning("SV6x66 full range CRC outcome proven after missing EOT confirmation; no data replay or stream resume.");
				}
				ResolveRestoreTransaction();
				if (isCancelled || cancellationToken.IsCancellationRequested)
					throw new OperationCanceledException("SV6x66 cancellation deferred through current-region CRC verification and resolution; stopped before next region.");
			}
			finally { deferRestoreCancellation = false; }
		}
		internal static IEnumerable<Tuple<string, int, int>> GetRestoreRegions()
		{
			Tuple<string, int, int>[] semanticSchedule = {
				Tuple.Create("Filesystem/tail", 0x141000, 0x200000),
				Tuple.Create("Bank B", 0x0A6000, 0x141000),
				Tuple.Create("Bank-A body", 0x00C000, 0x0A6000),
				Tuple.Create("Bank-A entry", 0x00B000, 0x00C000),
				Tuple.Create("OTA/runtime metadata", 0x005000, 0x00B000),
				Tuple.Create("Bootloader body", 0x001000, 0x004000),
				Tuple.Create("Reset vector", 0x000000, 0x001000),
				Tuple.Create("Boot selector", 0x004000, 0x005000),
			};
			foreach (Tuple<string, int, int> region in semanticSchedule)
				yield return Tuple.Create(region.Item1, region.Item2, region.Item3 - region.Item2);
		}
		internal static IEnumerable<Tuple<string, int, int>> GetRestoreRanges()
		{
			foreach (Tuple<string, int, int> region in GetRestoreRegions())
				for (int offset = 0; offset < region.Item3; offset += SectorBytes)
					yield return Tuple.Create(region.Item1, region.Item2 + offset, SectorBytes);
		}
		internal static void ValidateRestoreRequest(string sourcePath, byte[] image, string expectedSha256)
		{
			string backupDirectory = MiscUtils.getExecutableBackupsPath();
			string fileName = Path.GetFileName(sourcePath ?? "");
			if (!MiscUtils.isCanonicalFileInDirectory(sourcePath, backupDirectory)
				|| !fileName.StartsWith("readResult_SV6X66_", StringComparison.Ordinal)
				|| !fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("SV6x66 restore source is not a canonical executable-relative backup.");
			if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length != FlashBytes)
				throw new InvalidDataException("SV6x66 restore source no longer exists as an exact 2 MiB backup.");
			if (image == null || image.Length != FlashBytes)
				throw new InvalidDataException("SV6x66 restore image must be exactly 2 MiB.");
			using (SHA256 sha256 = SHA256.Create())
			{
				string actualSha256 = HexDigest(sha256, image);
				if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
					throw new InvalidDataException("SV6x66 immutable restore image SHA-256 validation failed.");
			}
		}

		byte[] ReadXmodemFlash(byte carrierBank, byte[] carrier, bool bankAware)
		{
			int received = 0;
			int lastDisplayedPercent = 0;
			string FormatReadProgress(int completed)
			{
				return $"Reading: ({completed * 100 / FlashBytes,3}%) 0x{completed:X6}";
			}
			string readProgress = FormatReadProgress(0);
			void XmPacketReceived(XMODEM sender, byte[] packet, bool endOfFileDetected)
			{
				received += packet.Length;
				if (!isCancelled)
				{
					int completed = Math.Min(received, FlashBytes);
					int percent = completed * 100 / FlashBytes;
					logger.setProgress(completed, FlashBytes);
					readProgress = FormatReadProgress(completed);
					if (completed < FlashBytes && percent != lastDisplayedPercent)
					{
						lastDisplayedPercent = percent;
						logger.setLogProgress(readProgress, Color.Black);
					}
				}
			}

			byte[] request = new byte[8];
			WriteLe32(request, 0, 0);
			WriteLe32(request, 4, FlashBytes);
			logger.setLogProgress(readProgress, Color.Black);
			byte[] image;
			try
			{
				ExecuteStubCommand(CMD_CUSTOM_XMODEM_READ, request, 2f);
				using (MemoryStream stream = new MemoryStream())
				{
					sharedStubXmodem.PacketReceived += XmPacketReceived;
					try
					{
						XMODEM.TerminationReasonEnum result = sharedStubXmodem.Receive(stream);
						if (result == XMODEM.TerminationReasonEnum.UserCancelled
							&& (isCancelled || cancellationToken.IsCancellationRequested))
							throw new OperationCanceledException();
						if (result != XMODEM.TerminationReasonEnum.EndOfFile)
							throw new IOException("Shared-stub XMODEM read failed with " + result);
					}
					finally
					{
						sharedStubXmodem.PacketReceived -= XmPacketReceived;
					}
					image = stream.ToArray();
					if (image.Length != FlashBytes)
						throw new IOException($"Flash read returned {image.Length} bytes; expected {FlashBytes} bytes");
					logger.setProgress(FlashBytes, FlashBytes);
					readProgress = FormatReadProgress(FlashBytes);
					logger.setLogProgress(readProgress + Environment.NewLine, Color.Black);
				}
			}
			catch
			{
				logger.setLogProgress(readProgress + Environment.NewLine, Color.Red);
				throw;
			}
			addLogLine();
			addLogLine("Verification...");
			ValidateFullFlashCrc(0, image);
			if (finalReadbackVerification == true)
			{
				CheckCancelled();
				addLogLine("Final backup readback...");
				logger.setProgress(0, FlashBytes);
				byte[] finalReadback = ReadXmodemRange(0, FlashBytes, received => logger.setProgress(received, FlashBytes));
				CheckCancelled();
				if (!finalReadback.SequenceEqual(image))
					throw new IOException("Final backup readback does not match the initial raw flash image.");
			}
			SanitizeVerifiedBackupBank(image, carrierBank, carrier, bankAware);
			addSuccess((finalReadbackVerification.HasValue ? "CRC verification passed." : "Verification passed.") + Environment.NewLine);
			if (finalReadbackVerification.HasValue)
				addLogLine(finalReadbackVerification.Value ? "Backup final readback passed." : "Backup final readback skipped.");
			addLogLine();
			return image;
		}

		byte[] ReadFullFlash()
		{
			byte[][] carriers = { SvApplicationImage.LoadCarrier(0), SvApplicationImage.LoadCarrier(1) };
			byte[] query = PrepareBankAwareLoader(carriers);
			SetStubBaud(baudrate);
			ValidateStubIdentityAndCapabilities();
			byte[] confirmed = ApplicationCommand(new byte[] { 0 });
			SvApplicationTransaction.ValidateIdle(confirmed);
			if (!confirmed.SequenceEqual(query))
				throw new InvalidDataException("SV6x66 loader bank/rollback identity changed before backup; no read was started.");
			byte carrierBank = query[3];
			addLogLine($"Read range: 0x000000-0x{FlashBytes - 1:X6} ({FlashBytes} bytes) at {baudrate} baud.");
			logger.setState("Reading SV6x66 flash", Color.Green);
			return ReadXmodemFlash(carrierBank, carriers[carrierBank], true);
		}

		public override void doRead(int startSector = 0, int sectors = 10, bool fullRead = false)
		{
			portOpenFailed = false;
			LastOperationSucceeded = false;
			readResult?.Dispose();
			readResult = null;
			backupTimer?.Stop();
			backupTimer = null;
			logger.setState("Starting SV6x66 full backup", Color.DarkOrange);
			try
			{
				if (!fullRead || startSector != 0 || sectors != FlashBytes / SectorBytes) {
					logger.setState("SV6x66 read parameters invalid", Color.Red);
					WriteSvError("SV6x66 supports only one complete 2 MiB backup operation.");
					return;
				}
				if (baudrate <= 0)
					throw new InvalidOperationException("Select a valid baud rate for SV6x66 backup.");
				backupTimer = Stopwatch.StartNew();
				addLogLine("Connecting to " + serialName + "...");
				addLogLine($"Loader staging uses {StubInitialBaud} baud; flash reading uses the selected {baudrate} baud.");
				readResult = new MemoryStream(ReadFullFlash(), false);
				LastOperationSucceeded = true;
				logger.setState("SV6x66 read complete", Color.DarkGreen);
				addSuccess("Read complete." + Environment.NewLine);
			}
			catch (OperationCanceledException)
			{
				backupTimer?.Stop();
				logger.setState("SV6x66 read cancelled", Color.Yellow);
				if (TechnicalLoggingEnabled)
					WriteSvWarning("SV6x66 backup cancelled. No backup file was saved. Manually reset the SV6x66 before retrying.");
			}
			catch (Exception exception) when (IsExplicitReadCancellation(exception))
			{
				backupTimer?.Stop();
				logger.setState("SV6x66 read cancelled", Color.Yellow);
				if (TechnicalLoggingEnabled)
					WriteSvWarning("SV6x66 backup cancelled. No backup file was saved. " + FormatExceptionDetail(exception));
			}
			catch (Exception exception)
			{
				backupTimer?.Stop();
				ReportOperationFailure(exception, false);
			}
			finally
			{
				xm = null;
				sharedStubXmodem = null;
				closePort();
			}
		}

		public void doRestoreFullBackup(string sourcePath, byte[] image, string expectedSha256)
		{
			portOpenFailed = false;
			LastOperationSucceeded = false;
			restoreTimer?.Stop();
			restoreTimer = null;
			restorePhaseTimings = null;
			restoreTransaction = null;
			restoreProgressLine = null;
			restoreProgressDiagnosticLine = null;
			restoreProgressLinePending = false;
			logger.setState("Starting SV6x66 full restore", Color.DarkOrange);
			try
			{
				ValidateRestoreRequest(sourcePath, image, expectedSha256);
				if (baudrate <= 0)
					throw new InvalidOperationException("Select a valid baud rate for SV6x66 restore.");
				restoreTimer = Stopwatch.StartNew();
				restoreTimingOrigin = Stopwatch.GetTimestamp();
				restorePhaseTimings = RestoreDiagnosticsEnabled ? new Dictionary<string, RestorePhaseStatistics>() : null;
				WriteSvWarning("Warning: Restoring SV6x66 overwrites the complete 2 MiB flash, including boot-critical data.");
				WriteSvWarning("Each current region is erased before upload; cancellation waits until that region is written and CRC-verified.");
				addLogLine("Source: " + Path.GetFileName(sourcePath) + ".");
				addLogLine("Connecting to " + serialName + "...");
				addLogLine($"Establishing communication at {baudrate} baud...");
				MeasureRestorePhase("loader_staging", () => {
					PrepareFullRestoreLoader();
					return 0;
				});
				restoreTransportMustRemainOpen = true;
				xm = null;
				EstablishRestoreTransactionSession(image);

				logger.setState("Writing SV6x66 flash", Color.DarkOrange);
				int completed = 0;
				logger.setProgress(0, FlashBytes);
				byte regionIndex = 0;
				foreach (Tuple<string, int, int> range in GetRestoreRegions())
				{
					CheckCancelled();
					if (RestoreDiagnosticsEnabled)
						logger.setState("Writing SV6x66 flash: " + range.Item1, Color.DarkOrange);
					byte[] rangeData = new byte[range.Item3];
					Buffer.BlockCopy(image, range.Item2, rangeData, 0, rangeData.Length);
					ProgramAndVerifyRegion(regionIndex++, range.Item1, range.Item2, rangeData, completed);
					completed += rangeData.Length;
				}
				FinalizeRestoreProgress(Color.Black, "Writing: (100%) 0x005000");

				addLogLine();
				addLogLine("Verification...");
				deferRestoreCancellation = true;
				try
				{
					uint expectedCrc = MeasureRestorePhase("final_host_crc", () => CalculateCrc(image));
					uint deviceCrc = MeasureRestorePhase("final_device_crc", () => ReadRangeCrc(0, FlashBytes));
					if (deviceCrc != expectedCrc)
						throw new IOException($"Final full-flash CRC32 mismatch: device {deviceCrc:X8}, source {expectedCrc:X8}");
					if (finalReadbackVerification ?? true)
					{
						logger.setProgress(0, FlashBytes);
						byte[] finalReadback = MeasureRestorePhase("final_full_image_readback", () =>
							ReadXmodemRange(0, FlashBytes, received => logger.setProgress(received, FlashBytes)));
						if (!MeasureRestorePhase("final_byte_comparison", () => finalReadback.SequenceEqual(image)))
							throw new IOException("Final full-flash byte-for-byte readback does not match the source image.");
						logger.setProgress(FlashBytes, FlashBytes);
					}
				}
				finally
				{
					deferRestoreCancellation = false;
				}
				addSuccess((!finalReadbackVerification.HasValue ? "Verification passed."
					: finalReadbackVerification.Value ? "CRC and final readback verification passed."
					: "CRC verification passed.") + Environment.NewLine);
				if (finalReadbackVerification == false)
					addLogLine("Write final readback skipped.");
				addLogLine();
				LastOperationSucceeded = true;
				logger.setState("SV6x66 restore complete", Color.DarkGreen);
				addSuccess("Restore complete." + Environment.NewLine);
				restoreTimer.Stop();
				addLogLine($"Completed in {restoreTimer.Elapsed.TotalSeconds:F3} s.");
			}
			catch (OperationCanceledException)
			{
				restoreTimer?.Stop();
				FinalizeRestoreProgress(Color.DarkOrange);
				logger.setState("SV6x66 restore cancelled", Color.Yellow);
				WriteSvWarning("SV6x66 full recovery cancelled. Restoring chip functionality requires SPI recovery; do not retry through UART.");
			}
			catch (Exception exception)
			{
				restoreTimer?.Stop();
				FinalizeRestoreProgress(Color.Red);
				ReportOperationFailure(exception, true);
			}
			finally
			{
				deferRestoreCancellation = false;
				restoreTransportMustRemainOpen = false;
				xm = null;
				sharedStubXmodem = null;
				try
				{
					closePort();
				}
				finally
				{
					EmitRestorePhaseTimings();
				}
			}
		}

		public override void doWrite(int startSector, byte[] data)
		{
			LastOperationSucceeded = false;
			logger.setState("SV6x66 generic write disabled", Color.Red);
			WriteSvError("SV6x66 arbitrary writes are disabled. Use the gated exact-backup restore path.");
		}

		public override void doReadAndWrite(int startSector, int sectors, string sourceFileName, WriteMode mode)
		{
			LastOperationSucceeded = false;
			logger.setState("SV6x66 combined write disabled", Color.Red);
			WriteSvError("SV6x66 combined backup-and-write is disabled. No target action was performed.");
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
				string fileName = MiscUtils.formatDateNowFileName("readResult_SV6X66", backupName, "bin");
				string backupDirectory = MiscUtils.getExecutableBackupsPath();
				Directory.CreateDirectory(backupDirectory);
				string fullPath = Path.Combine(backupDirectory, fileName);
				File.WriteAllBytes(fullPath, readResult.ToArray());
				backupTimer?.Stop();
				logger.setState("SV6x66 backup saved", Color.DarkGreen);
				addLogLine($"Saved: {readResult.Length} bytes to backups/{fileName}.");
				if (backupTimer != null)
					addLogLine($"Completed in {backupTimer.Elapsed.TotalSeconds:F3} s.");
				backupTimer = null;
				return true;
			}
			catch (Exception exception)
			{
				backupTimer?.Stop();
				logger.setState("SV6x66 backup save failed", Color.Red);
				WriteSvError("Save failed in " + MiscUtils.getExecutableBackupsPath()
					+ ": " + exception.Message);
				return false;
			}
		}

		public override bool doErase(int startSector = 0, int sectors = 10, bool bAll = false)
		{
			WriteSvError("Standalone erase is not supported for SV6x66.");
			return false;
		}
	}
}
