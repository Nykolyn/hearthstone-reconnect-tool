using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using ReconnectorCore;

namespace HsReconnectorApp
{
    public class MainForm : Form
    {
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 1;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_NOREPEAT = 0x4000;   // holding the keys fires once, not repeatedly
        private const uint VK_F12 = 0x7B;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly Button _reconnectButton;
        private readonly Label _statusLabel;
        private readonly Label _hotkeyLabel;
        private readonly CheckBox _topMostCheck;
        private readonly Timer _cooldownTimer;
        private readonly Timer _statusPollTimer;
        private bool _hotkeyRegistered;

        public MainForm()
        {
            SuspendLayout();

            // Every size below is in 96-DPI pixels and gets scaled to the monitor's DPI. The
            // manifest declares the app DPI-aware, so Windows does not stretch it; without this,
            // text (sized in points) grows on a 150% display while the controls around it do
            // not, and the status line gets clipped.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = "HS Reconnector";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(280, 170);
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(26, 26, 34);

            _reconnectButton = new Button
            {
                Text = "RECONNECT",
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(46, 52, 56),
                FlatStyle = FlatStyle.Flat,
                Bounds = new Rectangle(20, 15, 240, 60),
                Cursor = Cursors.Hand
            };
            _reconnectButton.FlatAppearance.BorderColor = Color.FromArgb(255, 184, 0);
            _reconnectButton.FlatAppearance.BorderSize = 2;
            _reconnectButton.Click += (s, e) => DoReconnect();

            _statusLabel = new Label
            {
                Text = "Hearthstone: checking…",
                ForeColor = Color.Gainsboro,
                Font = new Font("Segoe UI", 9.5f),
                Bounds = new Rectangle(20, 85, 240, 22),
                TextAlign = ContentAlignment.MiddleLeft
            };

            _hotkeyLabel = new Label
            {
                Text = "Hotkey: Ctrl+F12 (works in game)",
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 9f),
                Bounds = new Rectangle(20, 110, 240, 20),
                TextAlign = ContentAlignment.MiddleLeft
            };

            _topMostCheck = new CheckBox
            {
                Text = "Always on top",
                Checked = true,
                ForeColor = Color.Gainsboro,
                Font = new Font("Segoe UI", 9f),
                Bounds = new Rectangle(20, 135, 170, 24)
            };
            _topMostCheck.CheckedChanged += (s, e) => TopMost = _topMostCheck.Checked;

            // Version comes from <Version> in Directory.Build.props; shown so bug reports carry it.
            // Not in the title bar: the window is too narrow for it there.
            var versionLabel = new Label
            {
                Text = "v" + typeof(MainForm).Assembly.GetName().Version.ToString(3),
                ForeColor = Color.DimGray,
                Font = new Font("Segoe UI", 8.5f),
                Bounds = new Rectangle(190, 135, 70, 24),
                TextAlign = ContentAlignment.MiddleRight
            };

            Controls.Add(_reconnectButton);
            Controls.Add(_statusLabel);
            Controls.Add(_hotkeyLabel);
            Controls.Add(_topMostCheck);
            Controls.Add(versionLabel);

            _cooldownTimer = new Timer { Interval = 4000 };
            _cooldownTimer.Tick += (s, e) =>
            {
                _cooldownTimer.Stop();
                _reconnectButton.Enabled = true;
                _reconnectButton.Text = "RECONNECT";
            };

            _statusPollTimer = new Timer { Interval = 2000 };
            _statusPollTimer.Tick += (s, e) => UpdateHsStatus();

            // Checked before the first poll: without admin rights there is nothing useful to do,
            // and polling would already start reading Hearthstone's logs and the hosts file.
            if (Reconnect.IsElevated())
            {
                _statusPollTimer.Start();
                UpdateHsStatus();
            }
            else
            {
                _statusLabel.Text = "Not elevated — restart as admin!";
                _statusLabel.ForeColor = Color.OrangeRed;
                _reconnectButton.Enabled = false;
            }

            ResumeLayout(false);
            PerformLayout();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Here rather than in the constructor: only now has DPI scaling set the final size.
            var screen = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(screen.Right - Width - 20, screen.Bottom - Height - 20);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _hotkeyRegistered = RegisterHotKey(Handle, HOTKEY_ID, MOD_CONTROL | MOD_NOREPEAT, VK_F12);
            if (!_hotkeyRegistered)
                _hotkeyLabel.Text = "Hotkey Ctrl+F12 unavailable (in use)";
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_hotkeyRegistered)
                UnregisterHotKey(Handle, HOTKEY_ID);
            base.OnFormClosed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && (int)m.WParam == HOTKEY_ID)
            {
                DoReconnect();
                return;
            }
            base.WndProc(ref m);
        }

        private void UpdateHsStatus()
        {
            if (Reconnect.IsHearthstoneRunning())
            {
                // Keep the game-server address cached in the background so pressing the button
                // (or the hotkey mid-combat) costs no disk I/O before the disconnect. This also
                // primes the reverse-DNS entry that keeps Hearthstone from stalling ~15s on
                // connect (see ReverseDnsPrimer).
                GameServerLocator.BeginRefresh();

                if (ReverseDnsPrimer.LastError != null)
                {
                    _statusLabel.Text = "hosts write blocked — 15s stall stays";
                    _statusLabel.ForeColor = Color.OrangeRed;
                }
                else if (ReverseDnsPrimer.LastPrimedRange != null)
                {
                    _statusLabel.Text = "Ready — DNS primed " + ReverseDnsPrimer.LastPrimedRange;
                    _statusLabel.ForeColor = Color.LightGreen;
                }
                else
                {
                    _statusLabel.Text = "Hearthstone: running";
                    _statusLabel.ForeColor = Color.LightGreen;
                }
            }
            else
            {
                _statusLabel.Text = "Hearthstone: not running";
                _statusLabel.ForeColor = Color.Gray;
            }
        }

        private async void DoReconnect()
        {
            if (!_reconnectButton.Enabled)
                return;

            _reconnectButton.Enabled = false;
            _reconnectButton.Text = "WORKING…";

            // async void: an exception escaping here would take the whole app down, so a failure
            // (e.g. the TCP API could not be loaded) is shown on the button instead.
            ReconnectResult result;
            try
            {
                result = await Task.Run(() =>
                {
                    string addr;
                    ushort port;
                    if (!GameServerLocator.TryGetCached(out addr, out port))
                        GameServerLocator.Scan(out addr, out port);
                    return Reconnect.Disconnect(addr, port);
                });
            }
            catch (Exception ex)
            {
                var inner = ex is TypeInitializationException && ex.InnerException != null ? ex.InnerException : ex;
                result = new ReconnectResult { Error = inner.Message };
            }

            if (!result.HearthstoneRunning)
            {
                _reconnectButton.Text = "HS NOT RUNNING";
            }
            else if (result.Success)
            {
                _reconnectButton.Text = "RECONNECTING…";
                _statusLabel.Text = $"Closed {result.ClosedCount} — {result.Target}";
                _statusLabel.ForeColor = Color.LightGreen;
            }
            else
            {
                _reconnectButton.Text = "FAILED";
                _statusLabel.Text = result.Error ?? "Unknown error";
                _statusLabel.ForeColor = Color.OrangeRed;
            }

            _cooldownTimer.Start();
        }
    }
}
