using System;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace BK7231Flasher
{
    public partial class FormCustom : Form
    {
        public FormMain fm;

        public FormCustom()
        {
            InitializeComponent();

        }

        private void FormCustom_Load(object sender, EventArgs e)
        {

        }

        static int ParseStringToInt(string input, out bool hexadecimal)
        {
            hexadecimal = input.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (hexadecimal)
            {
                input = input.Substring(2);
                return int.Parse(input, NumberStyles.HexNumber);
            }
            else
            {
                return int.Parse(input);
            }
        }
        int ofs = 0;
        int len = 0;
        bool checkRound()
        {
            bool ofsHexadecimal;
            bool lenHexadecimal;
            try
            {
                ofs = ParseStringToInt(textBoxCh0.Text, out ofsHexadecimal);
                len = ParseStringToInt(textBoxCh1.Text, out lenHexadecimal);
                if (ofs < 0 || len < 0)
                    throw new ArgumentOutOfRangeException("ofs/len", "Offsets and lengths cannot be negative.");
            }
            catch (Exception)
            {
                MessageBox.Show("Please enter valid non-negative len/ofs values within the supported range.");
                return true;
            }
            bool bChanged = false;
            if((ofs % BK7231Flasher.SECTOR_SIZE) != 0)
            {
                var roundedOfs = (ofs / BK7231Flasher.SECTOR_SIZE) * BK7231Flasher.SECTOR_SIZE;
                textBoxCh0.Text = ofsHexadecimal ? "0x" + roundedOfs.ToString("X") : roundedOfs.ToString();
                bChanged = true;
            }
            if ((len % BK7231Flasher.SECTOR_SIZE) != 0)
            {
                var roundedLen = (len / BK7231Flasher.SECTOR_SIZE) * BK7231Flasher.SECTOR_SIZE;
                textBoxCh1.Text = lenHexadecimal ? "0x" + roundedLen.ToString("X") : roundedLen.ToString();
                bChanged = true;
            }
            if (bChanged)
            {
                MessageBox.Show("Rounded the start offset and/or length down to multiples of SECTOR_SIZE ("
                    +BK7231Flasher.SECTOR_SIZE+"). Please check the adjusted values.");
                return true;
            }

            return false;
        }
        FormMain.CustomParms cp;
        bool doSharedPrepare()
        {
            if (checkRound())
            {
                return true;
            }
            cp = new FormMain.CustomParms();
            cp.ofs = ofs;
            cp.len = len;
            return false;
        }
        private void button1_Click(object sender, EventArgs e)
        {
            if(doSharedPrepare())
            {
                return;
            }
            this.Close();
            fm.doCustomRead(cp);
        }

        private void buttonCustomWrite_Click(object sender, EventArgs e)
        {
            if (doSharedPrepare())
            {
                return;
            }
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "Bin files (*.bin)|*.bin|All files (*.*)|*.*",
                Title = "Select a BIN file"
            };

            if (openFileDialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }
            cp.sourceFileName = openFileDialog.FileName;
            this.Close();
            fm.doCustomWrite(cp);
        }

        private void btnRestoreRF_Click(object sender, EventArgs e)
        {
            if(doSharedPrepare())
            {
                return;
            }
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "Bin files (*.bin)|*.bin|All files (*.*)|*.*",
                Title = "Select a backup file"
            };

            if(openFileDialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }
            this.Close();
            var file = new FileStream(openFileDialog.FileName, FileMode.Open);
            var data = new byte[file.Length];
            file.Read(data, 0, data.Length);
            file.Dispose();
            fm.restoreRFfromBackup(data);
        }
    }
}
