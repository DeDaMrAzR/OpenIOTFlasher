using System;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace BK7231Flasher
{
    public partial class FormPrompt : Form
    {
        string result;
        bool bIsCanceled = true;
        public FormPrompt()
        {
            InitializeComponent();
        }

        private void buttonCancel_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void FormPrompt_Load(object sender, EventArgs e)
        {
        }
        public string getResult()
        {
            return result;
        }

        internal bool getIsCanceled()
        {
            return bIsCanceled;
        }

        private void buttonOk_Click(object sender, EventArgs e)
        {
			result = Regex.Replace(textBox1.Text, @"\s", "_");
            bIsCanceled = false;
            this.Close();
        }

        private void textBox1_TextChanged(object sender, EventArgs e)
        {
            var textboxSender = (TextBox)sender;
			var sanitizedText = Regex.Replace(textboxSender.Text, @"[^0-9a-zA-Z_\s-]", "");
			if (sanitizedText != textboxSender.Text)
			{
				var cursorPosition = textboxSender.SelectionStart;
				textboxSender.Text = sanitizedText;
				textboxSender.SelectionStart = cursorPosition;
			}
        }
    }
}
