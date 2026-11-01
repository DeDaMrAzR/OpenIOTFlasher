
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net.Configuration;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BK7231Flasher
{
	public partial class FormMain : Form, ILogListener
	{
		string firmwaresPath = "firmwares/";
		public static FormMain Singleton;
		int chosenBaudRate;
		string chosenSourceFile;
		FormCustom formCustom;
		CancellationTokenSource cts;
		volatile bool sv6x66OperationLockActive;
		BKType activeOperationPlatform = BKType.Invalid;
		TabPage sv6x66OperationLockedTab;
		bool terminalTabChangePromptActive;
		bool sv6x66OperationSavedAllowDrop;
		readonly Dictionary<Control, bool> sv6x66OperationEnabledStates = new Dictionary<Control, bool>();
		const int Sv6x66FullFlashBytes = 0x200000;
		const string Sv6x66BackupPrefix = "readResult_SV6X66_";

		sealed class Sv6x66RestoreRequest
		{
			internal string SourcePath;
			internal byte[] Image;
			internal string Sha256;
			internal bool? FinalReadbackVerification;
		}
		sealed class Sv6x66ApplicationRequest
		{
			internal SvApplicationImage Image;
			internal bool? FinalReadbackVerification;
			internal bool BackupFirst;
			internal string BackupName;
		}

		enum OperationUiTarget
		{
			Flasher,
			ReadRom,
		}

		OperationUiTarget currentOperationUiTarget = OperationUiTarget.Flasher;
		bool bSuppressReadRomPlatformEvents = false;
		bool bSuppressReadRomTargetEvents = false;


		public readonly int[] BaudRates = new int[] { 115200, 230400, 460800, 921600, 1500000, 2000000, 3000000, /*4000000, 6000000*/ };

		private bool IsBeken
		{
			get
			{ 
				switch(curType)
				{
					case BKType.BK7231M:
					case BKType.BK7231N:
					case BKType.BK7231T:
					case BKType.BK7231U:
					case BKType.BK7236:
					case BKType.BK7238:
					case BKType.BK7239N:
					case BKType.BK7252:
					case BKType.BK7252N:
					case BKType.BK7258:
						return true;
					default:
						return false;
				}
			}
		}

		public FormMain()
		{
			Singleton = this;
			InitializeComponent();
			tabControl1.Selecting += tabControl1_Selecting;
			tabControl1.Deselecting += tabControl1_Deselecting;
			ConfigureLogContextMenu(textBoxLog);
			ConfigureLogContextMenu(textBoxReadRomLog);
			InitializeBl602IoExtractorTab();
			InitializeTerminalTab();
			ConfigurePortDisplay(comboBoxUART);
			ConfigurePortDisplay(comboBoxReadRomUART);
			ConfigurePortDisplay(comboBoxTerminalPort);
			var version = Assembly.GetExecutingAssembly().GetCustomAttribute<BuildVersion>().Value;
			Text = $"OpenIOTFlasher {(string.IsNullOrWhiteSpace(version) ? "(local build)" : version)}";
		}

		void ConfigureLogContextMenu(RichTextBox logTextBox)
		{
			logTextBox.ReadOnly = true;
			ContextMenuStrip contextMenu = new ContextMenuStrip(components);
			ToolStripMenuItem copyItem = new ToolStripMenuItem("Copy");
			ToolStripMenuItem selectAllItem = new ToolStripMenuItem("Select all");

			copyItem.ShortcutKeys = Keys.Control | Keys.C;
			copyItem.Click += (sender, e) => logTextBox.Copy();
			selectAllItem.ShortcutKeys = Keys.Control | Keys.A;
			selectAllItem.Click += (sender, e) => logTextBox.SelectAll();
			contextMenu.Opening += (sender, e) =>
			{
				copyItem.Enabled = logTextBox.SelectionLength > 0;
				selectAllItem.Enabled = logTextBox.TextLength > 0;
			};

			contextMenu.Items.Add(copyItem);
			contextMenu.Items.Add(selectAllItem);
			logTextBox.ContextMenuStrip = contextMenu;
		}

		public string getFirmwareDir()
		{
			return firmwaresPath;
		}
		string[] allPorts;

		bool SupportsFinalReadbackOption()
		{
			return curType == BKType.SV6X66 || curType == BKType.BekenSPI || curType == BKType.GenericSPI;
		}

		bool? CaptureFinalReadbackOption()
		{
			return SupportsFinalReadbackOption() ? (bool?)checkBoxVerifyBackupWrite.Checked : null;
		}

		bool SupportsCompressionOption()
		{
			switch (curType)
			{
				case BKType.ECR6600:
				case BKType.RTL87X0C:
				case BKType.LN882H:
				case BKType.LN8825:
				case BKType.W800:
				case BKType.RDA5981:
				case BKType.TR6260:
				case BKType.GD32VW553:
				case BKType.RTL8710B:
				case BKType.RTL8720D:
				case BKType.RTL8721DA:
				case BKType.RTL8720E:
				case BKType.OPL1000A2:
					return true;
				default:
					return false;
			}
		}

		void UpdateAdvancedCheckboxControls(bool operationControlsEnabled = true)
		{
			bool enabled = operationControlsEnabled && worker == null
				&& !terminalFlasherTasks.Exists(task => !task.IsCompleted)
				&& !sv6x66OperationLockActive;
			checkBoxVerifyBackupWrite.Enabled = enabled && SupportsFinalReadbackOption();
			checkBoxAllowBackup.Enabled = enabled;
			checkBoxOverwriteBootloader.Enabled = enabled && (curType == BKType.BK7231N || curType == BKType.BK7231M);
			checkBoxSkipKeyCheck.Enabled = enabled && IsBeken;
			chkIgnoreCRCErr.Enabled = enabled && (IsBeken || SupportsCompressionOption());
			chkUseCompression.Enabled = enabled && SupportsCompressionOption();
		}
		Dictionary<string, string> portDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		[StructLayout(LayoutKind.Sequential)]
		struct PortDeviceInfo
		{
			internal int Size;
			internal Guid ClassGuid;
			internal uint DeviceInstance;
			internal IntPtr Reserved;
		}

		[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);
		[DllImport("setupapi.dll", SetLastError = true)]
		static extern bool SetupDiEnumDeviceInfo(IntPtr deviceSet, uint index, ref PortDeviceInfo deviceInfo);
		[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceSet, ref PortDeviceInfo deviceInfo,
			uint property, out uint registryType, byte[] buffer, uint bufferSize, out uint requiredSize);
		[DllImport("setupapi.dll", SetLastError = true)]
		static extern IntPtr SetupDiOpenDevRegKey(IntPtr deviceSet, ref PortDeviceInfo deviceInfo,
			uint scope, uint hardwareProfile, uint keyType, uint access);
		[DllImport("setupapi.dll", SetLastError = true)]
		static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceSet);

		void ConfigurePortDisplay(ComboBox comboBox)
		{
			comboBox.FormattingEnabled = true;
			comboBox.Format += FormatPortDisplay;
			comboBox.DropDown += ResizePortDropDown;
		}

		void ResizePortDropDown(object sender, EventArgs e)
		{
			var comboBox = (ComboBox)sender;
			int width = comboBox.Width;
			foreach (object item in comboBox.Items)
				width = Math.Max(width, TextRenderer.MeasureText(comboBox.GetItemText(item), comboBox.Font).Width
					+ SystemInformation.VerticalScrollBarWidth + 4);
			comboBox.DropDownWidth = width;
		}

		void FormatPortDisplay(object sender, ListControlConvertEventArgs e)
		{
			string port = e.ListItem as string;
			if (port != null && portDescriptions.TryGetValue(port, out string description))
				e.Value = port + " - " + description;
		}

		static string ReadPortDeviceDescription(IntPtr deviceSet, ref PortDeviceInfo deviceInfo, uint property)
		{
			byte[] buffer = new byte[2048];
			if (!SetupDiGetDeviceRegistryPropertyW(deviceSet, ref deviceInfo, property,
				out uint registryType, buffer, (uint)buffer.Length, out uint requiredSize)
				|| (registryType != 1 && registryType != 7) || requiredSize < 2 || requiredSize > buffer.Length)
				return "";
			return Encoding.Unicode.GetString(buffer, 0, (int)requiredSize).Split('\0')[0].Trim();
		}

		static Dictionary<string, string> ReadPortDescriptions(string[] ports)
		{
			var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			var requestedPorts = new HashSet<string>(ports, StringComparer.OrdinalIgnoreCase);
			if (requestedPorts.Count == 0)
				return descriptions;
			try
			{
				foreach (Guid deviceClass in new[] {
					new Guid("4d36e978-e325-11ce-bfc1-08002be10318"),
					new Guid("4d36e96d-e325-11ce-bfc1-08002be10318") })
				{
					Guid classGuid = deviceClass;
					IntPtr deviceSet = SetupDiGetClassDevsW(ref classGuid, IntPtr.Zero, IntPtr.Zero, 2);
					if (deviceSet == new IntPtr(-1))
						continue;
					try
					{
						for (uint index = 0; ; index++)
						{
							var deviceInfo = new PortDeviceInfo { Size = Marshal.SizeOf(typeof(PortDeviceInfo)) };
							if (!SetupDiEnumDeviceInfo(deviceSet, index, ref deviceInfo))
								break;
							try
							{
								IntPtr keyHandle = SetupDiOpenDevRegKey(deviceSet, ref deviceInfo, 1, 0, 1, 0x20019);
								if (keyHandle == new IntPtr(-1))
									continue;
								using (var safeHandle = new Microsoft.Win32.SafeHandles.SafeRegistryHandle(keyHandle, true))
								using (var key = Microsoft.Win32.RegistryKey.FromHandle(safeHandle))
								{
									string port = key.GetValue("PortName") as string;
									if (port == null || !requestedPorts.Contains(port))
										continue;
									string description = ReadPortDeviceDescription(deviceSet, ref deviceInfo, 12);
									if (string.IsNullOrWhiteSpace(description))
										description = ReadPortDeviceDescription(deviceSet, ref deviceInfo, 0);
									description = Regex.Replace(description, @"\s*\(" + Regex.Escape(port) + @"\)\s*$",
										"", RegexOptions.IgnoreCase).Trim();
									string hardwareId = ReadPortDeviceDescription(deviceSet, ref deviceInfo, 1);
									Match usbId = Regex.Match(hardwareId, @"VID_([0-9A-F]{4}).*?PID_([0-9A-F]{4})",
										RegexOptions.IgnoreCase);
									if (!string.IsNullOrWhiteSpace(description)
										&& !string.Equals(description, port, StringComparison.OrdinalIgnoreCase))
									{
										if (usbId.Success)
										{
											string manufacturer = ReadPortDeviceDescription(deviceSet, ref deviceInfo, 11);
											if (!string.IsNullOrWhiteSpace(manufacturer))
												description += " - " + manufacturer;
											description += " (" + usbId.Groups[1].Value.ToUpperInvariant() + "/"
												+ usbId.Groups[2].Value.ToUpperInvariant() + ")";
										}
										descriptions[port] = description;
									}
								}
							}
							catch
							{
							}
						}
					}
					finally
					{
						SetupDiDestroyDeviceInfoList(deviceSet);
					}
				}
			}
			catch
			{
			}
			return descriptions;
		}

		void setPorts(string [] newPorts)
		{
			if(allPorts != null)
			{
				if(allPorts.Length == newPorts.Length)
				{
					bool bChange = false;
					for(int i = 0; i < allPorts.Length; i++)
					{
						if(allPorts[i] != newPorts[i])
						{
							bChange = true;
							break;
						}
					}
					if(bChange == false)
					{
						RefreshTerminalPorts(newPorts);
						return;
					}
				}
			}

			string prevPort = getComboBoxSelectedText(comboBoxUART);
			string prevReadRomPort = getComboBoxSelectedText(comboBoxReadRomUART);
			portDescriptions = ReadPortDescriptions(newPorts);
			allPorts = newPorts;
			setPortComboBoxItems(comboBoxUART, allPorts, prevPort);
			setPortComboBoxItems(comboBoxReadRomUART, allPorts, prevReadRomPort);
			RefreshTerminalPorts(allPorts);
		}

		string getComboBoxSelectedText(ComboBox comboBox)
		{
			if(comboBox.SelectedIndex != -1)
			{
				return comboBox.SelectedItem.ToString();
			}
			return "";
		}

		void setPortComboBoxItems(ComboBox comboBox, string[] ports, string previousPort)
		{
			comboBox.Items.Clear();
			int newIndex = ports.Length - 1;
			for(int i = 0; i < ports.Length; i++)
			{
				if (previousPort == ports[i])
					newIndex = i;
				comboBox.Items.Add(ports[i]);
			}
			if(newIndex != -1)
			{
				comboBox.SelectedIndex = newIndex;
			}
		}
		void scanForCOMPorts()
		{
			string[] newPorts = SerialPort.GetPortNames();
			setPorts(newPorts);
		}
		string backupsPath = "backups";
		string getSv6x66BackupDirectory()
		{
			return MiscUtils.getExecutableBackupsPath();
		}

		bool tryGetSv6x66RestorePath(out string fullPath)
		{
			fullPath = "";
			if (comboBoxFirmware.SelectedItem == null)
				return false;
			string fileName = comboBoxFirmware.SelectedItem.ToString();
			if (!fileName.StartsWith(Sv6x66BackupPrefix, StringComparison.Ordinal)
				|| !fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
				|| Path.GetFileName(fileName) != fileName)
				return false;
			string backupDirectory = getSv6x66BackupDirectory();
			string candidate = Path.GetFullPath(Path.Combine(backupDirectory, fileName));
			if (!MiscUtils.isCanonicalFileInDirectory(candidate, backupDirectory))
				return false;
			try
			{
				if (!File.Exists(candidate) || new FileInfo(candidate).Length != Sv6x66FullFlashBytes)
					return false;
			}
			catch
			{
				return false;
			}
			fullPath = candidate;
			return true;
		}

		bool isSv6x66RestoreSelectionEligible()
		{
			string unusedPath;
			return curType == BKType.SV6X66
				&& worker == null
				&& checkBoxShowAdvanced.Checked
				&& checkBoxAllowBackup.Checked
				&& tryGetSv6x66RestorePath(out unusedPath);
		}

		bool isSv6x66ApplicationSelectionEligible()
		{
			return curType == BKType.SV6X66 && worker == null
				&& !string.IsNullOrEmpty(chosenSourceFile)
				&& SvApplicationImage.IsApplicationFileName(chosenSourceFile)
				&& File.Exists(chosenSourceFile);
		}

		static string sha256Hex(byte[] data)
		{
			using (SHA256 sha256 = SHA256.Create())
				return string.Concat(sha256.ComputeHash(data).Select(value => value.ToString("x2")));
		}

		private void Control_DragEnter(object sender, DragEventArgs e)
		{
			if (e.Data.GetDataPresent(DataFormats.FileDrop))
				e.Effect = DragDropEffects.Copy;
			else
				e.Effect = DragDropEffects.None;
		}

		private void Control_DragDrop(object sender, DragEventArgs e)
		{
			string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
			foreach (string file in files)
			{
				////MessageBox.Show("Dropped file: " + file);
				//CustomParms cp = new CustomParms();
				//cp.sourceFileName = file;
				//cp.ofs = 0;
				//cp.len = File.ReadAllBytes(file).Length;
				//doCustomWrite(cp);
				comboBoxFirmware.DropDownStyle = ComboBoxStyle.DropDown;
				comboBoxFirmware.Text = file;
				chosenSourceFile = file;
				addLog("You dragged and dropped file " + file + "...",Color.Black);
				if (curType == BKType.SV6X66) updatePlatformCapabilityButtons(worker == null);
			}
		}
		// string label_startRead = "Start Read Flash (Full backup)";
		///string label_stopRead = "Stop Read Flash";
		private void Form1_Load(object sender, EventArgs e)
		{
			this.DragEnter += new DragEventHandler(Control_DragEnter);
			this.DragDrop += new DragEventHandler(Control_DragDrop);

			var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
			var snsettings = (SettingsSection)config.GetSection("system.net/settings");
			snsettings.HttpWebRequest.UseUnsafeHeaderParsing = true;
			config.Save(ConfigurationSaveMode.Modified);
			ConfigurationManager.RefreshSection("system.net/settings");

			buttonIPSaveResultToFile.Enabled = false;
			setIPDeviceButtonsState(false);

			//  var t = new TuyaConfig();
			//  t.fromFile("W:/GIT/BK7231GUIFlashTool/BK7231Flasher/bin/Release/backups/CBU_2Gang_8046/readResult_BK7231N_QIO_2023-05-5--13-40-02.bin");
			//// t.extractKeys();

			tabControl1.TabPages.Remove(tabPagePageTool);
			if (Directory.Exists(backupsPath) == false)
			{
				Directory.CreateDirectory(backupsPath);
			}

			if (formObkCfg == null)
			{
				formObkCfg = new FormOBKConfig(this);
			}

			settings = MySettings.CreateAndLoad("settings.cfg");
			if (settings.RemoveKey("ScannerPass"))
			{
				settings.Save("settings.cfg");
			}
			List<string> recentIPs = settings.getRecentIPs();
			for(int i = recentIPs.Count-1; i >= 0; i--)
			{
				string s = recentIPs[i];
				comboBoxIP.Items.Add(s);
			}
			if(comboBoxIP.Items.Count == 0)
			{
				comboBoxIP.Items.Add(("127.0.0.1"));
			}
			comboBoxIP.SelectedIndex = 0;

			// do not overwrite user settings with default
			bWithinSettingSet = true;

			scanForCOMPorts();
			//setButtonReadLabel(label_startRead);
			setButtonStates(true);

			comboBoxChipType.DropDownStyle = ComboBoxStyle.DropDownList;
			comboBoxUART.DropDownStyle = ComboBoxStyle.DropDownList;
			comboBoxFirmware.DropDownStyle = ComboBoxStyle.DropDownList;
			comboBoxReadRomChipType.DropDownStyle = ComboBoxStyle.DropDownList;
			comboBoxReadRomUART.DropDownStyle = ComboBoxStyle.DropDownList;
			comboBoxReadRomBaudRate.DropDownStyle = ComboBoxStyle.DropDownList;

			populatePlatformComboBox(comboBoxChipType, BKType.BK7231N);
			populateReadRomPlatformComboBox();
			foreach(var baud in BaudRates)
			{
				comboBoxBaudRate.Items.Add(baud);
				comboBoxReadRomBaudRate.Items.Add(baud);
			}

			comboBoxBaudRate.SelectedIndex = 0;
			comboBoxReadRomBaudRate.SelectedIndex = 0;

			try
			{
				if (false==Directory.Exists(firmwaresPath))
				{
					Directory.CreateDirectory(firmwaresPath);
				}
			}
			catch(Exception ex)
			{

			}
			refreshType();
			refreshFirmwaresList();

			if (false)
			{
				downloadLatestFor(BKType.BK7231N);
			}
			// allow overwrite old settings with new
			bWithinSettingSet = false;
			applySettings();
			refreshAdvancedOptions();
			refreshType();
			refreshFirmwaresList();

			//foreach(var c in this.Controls)
			//{
			//    if(c is LinkLabel)
			//    {
			//        LinkLabel ll = c as LinkLabel;
			//        ll.LinkClicked += genericLinkClicked;
			//    }
			//    if(c is TabControl)
			//    {
			//        TabControl tc = c as TabControl;
			//        foreach(TabPage tp in tc.TabPages)
			//        {
			//            tp.
			//        }
			//    }
			//}
		}
		MySettings settings;
		void applySettings()
		{
			if (settings.HasKey("BaudRate"))
			{
				setComboBoxValueByContent(comboBoxBaudRate, settings.FindKeyValue("BaudRate"));
			}
			if (settings.HasKey("Platform"))
			{
				setComboBoxValueByContent(comboBoxChipType, settings.FindKeyValue("Platform"));
			}
			if (settings.HasKey("Port"))
			{
				setComboBoxValueByContent(comboBoxUART, settings.FindKeyValue("Port"));
			}
			if (settings.HasKey("ReadRomBaudRate"))
			{
				setComboBoxValueByContent(comboBoxReadRomBaudRate, settings.FindKeyValue("ReadRomBaudRate"));
			}
			if (settings.HasKey("ReadRomPort"))
			{
				setComboBoxValueByContent(comboBoxReadRomUART, settings.FindKeyValue("ReadRomPort"));
			}
			updateReadRomControlsForSelectedPlatform(false);
			if (settings.HasKey("bAdvanced"))
			{
			   checkBoxShowAdvanced.Checked = settings.FindKeyValueBool("bAdvanced");
			}
			if (settings.HasKey("bAllowBackupRestore"))
			{
				checkBoxAllowBackup.Checked = settings.FindKeyValueBool("bAllowBackupRestore");
			}
			if (settings.HasKey("ScannerUser"))
			{
				textBoxIPScannerUser.Text = settings.FindKeyValue("ScannerUser");
			}
			if (settings.HasKey("ScannerFirst"))
			{
				textBoxStartIP.Text = settings.FindKeyValue("ScannerFirst");
			}
			if (settings.HasKey("ScannerLast"))
			{
				textBoxEndIP.Text = settings.FindKeyValue("ScannerLast");
			}
			if (settings.HasKey("bUseCompressionIfPossible"))
			{
				chkUseCompression.Checked = settings.FindKeyValueBool("bUseCompressionIfPossible");
			}
		}
		public void setComboBoxValueByContent(ComboBox comboBox, string itemToSet)
		{
			int index = -1;
			for (int i = 0; i < comboBox.Items.Count; i++)
			{
				if (comboBox.Items[i].ToString() == itemToSet)
				{
					index = i;
					break;
				}
			}
			if(index != -1)
			{
				comboBox.SelectedIndex = index;
			}
		}

		void populatePlatformComboBox(ComboBox comboBox, BKType defaultType)
		{
			comboBox.Items.Clear();
			foreach(var chip in FlashPlatformCatalog.GetOrderedChipTypes())
			{
				comboBox.Items.Add(chip);
			}
			comboBox.SelectedIndex = comboBox.Items
				.Cast<ChipType>().ToList().FindIndex(x => x.Type == defaultType);
		}

		void populateReadRomPlatformComboBox()
		{
			bSuppressReadRomPlatformEvents = true;
			comboBoxReadRomChipType.Items.Clear();
			comboBoxReadRomChipType.Items.Add("");
			foreach(var chip in FlashPlatformCatalog.GetOrderedChipTypes()
				.Where(chip => RomReadCatalog.GetSupportedPlatforms().Contains(chip.Type)))
			{
				comboBoxReadRomChipType.Items.Add(chip);
			}
			comboBoxReadRomChipType.SelectedIndex = 0;
			bSuppressReadRomPlatformEvents = false;
		}

		BKType getSelectedChipType(ComboBox comboBox)
		{
			if(comboBox.SelectedItem is ChipType chip)
			{
				return chip.Type;
			}
			return BKType.Invalid;
		}
		void setSettingsKeyAndSave(string key, object value)
		{
			if (value == null)
				return;
			setSettingsKeyAndSave(key, value.ToString());
		}
		bool bWithinSettingSet;
		void setSettingsKeyAndSave(string key, string value)
		{
			if (bWithinSettingSet)
			{
				return;
			}
			bWithinSettingSet = true;
			settings.SetKeyValue(key, value);
			saveSettings();
			bWithinSettingSet = false;
		}
		void saveSettings()
		{
			settings.Save("settings.cfg");
		}
		int getBaudRateFromGUI()
		{
			return getBaudRateFromGUI(comboBoxBaudRate);
		}

		int getBaudRateFromGUI(ComboBox comboBox)
		{
			int r = 0;
			try
			{
				r = int.Parse(comboBox.Text);
			}
			catch(Exception ex)
			{
				return 0;
			}
			return r;
		}
		ProgressBar getActiveProgressBar()
		{
			return currentOperationUiTarget == OperationUiTarget.ReadRom ? progressBarReadRom : progressBar1;
		}

		RichTextBox getActiveLogTextBox()
		{
			return currentOperationUiTarget == OperationUiTarget.ReadRom ? textBoxReadRomLog : textBoxLog;
		}

		public void setProgress(int cur, int max)
		{
			if (cur > max)
				cur = max;
			ProgressBar activeProgressBar = getActiveProgressBar();
			activeProgressBar.BeginInvoke((MethodInvoker)delegate {
				// Running on the UI thread
				activeProgressBar.Maximum = max;
				activeProgressBar.Value = cur;
			});
		}
		public void setState(string s, Color col)
		{
		}
		public void addLog(string s, Color col)
		{
			addLogToTextBox(getActiveLogTextBox(), s, col);
		}

		public void setLogProgress(string s, Color col)
		{
			RichTextBox logTextBox = getActiveLogTextBox();
			logTextBox.Invoke((MethodInvoker)delegate {
				// Running on the UI thread
				RichTextUtil.ReplaceCurrentLine(logTextBox, s, col);
			});
		}

		void addReadRomLog(string s, Color col)
		{
			addLogToTextBox(textBoxReadRomLog, s, col);
		}

		void addLogToTextBox(RichTextBox logTextBox, string s, Color col)
		{
			logTextBox.Invoke((MethodInvoker)delegate {
				// Running on the UI thread
				RichTextUtil.AppendText(logTextBox, s, col);
			});
		}

		void addOperationLogLine(string text, Color color)
		{
			RichTextBox logTextBox = getActiveLogTextBox();
			logTextBox.Invoke((MethodInvoker)delegate {
				string linePrefix = logTextBox.TextLength > 0
					&& logTextBox.Text[logTextBox.TextLength - 1] != '\n'
					? Environment.NewLine
					: "";
				RichTextUtil.AppendText(logTextBox, linePrefix + text + Environment.NewLine, color);
			});
		}
		//public void setButtonReadLabel(string s)
		//{
		//    Singleton.buttonRead.Invoke((MethodInvoker)delegate {
		//        // Running on the UI thread
		//        Singleton.buttonRead.Text = s;
		//    });
		//}
		Task worker;
		string getSelectedSerialName()
		{
			return getSelectedSerialName(comboBoxUART);
		}

		string getSelectedSerialName(ComboBox comboBox)
		{
			if (comboBox.SelectedIndex != -1)
			{
				return comboBox.SelectedItem.ToString();
			}
			return "";
		}
		BaseFlasher flasher;
		string serialName;
		BKType curType;
		void refreshType()
		{
			refreshType(comboBoxChipType, comboBoxUART, comboBoxBaudRate);
			updateFlasherBaudRates();
			updateFlasherGuide();
		}

		void updateFlasherBaudRates()
		{
			if(comboBoxBaudRate.Items.Count == 0)
				return;

			bool fixedBaudList = curType == BKType.W600 || curType == BKType.SV6X66;
			int[] baudRates = curType == BKType.W600
				? BaudRates.Where(baud => baud != 1500000 && baud != 3000000).ToArray()
				: curType == BKType.SV6X66
					? BaudRates.Where(baud => baud != 3000000).ToArray()
					: BaudRates;
			object previousBaud = comboBoxBaudRate.SelectedItem;
			comboBoxBaudRate.DropDownStyle = fixedBaudList
				? ComboBoxStyle.DropDownList
				: ComboBoxStyle.DropDown;

			if(comboBoxBaudRate.Items.Cast<int>().SequenceEqual(baudRates))
				return;

			comboBoxBaudRate.Items.Clear();
			foreach(int baud in baudRates)
				comboBoxBaudRate.Items.Add(baud);

			if(previousBaud != null && comboBoxBaudRate.Items.Contains(previousBaud))
				comboBoxBaudRate.SelectedItem = previousBaud;
			else if(curType == BKType.W600 || curType == BKType.SV6X66)
				comboBoxBaudRate.SelectedItem = 2000000;
			else if(comboBoxBaudRate.Items.Count > 0)
				comboBoxBaudRate.SelectedIndex = 0;
		}

		void updateFlasherGuide()
		{
			BKType selectedType = getSelectedChipType(comboBoxChipType);
			groupBoxFlasherGuide.Text = "Connection guide: " + FlashPlatformCatalog.GetDisplayName(selectedType);
			string instructions = PlatformBootModeGuide.GetInstructions(selectedType);
			// The shared guide's missing-entry message is specific to the ROM reader.
			if (string.IsNullOrWhiteSpace(instructions)
				|| instructions == PlatformBootModeGuide.GetInstructions(BKType.Invalid))
			{
				instructions = "No connection instructions are available for this platform yet. "
					+ "Check the device's flashing documentation for wiring and boot-mode requirements.";
			}

			// Keep wiring lines together, with space between the explanatory paragraphs.
			string[] lines = instructions.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
			var formatted = new StringBuilder();
			for (int i = 0; i < lines.Length; i++)
			{
				if (i > 0)
				{
					formatted.AppendLine();
					if (!lines[i].StartsWith("- ") || !lines[i - 1].StartsWith("- "))
						formatted.AppendLine();
				}
				formatted.Append(lines[i]);
			}
			string text = formatted.ToString();
			if (textBoxFlasherGuide.Text != text)
			{
				textBoxFlasherGuide.Text = text;
				textBoxFlasherGuide.Select(0, 0);
				textBoxFlasherGuide.ScrollToCaret();
			}
		}

		void updateReadRomControlsForSelectedPlatform(bool logBootInstructions)
		{
			BKType selectedType = getSelectedChipType(comboBoxReadRomChipType);
			bSuppressReadRomTargetEvents = true;

			setReadRomTargetControl(radioButtonReadRomTargetEfuse, selectedType, RomReadKind.Efuse);
			setReadRomTargetControl(radioButtonReadRomTargetOtp, selectedType, RomReadKind.Otp);
			setReadRomTargetControl(radioButtonReadRomTargetRom, selectedType, RomReadKind.Rom);

			if (radioButtonReadRomTargetRom.Checked && !radioButtonReadRomTargetRom.Enabled)
				radioButtonReadRomTargetRom.Checked = false;
			if (radioButtonReadRomTargetOtp.Checked && !radioButtonReadRomTargetOtp.Enabled)
				radioButtonReadRomTargetOtp.Checked = false;
			if (radioButtonReadRomTargetEfuse.Checked && !radioButtonReadRomTargetEfuse.Enabled)
				radioButtonReadRomTargetEfuse.Checked = false;

			if (!radioButtonReadRomTargetRom.Enabled && !radioButtonReadRomTargetOtp.Enabled && !radioButtonReadRomTargetEfuse.Enabled)
			{
				radioButtonReadRomTargetRom.Checked = false;
				radioButtonReadRomTargetOtp.Checked = false;
				radioButtonReadRomTargetEfuse.Checked = false;
			}
			else if (!radioButtonReadRomTargetRom.Checked && !radioButtonReadRomTargetOtp.Checked && !radioButtonReadRomTargetEfuse.Checked)
			{
				if (radioButtonReadRomTargetEfuse.Enabled)
					radioButtonReadRomTargetEfuse.Checked = true;
				else if (radioButtonReadRomTargetOtp.Enabled)
					radioButtonReadRomTargetOtp.Checked = true;
				else
					radioButtonReadRomTargetRom.Checked = true;
			}

			bSuppressReadRomTargetEvents = false;
			updateReadRomBaudRates();
			updateReadRomRangeInfo();
			updateReadRomReadButtonState();
			if (logBootInstructions)
			{
				logReadRomBootInstructions(selectedType);
			}
		}

		void setReadRomTargetControl(RadioButton radioButton, BKType selectedType, RomReadKind kind)
		{
			RomReadTarget target = RomReadCatalog.GetTarget(selectedType, kind);
			radioButton.Enabled = target != null;
		}

		RomReadKind? getSelectedReadRomKind()
		{
			if (radioButtonReadRomTargetRom.Checked)
				return RomReadKind.Rom;
			if (radioButtonReadRomTargetOtp.Checked)
				return RomReadKind.Otp;
			if (radioButtonReadRomTargetEfuse.Checked)
				return RomReadKind.Efuse;
			return null;
		}

		RomReadTarget getSelectedReadRomTarget()
		{
			RomReadKind? kind = getSelectedReadRomKind();
			if (!kind.HasValue)
				return null;
			return RomReadCatalog.GetTarget(getSelectedChipType(comboBoxReadRomChipType), kind.Value);
		}

		void updateReadRomBaudRates()
		{
			RomReadTarget target = getSelectedReadRomTarget();
			object previousBaud = comboBoxReadRomBaudRate.SelectedItem;
			comboBoxReadRomBaudRate.Items.Clear();

			int[] baudRates = target != null && target.AllowedBaudRates.Length > 0 ? target.AllowedBaudRates : BaudRates;
			foreach (var baud in baudRates)
			{
				comboBoxReadRomBaudRate.Items.Add(baud);
			}

			if (previousBaud != null && comboBoxReadRomBaudRate.Items.Contains(previousBaud))
			{
				comboBoxReadRomBaudRate.SelectedItem = previousBaud;
			}
			else if (target != null && comboBoxReadRomBaudRate.Items.Contains(target.DefaultBaudRate))
			{
				comboBoxReadRomBaudRate.SelectedItem = target.DefaultBaudRate;
			}
			else if (comboBoxReadRomBaudRate.Items.Count > 0)
			{
				comboBoxReadRomBaudRate.SelectedIndex = 0;
			}
		}

		void updateReadRomRangeInfo()
		{
			RomReadTarget target = getSelectedReadRomTarget();
			if (target == null || target.Address.HasValue == false || target.Length.HasValue == false)
			{
				labelReadRomRangeStart.Text = "Space: -";
				labelReadRomRangeLength.Text = "Range: -";
				labelReadRomRangeEnd.Text = "Backend: -";
				labelReadRomRangeController.Text = "Ctrl: -";
				return;
			}

			int start = target.Address.Value;
			int length = target.Length.Value;
			int end = start + length;
			labelReadRomRangeStart.Text = "Space: " + formatReadRomDetailValue(target.AddressSpace);
			if(end > 0)
				labelReadRomRangeLength.Text = "Range: " + BaseFlasher.formatHex(start) + ".." + BaseFlasher.formatHex(end - 1) + " (" + BaseFlasher.formatHex(length) + ")";
			else
				labelReadRomRangeLength.Text = "Range: variable";
			labelReadRomRangeEnd.Text = "Backend: " + formatReadRomDetailValue(target.Backend);
			labelReadRomRangeController.Text = "Ctrl: " + formatReadRomDetailValue(target.Controller);
		}

		string formatReadRomDetailValue(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return "-";
			}
			return value;
		}

		void updateReadRomReadButtonState()
		{
			RomReadTarget target = getSelectedReadRomTarget();
			buttonReadRomRead.Enabled = worker == null && target != null;
		}

		void logReadRomBootInstructions(BKType selectedType)
		{
			if (selectedType == BKType.Invalid)
			{
				return;
			}
			string platformName = FlashPlatformCatalog.GetDisplayName(selectedType);
			addReadRomLog(Environment.NewLine + "ROM reader setup for " + platformName + ":" + Environment.NewLine, Color.DarkSlateGray);
			addReadRomLog(PlatformBootModeGuide.GetInstructions(selectedType) + Environment.NewLine, Color.DarkSlateGray);
		}

		void refreshType(ComboBox chipTypeComboBox, ComboBox uartComboBox, ComboBox baudRateComboBox)
		{
			curType = getSelectedChipType(chipTypeComboBox);
			applySerialControlState(chipTypeComboBox, uartComboBox, baudRateComboBox, true);
		}

		void applySerialControlState(ComboBox chipTypeComboBox, ComboBox uartComboBox, ComboBox baudRateComboBox,
			bool operationControlsEnabled, bool allowSerialSelectionWithoutPlatform = false)
		{
			BKType selectedType = getSelectedChipType(chipTypeComboBox);
			bool usesSerialPort = selectedType != BKType.Invalid && FlashPlatformCatalog.UsesSerialPort(selectedType);
			uartComboBox.Enabled = operationControlsEnabled && (usesSerialPort || allowSerialSelectionWithoutPlatform);
			baudRateComboBox.Enabled = operationControlsEnabled && (usesSerialPort
				|| (allowSerialSelectionWithoutPlatform && selectedType == BKType.Invalid));
		}
		bool interruptIfRequired()
		{
			if (worker != null)
			{
				var res = MessageBox.Show("Do you want to interrupt the current operation?", "Stop?", MessageBoxButtons.YesNo);
				if (res == DialogResult.Yes)
				{
					if(worker != null)
					{
						try
						{
							cts?.Cancel();
						}
						catch { }
						//worker.Abort();
					}
					if (sv6x66OperationLockActive)
					{
						addOperationLogLine("Interrupted by user.", Color.DarkOrange);
						return false;
					}
					worker = null;
					//setButtonReadLabel(label_startRead);
					setButtonStates(true);
					addOperationLogLine("Interrupted by user.", Color.DarkOrange);
				}
				return false;
			}
			return true;
		}
		bool doGenericOperationPreparations()
		{
			return doGenericOperationPreparations(comboBoxChipType, comboBoxUART, comboBoxBaudRate);
		}

		bool doReadRomOperationPreparations()
		{
			return doGenericOperationPreparations(comboBoxReadRomChipType, comboBoxReadRomUART, comboBoxReadRomBaudRate);
		}

		bool doGenericOperationPreparations(ComboBox chipTypeComboBox, ComboBox uartComboBox, ComboBox baudRateComboBox)
		{
			if (interruptIfRequired() == false)
			{
				return false;
			}
			refreshType(chipTypeComboBox, uartComboBox, baudRateComboBox);
			chosenBaudRate = getBaudRateFromGUI(baudRateComboBox);
			if (chosenBaudRate <= 0)
			{
				MessageBox.Show("Please enter a correct number for a baud rate.");
				return false;
			}
			if(uartComboBox.Enabled)
			{
				serialName = getSelectedSerialName(uartComboBox);
				if (serialName.Length <= 0)
				{
					MessageBox.Show("Please choose a correct serial port or connect one if not present.");
					return false;
				}
				if (EnsureTerminalPortAvailableForOperation(serialName) == false)
				{
					return false;
				}
			}
			else
			{
				serialName = "";
			}
			return true;
		}
		void downloadLatestFor(BKType type)
		{
			FormDownloader fd = new FormDownloader(this, type);
			fd.ShowDialog();
			refreshType();
			refreshFirmwaresList();
		}
		void clearUp()
		{
			if (flasher != null)
			{
				// cts.Cancel() can throw AggregateException if internal Task callbacks
				// fire when the task is already completing (RanToCompletion). Swallow it.
				try { cts.Cancel(); } catch { }
				flasher.Dispose();
				//flasher.closePort();
				flasher = null;
			}
		}
		
		void createFlasher(bool? finalReadback = null)
		{
			flasher = FlasherFactory.Create(curType, cts.Token);
			flasher.setBasic(this, serialName, curType, chosenBaudRate);
			flasher.setOverwriteBootloader(checkBoxOverwriteBootloader.Checked);
			flasher.setSkipKeyCheck(checkBoxSkipKeyCheck.Checked);
			flasher.setIgnoreCRCErr(chkIgnoreCRCErr.Checked);
			flasher.setUseCompression(chkUseCompression.Checked);
			flasher.setFinalReadbackVerification(finalReadback);
		}
		
		void testWrite()
		{
			clearUp();
			createFlasher();
			int startSector;
			int sectors;
			sectors = 5;
			startSector = BK7231Flasher.BOOTLOADER_SIZE;
			byte[] dat = new byte[sectors * BK7231Flasher.SECTOR_SIZE];
			int baseVal = BK7231Flasher.rand.Next();
			for(int i = 0; i < dat.Length; i++)
			{
				int useVal = baseVal + i;
				dat[i] = (byte)(useVal % 256);
			}
			flasher.doWrite(startSector, dat);
			worker = null;
			//setButtonReadLabel(label_startRead);
			clearUp();
			setButtonStates(true);
		}
		void testReadWrite()
		{
			clearUp();
			createFlasher();
			int startSector;
			int sectors;
			sectors = 2;
			startSector = BK7231Flasher.BOOTLOADER_SIZE;
			flasher.doTestReadWrite(startSector, sectors);
			worker = null;
			//setButtonReadLabel(label_startRead);
			clearUp();
			setButtonStates(true);
		}
		void doBackupAndFlashNew(bool? finalReadback)
		{
			clearUp();
			createFlasher(finalReadback);
			flasher.setBackupName(lastBackupNameEnteredByUser);
			int startSector = getBackupStartSectorForCurrentPlatform();
			int sectors = getBackupSectorCountForCurrentPlatform();
			flasher.doReadAndWrite(startSector, sectors, chosenSourceFile, WriteMode.ReadAndWrite);
			worker = null;
			//setButtonReadLabel(label_startRead);
			clearUp();
			setButtonStates(true);
		}
		internal void doCustomWrite(CustomParms cp)
		{
			using (new TerminalFlasherReservation(this))
			{
			showObkConfigFormIfPossible();
			if (doGenericOperationPreparations() == false)
			{
				return;
			}
			//setButtonReadLabel(label_stopRead);
			startWorkerThread(doOnlyFlashNew, cp);
			}
		}
		void doOnlyFlashNew(object oParm) => doOnlyFlashNew(oParm, null);

		void doOnlyFlashNew(object oParm, bool? finalReadback)
		{
			clearUp();
			createFlasher(finalReadback);
			int startSector;
			int sectors;
			CustomParms parms = null;
			if (oParm != null)
			{
				parms = oParm as CustomParms;
			}
			if(parms!=null)
			{
				startSector = parms.ofs;
				if(curType == BKType.XR806 || curType == BKType.XR809 || curType == BKType.XR872
					|| curType == BKType.ESP32 || curType == BKType.ESP32S2 || curType == BKType.ESP32C2
					|| curType == BKType.ESP32C3 || curType == BKType.ESP32C5 || curType == BKType.ESP32C6 || curType == BKType.ESP32C61
					|| curType == BKType.ESP32S3 || curType == BKType.ESP8266)
					startSector /= BK7231Flasher.SECTOR_SIZE;
				sectors = parms.len / BK7231Flasher.SECTOR_SIZE;
				chosenSourceFile = parms.sourceFileName;
			}
			else
			{
				startSector = getBackupStartSectorForCurrentPlatform();
				sectors = getBackupSectorCountForCurrentPlatform();
			}
			flasher.setCustomWriteMode(parms != null);
			flasher.doReadAndWrite(startSector, sectors, chosenSourceFile, WriteMode.OnlyWrite);
			worker = null;
			//setButtonReadLabel(label_startRead);
			clearUp();
			setButtonStates(true);
		}
		void doOnlyFlashOBKConfig()
		{
			clearUp();
			createFlasher();
			flasher.doReadAndWrite(0, 0, "", WriteMode.OnlyOBKConfig);
			worker = null;
			//setButtonReadLabel(label_startRead);
			clearUp();
			setButtonStates(true);
		}
		void detectChip()
		{
			clearUp();
			createFlasher();
			flasher.doDetect();
			worker = null;
			clearUp();
			setButtonStates(true);
		}
		int getBackupStartSectorForCurrentPlatform()
		{
			switch(curType)
			{
				case BKType.BK7231T:
				case BKType.BK7231U:
				case BKType.BK7252:
					return BK7231Flasher.BOOTLOADER_SIZE;
				default:
					return 0;

			}
		}
		int getBackupSectorCountForCurrentPlatform()
		{
#if false
			int sectors;
			sectors = (BK7231Flasher.FLASH_SIZE - getBackupStartSectorForCurrentPlatform()) / BK7231Flasher.SECTOR_SIZE;
			return sectors;
#else
			int sectors;
			sectors = (BK7231Flasher.FLASH_SIZE) / BK7231Flasher.SECTOR_SIZE;
			return sectors;
#endif
		}
		void restoreRF()
		{
			clearUp();
			createFlasher();
			int startOfs = RFPartitionUtil.getRFOffset(curType);
			byte[] data = RFPartitionUtil.constructRFDataFor(curType, BK7231Flasher.SECTOR_SIZE);
			if(startOfs < 0 || data.Length == 0)
			{
				worker = null;
				clearUp();
				setButtonStates(true);
				if(startOfs < 0) addLog($"RF restore is not supported on {curType}" + Environment.NewLine, Color.Red);
				else addLog("Generated RF partition is empty, not supported?" + Environment.NewLine, Color.DarkOrange);
				return;
			}
			flasher.doWrite(startOfs, data);
			if (flasher is BK7231Flasher bkFlasher && bkFlasher.LastOperationSucceeded)
			{
				addLog("RF partition restore completed." + Environment.NewLine, Color.Green);
			}
			worker = null;
			//setButtonReadLabel(label_startRead);
			clearUp();
			setButtonStates(true);
		}
		internal void restoreRFfromBackup(byte[] fileData)
		{
			using (new TerminalFlasherReservation(this))
			{
			clearUp();
			if(doGenericOperationPreparations() == false)
			{
				return;
			}
			startWorkerThread(restoreRFbkp, fileData);
			}
		}
		void restoreRFbkp(object fileData)
		{
			createFlasher();
			int startOfs = RFPartitionUtil.getRFOffset(curType);
			byte[] data = RFPartitionUtil.getRFFromBackup((byte[])fileData, curType, out int addr);
			if(startOfs < 0 || data.Length == 0)
			{
				worker = null;
				clearUp();
				setButtonStates(true);
				if(startOfs < 0) addLog($"RF restore is not supported on {curType}" + Environment.NewLine, Color.Red);
				else addLog("RF partition not found in backup. You may need to use \"Restore RF part\"" + Environment.NewLine, Color.DarkOrange);
				return;
			}
			addLog($"RF partition found at 0x{addr:X2}" + Environment.NewLine, Color.Green);
			flasher.doWrite(startOfs, data);
			worker = null;
			//setButtonReadLabel(label_startRead);
			clearUp();
			setButtonStates(true);
		}

		void eraseAll()
		{
			try
			{
				clearUp();
				createFlasher();
				int startOfs = BK7231Flasher.BOOTLOADER_SIZE;
				int sectors = (BK7231Flasher.FLASH_SIZE - startOfs) / BK7231Flasher.SECTOR_SIZE;
				flasher.doErase(startOfs, sectors, true);
			}
			catch(Exception ex)
			{
				addLog("Erase error: " + ex.Message + Environment.NewLine, Color.Red);
			}
			finally
			{
				worker = null;
				//setButtonReadLabel(label_startRead);
				setButtonStates(true);
				try { clearUp(); }
				catch(Exception cleanEx) { addLog("Erase cleanup error: " + cleanEx.Message + Environment.NewLine, Color.Red); }
			}
		}
		void readThread(object oParm) => readThread(oParm, null);

		void readThread(object oParm, bool? finalReadback)
		{
			bool ownsSv6x66OperationCompletion = sv6x66OperationLockActive
				&& activeOperationPlatform == BKType.SV6X66;
			try
			{
			bool isFullRead = true;
			CustomParms parms = null;
			if(oParm != null)
			{
				parms = oParm as CustomParms;
			}
			clearUp();
			createFlasher(finalReadback);
			flasher.setBackupName(lastBackupNameEnteredByUser);
			// thanks to wrap-around hack, we can read from start correctly
			int startSector;
			int sectors;
			if (parms!= null)
			{
				startSector = parms.ofs;
				if(curType == BKType.XR806 || curType == BKType.XR809 || curType == BKType.XR872
					|| curType == BKType.ESP32 || curType == BKType.ESP32S2 || curType == BKType.ESP32C2
					|| curType == BKType.ESP32C3 || curType == BKType.ESP32C5 || curType == BKType.ESP32C6 || curType == BKType.ESP32C61
					|| curType == BKType.ESP32S3 || curType == BKType.ESP8266)
					startSector /= BK7231Flasher.SECTOR_SIZE;
				sectors = parms.len / BK7231Flasher.SECTOR_SIZE;
				isFullRead = false;
			}
			else if(curType == BKType.SV6X66)
			{
				startSector = 0;
				sectors = 0x200000 / BK7231Flasher.SECTOR_SIZE;
			}
			else if(curType == BKType.BK7252)
			{
				startSector = 0x0;
				sectors = getBackupSectorCountForCurrentPlatform();
				addLog("^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*" + Environment.NewLine, Color.DarkOrange);
				addLog("BK7252 mode - full QIO backup will use BK7252U mapped reads." + Environment.NewLine, Color.DarkOrange);
				addLog("^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*^*" + Environment.NewLine, Color.DarkOrange);
			}
			else
			{
				startSector = 0x0;// getBackupStartSectorForCurrentPlatform();
				sectors = getBackupSectorCountForCurrentPlatform();
			}
			flasher.doRead(startSector, sectors, isFullRead);
			SV6x66Flasher sv6x66Flasher = flasher as SV6x66Flasher;
			if (sv6x66Flasher == null || sv6x66Flasher.LastOperationSucceeded)
			{
				bool saveSucceeded = flasher.saveReadResult(startSector);
				if (saveSucceeded && sv6x66Flasher != null)
					refreshFirmwaresListOnUiThread();
			}
			}
			catch(OperationCanceledException) when(cts != null && cts.IsCancellationRequested)
			{
			}
			catch(Exception exception)
			{
				if(!IsDisposed && !Disposing)
					addOperationLogLine("Read operation failed: " + exception.Message.TrimEnd('\r', '\n'), Color.Red);
			}
			finally
			{
				if (ownsSv6x66OperationCompletion)
					completeSv6x66Operation();
				else
				{
					worker = null;
			//setButtonReadLabel(label_startRead);
					try { clearUp(); }
					catch(Exception exception)
					{
						if(!IsDisposed && !Disposing)
							addOperationLogLine("Read cleanup failed: " + exception.Message.TrimEnd('\r', '\n'), Color.Red);
					}
					finally
					{
						if(!IsDisposed && !Disposing)
							setButtonStates(true);
					}
				}
			}
		}

		void restoreSv6x66Thread(object state)
		{
			bool ownsSv6x66OperationCompletion = sv6x66OperationLockActive
				&& activeOperationPlatform == BKType.SV6X66;
			try
			{
				Sv6x66RestoreRequest request = state as Sv6x66RestoreRequest;
				if (request == null)
					throw new InvalidOperationException("SV6x66 restore request is missing.");
				clearUp();
				createFlasher(request.FinalReadbackVerification);
				SV6x66Flasher sv6x66Flasher = flasher as SV6x66Flasher
					?? throw new InvalidOperationException("SV6x66 restore worker received the wrong flasher.");
				sv6x66Flasher.doRestoreFullBackup(request.SourcePath, request.Image, request.Sha256);
			}
			catch (Exception exception)
			{
				addLog("SV6x66 restore stopped: " + exception.Message + Environment.NewLine, Color.Red);
			}
			finally
			{
				if (ownsSv6x66OperationCompletion)
					completeSv6x66Operation();
			}
		}

		void installSv6x66ApplicationThread(object state)
		{
			try
			{
				Sv6x66ApplicationRequest request = state as Sv6x66ApplicationRequest
					?? throw new InvalidOperationException("SV6x66 application request is missing.");
				clearUp();
				createFlasher(request.FinalReadbackVerification);
				SV6x66Flasher target = flasher as SV6x66Flasher
					?? throw new InvalidOperationException("SV6x66 application worker received the wrong flasher.");
				if (request.BackupFirst) target.setBackupName(request.BackupName);
				target.doInstallApplication(request.Image, request.BackupFirst);
				if (request.BackupFirst) refreshFirmwaresListOnUiThread();
			}
			catch (Exception exception)
			{
				addLog("SV6x66 application installation stopped: " + exception.Message + Environment.NewLine, Color.Red);
			}
			finally { completeSv6x66Operation(); }
		}

		void blankThread(object oParm)
		{
			clearUp();
			createFlasher();
			flasher.doRead(0, 0, true);

			bool bBlank = true;
			int nonBlank = 0;
			byte[] data = flasher.getReadResult();
			if(data == null)
			{
				worker = null;
				//setButtonReadLabel(label_startRead);
				clearUp();
				setButtonStates(true);
				return;
			}
			for (int i = 0; i < data.Length; i++)
			{
				if (data[i] != 0xff)
				{
					bBlank = false;
					nonBlank++;
				}
			}
			if (bBlank)
			{
				addLog("Blank" + Environment.NewLine, Color.Green);
			}
			else
			{
				addLog("Not blank" + Environment.NewLine, Color.Yellow);
			}
			worker = null;
			//setButtonReadLabel(label_startRead);
			clearUp();
			setButtonStates(true);
		}
		void doOnlyReadOBKConfig()
		{
			clearUp();
			// thanks to wrap-around hack, we can read from start correctly
			int startSector = OBKFlashLayout.getConfigLocation(curType, out var sectors);
			if (IsBeken && (sectors <= 0 || startSector < 0))
			{
				addLog($"OBK config location is not defined for {curType}." + Environment.NewLine, Color.Red);
				worker = null;
				setButtonStates(true);
				return;
			}
			createFlasher();
			if(curType == BKType.BL602 || curType == BKType.BL702 || curType == BKType.BL616)
			{
				addLog("Reading partitions..." + Environment.NewLine, Color.Black);
				if(curType == BKType.BL702)
				{
					flasher.doRead(0x1000, 1);
				}
				else
				{
					flasher.doRead(0xE000, 1);
				}
				
				var ptdata = flasher.getReadResult();
				if(ptdata == null)
				{
					worker = null;
					clearUp();
					setButtonStates(true);
					return;
				}
				try
				{
					var partition = BL602Utils.PT_Parse(ptdata).First(x => x.Name == "PSM");
					startSector = (int)partition.Address0;
					sectors = (int)partition.Length0 / BK7231Flasher.SECTOR_SIZE;
				}
				catch(InvalidOperationException)
				{
					addLog("No PSM partition! Can't read config." + Environment.NewLine, Color.Red);
					worker = null;
					clearUp();
					setButtonStates(true);
					throw;
				}
				catch(InvalidDataException ex)
				{
					addLog($"Partition error: {ex.Message}" + Environment.NewLine, Color.Red);
					addLog($"Can't read config." + Environment.NewLine, Color.Red);
					worker = null;
					clearUp();
					setButtonStates(true);
					throw;
				}
				catch(Exception ex)
				{
					worker = null;
					clearUp();
					setButtonStates(true);
				}
			}

			if(curType == BKType.XR806 || curType == BKType.XR809 || curType == BKType.XR872)
			{
				flasher.doRead(startSector / BK7231Flasher.SECTOR_SIZE, sectors);
			}
			else if(curType == BKType.BL602 || curType == BKType.BL702 || curType == BKType.BL616)
			{
				// do it like that so that there would be no need for re-sync
				((BL602Flasher)flasher).doReadInternal(startSector, sectors * BK7231Flasher.SECTOR_SIZE);
			}
			else
			{
				flasher.doRead(startSector, sectors);
			}
			byte[] res = flasher.getReadResult();
			bool bError = formObkCfg.tryToLoadOBKConfig(res, curType, false);
			if(bError)
			{
				addLog("OBK config load failed." + Environment.NewLine, Color.DarkOrange);
			}
			else
			{
				addLog("OBK config loaded. You can now view it by clicking 'Change OBK settings' button."+Environment.NewLine, Color.Black);
				addLog("You can also edit it however you want." + Environment.NewLine, Color.Black);
				addLog("You can also use 'Write OBK config' button to write it back with your changes." + Environment.NewLine, Color.Black);
			}
			worker = null;
			//setButtonReadLabel(label_startRead);
			clearUp();
			setButtonStates(true);
		}
		public static string getFirmwarePrefix(BKType t)
		{
			switch(t)
			{
				case BKType.BK7231N:
				case BKType.BK7231M:
				case BKType.BK7236:
				case BKType.BK7238:
				case BKType.BK7239N:
				case BKType.BK7252N:
				case BKType.BK7258:
					return $"Open{t}_QIO_";
				case BKType.BK7231T:
				case BKType.BK7231U:
				case BKType.BK7252:
					return $"Open{t}_UA_";
				default:
					return $"Open{t}_";
			}
		}
		public bool checkFirmwareForCurType(string s)
		{
			bool sv6x66Backup = s.StartsWith(Sv6x66BackupPrefix, StringComparison.Ordinal);
			if (s.StartsWith("readResult_"))
			{
				s = s.Substring("readResult_".Length);
				s = "Open" + s;
			}
			switch(curType)
			{
				case BKType.SV6X66:
					return (sv6x66Backup && s.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
						|| (s.StartsWith("OpenSV6X66_", StringComparison.Ordinal)
							&& SvApplicationImage.IsApplicationFileName(s));
				case BKType.BK7231T:
				case BKType.BK7231U:
				case BKType.BK7231N:
				case BKType.BK7231M:
				case BKType.BK7236:
				case BKType.BK7238:
				case BKType.BK7239N:
				case BKType.BK7252:
				case BKType.BK7252N:
				case BKType.BK7258:
					if(bIsBekenFirmwareForCurType(s))
					{
						return true;
					}
					break;
				case BKType.BekenSPI:
					if(s.StartsWith("OpenBK", StringComparison.OrdinalIgnoreCase)
						&& s.IndexOf("_QIO_", StringComparison.OrdinalIgnoreCase) >= 0
						&& s.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
					break;
				case BKType.W600:
				case BKType.W800:
					if(s.StartsWith($"Open{curType}_") && (s.EndsWith(".fls") || s.EndsWith(".bin")))
					{
						return true;
					}
					break;
				default:
					if(s.StartsWith($"Open{curType}_"))
					{
						return true;
					}
					break;
			}

			/*string prefix = getFirmwarePrefix(curType);
			if (s.StartsWith(prefix))
			{
				return true;
			}*/
			return false;
		}
		bool bIsBekenFirmwareForCurType(string s)
		{
			string platformPrefix = $"Open{curType}_";
			if (s.StartsWith(platformPrefix, StringComparison.OrdinalIgnoreCase) == false)
				return false;

			string rest = s.Substring(platformPrefix.Length);
			return rest.StartsWith("QIO_", StringComparison.OrdinalIgnoreCase)
				|| rest.StartsWith("UA_", StringComparison.OrdinalIgnoreCase)
				|| rest.IndexOf("_QIO_", StringComparison.OrdinalIgnoreCase) > 0
				|| rest.IndexOf("_UA_", StringComparison.OrdinalIgnoreCase) > 0;
		}
		public OBKConfig getConfig()
		{
			  return formObkCfg.getCFG();
		}
		public OBKConfig getConfigToWrite()
		{
			if (checkBoxAutoOBKConfig.Checked)
			{
				if (IsBeken && OBKFlashLayout.hasConfigLocation(curType) == false)
				{
					addLog($"Automatic OBK config injection is not supported on {curType}; continuing without it." + Environment.NewLine, Color.DarkOrange);
					return null;
				}
				return formObkCfg.getCFG();
			}
			return null;
		}
		public void onReadResultQIOSaved(byte[] dat, string lastEncryptionKey, string fullPath)
		{
			bool obkConfigExtracted = false;
			if (checkBoxReadOBKConfig.Checked)
			{
				addLog("Backup created, now will attempt to extract OBK config." + Environment.NewLine, Color.Gray);
				if ((IsBeken && OBKFlashLayout.hasConfigLocation(curType) == false)
					|| curType == BKType.RTL8721DA || curType == BKType.RTL8720E)
				{
					addLog("Can't extract OBK config from backup for this chip." + Environment.NewLine, Color.DarkRed);
				}
				else if(formObkCfg.tryToLoadOBKConfig(dat, curType))
				{
					addLog("OBK config not found." + Environment.NewLine, Color.DarkRed);
				}
				else
				{
					addLog("OBK config extracted." + Environment.NewLine, Color.Green);
					obkConfigExtracted = true;
				}
			}
			else
			{
				addLog("Backup created, but OBK config reading is disabled on GUI, skipping extraction." + Environment.NewLine, Color.Gray);
			}

			if (obkConfigExtracted && curType == BKType.W600)
			{
				addLog("Valid OBK config found; automatic Tuya config scan skipped." + Environment.NewLine, Color.Gray);
			}
			else if (checkBoxAutoReadTuya.Checked == false)
			{
				addLog("Backup created, but Tuya config reading is disabled on GUI, skipping extraction."+Environment.NewLine, Color.Gray);
			}
			else
			{
				byte[] mac = MACOperations(dat);
				addLog("Backup created, will now attempt to extract Tuya config." + Environment.NewLine, Color.Gray);
				try
				{
					TuyaConfig tc = new TuyaConfig();
					if (tc.fromBytes(dat) == false)
					{
						bool classicExtractFailed = tc.extractKeys();
						bool hasEnhancedFallback = classicExtractFailed && tc.hasEnhancedExtractionData();
						if (!classicExtractFailed || hasEnhancedFallback)
						{
							Singleton.buttonRead.Invoke((MethodInvoker)delegate {
								// Running on the UI thread
								FormExtractedConfig fo = new FormExtractedConfig();
								fo.showConfig(tc);
								if(mac != null) fo.showMAC(mac);
								fo.showEncryption(lastEncryptionKey);
								fo.Show();
							});
							// also pass to new config window
							formObkCfg.loadFromTuyaConfig(tc);

							addLog("Tuya config extracted and shown." + Environment.NewLine, Color.Green);
						}
						else
						{
							addLog("Sorry, failed to extract keys from Tuya Config in backup binary." + Environment.NewLine, Color.DarkOrange);
						}
					}
					else
					{
						addLog("Sorry, failed to find Tuya Config in backup binary." + Environment.NewLine, Color.DarkOrange);
					}
				}
				catch (Exception ex)
				{
					addLog("Sorry, failed to find Tuya Config in backup binary due to an unknown exception " + ex.ToString() + "" + Environment.NewLine, Color.DarkRed);
				}
			}
		}
		private byte[] MACOperations(byte[] data)
		{
			byte[] mac = null;
			if(IsBeken)
			{
				if (RFPartitionUtil.getRFOffset(curType) < 0)
				{
					addLog($"RF/MAC extraction is not defined for {curType}." + Environment.NewLine, Color.DarkOrange);
					return null;
				}
				mac = RFPartitionUtil.getMACFromQio(data, curType, out var isRFFixRequired);
				if(isRFFixRequired)
				{
					addLog("Your device requires moving or recreating RF partition." + Environment.NewLine, Color.DarkOrange);
					addLog("To do it, first enable \"Show advanced options\"." + Environment.NewLine, Color.DarkOrange);
					addLog("Moving (the best way) - press \"Custom\", \"Restore RF from backup\" and select your backup." + Environment.NewLine, Color.DarkOrange);
					addLog("Recreating - press \"Restore RF part\"." + Environment.NewLine, Color.DarkOrange);
					addLog("If you are flashing via \"Backup and flash new\", then it will be moved automatically." + Environment.NewLine, Color.DarkOrange);
				}
			}
			else
			{
				try
				{
					mac = flasher.ReadMAC();
				}
				catch
				{
					mac = null;
				}
			}
			if(mac != null)
			{
				addLog($"MAC seems to be {mac[0]:X2}:{mac[1]:X2}:{mac[2]:X2}:{mac[3]:X2}:{mac[4]:X2}:{mac[5]:X2}" + Environment.NewLine, Color.Green);
				formObkCfg.onMACLoaded(mac, curType);
			}
			return mac;
		}
		public int addToFirmaresList(string dir, bool reportErrors = false)
		{
			string[] files;
			try
			{
				files = Directory.GetFiles(dir);
			}
			catch (Exception ex)
			{
				if (reportErrors)
					addLog("Failed to list backup directory " + Path.GetFullPath(dir)
						+ ": " + ex.Message + Environment.NewLine, Color.Red);
				return 0;
			}
			for (int i = 0; i < files.Length; i++)
			{
				string fname = files[i];
				fname = Path.GetFileName(fname);
				if (checkFirmwareForCurType(fname) == false)
				{
					continue;
				}
				comboBoxFirmware.Items.Add(fname);
			}
			return files.Length;
		}
		public void refreshFirmwaresList()
		{
			string previousSelection = comboBoxFirmware.SelectedItem as string;
			string previousSource = chosenSourceFile;
			comboBoxFirmware.Items.Clear();
			bool firmwareDirectoryExists = Directory.Exists(firmwaresPath);
			int found = 0;
			if (firmwareDirectoryExists)
				found += addToFirmaresList(firmwaresPath);
			if (checkBoxAllowBackup.Checked && checkBoxShowAdvanced.Checked)
			{
				string backupDirectory = curType == BKType.SV6X66
					? getSv6x66BackupDirectory()
					: backupsPath;
				if (Directory.Exists(backupDirectory))
					found += addToFirmaresList(backupDirectory, curType == BKType.SV6X66);
			}
			labelMatchingFirmwares.Text = "" + found + " total bins, " + comboBoxFirmware.Items.Count + " matching.";

			if (previousSelection != null && comboBoxFirmware.Items.Contains(previousSelection))
			{
				comboBoxFirmware.SelectedItem = previousSelection;
			}
			else if (previousSelection == null && !string.IsNullOrEmpty(previousSource) && File.Exists(previousSource))
			{
				comboBoxFirmware.SelectedIndex = -1;
				comboBoxFirmware.DropDownStyle = ComboBoxStyle.DropDown;
				comboBoxFirmware.Text = previousSource;
				chosenSourceFile = previousSource;
			}
			else if (comboBoxFirmware.Items.Count > 0)
			{
				comboBoxFirmware.SelectedIndex = 0;
			}
			else
			{
				// If we previously had a manual (drag/drop or browse) path in the textbox, clear it.
				comboBoxFirmware.SelectedIndex = -1;
				comboBoxFirmware.Text = "";
				chosenSourceFile = "";
			}
			updatePlatformCapabilityButtons(worker == null);
		}

		void refreshFirmwaresListOnUiThread()
		{
			if (InvokeRequired)
				Invoke((MethodInvoker)delegate { refreshFirmwaresList(); });
			else
				refreshFirmwaresList();
		}

		string getFirmwareCleanupDirectory()
		{
			string directory = Path.GetFullPath(firmwaresPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			foreach (string backupPath in new[] { Path.GetFullPath(backupsPath), getSv6x66BackupDirectory() })
			{
				string backup = backupPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				if (string.Equals(directory, backup, StringComparison.OrdinalIgnoreCase)
					|| directory.StartsWith(backup + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
					|| backup.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
					throw new IOException("Firmware and backup directories overlap; cleanup cancelled.");
				foreach (string path in new[] { directory, backup })
					for (DirectoryInfo ancestor = new DirectoryInfo(path); ancestor != null; ancestor = ancestor.Parent)
						if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
							throw new IOException("Linked firmware or backup directory; cleanup cancelled: " + ancestor.FullName);
			}
			return directory;
		}

		public void clearFirmwaresList()
		{
			if (worker != null)
			{
				addLog("Firmware cleanup unavailable during an operation." + Environment.NewLine, Color.DarkOrange);
				return;
			}
			string previousSelection = comboBoxFirmware.SelectedItem as string;
			string previousSource = chosenSourceFile;
			try
			{
				string directory = getFirmwareCleanupDirectory();
				if (!Directory.Exists(directory))
				{
					addLog("No firmware cache files to remove." + Environment.NewLine, Color.Black);
					return;
				}
				var files = new List<FileInfo>();
				foreach (string path in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly))
				{
					var file = new FileInfo(path);
					string extension = file.Extension;
					if (!MiscUtils.isCanonicalFileInDirectory(path, directory)
						|| (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
						|| file.Name.StartsWith("readResult_", StringComparison.OrdinalIgnoreCase)
						|| file.Name.IndexOf("backup", StringComparison.OrdinalIgnoreCase) >= 0
						|| !(extension.Equals(".bin", StringComparison.OrdinalIgnoreCase)
							|| extension.Equals(".img", StringComparison.OrdinalIgnoreCase)
							|| extension.Equals(".fls", StringComparison.OrdinalIgnoreCase)))
						continue;
					files.Add(file);
				}
				files = files.OrderByDescending(file => file.LastWriteTimeUtc)
					.ThenBy(file => file.Name, StringComparer.Ordinal).ToList();
				if (files.Count == 0)
				{
					addLog("No old firmware cache images to remove." + Environment.NewLine, Color.Black);
					return;
				}
				FileInfo newest = files[0];
				addLog("Keeping newest firmware cache image by last-modified UTC: " + newest.Name
					+ " (" + newest.LastWriteTimeUtc.ToString("O") + ")." + Environment.NewLine, Color.Black);
				for (int index = 1; index < files.Count; index++)
				{
					FileInfo file = files[index];
					try
					{
						if (!string.Equals(getFirmwareCleanupDirectory(), directory, StringComparison.OrdinalIgnoreCase)
							|| !MiscUtils.isCanonicalFileInDirectory(file.FullName, directory)
							|| !File.Exists(newest.FullName)
							|| (File.GetAttributes(newest.FullName) & FileAttributes.ReparsePoint) != 0)
							throw new IOException("Firmware cache changed; cleanup cancelled.");
						var current = new FileInfo(file.FullName);
						if ((current.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
							|| current.Length != file.Length || current.LastWriteTimeUtc != file.LastWriteTimeUtc)
							throw new IOException("File changed or is linked; not removed.");
						File.Delete(file.FullName);
						addLog("Removed old firmware cache image: " + file.Name + Environment.NewLine, Color.Black);
					}
					catch (Exception exception)
					{
						addLog("Failed to remove " + file.Name + ": " + exception.Message + Environment.NewLine, Color.Red);
					}
				}
			}
			catch (Exception exception)
			{
				addLog("Firmware cleanup failed: " + exception.Message + Environment.NewLine, Color.Red);
			}
			finally
			{
				refreshFirmwaresList();
				if (previousSelection != null)
					setComboBoxValueByContent(comboBoxFirmware, previousSelection);
				else if (!string.IsNullOrWhiteSpace(previousSource) && File.Exists(previousSource))
				{
					comboBoxFirmware.SelectedIndex = -1;
					comboBoxFirmware.DropDownStyle = ComboBoxStyle.DropDown;
					comboBoxFirmware.Text = previousSource;
					chosenSourceFile = previousSource;
				}
			}
		}
		private void comboBoxChipType_SelectedIndexChanged(object sender, EventArgs e)
		{
			// Changing platform should reset any manually typed / drag-dropped firmware path
			comboBoxFirmware.DropDownStyle = ComboBoxStyle.DropDownList;
			comboBoxFirmware.SelectedIndex = -1;
			comboBoxFirmware.Text = "";
			chosenSourceFile = "";

			refreshType();
			updatePlatformCapabilityButtons(worker == null);
			refreshFirmwaresList();
			setSettingsKeyAndSave("Platform", comboBoxChipType.SelectedItem);
		}

		private void buttonDownloadLatest_Click(object sender, EventArgs e)
		{
			refreshType();
			downloadLatestFor(curType);
		}
		private void comboBoxFirmware_SelectedIndexChanged(object sender, EventArgs e)
		{
			if (comboBoxFirmware.SelectedItem != null)
			{
				// If user previously drag/dropped or used browse (free-text), switch back to list mode once an item is selected.
				if (comboBoxFirmware.DropDownStyle != ComboBoxStyle.DropDownList)
				{
					comboBoxFirmware.DropDownStyle = ComboBoxStyle.DropDownList;
				}

				chosenSourceFile = Path.Combine(firmwaresPath, comboBoxFirmware.SelectedItem.ToString());
				// kinda hacky, but we are 100% sure that there are no backups in firmwares, so should be ok
				if (File.Exists(chosenSourceFile) == false)
				{
					string backupDirectory = curType == BKType.SV6X66
						? getSv6x66BackupDirectory()
						: backupsPath;
					chosenSourceFile = Path.Combine(backupDirectory, comboBoxFirmware.SelectedItem.ToString());
				}
			}
			else
			{
				chosenSourceFile = "";
			}
			updatePlatformCapabilityButtons(worker == null);
		}

		private void comboBoxFirmware_Click(object sender, EventArgs e)
		{
			refreshType();
			if (comboBoxFirmware.Items.Count == 0)
			{
				var res = MessageBox.Show("No firmware files found for the selected platform. Do you want to download one automatically?",
					"Download?", MessageBoxButtons.YesNo);
				if (res == DialogResult.Yes)
				{
					downloadLatestFor(curType);
				}
			}
		}

		private void buttonBrowseFirmware_Click(object sender, EventArgs e)
		{
			using (OpenFileDialog ofd = new OpenFileDialog())
			{
				ofd.Title = "Select firmware to flash";
				ofd.Filter = "Firmware files (*.bin;*.rbl;*.fls;*.img)|*.bin;*.rbl;*.fls;*.img|All files (*.*)|*.*";
				if (curType == BKType.SV6X66)
					ofd.Filter = "SV6x66 UART firmware (*.bin)|*.bin|SV6x66 OTA firmware (*.sv6ota)|*.sv6ota|All files (*.*)|*.*";
				ofd.CheckFileExists = true;
				ofd.Multiselect = false;
				ofd.RestoreDirectory = true;

				if (ofd.ShowDialog(this) != DialogResult.OK)
				{
					return;
				}

				// Match existing drag/drop behaviour
				comboBoxFirmware.DropDownStyle = ComboBoxStyle.DropDown;
				comboBoxFirmware.SelectedIndex = -1;
				comboBoxFirmware.Text = ofd.FileName;
				chosenSourceFile = ofd.FileName;

				addLog("Selected firmware file " + ofd.FileName + "." + Environment.NewLine, Color.Black);
				if (curType == BKType.SV6X66) updatePlatformCapabilityButtons(worker == null);
			}
		}


		private void buttonRead_Click(object sender, EventArgs e)
		{
			using (new TerminalFlasherReservation(this))
			{
			if (doGenericOperationPreparations() == false)
			{
				return;
			}
			bool? finalReadback = CaptureFinalReadbackOption();
			if (curType == BKType.SV6X66)
			{
				DialogResult result = MessageBox.Show(
					"Loader erases inactive bank and it can not be preserved!",
					"SV6x66 backup warning",
					MessageBoxButtons.YesNo,
					MessageBoxIcon.Warning);
				if (result != DialogResult.Yes)
				{
					return;
				}
				if (promptForBackupName() == false)
				{
					return;
				}
				startWorkerThread(state => readThread(state, finalReadback), null);
				return;
			}
			if (promptForBackupName() == false)
			{
				return;
			}
			// setButtonReadLabel(label_stopRead);
			startWorkerThread(state => readThread(state, finalReadback), null);
			}
		}
		private void buttonBlankCheck_Click(object sender, EventArgs e)
		{
			using (new TerminalFlasherReservation(this))
			{
			if (doGenericOperationPreparations() == false)
			{
				return;
			}
			startWorkerThread(blankThread, null);
			}
		}
		private void buttonTestReadWrite_Click(object sender, EventArgs e)
		{
			using (new TerminalFlasherReservation(this))
			{
			if (true)
			{
				MessageBox.Show("This option is developer-only, it's disabled in release");
				return;
			}
			if (doGenericOperationPreparations() == false)
			{
				return;
			}
			//setButtonReadLabel(label_stopRead);
			startWorkerThread(testReadWrite);
			}
		}

		private void buttonTestWrite_Click(object sender, EventArgs e)
		{
			using (new TerminalFlasherReservation(this))
			{
			if (true)
			{
				MessageBox.Show("This option is developer-only, it's disabled in release");
				return;
			}
			if (doGenericOperationPreparations() == false)
			{
				return;
			}
			//setButtonReadLabel(label_stopRead);
			startWorkerThread(testWrite);
			}
		}
		string lastBackupNameEnteredByUser = "unnamedDump.bin";
		bool promptForBackupName()
		{
			FormPrompt p = new FormPrompt();
			p.ShowDialog();
			if(p.getIsCanceled())
			{
				lastBackupNameEnteredByUser = "";
				return false;
			}
			lastBackupNameEnteredByUser = p.getResult();
			return true;
		}
		private void button4_Click(object sender, EventArgs e)
		{
			using (new TerminalFlasherReservation(this))
			{
			if (curType == BKType.SV6X66)
			{
				if (!isSv6x66ApplicationSelectionEligible() || !SvApplicationImage.HasEmbeddedLoaders)
				{
					MessageBox.Show("Select an SV6x66 application firmware file in a build with its embedded loaders.",
						"SV6x66 backup then write unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
					return;
				}
				SvApplicationImage application;
				try { application = SvApplicationImage.Load(chosenSourceFile); }
				catch (Exception exception)
				{
					MessageBox.Show("The firmware or embedded SV6x66 loader could not be validated before connecting: "
						+ exception.Message, "SV6x66 application unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error);
					return;
				}
				if (!doGenericOperationPreparations() || !promptForBackupName()) return;
				if (MessageBox.Show("Loader erases inactive bank and it can not be preserved!",
					"SV6x66 backup then firmware installation", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
					return;
				startWorkerThread(installSv6x66ApplicationThread, new Sv6x66ApplicationRequest {
					Image = application, BackupFirst = true, BackupName = lastBackupNameEnteredByUser,
					FinalReadbackVerification = CaptureFinalReadbackOption()
				});
				return;
			}
			showObkConfigFormIfPossible();
			if (doGenericOperationPreparations() == false)
			{
				return;
			}
			if (promptForBackupName() == false)
			{
				return;
			}
			//setButtonReadLabel(label_stopRead);
			bool? finalReadback = CaptureFinalReadbackOption();
			startWorkerThread(() => doBackupAndFlashNew(finalReadback));
			}
		}

		private void timer100ms_Tick(object sender, EventArgs e)
		{
			scanForCOMPorts();
			UpdateAdvancedCheckboxControls();
		}
		void setButtonStates(bool b)
		{
			Singleton.tabControl1.Invoke((MethodInvoker)delegate {
				// Running on the UI thread
				setButtonStatesOnUiThread(b);
			});
		}

		void setButtonStatesOnUiThread(bool b)
		{
				buttonRead.Enabled = b;
				buttonTestReadWrite.Enabled = b;
				buttonTestWrite.Enabled = b;
				buttonDoBackupAndFlashNew.Enabled = b;
				buttonWriteOnly.Enabled = b;
				buttonCustomOperation.Enabled = b;
				buttonEraseAll.Enabled = b;
				updatePlatformCapabilityButtons(b);
				buttonStop.Enabled = !b;
				buttonReadRomStop.Enabled = !b;
				comboBoxReadRomChipType.Enabled = b;
				groupBoxReadRomTargets.Enabled = b;
				applySerialControlState(comboBoxReadRomChipType, comboBoxReadRomUART, comboBoxReadRomBaudRate, b, true);
				updateReadRomReadButtonState();
		}

		void tabControl1_Selecting(object sender, TabControlCancelEventArgs e)
		{
			if (scannerOtaCancellation != null) e.Cancel = true;
			if (sv6x66OperationLockActive && e.TabPage != sv6x66OperationLockedTab)
				e.Cancel = true;
		}

		void tabControl1_Deselecting(object sender, TabControlCancelEventArgs e)
		{
			if (sv6x66OperationLockActive)
			{
				e.Cancel = true;
				return;
			}
			if (e.TabPage != tabPageTerminal)
				return;
			if (terminalTabChangePromptActive)
			{
				e.Cancel = true;
				return;
			}
			if (!IsTerminalConnected)
				return;

			e.Cancel = true;
			terminalTabChangePromptActive = true;
			try
			{
				if (MessageBox.Show(this, "The terminal port will be disconnected. Continue?",
					"Terminal connection", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
					MessageBoxDefaultButton.Button2) != DialogResult.Yes)
					return;
				CancelTerminalReconnect();
				DisconnectTerminal();
				e.Cancel = false;
			}
			finally
			{
				terminalTabChangePromptActive = false;
			}
		}

		void beginSv6x66OperationLock(OperationUiTarget uiTarget, BKType operationPlatform)
		{
			if (uiTarget != OperationUiTarget.Flasher || operationPlatform != BKType.SV6X66)
				return;

			Action applyLock = () =>
			{
				sv6x66OperationEnabledStates.Clear();
				sv6x66OperationSavedAllowDrop = AllowDrop;
				sv6x66OperationLockedTab = tabControl1.SelectedTab;
				foreach (Control control in getDescendantControls(this))
					sv6x66OperationEnabledStates[control] = control.Enabled;
				AllowDrop = false;
				sv6x66OperationLockActive = true;
				applySv6x66OperationLockOnUiThread();
			};

			if (InvokeRequired)
				Invoke((MethodInvoker)delegate { applyLock(); });
			else
				applyLock();
		}

		void enforceSv6x66OperationLock()
		{
			if (!sv6x66OperationLockActive)
				return;
			if (InvokeRequired)
				Invoke((MethodInvoker)delegate { applySv6x66OperationLockOnUiThread(); });
			else
				applySv6x66OperationLockOnUiThread();
		}

		void applySv6x66OperationLockOnUiThread()
		{
			foreach (Control control in sv6x66OperationEnabledStates.Keys)
			{
				if (isUserInteractiveControl(control) && control != buttonStop && control != buttonClearLog
					&& control != textBoxFlasherGuide)
					control.Enabled = false;
			}
			buttonStop.Enabled = true;
			buttonClearLog.Enabled = true;
		}

		IEnumerable<Control> getDescendantControls(Control parent)
		{
			foreach (Control child in parent.Controls)
			{
				yield return child;
				foreach (Control descendant in getDescendantControls(child))
					yield return descendant;
			}
		}

		bool isUserInteractiveControl(Control control)
		{
			return control is ButtonBase
				|| control is ComboBox
				|| control is TextBoxBase
				|| control is ListControl
				|| control is ListView
				|| control is TreeView
				|| control is UpDownBase
				|| control is DateTimePicker
				|| control is TrackBar
				|| control is DataGridView
				|| control is PropertyGrid
				|| control is LinkLabel
				|| control is ToolStrip;
		}

		void completeSv6x66Operation()
		{
			clearUpActiveFlasherSafely();
			Action restoreUi = () =>
			{
				worker = null;
				setButtonStatesOnUiThread(true);
				foreach (KeyValuePair<Control, bool> state in sv6x66OperationEnabledStates)
				{
					if (!state.Key.IsDisposed)
						state.Key.Enabled = state.Value;
				}
				AllowDrop = sv6x66OperationSavedAllowDrop;
				sv6x66OperationEnabledStates.Clear();
				sv6x66OperationLockedTab = null;
				activeOperationPlatform = BKType.Invalid;
				sv6x66OperationLockActive = false;
				updatePlatformCapabilityButtons(true);
			};

			if (IsDisposed || Disposing)
			{
				worker = null;
				activeOperationPlatform = BKType.Invalid;
				sv6x66OperationLockActive = false;
				return;
			}
			if (InvokeRequired)
				Invoke((MethodInvoker)delegate { restoreUi(); });
			else
				restoreUi();
		}

		void clearUpActiveFlasherSafely()
		{
			BaseFlasher activeFlasher = flasher;
			flasher = null;
			if (activeFlasher == null)
				return;
			try { activeFlasher.closePort(); } catch { }
			try { activeFlasher.Dispose(); } catch { }
		}

		void updatePlatformCapabilityButtons(bool operationControlsEnabled)
		{
			refreshAdvancedOptions();
			UpdateAdvancedCheckboxControls(operationControlsEnabled);
			bool sv6x66ReadOnly = curType == BKType.SV6X66;
			bool obkConfigSupported = IsBeken == false || OBKFlashLayout.hasConfigLocation(curType);
			bool rfSupported = IsBeken == false || RFPartitionUtil.getRFOffset(curType) >= 0;
			buttonTestReadWrite.Enabled = operationControlsEnabled && !sv6x66ReadOnly;
			buttonTestWrite.Enabled = operationControlsEnabled && !sv6x66ReadOnly;
			buttonWriteOnly.Enabled = operationControlsEnabled
				&& (!sv6x66ReadOnly || isSv6x66RestoreSelectionEligible()
					|| (isSv6x66ApplicationSelectionEligible() && SvApplicationImage.HasEmbeddedLoaders));
			if (isSv6x66ApplicationSelectionEligible() && !SvApplicationImage.HasEmbeddedLoaders)
				addLog("SV6x66 firmware installation is unavailable in this flasher build." + Environment.NewLine, Color.DarkOrange);
			buttonDoBackupAndFlashNew.Enabled = operationControlsEnabled
				&& (!sv6x66ReadOnly || (isSv6x66ApplicationSelectionEligible() && SvApplicationImage.HasEmbeddedLoaders));
			buttonCustomOperation.Enabled = operationControlsEnabled && !sv6x66ReadOnly;
			buttonEraseAll.Enabled = operationControlsEnabled && !sv6x66ReadOnly;
			buttonBlankCheck.Enabled = operationControlsEnabled && !sv6x66ReadOnly;
			buttonDetect.Enabled = operationControlsEnabled && !sv6x66ReadOnly;
			buttonDownloadLatest.Enabled = operationControlsEnabled && !sv6x66ReadOnly;
			buttonBrowseFirmware.Enabled = operationControlsEnabled;
			buttonWriteOBKConfig.Enabled = operationControlsEnabled && obkConfigSupported && !sv6x66ReadOnly;
			buttonReadOBKConfig.Enabled = operationControlsEnabled && obkConfigSupported && !sv6x66ReadOnly;
			buttonRestoreRF.Enabled = operationControlsEnabled && rfSupported && !sv6x66ReadOnly;
		}
		private void buttonStop_Click(object sender, EventArgs e)
		{
			interruptIfRequired();
		}

		private void buttonClearLog_Click(object sender, EventArgs e)
		{
			textBoxLog.Text = "";
		}

		private void comboBoxUART_SelectedIndexChanged(object sender, EventArgs e)
		{
			setSettingsKeyAndSave("Port", comboBoxUART.SelectedItem);
		}

		private void comboBoxBaudRate_SelectedIndexChanged(object sender, EventArgs e)
		{
			setSettingsKeyAndSave("BaudRate", comboBoxBaudRate.SelectedItem);
		}

		private void comboBoxReadRomChipType_SelectedIndexChanged(object sender, EventArgs e)
		{
			if (bSuppressReadRomPlatformEvents)
			{
				return;
			}
			applySerialControlState(comboBoxReadRomChipType, comboBoxReadRomUART, comboBoxReadRomBaudRate, worker == null, true);
			updateReadRomControlsForSelectedPlatform(tabControl1.SelectedTab == tabPageReadRom);
		}

		private void comboBoxReadRomUART_SelectedIndexChanged(object sender, EventArgs e)
		{
			setSettingsKeyAndSave("ReadRomPort", comboBoxReadRomUART.SelectedItem);
		}

		private void comboBoxReadRomBaudRate_SelectedIndexChanged(object sender, EventArgs e)
		{
			setSettingsKeyAndSave("ReadRomBaudRate", comboBoxReadRomBaudRate.SelectedItem);
		}

		private void radioButtonReadRomTarget_CheckedChanged(object sender, EventArgs e)
		{
			if (bSuppressReadRomTargetEvents)
				return;
			updateReadRomBaudRates();
			updateReadRomRangeInfo();
			updateReadRomReadButtonState();
		}

		private void buttonReadRomRead_Click(object sender, EventArgs e)
		{
			using (new TerminalFlasherReservation(this))
			{
			RomReadTarget target = getSelectedReadRomTarget();
			if (target == null)
			{
				MessageBox.Show("No ROM/OTP/eFuse read target is available for this platform yet.");
				return;
			}
			if (doReadRomOperationPreparations() == false)
			{
				return;
			}
			if (promptForBackupName() == false)
			{
				return;
			}
			startWorkerThread(readRomThread, target, OperationUiTarget.ReadRom);
			}
		}

		void readRomThread(object oParm)
		{
			RomReadTarget target = oParm as RomReadTarget;
			if (target == null)
			{
				addLog("No read target selected." + Environment.NewLine, Color.Red);
				worker = null;
				currentOperationUiTarget = OperationUiTarget.Flasher;
				setButtonStates(true);
				return;
			}

			try
			{
				string targetKindName = RomReadCatalog.GetKindDisplayName(target.Kind);
				addLog("Reading " + targetKindName + "..." + Environment.NewLine, Color.Black);
				addLog(Environment.NewLine + "Selected read target: " + target.DisplayName + Environment.NewLine, Color.Black);
				addLog("Platform: " + FlashPlatformCatalog.GetDisplayName(target.Platform) + Environment.NewLine, Color.Black);
				addLog("Baud rate: " + chosenBaudRate + Environment.NewLine, Color.Black);
				addLog("Address: " + (target.Address.HasValue ? BaseFlasher.formatHex(target.Address.Value) : "not catalogued yet") + Environment.NewLine, Color.Black);
				if(target.Length.HasValue && target.Length.Value > 0)
					addLog("Length: " + (target.Length.HasValue ? BaseFlasher.formatHex(target.Length.Value) : "not catalogued yet") + Environment.NewLine, Color.Black);

				clearUp();
				createFlasher();
				IRomReadFlasher romFlasher = flasher as IRomReadFlasher;
				if (romFlasher == null)
				{
					addLog("Selected platform does not implement this read target." + Environment.NewLine, Color.Red);
					return;
				}

				byte[] result = romFlasher.ReadRomTarget(target);
				if (result == null)
				{
					addLog(targetKindName + " read failed or was cancelled." + Environment.NewLine, Color.Red);
					return;
				}

				saveReadRomResult(target, targetKindName, result);
			}
			catch (Exception ex)
			{
				string targetKindName = target == null ? "Selected target" : RomReadCatalog.GetKindDisplayName(target.Kind);
				addLog(targetKindName + " read error: " + ex.Message + Environment.NewLine, Color.Red);
			}
			finally
			{
				worker = null;
				currentOperationUiTarget = OperationUiTarget.Flasher;
				clearUp();
				setButtonStates(true);
			}
		}

		void saveReadRomResult(RomReadTarget target, string targetKindName, byte[] result)
		{
			Directory.CreateDirectory(backupsPath);
			if (target.OutputSlices.Count == 0)
			{
				string targetName = string.IsNullOrEmpty(target.OutputFileNameTag)
					? target.Kind.ToString().ToUpperInvariant()
					: target.OutputFileNameTag;
				saveReadRomBytes(target, targetName, targetKindName, result);
				return;
			}

			foreach (RomReadOutputSlice slice in target.OutputSlices)
			{
				if (slice.Offset < 0 || slice.Length <= 0 || slice.Offset + slice.Length > result.Length)
				{
					throw new InvalidOperationException(target.DisplayName + " output slice " + slice.DisplayName + " is outside the returned payload.");
				}

				byte[] sliceBytes = new byte[slice.Length];
				Array.Copy(result, slice.Offset, sliceBytes, 0, slice.Length);
				string fileNameTag = string.IsNullOrEmpty(slice.FileNameTag) ? target.Kind.ToString().ToUpperInvariant() : slice.FileNameTag;
				saveReadRomBytes(target, fileNameTag, targetKindName + " " + slice.DisplayName, sliceBytes);
			}
		}

		void saveReadRomBytes(RomReadTarget target, string fileNameTag, string logName, byte[] data)
		{
			string fileName = MiscUtils.formatDateNowFileName("readResult_" + target.Platform + "_" + fileNameTag, lastBackupNameEnteredByUser, "bin");
			string fullPath = Path.Combine(backupsPath, fileName);
			File.WriteAllBytes(fullPath, data);
			addLog(logName + " read saved " + data.Length + " bytes to " + fullPath + Environment.NewLine, Color.Green);
		}

		private void buttonReadRomStop_Click(object sender, EventArgs e)
		{
			interruptIfRequired();
		}

		private void buttonReadRomClearLog_Click(object sender, EventArgs e)
		{
			textBoxReadRomLog.Text = "";
		}

		private void checkBoxShowAdvanced_CheckedChanged(object sender, EventArgs e)
		{
			refreshAdvancedOptions();
			refreshFirmwaresList();
			updatePlatformCapabilityButtons(worker == null);
			setSettingsKeyAndSave("bAdvanced", checkBoxShowAdvanced.Checked);
		}
		void refreshAdvancedOptions()
		{
			showAdvancedOptions(checkBoxShowAdvanced.Checked);
		}

		private void showAdvancedOptions(bool b)
		{
			checkBoxVerifyBackupWrite.Visible = b;
			// Reserve the advanced controls' column only while those controls are shown.
			int guideLeft = b ? checkBoxOverwriteBootloader.Right + checkBoxShowAdvanced.Left : checkBoxShowAdvanced.Left;
			groupBoxFlasherGuide.SetBounds(guideLeft, groupBoxFlasherGuide.Top,
				textBoxLog.Left - checkBoxShowAdvanced.Left - guideLeft, groupBoxFlasherGuide.Height);
			buttonTestReadWrite.Visible = b;
			buttonTestWrite.Visible = b;
			buttonEraseAll.Visible = b;
			buttonRestoreRF.Visible = b;
			buttonBlankCheck.Visible = b;
			buttonDetect.Visible = b;
			checkBoxAllowBackup.Visible = b;
			checkBoxOverwriteBootloader.Visible = b;
			checkBoxSkipKeyCheck.Visible = b;
			buttonCustomOperation.Visible = b;
			chkIgnoreCRCErr.Visible = b;
			chkUseCompression.Visible = b;
			updateFirmwareBrowseVisibility();
		}

		void updateFirmwareBrowseVisibility()
		{
			// Advanced-only firmware browse button + combobox width management
			if (buttonBrowseFirmware != null)
			{
				buttonBrowseFirmware.Visible = true;
			}

		}
		
		private void buttonOpenBackupsDir_Click(object sender, EventArgs e)
		{
			string path = curType == BKType.SV6X66
				? getSv6x66BackupDirectory()
				: Path.Combine(Directory.GetCurrentDirectory(), backupsPath);
			try
			{
				// opens the folder in explorer
				Directory.CreateDirectory(path);
				Process.Start("explorer.exe", path);
			}
			catch(Exception ex)
			{
				MessageBox.Show("Failed to open backup directory " + path + ": " + ex.Message,
					"Backup directory error", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}

		private void buttonClearOldFirmware_Click(object sender, EventArgs e)
		{
			var res = MessageBox.Show("Remove older downloaded firmware files, keeping only the most recently modified one?\n\nBackups will not be removed.", "Remove old firmware files", MessageBoxButtons.YesNo);
			if (res == DialogResult.Yes)
			{
				clearFirmwaresList();
			}
		}

		private void buttonWriteOnly_Click(object sender, EventArgs e)
		{
			using (new TerminalFlasherReservation(this))
			{
			if (curType == BKType.SV6X66)
			{
				if (isSv6x66ApplicationSelectionEligible())
				{
					SvApplicationImage application;
					try { application = SvApplicationImage.Load(chosenSourceFile); }
					catch (Exception exception)
					{
						MessageBox.Show("The firmware or embedded SV6x66 loader could not be validated before connecting: "
							+ exception.Message,
							"SV6x66 application unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error);
						return;
					}
					if (!doGenericOperationPreparations()) return;
					if (MessageBox.Show("Loader erases inactive bank and it can not be preserved!",
						"SV6x66 firmware installation", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
						return;
					startWorkerThread(installSv6x66ApplicationThread, new Sv6x66ApplicationRequest {
						Image = application, FinalReadbackVerification = CaptureFinalReadbackOption()
					});
					return;
				}
				if (doGenericOperationPreparations() == false)
					return;
				string sourcePath;
				if (!checkBoxShowAdvanced.Checked || !checkBoxAllowBackup.Checked
					|| !tryGetSv6x66RestorePath(out sourcePath))
				{
					MessageBox.Show(
						"SV6x66 restore requires Show advanced options, Allow backup restore, and a selected canonical 2 MiB SV6x66 backup.",
						"SV6x66 restore unavailable",
						MessageBoxButtons.OK,
						MessageBoxIcon.Information);
					updatePlatformCapabilityButtons(worker == null);
					return;
				}

				byte[] image;
				try
				{
					image = File.ReadAllBytes(sourcePath);
					if (image.Length != Sv6x66FullFlashBytes)
						throw new InvalidDataException($"Selected backup is {image.Length} bytes; exactly {Sv6x66FullFlashBytes} bytes are required.");
				}
				catch (Exception exception)
				{
					MessageBox.Show("Failed to load SV6x66 backup " + sourcePath + ": " + exception.Message,
						"SV6x66 backup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
					updatePlatformCapabilityButtons(worker == null);
					return;
				}

				Sv6x66RestoreRequest request = new Sv6x66RestoreRequest {
					SourcePath = sourcePath,
					Image = image,
					Sha256 = sha256Hex(image),
					FinalReadbackVerification = CaptureFinalReadbackOption(),
				};
				DialogResult confirmation = MessageBox.Show(
					"If this procedure is stopped before completion, restoring chip functionality will require SPI recovery!",
					"SV6x66 full-flash restore warning",
					MessageBoxButtons.YesNo,
					MessageBoxIcon.Warning);
				if (confirmation != DialogResult.Yes)
					return;
				startWorkerThread(restoreSv6x66Thread, request);
				return;
			}
			showObkConfigFormIfPossible();
			if (doGenericOperationPreparations() == false)
			{
				return;
			}
			//setButtonReadLabel(label_stopRead);
			bool? finalReadback = CaptureFinalReadbackOption();
			startWorkerThread(state => doOnlyFlashNew(state, finalReadback), null);
			}
		}

		private void genericLinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
		{
			LinkLabel ll = sender as LinkLabel;
			System.Diagnostics.Process.Start(ll.Text);
		}

		private void buttonEraseAll_Click(object sender, EventArgs e)
		{
			using (new TerminalFlasherReservation(this))
			{
			string _nl = Environment.NewLine;
			string _msg =
				"BK7231T / BK7231U / BK7252:" + _nl +
				"- Erases from 0x11000. Bootloader (0x000000-0x010FFF) must be preserved on these chips." + _nl + _nl +
				"BK7231N / BK7231M / BK7236 / BK7238 / BK7239N / BK7252N / BK7258:" + _nl +
				"- Erases from 0x11000. Bootloader safe to erase on these but tool preserves it." + _nl +
				"- Config, RF and MAC data above 0x11000 will be removed on all BK chips." + _nl + _nl +
				"Full chip erase: W600, W800, BL602, BL702, BL616 / BL618, ECR6600, TR6260, LN882H, LN8825B, XR806, XR809, XR872, RTL8710B, RTL8720DN, RTL87X0C, RTL8721DA, RTL8720E, RDA5981, Beken SPI / Generic SPI, ESP8266, ESP32 family." + _nl + _nl +
				"All BK series UART chips negotiate to the GUI baud rate before erasing - lower baud may help if erase fails." + _nl + _nl +
				"Continue?";
			var res = MessageBox.Show(_msg, "WARNING! NUKE CHIP?", MessageBoxButtons.YesNo);
			if (res == DialogResult.Yes)
			{
				if (doGenericOperationPreparations() == false)
				{
					return;
				}
				//setButtonReadLabel(label_stopRead);
				startWorkerThread(eraseAll);
			}
			}
		}

		private void buttonRestoreRF_Click(object sender, EventArgs e)
		{
			using (new TerminalFlasherReservation(this))
			{
			var res = MessageBox.Show("This will overwrite RF partition with new one with random MAC. " +
				"This can be used to fix strange issue where your device works in AP but don't want to connect to your WiFi. " +
				"It can also fix issue where you have 00 00 00 MAC. You must have CORRECT PLATFORM selected first (T or N), because offsets are different.", "WARNING! NUKE RF?", MessageBoxButtons.YesNo);
			if (res == DialogResult.Yes)
			{
				if (doGenericOperationPreparations() == false)
				{
					return;
				}
				//setButtonReadLabel(label_stopRead);
				startWorkerThread(restoreRF);
			}
			}
		}

		private void checkBoxAllowBackup_CheckedChanged(object sender, EventArgs e)
		{
			setSettingsKeyAndSave("bAllowBackupRestore", checkBoxAllowBackup.Checked);
			refreshFirmwaresList();
			updatePlatformCapabilityButtons(worker == null);
		}

		private void linkLabel4_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
		{
			System.Diagnostics.Process.Start("https://openbekeniot.github.io/webapp/templateImporter.html");
		}
		FormOBKConfig formObkCfg;
		private void buttonChangeOBKSettings_Click(object sender, EventArgs e)
		{
			formObkCfg.Show();
		}
		void showObkConfigFormIfPossible()
		{
			if (formObkCfg.Visible)
			{
				formObkCfg.saveBinding();
			}
		}
		private void buttonWriteOBKConfig_Click(object sender, EventArgs e)
		{
			using (new TerminalFlasherReservation(this))
			{
			showObkConfigFormIfPossible();
			if (doGenericOperationPreparations() == false)
			{
				return;
			}
			//setButtonReadLabel(label_stopRead);
			startWorkerThread(doOnlyFlashOBKConfig);
			}
		}

		private void buttonReadOBKConfig_Click(object sender, EventArgs e)
		{
			using (new TerminalFlasherReservation(this))
			{
			if (doGenericOperationPreparations() == false)
			{
				return;
			}
			//setButtonReadLabel(label_stopRead);
			startWorkerThread(doOnlyReadOBKConfig);
			}
		}

		public class CustomParms
		{
			public int ofs, len;
			public string sourceFileName;
			internal bool bBlankCheck;
		}
		public void doCustomRead(CustomParms customRead)
		{
			using (new TerminalFlasherReservation(this))
			{
			if (doGenericOperationPreparations() == false)
			{
				return;
			}
			if (promptForBackupName() == false)
			{
				return;
			}
			startWorkerThread(readThread, customRead);
			}
		}
		
		void startWorkerThread(Action<object> ts, object customArg)
		{
			startWorkerThread(ts, customArg, OperationUiTarget.Flasher);
		}

		void startWorkerThread(Action<object> ts, object customArg, OperationUiTarget uiTarget)
		{
			BKType operationPlatform = curType;
			currentOperationUiTarget = uiTarget;
			activeOperationPlatform = operationPlatform;
			cts?.Dispose();
			cts = new CancellationTokenSource();
			beginSv6x66OperationLock(uiTarget, operationPlatform);
			setButtonStates(false);
			enforceSv6x66OperationLock();
			worker = new Task(ts, customArg, cts.Token);
			terminalFlasherTasks.RemoveAll(task => task.IsCompleted);
			terminalFlasherTasks.Add(worker);
			worker.Start();
		}
		void startWorkerThread(Action ts)
		{
			startWorkerThread(ts, OperationUiTarget.Flasher);
		}

		void startWorkerThread(Action ts, OperationUiTarget uiTarget)
		{
			BKType operationPlatform = curType;
			currentOperationUiTarget = uiTarget;
			activeOperationPlatform = operationPlatform;
			cts?.Dispose();
			cts = new CancellationTokenSource();
			beginSv6x66OperationLock(uiTarget, operationPlatform);
			setButtonStates(false);
			enforceSv6x66OperationLock();
			worker = new Task(ts, cts.Token);
			terminalFlasherTasks.RemoveAll(task => task.IsCompleted);
			terminalFlasherTasks.Add(worker);
			worker.Start();
		}
		private void buttonStartScan_Click(object sender, EventArgs e)
		{
			startOrStopScannerThread();
		}

		private void textBoxScannerThreads_TextChanged(object sender, EventArgs e)
		{
			setMaxWorkersCountFromGUI();
		}

		private static bool sendRebootIfConfirmed(
			OBKDeviceAPI device,
			DialogResult confirmation)
		{
			if (device == null || confirmation != DialogResult.Yes)
			{
				return false;
			}
			device.sendCmnd("reboot", null);
			return true;
		}

		private void listView1_MouseClick(object sender, MouseEventArgs e)
		{
			if (scannerOtaCancellation != null) return;
			if (e.Button == MouseButtons.Right)
			{
				ListViewItem selectedItem = listView1.GetItemAt(e.X, e.Y);
				OBKDeviceAPI dev = selectedItem?.Tag as OBKDeviceAPI;
				if (selectedItem == null || dev == null)
				{
					return;
				}
				selectedItem.Selected = true;
				selectedItem.Focused = true;

				scannerDeviceMenu?.Dispose();
				scannerDeviceMenu = new ContextMenuStrip(components);
				ContextMenuStrip contextMenu = scannerDeviceMenu;

				ToolStripMenuItem openPageMenuItem = new ToolStripMenuItem("Open page");
				openPageMenuItem.Click += (s, args) =>
				{
					string url = selectedItem.SubItems[1].Text; 
					System.Diagnostics.Process.Start("http://"+url);
				};
				contextMenu.Items.Add(openPageMenuItem);

				ToolStripMenuItem copyUrlMenuItem = new ToolStripMenuItem("Copy URL");
				copyUrlMenuItem.Click += (s, args) =>
				{
					string url = selectedItem.SubItems[1].Text; 
					Clipboard.SetText(url);
				};
				contextMenu.Items.Add(copyUrlMenuItem);
				var otaMenuItem = new ToolStripMenuItem("OTA from local file...");
				otaMenuItem.Enabled = !dev.isTasmota() && OBKOtaImage.FileFilter(dev.getChipSet()) != null
					&& scan == null && buttonStartMassBackup.Enabled && worker == null
					&& !terminalFlasherTasks.Exists(task => !task.IsCompleted);
				otaMenuItem.Click += (s, args) => startScannerOta(dev);
				contextMenu.Items.Add(otaMenuItem);


				ToolStripMenuItem rebootMenuItem = new ToolStripMenuItem("Reboot");
				rebootMenuItem.Click += (s, args) =>
				{
					DialogResult confirmation = MessageBox.Show(
						"Reboot device at " + dev.getAdr() + "?",
						"Confirm reboot",
						MessageBoxButtons.YesNo,
						MessageBoxIcon.Warning,
						MessageBoxDefaultButton.Button2);
					sendRebootIfConfirmed(dev, confirmation);
				};
				contextMenu.Items.Add(rebootMenuItem);

				for (int i = 0; i < dev.getPowerSlotsCount(); i++)
				{
					int slotIndex = i+1; 
					ToolStripMenuItem toggleMenuItem = new ToolStripMenuItem("Toggle POWER"+ slotIndex);
					toggleMenuItem.Click += (s, args) =>
					{
						OBKDeviceAPI devo = selectedItem.Tag as OBKDeviceAPI;
						devo.sendCmnd("POWER"+ slotIndex + " TOGGLE", null);
					};
					contextMenu.Items.Add(toggleMenuItem);
				}
				
				if(dev.hasDimmerSupport())
				{
					ToolStripMenuItem dimmerToolsMenuItem = new ToolStripMenuItem("Dimmer");
					contextMenu.Items.Add(dimmerToolsMenuItem);

					for(int i = 0; i <= 100; i+=25)
					{
						int savedI = i;
						ToolStripMenuItem dimmerMenuItem = new ToolStripMenuItem("Set Dimmer "+i+"%");
						dimmerMenuItem.Click += (s, args) =>
						{
							OBKDeviceAPI devo = selectedItem.Tag as OBKDeviceAPI;
							devo.sendCmnd("Dimmer " + savedI + "", null);
						};
						dimmerToolsMenuItem.DropDownItems.Add(dimmerMenuItem);
					}
				}
				if (dev.hasCTSupport())
				{
					ToolStripMenuItem ctToolsMenuItem = new ToolStripMenuItem("CT");
					contextMenu.Items.Add(ctToolsMenuItem);

					for (int i = 154; i <= 500; i += 173)
					{
						int savedI = i;
						ToolStripMenuItem ctMenuItem = new ToolStripMenuItem("Set CT " + i + "");
						ctMenuItem.Click += (s, args) =>
						{
							OBKDeviceAPI devo = selectedItem.Tag as OBKDeviceAPI;
							devo.sendCmnd("CT " + savedI + "", null);
						};
						ctToolsMenuItem.DropDownItems.Add(ctMenuItem);
					}
				}
				if (dev.hasColorSupport())
				{
					ToolStripMenuItem colorToolsMenuItem = new ToolStripMenuItem("Color");
					contextMenu.Items.Add(colorToolsMenuItem);

					for (int i = 0; i < Colors.list.GetLength(0); i++)
					{
						int savedI = i;
						string name = Colors.list[i, 0];
						string code = Colors.list[i, 1];
						ToolStripMenuItem colorMenuItem = new ToolStripMenuItem("Set " + name + " ("+code+")");
						colorMenuItem.Click += (s, args) =>
						{
							OBKDeviceAPI devo = selectedItem.Tag as OBKDeviceAPI;
							devo.sendCmnd("Color " + code + "", null);
						};
						colorToolsMenuItem.DropDownItems.Add(colorMenuItem);
					}
				}
				contextMenu.Show(listView1, e.Location);
			}
		}

		private void buttonIPCFGDump_Click(object sender, EventArgs e)
		{
			doSingleDeviceOBKCFGDump();
		}

		private void buttonIPDownloadTuyaConfig_Click(object sender, EventArgs e)
		{
			doSingleDeviceTuyaCFGDump();
		}

		private void buttonIPDump2MB_Click(object sender, EventArgs e)
		{
			MessageBox.Show("Not implemented yet, as it's not needed");
		}

		private void buttonIPSaveResultToFile_Click(object sender, EventArgs e)
		{
			onIPSaveResultToFileClicked();
		}

		private void buttonStartMassBackup_Click(object sender, EventArgs e)
		{
			if (scannerOtaCancellation != null) return;
			buttonStartMassBackup.Enabled = false;
			OBKMassBackup backup = new OBKMassBackup();
			for(int i = 0; i < listView1.Items.Count; i++)
			{
				ListViewItem it = listView1.Items[i];
				OBKDeviceAPI dev = it.Tag as OBKDeviceAPI;
				backup.addDevice(dev);
			}
			backup.setOnProgress(onMassBackupProgress);
			backup.setOnFinished(onMassBackupFinish);
			backup.beginBackupThread();
		}
		private void onMassBackupFinish(int totalErrors, int totalRetries)
		{
			onMassBackupProgress("Complete. Errors: " + totalErrors + "; retries: " + totalRetries + ".");
			postMassBackupUpdate(delegate {
				buttonStartMassBackup.Enabled = true;
			});
		}
		private void onMassBackupProgress(string txt)
		{
			postMassBackupUpdate(delegate {
				labelMassBackupProgress.Text = "Status: " + txt;
			});
		}
		private void postMassBackupUpdate(MethodInvoker update)
		{
			if (IsDisposed || Disposing || !IsHandleCreated)
				return;
			try
			{
				BeginInvoke((MethodInvoker)delegate {
					if (!IsDisposed && !Disposing)
						update();
				});
			}
			catch (InvalidOperationException) { }
		}

		private void textBoxIPScannerUser_TextChanged(object sender, EventArgs e)
		{
			setSettingsKeyAndSave("ScannerUser", textBoxIPScannerUser.Text);
		}

		private void textBoxStartIP_TextChanged(object sender, EventArgs e)
		{
			setSettingsKeyAndSave("ScannerFirst", textBoxStartIP.Text);
		}

		private void textBoxEndIP_TextChanged(object sender, EventArgs e)
		{
			setSettingsKeyAndSave("ScannerLast", textBoxEndIP.Text);
		}

		private void buttonIPScannerOpenDir_Click(object sender, EventArgs e)
		{
			try
			{
				// opens the folder in explorer
				string path = Path.Combine(Directory.GetCurrentDirectory(), backupsPath);
				Process.Start("explorer.exe", path);
			}
			catch (Exception ex)
			{
				MessageBox.Show("Failed, no backups done yet!");
			}
		}

		private void buttonTuyaConfig_CopyJSONToClipBoard_Click(object sender, EventArgs e)
		{
			string text = textBoxTuyaCFGJSON?.Text;
			if (string.IsNullOrEmpty(text))
			{
				MessageBox.Show("No JSON text available to copy.", "Copy to clipboard", MessageBoxButtons.OK, MessageBoxIcon.Information);
			}
			else
			{
				try
				{
					Clipboard.SetText(text);
				}
				catch (Exception ex)
				{
					MessageBox.Show("Failed to copy to clipboard: " + ex.Message, "Clipboard error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				}
			}
		}

		private void buttonTuyaConfig_CopyTextToClipBoard_Click(object sender, EventArgs e)
		{
			string text = textBoxTuyaCFGText?.Text;
			if (string.IsNullOrEmpty(text))
			{
				MessageBox.Show("No text description available to copy.", "Copy to clipboard", MessageBoxButtons.OK, MessageBoxIcon.Information);
			}
			else
			{
				try
				{
					Clipboard.SetText(text);
				}
				catch (Exception ex)
				{
					MessageBox.Show("Failed to copy to clipboard: " + ex.Message, "Clipboard error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				}
			}
		}
		private void linkLabel5_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
		{
			System.Diagnostics.Process.Start("https://www.youtube.com/watch?v=WunlqIMAdgw");
		}

		private void FormMain_FormClosing(object sender, FormClosingEventArgs e)
		{
			try { cts?.Cancel(); } catch { }
			try { flasher?.closePort(); } catch { }
			CloseTerminalPort();
			try { scannerOtaCancellation?.Cancel(); } catch (ObjectDisposedException) { }
			killScanner();
		}

		private void checkBoxOverwriteBootloader_CheckedChanged(object sender, EventArgs e)
		{
			if(checkBoxOverwriteBootloader.Checked == true)
			{
				DialogResult res = MessageBox.Show("This will break bootloader if used incorrectly. Do you have a backup? Are you sure?",
					"Are you sure?", MessageBoxButtons.YesNo);
				if(res == DialogResult.No)
				{
					checkBoxOverwriteBootloader.Checked = false;
				}
			}
		}

		private void checkBoxSkipKeyCheck_CheckedChanged(object sender, EventArgs e)
		{

		}
		private void buttonCustomOperation_Click(object sender, EventArgs e)
		{
			if (formCustom == null)
			{
				formCustom = new FormCustom();
				formCustom.fm = this;
			}
			formCustom.ShowDialog();
		}

		class OTAData
		{
			public string ip;
			public byte[] data;
		}
		private void buttonOTAFlash_Click(object sender, EventArgs e)
		{
			string fname = ("D:/OpenBK7231T_1.17.799.rbl");
			buttonOTAFlash.Enabled = false;
			string ip = textBoxOTATarget.Text;
			OTAData data = new OTAData();
			data.ip = ip;
			data.data = System.IO.File.ReadAllBytes(fname);
			System.Threading.Thread thread = new System.Threading.Thread(ThreadOTA);
			thread.Start(data);
		}
		public void ThreadOTA(object ocb)
		{
			OTAData d = (OTAData)ocb;
			try
			{
				var request = (System.Net.WebRequest)System.Net.WebRequest.Create($"http://{d.ip}/api/ota");
				request.Method = "POST";
				request.ContentType = "application/octet-stream";
				using (var stream = request.GetRequestStream())
				{
					stream.Write(d.data, 0, d.data.Length);
				}
				try
				{
					using (var response = (System.Net.HttpWebResponse)request.GetResponse())
					{
						if (response.StatusCode != System.Net.HttpStatusCode.OK)
						{
							//        throw new Exception($"Failed to send OTA file: {response.StatusCode} {response.StatusDescription}");
						}
					}
				}
				catch (Exception ex2)
				{
				}
				request = (System.Net.WebRequest)System.Net.WebRequest.Create($"http://{d.ip}/api/reboot");
				request.Method = "POST";
				try
				{
					using (var response = (System.Net.HttpWebResponse)request.GetResponse())
					{
						if (response.StatusCode != System.Net.HttpStatusCode.OK)
						{
							//      throw new Exception($"Failed to send reboot command: {response.StatusCode} {response.StatusDescription}");
						}
					}
				}
				catch (Exception ex2)
				{
				}

				System.Windows.Forms.MessageBox.Show("OTA update completed successfully.");
			}
			catch (Exception ex)
			{
				System.Windows.Forms.MessageBox.Show($"Error: {ex.Message}");
			}
			finally
			{
				this.Invoke((MethodInvoker)delegate {
					buttonOTAFlash.Enabled = true;
				});
			}
		}

		private void radioButton2_CheckedChanged(object sender, EventArgs e)
		{

		}

		private void tabPage8_Enter(object sender, EventArgs e)
		{
			refreshOTAs();
		}
		void refreshOTAs()
		{

		}

		private void chkIgnoreCRCErr_CheckedChanged(object sender, EventArgs e)
		{

		}


		private void buttonDetect_Click(object sender, EventArgs e)
		{
			using (new TerminalFlasherReservation(this))
			{
			if (doGenericOperationPreparations() == false)
			{
				return;
			}
			startWorkerThread(detectChip);
			}
		}

		private void chkUseCompression_CheckedChanged(object sender, EventArgs e)
		{
			setSettingsKeyAndSave("bUseCompressionIfPossible", chkUseCompression.Checked); 
		}
	}

	[AttributeUsage(AttributeTargets.Assembly)]
	public class BuildVersion : Attribute
	{
		public string Value { get; set; }

		public BuildVersion(string value)
		{
			Value = value;
		}
	}
}
