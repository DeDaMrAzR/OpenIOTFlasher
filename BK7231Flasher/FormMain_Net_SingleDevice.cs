using System;
using System.IO;
using System.Net;
using System.Windows.Forms;

namespace BK7231Flasher
{
    public partial class FormMain : Form, ILogListener
    {
        OBKDeviceAPI dev;
        byte[] ipOperationResult;
        int ipOperationResultLen;
        string ipOperationResultKind;
        string ipDownloadOperationKind;

        int ipDownloadGeneration;

        void invalidateIPDevice(object sender, EventArgs e)
        {
            dev = null;
            ipDownloadGeneration++;
            ipOperationResult = null;
            ipOperationResultLen = 0;
            ipOperationResultKind = null;
            buttonIPSaveResultToFile.Enabled = false;
            setIPDeviceButtonsState(false);
            labelCheckCommunicationStatus.Text = "Check communication with this address first.";
            labelIPOperationStatus.Text = "";
        }
        void postIPDeviceUpdate(OBKDeviceAPI source, MethodInvoker action)
        {
            if (IsDisposed || Disposing || !IsHandleCreated)
                return;
            try
            {
                BeginInvoke((MethodInvoker)delegate {
                    if (!IsDisposed && !Disposing && ReferenceEquals(dev, source))
                        action();
                });
            }
            catch (InvalidOperationException) { }
        }
        public void onGetInfoReply(OBKDeviceAPI self)
        {
            postIPDeviceUpdate(self, delegate {
                try
                {
                    if (self.getInfoFailed())
                    {
                        labelCheckCommunicationStatus.Text = "Failed to get reply." + Environment.NewLine;
                        setIPDeviceButtonsState(false);
                    }
                    else
                    {
                        labelCheckCommunicationStatus.Text = " success!" + Environment.NewLine + self.getInfoText();
                        setIPDeviceButtonsState(true);
                    }
                }
                catch (Exception ex)
                {
                    invalidateIPDevice(null, EventArgs.Empty);
                    labelCheckCommunicationStatus.Text = "Failed to read reply: " + ex.Message;
                }
            });
        }
        private void buttonCheckCommunication_Click(object sender, EventArgs e)
        {
            invalidateIPDevice(sender, e);
            IPAddress tg;
            bool bOk = IPAddress.TryParse(comboBoxIP.Text, out tg);
            if (bOk)
            {
                settings.addRecentTargetIP(comboBoxIP.Text);
                saveSettings();
                labelCheckCommunicationStatus.Text = "Sending GET...";
                dev = new OBKDeviceAPI(comboBoxIP.Text);
                dev.sendGetInfo(onGetInfoReply);
            }
            else
            {
                labelCheckCommunicationStatus.Text = "Invalid IP.";
                MessageBox.Show("Please enter valid IP string");
            }
        }
        bool checkIsIPOBKDeviceAndShowError()
        {
            if (dev == null)
            {
                MessageBox.Show("Check communication with this address first.");
                return false;
            }
            BKType type = dev.getBKType();
            if (type == BKType.Invalid)
            {
                MessageBox.Show("This is implemented only for BK7231N and BK7231T");
                return false;
            }
            return true;
        }
        void doSingleDeviceOBKCFGDump()
        {
            // is this BK7231?
            if (checkIsIPOBKDeviceAndShowError() == false)
            {
                return;
            }
            // this is BK7231!
            setupDownloadOperationProgressBar("Downloading OBK CFG dump...");
            OBKDeviceAPI target = dev;
            int generation = ipDownloadGeneration;
            target.sendGetFlashChunk_OBKConfig(
                (data, len) => postIPDeviceUpdate(target, delegate {
                    if (generation == ipDownloadGeneration)
                        onOBKConfigDownloaded(data, len);
                }),
                (done, total) => postIPDeviceUpdate(target, delegate {
                    if (generation == ipDownloadGeneration)
                        onOBKConfigProgress(done, total);
                }));
        }
        void setupDownloadOperationProgressBar(string opStr)
        {
            ipDownloadGeneration++;
            ipOperationResult = null;
            ipOperationResultLen = 0;
            buttonIPSaveResultToFile.Enabled = false;
            ipDownloadOperationKind = opStr;
            updateIPOperationStatus(" starting...");
        }


