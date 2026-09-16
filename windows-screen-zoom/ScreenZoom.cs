using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenZoom
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Home());
        }
    }

    internal sealed class Home : Form
    {
        private readonly Timer startTimer = new Timer { Interval = 100 };
        private readonly Label status;
        private ZoomView view;
        private DateTime readyAt;
        private bool registered;

        public Home()
        {
            Text = "Screen Zoom";
            ClientSize = new Size(510, 345);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = Color.FromArgb(20, 25, 35);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 11);
            Controls.Add(new Label { Text = "Zoom. Hold. Focus.", Font = new Font("Segoe UI", 23, FontStyle.Bold),
                Location = new Point(28, 24), Size = new Size(460, 45) });
            Controls.Add(new Label { Text = "Freeze your desktop and magnify a fixed point.\r\nMouse movement never pans the view.",
                Location = new Point(30, 82), Size = new Size(450, 55), ForeColor = Color.LightGray });
            Controls.Add(new Label { Text = "Wheel or + / -   Zoom from 100% to 800%\r\nEsc   Unlock and return to your desktop\r\nCtrl + Alt + Z   Start from any app",
                Location = new Point(30, 151), Size = new Size(450, 85) });
            var start = new Button { Text = "Start in 3 seconds", Location = new Point(30, 248), Size = new Size(235, 43),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(66, 104, 230), ForeColor = Color.White };
            start.Click += delegate { Schedule(3); };
            Controls.Add(start);
            status = new Label { Text = "Place the pointer on the detail you want to zoom.",
                Location = new Point(30, 307), Size = new Size(460, 26), Font = new Font("Segoe UI", 9) };
            Controls.Add(status);
            startTimer.Tick += StartWhenReady;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            registered = Native.RegisterHotKey(Handle, 1, 0x4003, (uint)Keys.Z);
            if (!registered) status.Text = "Shortcut unavailable. Use the Start button instead.";
        }

        private void Schedule(int seconds)
        {
            if (view != null || startTimer.Enabled) return;
            Hide();
            readyAt = DateTime.UtcNow.AddSeconds(seconds);
            startTimer.Start();
        }

        private void StartWhenReady(object sender, EventArgs e)
        {
            if (DateTime.UtcNow < readyAt) return;
            // Wait for the initiating chord/button to be released so no modifier
            // key-up is swallowed after its key-down reached another application.
            for (int key = 1; key < 256; key++)
                if ((Native.GetAsyncKeyState(key) & 0x8000) != 0) return;
            startTimer.Stop();
            Bitmap capture = null;
            try
            {
                Rectangle bounds = SystemInformation.VirtualScreen;
                Point anchor = Cursor.Position;
                capture = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
                using (Graphics g = Graphics.FromImage(capture))
                    g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                view = new ZoomView(capture, bounds, anchor);
                capture = null; // The view owns the bitmap from here on.
                view.FormClosed += delegate { view = null; Show(); Activate(); };
                view.Show();
            }
            catch (Exception ex)
            {
                if (capture != null) capture.Dispose();
                if (view != null) { view.Dispose(); view = null; }
                Show();
                MessageBox.Show(this, "Could not start zoom.\r\n" + ex.Message, "Screen Zoom", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == 1) { Schedule(1); return; }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                startTimer.Dispose();
                if (view != null) view.Dispose();
                if (registered) Native.UnregisterHotKey(Handle, 1);
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class ZoomView : Form
    {
        private readonly Bitmap capture;
        private readonly Point anchor;
        private readonly Rectangle desktop;
        private readonly Native.HookProc keyboardProc;
        private readonly Timer safetyTimer = new Timer { Interval = 250 };
        private IntPtr keyboardHook;
        private int step = 1;
        private int wheelRemainder;
        private bool closing;

        public ZoomView(Bitmap capture, Rectangle desktop, Point pointer)
        {
            this.capture = capture;
            this.desktop = desktop;
            anchor = new Point(pointer.X - desktop.X, pointer.Y - desktop.Y);
            keyboardProc = Keyboard;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = desktop;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            BackColor = Color.Black;
            safetyTimer.Tick += delegate
            {
                // Fail open if Windows switches desktops, another application
                // takes focus, or the monitor arrangement changes.
                if (!SystemInformation.VirtualScreen.Equals(desktop) || Native.GetForegroundWindow() != Handle)
                    Unlock();
            };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            keyboardHook = Native.SetWindowsHookEx(13, keyboardProc, Native.GetModuleHandle(null), 0);
            if (keyboardHook == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Unlock();
                MessageBox.Show("Could not lock keyboard input: " + new Win32Exception(error).Message, "Screen Zoom");
                return;
            }
            safetyTimer.Start();
        }

        private IntPtr Keyboard(int code, IntPtr message, IntPtr data)
        {
            if (code < 0 || closing) return Native.CallNextHookEx(keyboardHook, code, message, data);
            int msg = message.ToInt32();
            int key = Marshal.ReadInt32(data);
            // Queue work and return immediately: a slow low-level hook can be
            // silently removed by Windows. Never paint or capture in this hook.
            if (msg == 0x100 || msg == 0x104)
            {
                if (key == (int)Keys.Escape) BeginInvoke((Action)Unlock);
                else if (key == (int)Keys.Oemplus || key == (int)Keys.Add)
                    BeginInvoke((Action)delegate { Zoom(1); });
                else if (key == (int)Keys.OemMinus || key == (int)Keys.Subtract)
                    BeginInvoke((Action)delegate { Zoom(-1); });
            }
            return new IntPtr(1);
        }

        private void Zoom(int direction)
        {
            if (closing) return;
            step = Math.Max(0, Math.Min(28, step + direction));
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            wheelRemainder += e.Delta;
            while (Math.Abs(wheelRemainder) >= 120)
            {
                int direction = Math.Sign(wheelRemainder);
                wheelRemainder -= direction * 120;
                Zoom(direction);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            float zoom = 1f + step * 0.25f;
            float width = capture.Width / zoom;
            float height = capture.Height / zoom;
            // Keep the original pointer position at the same screen coordinate.
            // The current pointer position is deliberately never consulted.
            float x = Math.Max(0, Math.Min(capture.Width - width, anchor.X * (1f - 1f / zoom)));
            float y = Math.Max(0, Math.Min(capture.Height - height, anchor.Y * (1f - 1f / zoom)));
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(capture, ClientRectangle, new RectangleF(x, y, width, height), GraphicsUnit.Pixel);
            // One instruction card per monitor keeps the escape route visible.
            foreach (Screen screen in Screen.AllScreens)
            {
                Rectangle area = screen.Bounds;
                int left = area.Left - desktop.Left + 20;
                int top = area.Top - desktop.Top + 20;
                using (var background = new SolidBrush(Color.FromArgb(235, 20, 25, 35)))
                using (var font = new Font("Segoe UI", 12, FontStyle.Bold))
                {
                    e.Graphics.FillRectangle(background, left, top, 390, 62);
                    e.Graphics.DrawString(String.Format("{0:0}%  |  FROZEN & LOCKED", zoom * 100), font, Brushes.White, left + 14, top + 8);
                    e.Graphics.DrawString("Wheel / + / -  zoom    |    Esc  unlock", Font, Brushes.White, left + 14, top + 36);
                }
            }
        }

        private void Unlock()
        {
            if (closing) return;
            closing = true;
            ReleaseHook();
            Close();
        }

        private void ReleaseHook()
        {
            safetyTimer.Stop();
            if (keyboardHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(keyboardHook);
                keyboardHook = IntPtr.Zero;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x007E && IsHandleCreated) BeginInvoke((Action)Unlock); // WM_DISPLAYCHANGE
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            closing = true;
            ReleaseHook();
            if (disposing) { safetyTimer.Dispose(); capture.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal static class Native
    {
        internal delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] internal static extern IntPtr GetModuleHandle(string module);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int kind, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    }
}
