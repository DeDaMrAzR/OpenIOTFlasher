using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace BK7231Flasher
{
    public enum BKType
    {
        BK7231M,
        BK7231N,
        BK7231T,
        BK7231U,
        BK7236,
        BK7238,
        BK7239N,
        BK7252,
        BK7252N,
        BK7258,
        BekenSPI,
        BL602,
        BL616,
        BL702,
        ECR6600,
        ESP32,
        ESP32S2,
        ESP32C2,
        ESP32C3,
        ESP32C5,
        ESP32C6,
        ESP32C61,
        ESP32S3,
        ESP8266,
        GD32VW553,
        GenericSPI,
        LN882H,
        LN8825,
        OPL1000A2,
        RDA5981,
        RTL8710B,
        RTL8720D,
        RTL8720E,
        RTL8721DA,
        RTL87X0C,
        TR6260,
        W600,
        W800,
        XR806,
        XR809,
        XR872,
		SV6X66,

        Detect,
        Invalid,
    }
    public enum WriteMode
    {
        ReadAndWrite,
        OnlyWrite,
        OnlyOBKConfig,
        OnlyErase
    }

    public class ChipType
    {
        public BKType Type { get; }

        public string Name { get; }

        public ChipType(BKType type, string name)
        {
            Type = type;
            Name = name;
        }

        public override string ToString()
        {
            return Name;
        }
    }

    public class BaseFlasher : IDisposable
    {
        protected ILogListener logger;
        protected string backupName;
        protected bool bOverwriteBootloader = false;
        protected bool bSkipKeyCheck;
        protected bool bIgnoreCRCErr = false;
        protected bool bCustomWriteMode = false;
        protected bool bUseCompressionIfPossible = false;
		protected bool? finalReadbackVerification;
        protected SerialPort serial;
        protected string serialName;
        protected BKType chipType = BKType.BK7231N;
        protected int baudrate = 921600;
        protected CancellationToken cancellationToken;
        protected XMODEM xm;
        protected bool isCancelled = false;
        private bool serialConnectionLost;
        private bool serialConnectionLostLogged;
        private bool serialPortWasOpen;
        private bool serialPortOpenFailed;

        public BaseFlasher(CancellationToken ct)
        {
            cancellationToken = ct;
            ct.Register(() =>
            {
                isCancelled = true;
                if(xm != null)
                {
                    xm?.CancelFileTransfer();
                    xm.InProgress.Wait(500);
                }
                if(!KeepSerialPortOpenForCancellationRecovery())
                    closePort();
            });
        }

        protected virtual bool KeepSerialPortOpenForCancellationRecovery()
        {
            return false;
        }

        public void setBasic(ILogListener logger, string serialName, BKType bkType, int baudrate = 921600)
        {
            this.logger = logger;
            this.serialName = serialName;
            this.chipType = bkType;
            this.baudrate = baudrate;
            serialConnectionLost = false;
            serialConnectionLostLogged = false;
            serialPortWasOpen = false;
            serialPortOpenFailed = false;
        }

        protected bool HasSerialConnectionBeenLost => serialConnectionLost;
        protected bool HasSerialPortOpenFailed => serialPortOpenFailed;

        protected void ReportSerialPortOpenFailure(Exception ex)
        {
            serialPortOpenFailed = true;
            string portName = string.IsNullOrWhiteSpace(serialName) ? "COM port" : serialName;
            string message = ex is UnauthorizedAccessException
                ? portName + " is busy or access is denied."
                : "Unable to open " + portName + ": " + ex.Message;
            addErrorLine(message);
            logger?.setState("COM port unavailable", Color.Red);
        }

        protected bool IsSerialConnectionLostException(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                string message = current.Message ?? string.Empty;
                string stackTrace = current.StackTrace ?? string.Empty;
                bool serialStack = stackTrace.IndexOf("System.IO.Ports", StringComparison.OrdinalIgnoreCase) >= 0
                    || stackTrace.IndexOf("SerialStream", StringComparison.OrdinalIgnoreCase) >= 0
                    || stackTrace.IndexOf("SerialPort", StringComparison.OrdinalIgnoreCase) >= 0;

                if (current is InvalidOperationException
                    && (message.IndexOf("port is closed", StringComparison.OrdinalIgnoreCase) >= 0
                        || message.IndexOf("port is not open", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }
                if (current is ObjectDisposedException && serialStack)
                {
                    return true;
                }
                if ((current is UnauthorizedAccessException || current is IOException) && serialStack)
                {
                    return true;
                }
            }
            return false;
        }

        protected bool HandleSerialConnectionLost(Exception ex)
        {
            if (IsSerialConnectionLostException(ex) == false)
            {
                return false;
            }

            serialConnectionLost = true;
            if (cancellationToken.IsCancellationRequested || serialConnectionLostLogged)
            {
                return true;
            }

			FinishOperationProgress();
            serialConnectionLostLogged = true;
            logger?.setLogProgress("COM port disconnected" + Environment.NewLine, Color.Red);
            logger?.setState("COM port disconnected", Color.Red);
            return true;
        }

        protected void LogOperationException(string context, Exception ex)
        {
            if (HandleSerialConnectionLost(ex))
            {
                return;
            }
            addErrorLine(context + ex.Message);
        }

        private bool LooksLikeSerialConnectionLostText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            return text.IndexOf("access to the port is denied", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("port is closed", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("port is not open", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("device attached to the system is not functioning", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("semaphore timeout period has expired", StringComparison.OrdinalIgnoreCase) >= 0;
        }

		bool operationProgressActive;

		protected void SetOperationProgress(string text, Color color)
		{
			operationProgressActive = !string.IsNullOrEmpty(text) && !text.EndsWith("\n", StringComparison.Ordinal);
			logger?.setLogProgress(text, color);
		}

		protected void FinishOperationProgress()
		{
			if(!operationProgressActive)
				return;
			operationProgressActive = false;
			logger?.addLog(Environment.NewLine, Color.Black);
		}

        private void WriteLog(string text, Color color)
        {
			FinishOperationProgress();
            try
            {
                if (serial != null && serial.IsOpen)
                {
                    serialPortWasOpen = true;
                }
            }
            catch
            {
            }

            if (serialPortWasOpen && LooksLikeSerialConnectionLostText(text))
            {
                serialConnectionLost = true;
                if (cancellationToken.IsCancellationRequested || serialConnectionLostLogged)
                {
                    return;
                }
                serialConnectionLostLogged = true;
                logger?.setLogProgress("COM port disconnected" + Environment.NewLine, Color.Red);
                logger?.setState("COM port disconnected", Color.Red);
                return;
            }
            logger.addLog(text, color);
        }
        public void addLog(string format, params object[] args)
        {
            string s = string.Format(format, args);
            WriteLog(s, Color.Black);
        }
        public void addLog(string s)
        {
            WriteLog(s, Color.Black);
        }
        public void addLogLine(string format = "", params object[] args)
        {
            string s = string.Format(format, args);
            WriteLog(s + Environment.NewLine, Color.Black);
        }
        public void addLogLine(string s)
        {
            WriteLog(s+Environment.NewLine, Color.Black);
        }
        public void addErrorLine(string s)
        {
            WriteLog(s + Environment.NewLine, Color.Red);
        }
        public void addError(string s)
        {
            WriteLog(s, Color.Red);
        }
        public void addSuccess(string s)
        {
            WriteLog(s, Color.Green);
        }
        public void addWarning(string s)
        {
            WriteLog(s, Color.Orange);
        }
        public void addWarningLine(string s)
        {
            WriteLog(s + Environment.NewLine, Color.Orange);
        }
        public void setBackupName(string newName)
        {
            this.backupName = newName;
            if (this.backupName.Length == 0)
            {
                addLog("Backup name has not been set, so output file will only contain flash type/date." + Environment.NewLine);
            }
            else
            {
                addLog("Backup name is set to " + this.backupName + "." + Environment.NewLine);
            }
        }
        public static string formatHex(int i)
        {
            return "0x" + i.ToString("X2");
        }
        public static string formatHex(uint i)
        {
            return "0x" + i.ToString("X2");
        }
        public static string formatHex(long i)
        {
            return "0x" + i.ToString("X2");
        }
		protected static string FormatFlashInfo(int jedecId, string manufacturer, long sizeBytes, string model = null)
		{
			string jedecBytes = string.Format("{0:X2} {1:X2} {2:X2}",
				jedecId & 0xff, (jedecId >> 8) & 0xff, (jedecId >> 16) & 0xff);
			string size;
			if(sizeBytes > 0 && sizeBytes % (1024 * 1024) == 0)
				size = (sizeBytes / (1024 * 1024)) + " MiB";
			else if(sizeBytes > 0 && sizeBytes % 1024 == 0)
				size = (sizeBytes / 1024) + " KiB";
			else
				size = sizeBytes + " bytes";
			string identity = string.IsNullOrEmpty(manufacturer) ? "unknown" : manufacturer;
			if(!string.IsNullOrEmpty(model))
				identity += " " + model;
			return "Detected flash: " + identity + ", JEDEC ID " + jedecBytes
				+ ", " + size + " (" + sizeBytes + " bytes).";
		}
        public void setSkipKeyCheck(bool b)
        {
            bSkipKeyCheck = b;
        }
        public void setIgnoreCRCErr(bool b)
        {
            bIgnoreCRCErr = b;
        }
        public void setUseCompression(bool b)
        {
            bUseCompressionIfPossible = b;
        }
		public void setFinalReadbackVerification(bool? enabled)
		{
			finalReadbackVerification = enabled;
		}

        public void setOverwriteBootloader(bool b)
        {
            bOverwriteBootloader = b;
        }
        public void setCustomWriteMode(bool b)
        {
            bCustomWriteMode = b;
        }
        public virtual void doWrite(int startSector, byte[] data)
        {

        }
        public virtual void doRead(int startSector = 0x000, int sectors = 10, bool fullRead = false)
        {

        }
        public virtual byte[] getReadResult()
        {
            return null;
        }
        public virtual bool doErase(int startSector = 0x000, int sectors = 10, bool bAll = false)
        {
            return false;
        }
        public virtual void closePort()
        {
            if (serial != null)
            {
                serial.Close();
                serial.Dispose();
            }
        }
        public virtual void doTestReadWrite(int startSector = 0x000, int sectors = 10)
        {
        }

        public virtual void doDetect()
        {
        }

        public virtual void doReadAndWrite(int startSector, int sectors, string sourceFileName, WriteMode rwMode)
        {
        }
        public virtual bool saveReadResult(int startOffset)
        {
            return false;
        }

        public virtual void Xm_PacketSent(int sentBytes, int total, int sequence, uint offset)
        {
			if ((sequence % 4) == 1 || sentBytes >= total)
				SetOperationProgress($"Transferring: {sentBytes}/{total} bytes", Color.Black);
			logger.setProgress(sentBytes, total);
			if (sentBytes >= total) FinishOperationProgress();
        }

        public virtual void Dispose() { }

        public static string HashToStr(byte[] data)
        {
            var sb = new StringBuilder();
            foreach(byte b in data)
                sb.Append(b.ToString("X2"));

            return sb.ToString();
        }

        internal virtual byte[] ReadMAC() => null;

        internal static byte[] Decompress(byte[] data)
        {
            using MemoryStream decompressedStream = new MemoryStream();
            using MemoryStream compressStream = new MemoryStream(data);
            using DeflateStream deflateStream = new DeflateStream(compressStream, CompressionMode.Decompress);
            deflateStream.CopyTo(decompressedStream);
            byte[] decompressedArray = decompressedStream.ToArray();

            return decompressedArray;
        }

        internal static byte[] Compress(byte[] data)
        {
            using var inputStream = new MemoryStream(data);
            using var compressStream = new MemoryStream();
            using var compressor = new DeflateStream(compressStream, CompressionLevel.Optimal);
            inputStream.CopyTo(compressor);
            compressor.Close();
            return compressStream.ToArray();
        }
    }
}

