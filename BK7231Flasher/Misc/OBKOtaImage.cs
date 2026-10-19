using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BK7231Flasher
{
	internal sealed class OBKOtaImage
	{
		internal readonly byte[] Data;
		internal readonly string FileName;
		internal readonly bool IsSv6;
		internal readonly bool IsWinnerMicro;

		OBKOtaImage(string path, byte[] data, string chipset)
		{
			FileName = Path.GetFileName(path);
			Data = data;
			IsSv6 = Normalize(chipset) == "SV6X66";
			IsWinnerMicro = Normalize(chipset) == "W600" || Normalize(chipset) == "W800";
		}

		static string Normalize(string chipset) => (chipset ?? "").Replace("-", "").Replace("_", "").ToUpperInvariant();

		static bool GetProfile(string chipset, out string prefix, out string suffix)
		{
			string chip = Normalize(chipset);
			suffix = "_ota.img";
			switch (chip)
			{
				case "BK7231T":
				case "BK7231N":
				case "BK7231U":
				case "BK7236":
				case "BK7238":
				case "BK7239N":
				case "BK7252":
				case "BK7252N":
					suffix = ".rbl";
					break;
				case "BL602":
					suffix = "_OTA.bin.xz.ota";
					break;
				case "LN882H":
					suffix = "_OTA.bin";
					break;
				case "W600":
					suffix = "_gz.img";
					break;
				case "W800":
				case "XR806":
				case "XR872":
				case "RTL8710A":
				case "RTL8710B":
				case "RTL87X0C":
				case "RTL8720D":
				case "RTL8721DA":
				case "RTL8720E":
				case "ECR6600":
				case "GD32VW553":
				case "RDA5981":
				case "TR6260":
				case "TXW81X":
				case "LN8825":
					break;
				case "ESP32":
				case "ESP32C2":
				case "ESP32C3":
				case "ESP32C5":
				case "ESP32C6":
				case "ESP32C61":
				case "ESP32S2":
				case "ESP32S3":
				case "ESP8266":
				case "ESP8285":
					if (chip == "ESP8285") chip = "ESP8266";
					suffix = ".img";
					break;
				case "SV6X66":
					suffix = ".sv6ota";
					break;
				default:
					prefix = null;
					return false;
			}
			prefix = "Open" + chip + "_";
			return true;
		}

		internal static string FileFilter(string chipset)
		{
			if (!GetProfile(chipset, out string prefix, out string suffix)) return null;
			return "OTA firmware (" + prefix + "*" + suffix + ")|" + prefix + "*" + suffix;
		}

		internal static OBKOtaImage Load(string path, string chipset)
		{
			if (!GetProfile(chipset, out string prefix, out string suffix))
				throw new InvalidDataException("No confirmed local-file OTA profile for this device.");
			string name = Path.GetFileName(path);
			if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
				|| !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("Select the OTA package matching " + prefix + "*" + suffix + ".");
			FileInfo file = new FileInfo(path);
			if (file.Length < 32 || file.Length > 16 * 1024 * 1024)
				throw new InvalidDataException("Local-file OTA accepts packages from 32 bytes to 16 MiB; this limit does not establish device capacity.");
			byte[] data = File.ReadAllBytes(path);
			if (data.Length != file.Length)
				throw new InvalidDataException("OTA package changed while being read.");
			if (Normalize(chipset) == "BL602" && (data.Length <= 512
				|| Encoding.ASCII.GetString(data, 0, 9) != "BL60X_OTA"))
				throw new InvalidDataException("Missing BL602 OTA header or payload.");
			if (Normalize(chipset) == "SV6X66")
			{
				SvApplicationImage application = SvApplicationImage.Load(path);
				if (!string.Equals(application.SourceSha256, SvApplicationImage.Digest(SHA256.Create(), data), StringComparison.Ordinal))
					throw new InvalidDataException("SV6x66 package changed during validation.");
			}
			return new OBKOtaImage(path, data, chipset);
		}
	}
}