        void updateIPOperationStatus(string subStr)
        {
            labelIPOperationStatus.Text = ipDownloadOperationKind + " " + subStr;
        }

        private void onGenericProgress(int done, int total)
        {
            Singleton.progressBarIPOperation.Invoke((MethodInvoker)delegate {
                if (done > total)
                    done = total;
                progressBarIPOperation.SetState(1);
                progressBarIPOperation.Maximum = total;
                progressBarIPOperation.Value = done;
                float per = (float)done / (float)total;
                per *= 100.0f;
                updateIPOperationStatus(" done " + per.ToString("0.0")+"%");
            });
        }
        private void onOBKConfigProgress(int done, int total)
        {
            onGenericProgress(done, total);
        }
        private int saveIPBinaryResultTo(string filePath)
        {
            if (ipOperationResult == null || ipOperationResultLen <= 0
                || ipOperationResultLen != ipOperationResult.Length)
                return 0;
            using (FileStream fs = new FileStream(filePath, FileMode.Create))
            {
                fs.Write(ipOperationResult, 0, ipOperationResultLen);
            }
            return ipOperationResultLen;
        }
        private void onIPSaveResultToFileClicked()
        {
            SaveFileDialog saveFileDialog = new SaveFileDialog();
            saveFileDialog.Filter = "Binary files (*.bin)|*.bin|All files (*.*)|*.*";
            DialogResult result = saveFileDialog.ShowDialog();
            if (result == DialogResult.OK && saveFileDialog.FileName.Length > 0)
            {
                int written = saveIPBinaryResultTo(saveFileDialog.FileName);
                if (written == 0)
                {
                    MessageBox.Show("Failed to save file.");
                }
                else
                {
                    MessageBox.Show("Config saved. Remember that you can just drag and drop it over the form to view it later.");
                }
            }
        }
        private void handleGenericProgressFinish(byte [] data, int len, string kind)
        {
            if (data == null || len <= 0 || len != data.Length)
            {
                data = null;
                len = 0;
            }
            this.ipOperationResult = data;
            this.ipOperationResultLen = len;
            this.ipOperationResultKind = kind;

            Singleton.progressBarIPOperation.Invoke((MethodInvoker)delegate {
                if (data != null)
                {
                    progressBarIPOperation.Value = progressBarIPOperation.Maximum;
                    updateIPOperationStatus(" success!");
                    buttonIPSaveResultToFile.Enabled = true;
                }
                else
                {
                    progressBarIPOperation.SetState(2);
                    updateIPOperationStatus(" failed!");
                    buttonIPSaveResultToFile.Enabled = false;
                }
            });
        }
        private void onOBKConfigDownloaded(byte[] data, int dataLen)
        {
            handleGenericProgressFinish(data, dataLen,"OBKCFG");
        }
        private void onTuyaConfigDownloaded(byte[] data, int dataLen)
        {
            handleGenericProgressFinish(data, dataLen,"TuyaCFG");
        }
        private void onTuyaConfigProgress(int done, int total)
        {
            onGenericProgress(done, total);
        }

        void doSingleDeviceTuyaCFGDump()
        {
            // is this BK7231?
            if (checkIsIPOBKDeviceAndShowError() == false)
            {
                return;
            }
            // this is BK7231!
            setupDownloadOperationProgressBar("Downloading Tuya CFG dump from OBK device...");
            OBKDeviceAPI target = dev;
            int generation = ipDownloadGeneration;
            target.sendGetFlashChunk_TuyaCFGFromOBKDevice(
                (data, len) => postIPDeviceUpdate(target, delegate {
                    if (generation == ipDownloadGeneration)
                        onTuyaConfigDownloaded(data, len);
                }),
                (done, total) => postIPDeviceUpdate(target, delegate {
                    if (generation == ipDownloadGeneration)
                        onTuyaConfigProgress(done, total);
                }));
        }


        void setIPDeviceButtonsState(bool b)
        {
            buttonIPCFGDump.Enabled = b && dev != null && OBKFlashLayout.hasConfigLocation(dev.getBKType());
            buttonIPDump2MB.Enabled = b;
            buttonIPDownloadTuyaConfig.Enabled = b;
        }
    }
}
