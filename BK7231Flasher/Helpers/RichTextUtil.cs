using System;
using System.Drawing;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BK7231Flasher
{
    class RichTextUtil
    {
        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int wMsg, int wParam, int lParam);
		[DllImport("user32.dll", EntryPoint = "SendMessageW")]
		private static extern IntPtr SendMessage(IntPtr hWnd, int wMsg, IntPtr wParam, ref Point lParam);

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_ERASE = 0x0004;
        private const uint RDW_ALLCHILDREN = 0x0080;
        private const uint RDW_FRAME = 0x0400;

        private const int WM_VSCROLL = 0x115;
        private const int SB_BOTTOM  = 7;
		private const int WM_SETREDRAW = 0x0B;
		private const int EM_GETSCROLLPOS = 0x04DD;
		private const int EM_SETSCROLLPOS = 0x04DE;

		public static void AppendText(RichTextBox box, string text, Color color, bool scrollToBottom = true)
		{
			AppendText(box, new[] { new KeyValuePair<string, Color>(text, color) }, scrollToBottom);
		}

		public static void AppendText(RichTextBox box, IEnumerable<KeyValuePair<string, Color>> output, bool scrollToBottom = true)
		{
			IntPtr handle = box.Handle;
			Point scrollPosition = Point.Empty;
			int selectionStart = 0;
			int selectionLength = 0;
			if (!scrollToBottom)
			{
				SendMessage(handle, EM_GETSCROLLPOS, IntPtr.Zero, ref scrollPosition);
				selectionStart = box.SelectionStart;
				selectionLength = box.SelectionLength;
			}
			bool suspendRedraw = box.Visible;
			if (suspendRedraw)
				SendMessage(handle, WM_SETREDRAW, 0, 0);
			try
			{
				box.Select(box.TextLength, 0);
				foreach (KeyValuePair<string, Color> part in output)
				{
					box.SelectionColor = part.Value;
					box.AppendText(part.Key);
				}
				box.SelectionColor = box.ForeColor;
				if (scrollToBottom)
					ScrollToBottom(box);
				else
				{
					selectionStart = Math.Min(selectionStart, box.TextLength);
					box.Select(selectionStart, Math.Min(selectionLength, box.TextLength - selectionStart));
					SendMessage(handle, EM_SETSCROLLPOS, IntPtr.Zero, ref scrollPosition);
				}
			}
			finally
			{
				if (suspendRedraw)
				{
					SendMessage(handle, WM_SETREDRAW, 1, 0);
					RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero,
						RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN);
				}
			}
		}

		public static void ScrollToBottom(RichTextBox box)
		{
			box.Select(box.TextLength, 0);
			box.ScrollToCaret();
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
