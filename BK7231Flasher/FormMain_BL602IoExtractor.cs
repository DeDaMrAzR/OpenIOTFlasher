using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BK7231Flasher
{
	public partial class FormMain : Form, ILogListener
	{
		private TabPage tabPageBl602Io;
		private TextBox textBoxBl602IoOutput;
		private Button buttonBl602IoOpen;
		private Button buttonBl602IoCopy;
		private Button buttonBl602IoSave;
		private CheckBox checkBoxBl602IoShowDiagnostics;
		private CheckBox checkBoxBl602IoDeepScan;
		private Label labelBl602IoStatus;
		private BL602IoAnalyzer.AnalysisResult lastBl602IoResult;
		private string lastBl602IoPath;

		private void InitializeBl602IoExtractorTab()
		{
			tabPageBl602Io = new TabPage
			{
				Text = "BL602 I/O",
				UseVisualStyleBackColor = true,
				AllowDrop = true,
				Padding = new Padding(8),
			};
			tabPageBl602Io.DragEnter += TabPageBl602Io_DragEnter;
			tabPageBl602Io.DragDrop += TabPageBl602Io_DragDrop;

			FlowLayoutPanel commandBar = new FlowLayoutPanel
			{
				Dock = DockStyle.Top,
				Height = 34,
				FlowDirection = FlowDirection.LeftToRight,
				WrapContents = false,
				Padding = new Padding(0),
			};

			buttonBl602IoOpen = new Button
			{
				Text = "Open BL602 dump...",
				AutoSize = true,
				Height = 26,
			};
			buttonBl602IoOpen.Click += ButtonBl602IoOpen_Click;

			buttonBl602IoCopy = new Button
			{
				Text = "Copy report",
				AutoSize = true,
				Height = 26,
				Enabled = false,
			};
			buttonBl602IoCopy.Click += ButtonBl602IoCopy_Click;

			buttonBl602IoSave = new Button
			{
				Text = "Save text...",
				AutoSize = true,
				Height = 26,
				Enabled = false,
			};
			buttonBl602IoSave.Click += ButtonBl602IoSave_Click;

			checkBoxBl602IoShowDiagnostics = new CheckBox
			{
				Text = "Show diagnostics (not I/O assignments)",
				AutoSize = true,
				Checked = false,
				Padding = new Padding(8, 5, 0, 0),
			};
			checkBoxBl602IoShowDiagnostics.CheckedChanged += CheckBoxBl602IoShowDiagnostics_CheckedChanged;

			checkBoxBl602IoDeepScan = new CheckBox
			{
				Text = "Deep fallback scan (headerless dumps)",
				AutoSize = true,
				Checked = false,
				Padding = new Padding(8, 5, 0, 0),
			};

			commandBar.Controls.Add(buttonBl602IoOpen);
			commandBar.Controls.Add(buttonBl602IoCopy);
			commandBar.Controls.Add(buttonBl602IoSave);
			commandBar.Controls.Add(checkBoxBl602IoShowDiagnostics);
			commandBar.Controls.Add(checkBoxBl602IoDeepScan);

			labelBl602IoStatus = new Label
			{
				Dock = DockStyle.Top,
				Height = 28,
				TextAlign = ContentAlignment.MiddleLeft,
				Text = "Drop a BL602 full flash dump here, or open one. I/O evidence and optional non-assignment diagnostics are reported separately.",
				AutoEllipsis = true,
			};

			textBoxBl602IoOutput = new TextBox
			{
				Dock = DockStyle.Fill,
				Multiline = true,
				ReadOnly = true,
				WordWrap = false,
				ScrollBars = ScrollBars.Both,
				Font = new Font(FontFamily.GenericMonospace, 9.0f),
				AcceptsTab = true,
			};

			tabPageBl602Io.Controls.Add(textBoxBl602IoOutput);
			tabPageBl602Io.Controls.Add(labelBl602IoStatus);
			tabPageBl602Io.Controls.Add(commandBar);
			int terminalTabIndex = tabControl1.TabPages.IndexOf(tabPageTerminal);
			int insertTabIndex = terminalTabIndex >= 0 ? terminalTabIndex + 1 : tabControl1.TabPages.IndexOf(tabPage2);
			TabPage[] trailingTabs = new TabPage[tabControl1.TabPages.Count - insertTabIndex];
			for (int index = 0; index < trailingTabs.Length; index++)
				trailingTabs[index] = tabControl1.TabPages[insertTabIndex + index];
			foreach (TabPage trailingTab in trailingTabs)
				tabControl1.TabPages.Remove(trailingTab);
			tabControl1.TabPages.Add(tabPageBl602Io);
			tabControl1.TabPages.AddRange(trailingTabs);
		}

		private void TabPageBl602Io_DragEnter(object sender, DragEventArgs e)
		{
			if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
				e.Effect = DragDropEffects.Copy;
		}

		private async void TabPageBl602Io_DragDrop(object sender, DragEventArgs e)
		{
			if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
				return;
			string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
			if (files == null || files.Length == 0)
				return;
			await AnalyseBl602IoFileAsync(files[0]);
		}

		private async void ButtonBl602IoOpen_Click(object sender, EventArgs e)
		{
			using (OpenFileDialog dialog = new OpenFileDialog())
			{
				dialog.Title = "Open BL602 full flash dump";
				dialog.Filter = "Binary flash dumps (*.bin;*.img;*.dump)|*.bin;*.img;*.dump|All files (*.*)|*.*";
				dialog.FilterIndex = 1;
				dialog.RestoreDirectory = true;
				if (dialog.ShowDialog(this) == DialogResult.OK)
					await AnalyseBl602IoFileAsync(dialog.FileName);
			}
		}

		private void ButtonBl602IoCopy_Click(object sender, EventArgs e)
		{
			if (!string.IsNullOrEmpty(textBoxBl602IoOutput.Text))
				Clipboard.SetText(textBoxBl602IoOutput.Text);
		}

		private void ButtonBl602IoSave_Click(object sender, EventArgs e)
		{
			if (string.IsNullOrEmpty(textBoxBl602IoOutput.Text))
				return;
			using (SaveFileDialog dialog = new SaveFileDialog())
			{
				dialog.Title = "Save BL602 I/O findings";
				dialog.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
				dialog.DefaultExt = "txt";
				string baseName = string.IsNullOrWhiteSpace(lastBl602IoPath)
					? "BL602_IO_findings"
					: Path.GetFileNameWithoutExtension(lastBl602IoPath) + "_IO_findings";
				dialog.FileName = baseName + ".txt";
				dialog.RestoreDirectory = true;
				if (dialog.ShowDialog(this) == DialogResult.OK)
					File.WriteAllText(dialog.FileName, textBoxBl602IoOutput.Text);
			}
		}

		private void CheckBoxBl602IoShowDiagnostics_CheckedChanged(object sender, EventArgs e)
		{
			RenderLastBl602IoResult();
		}

		private async Task AnalyseBl602IoFileAsync(string fileName)
		{
			if (string.IsNullOrWhiteSpace(fileName))
				return;

			SetBl602IoBusy(true, "Reading and analysing " + Path.GetFileName(fileName) + "...");
			textBoxBl602IoOutput.Clear();
			lastBl602IoResult = null;
			lastBl602IoPath = fileName;

			try
			{
				bool deepScan = checkBoxBl602IoDeepScan.Checked;
				BL602IoAnalyzer.AnalysisResult result = await Task.Run(() =>
				{
					byte[] data = File.ReadAllBytes(fileName);
					BL602IoAnalyzer.AnalysisOptions options = new BL602IoAnalyzer.AnalysisOptions
					{
						DeepApplicationScan = deepScan,
						IncludeGenericPeripheralClues = true,
					};
					return BL602IoAnalyzer.Analyze(data, Path.GetFileName(fileName), options);
				});

				lastBl602IoResult = result;
				RenderLastBl602IoResult();
				labelBl602IoStatus.Text = string.Format(
					"Analysed {0}: {1} DTB(s), {2} I/O finding row(s), {3} diagnostic item(s).",
					Path.GetFileName(fileName),
					result.Dtbs.Count,
					result.Findings.Count,
					result.Diagnostics.Count);
			}
			catch (Exception ex)
			{
				textBoxBl602IoOutput.Text = "BL602 I/O analysis failed:" + Environment.NewLine + ex;
				labelBl602IoStatus.Text = "Analysis failed: " + ex.Message;
			}
			finally
			{
				SetBl602IoBusy(false, labelBl602IoStatus.Text);
			}
		}

		private void RenderLastBl602IoResult()
		{
			if (lastBl602IoResult == null)
				return;
			textBoxBl602IoOutput.Text = lastBl602IoResult.ToPlainText(checkBoxBl602IoShowDiagnostics.Checked);
			buttonBl602IoCopy.Enabled = textBoxBl602IoOutput.TextLength != 0;
			buttonBl602IoSave.Enabled = textBoxBl602IoOutput.TextLength != 0;
		}

		private void SetBl602IoBusy(bool busy, string status)
		{
			buttonBl602IoOpen.Enabled = !busy;
			buttonBl602IoCopy.Enabled = !busy && lastBl602IoResult != null;
			buttonBl602IoSave.Enabled = !busy && lastBl602IoResult != null;
			checkBoxBl602IoShowDiagnostics.Enabled = !busy;
			checkBoxBl602IoDeepScan.Enabled = !busy;
			labelBl602IoStatus.Text = status ?? string.Empty;
			UseWaitCursor = busy;
		}
	}
}
