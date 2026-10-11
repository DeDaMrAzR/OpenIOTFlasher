using System;
using System.Drawing;
using System.IO.Ports;
using System.Text;
using System.Windows.Forms;

namespace BK7231Flasher
{
    public partial class FormMain : Form, ILogListener
    {
        private TabPage tabPageTerminal;
        private ComboBox comboBoxTerminalPort;
        private ComboBox comboBoxTerminalBaud;
        private ComboBox comboBoxTerminalLineEnding;
        private Button buttonTerminalConnect;
        private Button buttonTerminalClear;
        private Button buttonTerminalSend;
		private CheckBox checkBoxTerminalAutoScroll;
        private Label labelTerminalStatus;
        private RichTextBox textBoxTerminalLog;
        private TextBox textBoxTerminalCommand;
        private SerialPort terminalSerialPort;
        private readonly object terminalReceiveLock = new object();
        private readonly StringBuilder terminalPreConnectText = new StringBuilder();
        private bool terminalConnectionAnnounced;
        private bool terminalPendingCarriageReturn;
        private bool terminalPreviousWasLineBreak;

        private void InitializeTerminalTab()
        {
            tabPageTerminal = new TabPage
            {
                Text = "Terminal",
                UseVisualStyleBackColor = true,
                Padding = new Padding(8),
            };

            FlowLayoutPanel connectionBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 34,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0),
            };

            connectionBar.Controls.Add(new Label
            {
                Text = "COM port:",
                AutoSize = true,
                Padding = new Padding(0, 6, 0, 0),
            });

