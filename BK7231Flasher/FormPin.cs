using System;
using System.Globalization;
using System.Windows.Forms;

namespace BK7231Flasher
{
    public partial class FormPin : Form
    {
        int index;
        OBKConfig cfg;

        public FormPin()
        {
            InitializeComponent();
            comboBoxRole.Items.AddRange(OBKRoles.names);
        }

        private void FormPin_Load(object sender, EventArgs e)
        {

        }

        internal void setupEditingPin(OBKConfig cfg, int index)
        {
            this.index = index;
            this.cfg = cfg;
            comboBoxRole.SelectedIndex = cfg.getPinRole(index);
            textBoxCh0.Text = ""+cfg.getPinChannel(index);
            textBoxCh1.Text = "" + cfg.getPinChannel2(index);
        }

        private void buttonOk_Click(object sender, EventArgs e)
        {
            if (!int.TryParse(textBoxCh0.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ch0) || ch0 < byte.MinValue || ch0 > byte.MaxValue)
            {
                MessageBox.Show("Channel 0 must be a whole number from 0 to 255.");
                return;
            }
            if (!int.TryParse(textBoxCh1.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ch1) || ch1 < byte.MinValue || ch1 > byte.MaxValue)
            {
                MessageBox.Show("Channel 1 must be a whole number from 0 to 255.");
                return;
            }
            cfg.setPinRole(this.index, (byte)comboBoxRole.SelectedIndex);
            cfg.setPinChannel(this.index, (byte)ch0);
            cfg.setPinChannel2(this.index, (byte)ch1);
            this.Close();
        }
    }
}
