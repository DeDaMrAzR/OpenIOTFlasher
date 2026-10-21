using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BK7231Flasher
{
    public partial class FormMain : Form, ILogListener
    {
		private ComboBox comboBoxTerminalColors;
		private bool terminalReconnectWanted;
		private bool terminalClosing;
		private string terminalReconnectPort;
		private int terminalReconnectBaud;
		private DateTime terminalNextReconnectUtc;
		private string terminalReconnectError;
		private int terminalConnectionGeneration;
		private int terminalOpeningGeneration;
		private SerialPort terminalOpeningPort;
		private Task terminalOpenTask;
		private readonly List<Task> terminalFlasherTasks = new List<Task>();
		private int terminalFlasherReservations;
        private SerialPort terminalSerialPort;
        private readonly object terminalReceiveLock = new object();
        private readonly StringBuilder terminalPreConnectText = new StringBuilder();
        private bool terminalConnectionAnnounced;
        private bool terminalPendingCarriageReturn;
        private bool terminalPreviousWasLineBreak;

		private void InitializeTerminalTab()
		{
			Color standardBackground = textBoxTerminalLog.BackColor;
			Color standardForeground = textBoxTerminalLog.ForeColor;
			comboBoxTerminalColors = new ComboBox
			{
				Name = "comboBoxTerminalColors",
				AccessibleName = "Terminal colors",
				DropDownStyle = ComboBoxStyle.DropDownList,
				Location = new Point(checkBoxTerminalAutoScroll.Right + 8, comboBoxTerminalBaud.Top),
				Size = new Size(190, comboBoxTerminalBaud.Height),
				TabIndex = 9
			};
			comboBoxTerminalColors.Items.AddRange(new object[] {
				"Standard", "Black / White text", "Black / Green text"
			});
			comboBoxTerminalColors.SelectedIndex = 0;
			comboBoxTerminalColors.SelectedIndexChanged += (sender, e) => {
				int selectionStart = textBoxTerminalLog.SelectionStart;
				int selectionLength = textBoxTerminalLog.SelectionLength;
				textBoxTerminalLog.BackColor = comboBoxTerminalColors.SelectedIndex == 0
					? standardBackground : Color.Black;
				textBoxTerminalLog.ForeColor = comboBoxTerminalColors.SelectedIndex == 0
					? standardForeground : comboBoxTerminalColors.SelectedIndex == 1 ? Color.White : Color.Lime;
				textBoxTerminalLog.SelectAll();
				textBoxTerminalLog.SelectionColor = textBoxTerminalLog.ForeColor;
				textBoxTerminalLog.Select(selectionStart, selectionLength);
			};
			tabPageTerminal.Controls.Add(comboBoxTerminalColors);
			comboBoxTerminalLineEnding.SelectedIndex = 1;
			ConfigureLogContextMenu(textBoxTerminalLog);
			checkBoxTerminalAutoScroll.CheckedChanged += (sender, e) => {
				if (checkBoxTerminalAutoScroll.Checked)
					RichTextUtil.ScrollToBottom(textBoxTerminalLog);
			};
		}

		private void ComboBoxTerminalPort_SelectionChangeCommitted(object sender, EventArgs e)
		{
			CancelTerminalReconnect();
			SetTerminalConnectedState(IsTerminalConnected);
		}

		private void CheckBoxTerminalAutoReconnect_CheckedChanged(object sender, EventArgs e)
		{
			if (!checkBoxTerminalAutoReconnect.Checked)
				CancelTerminalReconnect();
			else if (IsTerminalConnected)
				RememberTerminalReconnectSettings();
			SetTerminalConnectedState(IsTerminalConnected);
		}

		private void ButtonTerminalClear_Click(object sender, EventArgs e)
		{
			textBoxTerminalLog.Clear();
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
			if (terminalOpenTask != null)
			{
				MessageBox.Show(this, "Terminal is finishing a connection attempt. Please retry the operation.",
					"Terminal connection pending", MessageBoxButtons.OK, MessageBoxIcon.Information);
				return false;
			}
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

			DisconnectTerminal();
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
					DisconnectTerminal();
                }
            }

			if (!TerminalPortListMatches(ports))
				setPortComboBoxItems(comboBoxTerminalPort, ports, previousPort);
			TryTerminalReconnect(ports);
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
			if (terminalSerialPort != null || terminalReconnectWanted || terminalOpenTask != null)
            {
				CancelTerminalReconnect();
				DisconnectTerminal();
                return;
            }
			if (TerminalFlasherBusy)
			{
				labelTerminalStatus.Text = "Waiting for flasher operation to finish";
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

			OpenTerminalPortAsync(portName, baudRate, false);
        }

		private bool TerminalFlasherBusy => terminalFlasherReservations != 0 || worker != null
			|| terminalFlasherTasks.Exists(task => !task.IsCompleted)
			|| flasher != null || sv6x66OperationLockActive;

		private sealed class TerminalFlasherReservation : IDisposable
		{
			private FormMain owner;
			internal TerminalFlasherReservation(FormMain owner)
			{
				this.owner = owner;
				owner.terminalFlasherReservations++;
				owner.InvalidateTerminalOpening();
			}
			public void Dispose()
			{
				if (owner == null)
					return;
				owner.terminalFlasherReservations--;
				owner = null;
			}
		}

		private void InvalidateTerminalOpening()
		{
			terminalConnectionGeneration++;
			SerialPort opening = terminalOpeningPort;
			if (opening != null)
				Task.Run(() => DisposeTerminalPort(opening));
		}

		private void CancelTerminalReconnect()
		{
			terminalReconnectWanted = false;
			terminalReconnectError = null;
			InvalidateTerminalOpening();
		}

		private void RememberTerminalReconnectSettings()
		{
			terminalReconnectPort = terminalSerialPort.PortName;
			terminalReconnectBaud = terminalSerialPort.BaudRate;
			terminalReconnectWanted = checkBoxTerminalAutoReconnect.Checked;
			terminalReconnectError = null;
		}

		private void TryTerminalReconnect(string[] ports)
		{
			if (terminalClosing || !terminalReconnectWanted || IsTerminalConnected)
				return;
			UpdateTerminalStatus(false);
			if (TerminalFlasherBusy || terminalOpenTask != null || DateTime.UtcNow < terminalNextReconnectUtc
				|| Array.IndexOf(ports, terminalReconnectPort) < 0)
				return;
			terminalNextReconnectUtc = DateTime.UtcNow.AddSeconds(1);
			OpenTerminalPortAsync(terminalReconnectPort, terminalReconnectBaud, true);
		}

		private static void DisposeTerminalPort(SerialPort port)
		{
			try { port.Dispose(); } catch { }
		}

		private async void OpenTerminalPortAsync(string portName, int baudRate, bool automatic)
		{
			if (terminalClosing || terminalOpenTask != null || TerminalFlasherBusy)
				return;
			int generation = ++terminalConnectionGeneration;
			terminalOpeningGeneration = generation;
			SerialPort port = null;
			Task opening = null;
			try
			{
				port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
				{
					Handshake = Handshake.None,
					Encoding = Encoding.ASCII,
					ReadTimeout = 500,
					WriteTimeout = 500,
				};
				terminalOpeningPort = port;
				opening = Task.Run(() => port.Open());
				terminalOpenTask = opening;
				SetTerminalConnectedState(false);
				await opening;
				if (terminalClosing || generation != terminalConnectionGeneration || TerminalFlasherBusy
					|| (automatic && !terminalReconnectWanted))
					return;
				lock (terminalReceiveLock)
				{
					terminalConnectionAnnounced = false;
					terminalPendingCarriageReturn = false;
					terminalPreviousWasLineBreak = false;
					terminalPreConnectText.Clear();
					terminalSerialPort = port;
					port.DataReceived += TerminalSerialPort_DataReceived;
					AppendTerminalReceivedText(port.ReadExisting());
					FlushTerminalPreConnectText();
				}
				RememberTerminalReconnectSettings();
				setComboBoxValueByContent(comboBoxTerminalPort, portName);
				comboBoxTerminalBaud.Text = baudRate.ToString();
				SetTerminalConnectedState(true);
				if (!automatic)
					textBoxTerminalCommand.Focus();
			}
			catch (Exception exception)
			{
				if (!terminalClosing && generation == terminalConnectionGeneration)
				{
					if (port != null && terminalSerialPort == port)
						DisconnectTerminal();
					if (automatic)
						terminalReconnectError = exception.Message;
					else
						AppendTerminalLine("Unable to open " + portName + ": " + exception.Message, Color.Red);
				}
			}
			finally
			{
				if (port != null && terminalSerialPort != port)
					await Task.Run(() => DisposeTerminalPort(port));
				if (terminalOpeningPort == port)
					terminalOpeningPort = null;
				if (terminalOpenTask == opening)
					terminalOpenTask = null;
				if (!terminalClosing && !IsDisposed && !Disposing)
					SetTerminalConnectedState(IsTerminalConnected);
			}
		}

		private void ComboBoxTerminalBaud_SelectionChangeCommitted(object sender, EventArgs e)
		{
			if (comboBoxTerminalBaud.SelectedItem == null)
				return;

			ApplySelectedTerminalBaudRate((int)comboBoxTerminalBaud.SelectedItem);
		}

		private void ApplySelectedTerminalBaudRate(int newBaudRate)
		{
			SerialPort port = terminalSerialPort;
			if (port == null || !IsTerminalConnected)
				return;

			int previousBaudRate = port.BaudRate;
			if (newBaudRate == previousBaudRate)
				return;

			try
			{
				port.BaudRate = newBaudRate;
				RememberTerminalReconnectSettings();
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
				lock (terminalReceiveLock)
				{
					if (port != terminalSerialPort)
						return;
					string received = port.ReadExisting();
					if (!string.IsNullOrEmpty(received))
						AppendTerminalReceivedText(received);
				}
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
						DisconnectTerminal();
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
				DisconnectTerminal();
            }
        }

		private void DisconnectTerminal()
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
        }

        private void CloseTerminalPort()
        {
			terminalClosing = true;
			CancelTerminalReconnect();
			DisconnectTerminal();
        }

        private void SetTerminalConnectedState(bool connected)
        {
			comboBoxTerminalPort.Enabled = !connected && terminalOpenTask == null;
			comboBoxTerminalBaud.Enabled = connected || (!terminalReconnectWanted && terminalOpenTask == null);
			buttonTerminalConnect.Text = connected || terminalReconnectWanted || terminalOpenTask != null ? "Disconnect" : "Connect";
            buttonTerminalSend.Enabled = connected;
			UpdateTerminalStatus(connected);
			enforceSv6x66OperationLock();
        }

		private void UpdateTerminalStatus(bool connected)
		{
			labelTerminalStatus.Text = connected
				? "Connected at " + terminalSerialPort.BaudRate + " baud"
				: terminalOpenTask != null ? (terminalOpeningGeneration == terminalConnectionGeneration ? "Connecting..." : "Disconnecting...")
				: terminalReconnectWanted && TerminalFlasherBusy ? "Reconnect paused for flasher operation"
				: terminalReconnectWanted ? "Waiting for " + terminalReconnectPort
					+ (string.IsNullOrWhiteSpace(terminalReconnectError) ? "" : ": " + terminalReconnectError)
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
								comboBoxTerminalColors.SelectedIndex == 0 ? color : textBoxTerminalLog.ForeColor,
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
				comboBoxTerminalColors.SelectedIndex == 0 ? color : textBoxTerminalLog.ForeColor,
				checkBoxTerminalAutoScroll.Checked);
        }
    }
}