            comboBoxTerminalPort = new ComboBox
            {
                Width = 110,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            connectionBar.Controls.Add(comboBoxTerminalPort);

            connectionBar.Controls.Add(new Label
            {
                Text = "Baud:",
                AutoSize = true,
                Padding = new Padding(8, 6, 0, 0),
            });

            comboBoxTerminalBaud = new ComboBox
            {
                Width = 100,
                DropDownStyle = ComboBoxStyle.DropDown,
                Text = "115200",
            };
            comboBoxTerminalBaud.Items.AddRange(new object[]
            {
                9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600,
                1500000, 2000000, 3000000,
            });
			comboBoxTerminalBaud.SelectionChangeCommitted += ComboBoxTerminalBaud_SelectionChangeCommitted;
            connectionBar.Controls.Add(comboBoxTerminalBaud);

            buttonTerminalConnect = new Button
            {
                Text = "Connect",
                AutoSize = true,
                Height = 26,
            };
            buttonTerminalConnect.Click += ButtonTerminalConnect_Click;
            connectionBar.Controls.Add(buttonTerminalConnect);

            buttonTerminalClear = new Button
            {
                Text = "Clear",
                AutoSize = true,
                Height = 26,
            };
            buttonTerminalClear.Click += (sender, e) => textBoxTerminalLog.Clear();
            connectionBar.Controls.Add(buttonTerminalClear);

			checkBoxTerminalAutoScroll = new CheckBox
			{
				Text = "Auto-scroll",
				AutoSize = true,
				Checked = true,
				Padding = new Padding(8, 5, 0, 0),
			};
			connectionBar.Controls.Add(checkBoxTerminalAutoScroll);

            labelTerminalStatus = new Label
            {
                Text = "Disconnected",
                AutoSize = true,
                AutoEllipsis = true,
                Padding = new Padding(8, 6, 0, 0),
            };
            connectionBar.Controls.Add(labelTerminalStatus);

            TableLayoutPanel sendBar = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 34,
                ColumnCount = 3,
                RowCount = 1,
                Padding = new Padding(0, 4, 0, 0),
            };
            sendBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            sendBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80F));
            sendBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80F));

            textBoxTerminalCommand = new TextBox
            {
                Dock = DockStyle.Fill,
            };
            textBoxTerminalCommand.KeyDown += TextBoxTerminalCommand_KeyDown;

            comboBoxTerminalLineEnding = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            comboBoxTerminalLineEnding.Items.AddRange(new object[] { "CRLF", "LF", "CR", "None" });
            comboBoxTerminalLineEnding.SelectedIndex = 1;

            buttonTerminalSend = new Button
            {
                Text = "Send",
                Dock = DockStyle.Fill,
                Enabled = false,
            };
            buttonTerminalSend.Click += ButtonTerminalSend_Click;

            sendBar.Controls.Add(textBoxTerminalCommand, 0, 0);
            sendBar.Controls.Add(comboBoxTerminalLineEnding, 1, 0);
            sendBar.Controls.Add(buttonTerminalSend, 2, 0);

            textBoxTerminalLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                WordWrap = false,
                DetectUrls = false,
                BackColor = SystemColors.Window,
                Font = new Font(FontFamily.GenericMonospace, 9.0f),
            };
            ConfigureLogContextMenu(textBoxTerminalLog);

            tabPageTerminal.Controls.Add(textBoxTerminalLog);
            tabPageTerminal.Controls.Add(sendBar);
            tabPageTerminal.Controls.Add(connectionBar);
            EnsureTerminalTabPresent();
        }

        private void EnsureTerminalTabPresent()
        {
            if (tabPageTerminal == null || tabControl1.TabPages.Contains(tabPageTerminal))
                return;

            tabControl1.TabPages.Insert(Math.Min(2, tabControl1.TabPages.Count), tabPageTerminal);
        }

        private bool IsTerminalConnected
        {
            get
            {
                try
                {
                    return terminalSerialPort != null && terminalSerialPort.IsOpen;
                }
                catch
                {
                    return false;
                }
            }
        }

        private bool EnsureTerminalPortAvailableForOperation(string requestedPort)
        {
            if (!IsTerminalConnected
                || !string.Equals(terminalSerialPort.PortName, requestedPort, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            DialogResult result = MessageBox.Show(
                this,
                requestedPort + " is currently connected in Terminal. Disconnect it and continue?",
                "COM port in use",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes)
            {
                return false;
            }

            AppendTerminalLine("Disconnected from " + requestedPort + " for flasher operation.", Color.DarkOrange);
            DisconnectTerminal(false);
            return true;
        }

        private void RefreshTerminalPorts(string[] ports)
        {
            if (comboBoxTerminalPort == null)
                return;

            string previousPort = getSelectedSerialName(comboBoxTerminalPort);
            SerialPort activePort = terminalSerialPort;
            if (activePort != null)
            {
                previousPort = activePort.PortName;
                bool connectedPortStillPresent = Array.IndexOf(ports, previousPort) >= 0;
                bool connectionStillOpen;
                try
                {
                    connectionStillOpen = activePort.IsOpen;
                }
                catch
                {
                    connectionStillOpen = false;
                }

                if (!connectedPortStillPresent || !connectionStillOpen)
                {
                    AppendTerminalLine("COM port disconnected", Color.Red);
                    DisconnectTerminal(false);
                }
            }

            if (TerminalPortListMatches(ports))
                return;

            setPortComboBoxItems(comboBoxTerminalPort, ports, previousPort);
        }

        private bool TerminalPortListMatches(string[] ports)
        {
            if (comboBoxTerminalPort.Items.Count != ports.Length)
                return false;

            for (int index = 0; index < ports.Length; index++)
            {
                if (!string.Equals(comboBoxTerminalPort.Items[index].ToString(), ports[index], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private void ButtonTerminalConnect_Click(object sender, EventArgs e)
        {
            if (terminalSerialPort != null)
            {
                DisconnectTerminal(true);
                return;
            }

            string portName = getSelectedSerialName(comboBoxTerminalPort);
            if (string.IsNullOrWhiteSpace(portName))
            {
                AppendTerminalLine("Select a COM port first.", Color.Red);
                return;
            }

            int baudRate;
            if (!int.TryParse(comboBoxTerminalBaud.Text, out baudRate) || baudRate <= 0)
            {
                AppendTerminalLine("Enter a valid baud rate.", Color.Red);
                return;
            }

            SerialPort port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                Encoding = Encoding.ASCII,
                ReadTimeout = 500,
                WriteTimeout = 500,
            };
            lock (terminalReceiveLock)
            {
                terminalConnectionAnnounced = false;
                terminalPendingCarriageReturn = false;
                terminalPreviousWasLineBreak = false;
                terminalPreConnectText.Clear();
            }
            port.DataReceived += TerminalSerialPort_DataReceived;
            terminalSerialPort = port;

            try
            {
                port.Open();
                SetTerminalConnectedState(true);
                AppendTerminalLine("Connected to " + portName + " at " + baudRate + " baud.", Color.Green);
                FlushTerminalPreConnectText();
                textBoxTerminalCommand.Focus();
            }
            catch (Exception ex)
            {
                terminalSerialPort = null;
                port.DataReceived -= TerminalSerialPort_DataReceived;
                port.Dispose();
                AppendTerminalLine("Unable to open " + portName + ": " + ex.Message, Color.Red);
            }
        }

		private void ComboBoxTerminalBaud_SelectionChangeCommitted(object sender, EventArgs e)
		{
			ApplySelectedTerminalBaudRate();
		}

		private void ApplySelectedTerminalBaudRate()
		{
			SerialPort port = terminalSerialPort;
			if (port == null || !IsTerminalConnected)
				return;

			int newBaudRate;
			if (!int.TryParse(comboBoxTerminalBaud.Text, out newBaudRate) || newBaudRate <= 0)
			{
				AppendTerminalLine("Enter a valid baud rate.", Color.Red);
				comboBoxTerminalBaud.Text = port.BaudRate.ToString();
				return;
			}

			int previousBaudRate = port.BaudRate;
			if (newBaudRate == previousBaudRate)
				return;

			try
			{
				port.BaudRate = newBaudRate;
				comboBoxTerminalBaud.Text = port.BaudRate.ToString();
				UpdateTerminalStatus(true);
				AppendTerminalLine(
					"Baud rate changed from " + previousBaudRate + " to " + port.BaudRate + ".",
					Color.DarkGreen);
			}
			catch (Exception ex)
			{
				int activeBaudRate = previousBaudRate;
				try
				{
					activeBaudRate = port.BaudRate;
					UpdateTerminalStatus(true);
				}
				catch
				{
				}
				comboBoxTerminalBaud.Text = activeBaudRate.ToString();
				AppendTerminalLine("Unable to change baud rate: " + ex.Message, Color.Red);
			}
		}

        private void TerminalSerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            SerialPort port = sender as SerialPort;
            if (port == null || port != terminalSerialPort)
                return;

            try
            {
                string received = port.ReadExisting();
                if (!string.IsNullOrEmpty(received))
                    AppendTerminalReceivedText(received);
            }
            catch (Exception ex)
            {
                if (port != terminalSerialPort || IsDisposed || Disposing)
                    return;

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (port != terminalSerialPort)
                            return;
                        AppendTerminalLine("Receive failed: " + ex.Message, Color.Red);
                        DisconnectTerminal(false);
                    });
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        private void ButtonTerminalSend_Click(object sender, EventArgs e)
        {
            SendTerminalCommand();
        }

        private void TextBoxTerminalCommand_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter)
                return;

            e.SuppressKeyPress = true;
            SendTerminalCommand();
        }

        private void SendTerminalCommand()
        {
            if (!IsTerminalConnected)
            {
                AppendTerminalLine("Connect to a COM port before sending.", Color.Red);
                return;
            }

            string command = textBoxTerminalCommand.Text;
            if (command.Length == 0)
                return;

            string lineEnding;
            switch (comboBoxTerminalLineEnding.SelectedItem as string)
            {
                case "LF":
                    lineEnding = "\n";
                    break;
                case "CR":
                    lineEnding = "\r";
                    break;
                case "None":
                    lineEnding = "";
                    break;
                default:
                    lineEnding = "\r\n";
                    break;
            }

            try
            {
                terminalSerialPort.Write(command + lineEnding);
                AppendTerminalLine("> " + command, Color.Blue);
                textBoxTerminalCommand.Clear();
            }
            catch (Exception ex)
            {
                AppendTerminalLine("Send failed: " + ex.Message, Color.Red);
                DisconnectTerminal(false);
            }
        }

        private void DisconnectTerminal(bool reportDisconnect)
        {
            SerialPort port = terminalSerialPort;
            terminalSerialPort = null;

            lock (terminalReceiveLock)
            {
                terminalConnectionAnnounced = false;
                terminalPendingCarriageReturn = false;
                terminalPreviousWasLineBreak = false;
                terminalPreConnectText.Clear();
            }

            if (port != null)
            {
                port.DataReceived -= TerminalSerialPort_DataReceived;
                try
                {
                    if (port.IsOpen)
                        port.Close();
                }
                catch
                {
                }
                try
                {
                    port.Dispose();
                }
                catch
                {
                }
            }

            SetTerminalConnectedState(false);
            if (reportDisconnect)
                AppendTerminalLine("Disconnected.", Color.DarkOrange);
        }

        private void CloseTerminalPort()
        {
            DisconnectTerminal(false);
        }

        private void SetTerminalConnectedState(bool connected)
        {
            comboBoxTerminalPort.Enabled = !connected;
			comboBoxTerminalBaud.Enabled = true;
            buttonTerminalConnect.Text = connected ? "Disconnect" : "Connect";
            buttonTerminalSend.Enabled = connected;
			UpdateTerminalStatus(connected);
        }

		private void UpdateTerminalStatus(bool connected)
		{
			labelTerminalStatus.Text = connected
				? "Connected at " + terminalSerialPort.BaudRate + " baud"
				: "Disconnected";
			labelTerminalStatus.ForeColor = connected ? Color.DarkGreen : SystemColors.ControlText;
		}

        private void AppendTerminalLine(string text, Color color)
        {
            AppendTerminalText(text + Environment.NewLine, color);
        }

        private void AppendTerminalReceivedText(string text)
        {
            string normalized;
            lock (terminalReceiveLock)
            {
                if (!terminalConnectionAnnounced)
                {
                    terminalPreConnectText.Append(text);
                    return;
                }
                normalized = NormalizeTerminalLineEndings(text);
            }

            if (normalized.Length > 0)
                AppendTerminalText(normalized, Color.Black);
        }

        private void FlushTerminalPreConnectText()
        {
            string normalized;
            lock (terminalReceiveLock)
            {
                terminalConnectionAnnounced = true;
                normalized = NormalizeTerminalLineEndings(terminalPreConnectText.ToString());
                terminalPreConnectText.Clear();
            }

            if (normalized.Length > 0)
                AppendTerminalText(normalized, Color.Black);
        }

        private string NormalizeTerminalLineEndings(string text)
        {
            StringBuilder normalized = new StringBuilder(text.Length + 16);
            int index = 0;

            if (terminalPendingCarriageReturn)
            {
                AppendNormalizedTerminalLineBreak(normalized);
                terminalPendingCarriageReturn = false;
                if (text.Length > 0 && text[0] == '\n')
                    index = 1;
            }

            for (; index < text.Length; index++)
            {
                char value = text[index];
                if (value == '\r')
                {
                    if (index + 1 >= text.Length)
                    {
                        terminalPendingCarriageReturn = true;
                        continue;
                    }
                    if (text[index + 1] == '\n')
                        index++;
                    AppendNormalizedTerminalLineBreak(normalized);
                }
                else if (value == '\n')
                {
                    AppendNormalizedTerminalLineBreak(normalized);
                }
                else
                {
                    normalized.Append(value);
                    terminalPreviousWasLineBreak = false;
                }
            }

            return normalized.ToString();
        }

        private void AppendNormalizedTerminalLineBreak(StringBuilder normalized)
        {
            if (!terminalPreviousWasLineBreak)
                normalized.Append(Environment.NewLine);
            terminalPreviousWasLineBreak = true;
        }

        private void AppendTerminalText(string text, Color color)
        {
            if (textBoxTerminalLog == null || textBoxTerminalLog.IsDisposed)
                return;

            if (textBoxTerminalLog.InvokeRequired)
            {
                try
                {
                    textBoxTerminalLog.BeginInvoke((MethodInvoker)delegate
                    {
                        if (!textBoxTerminalLog.IsDisposed)
							RichTextUtil.AppendText(
								textBoxTerminalLog,
								text,
								color,
								checkBoxTerminalAutoScroll.Checked);
                    });
                }
                catch (InvalidOperationException)
                {
                }
                return;
            }

			RichTextUtil.AppendText(
				textBoxTerminalLog,
				text,
				color,
				checkBoxTerminalAutoScroll.Checked);
        }
    }
}
