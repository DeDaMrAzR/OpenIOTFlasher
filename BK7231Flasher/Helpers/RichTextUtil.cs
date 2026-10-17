using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BK7231Flasher
{
    class RichTextUtil
    {
        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int wMsg, int wParam, int lParam);

        private const int WM_VSCROLL = 0x115;
        private const int SB_BOTTOM  = 7;
		private const int EM_GETFIRSTVISIBLELINE = 0xCE;
		private const int EM_LINESCROLL = 0xB6;

		public static void AppendText(RichTextBox box, string text, Color color, bool scrollToBottom = true)
		{
			int firstVisibleLine = 0;
			int selectionStart = 0;
			int selectionLength = 0;
			if (!scrollToBottom)
			{
				firstVisibleLine = SendMessage(box.Handle, EM_GETFIRSTVISIBLELINE, 0, 0);
				selectionStart = box.SelectionStart;
				selectionLength = box.SelectionLength;
			}

			box.SelectionStart = box.TextLength;
			box.SelectionLength = 0;

			box.SelectionColor = color;
			box.AppendText(text);
			box.SelectionColor = box.ForeColor;

			if (scrollToBottom)
			{
				SendMessage(box.Handle, WM_VSCROLL, SB_BOTTOM, 0);
				return;
			}

			box.SelectionStart = Math.Min(selectionStart, box.TextLength);
			box.SelectionLength = Math.Min(selectionLength, box.TextLength - box.SelectionStart);
			int currentFirstVisibleLine = SendMessage(box.Handle, EM_GETFIRSTVISIBLELINE, 0, 0);
			SendMessage(box.Handle, EM_LINESCROLL, 0, firstVisibleLine - currentFirstVisibleLine);
		}

        public static void ReplaceCurrentLine(RichTextBox box, string text, Color color)
        {
            int lineStart = box.Text.LastIndexOf('\n');
            lineStart = lineStart < 0 ? 0 : lineStart + 1;

            box.SelectionStart = lineStart;
            box.SelectionLength = box.TextLength - lineStart;
            box.SelectionColor = color;
            box.SelectedText = text;
            box.SelectionColor = box.ForeColor;

            SendMessage(box.Handle, WM_VSCROLL, SB_BOTTOM, 0);
        }
    }
}
