using System;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Rdptun.App
{
    public sealed class MainForm : Form
    {
        private readonly TextBox _server = new TextBox();
        private readonly TextBox _port = new TextBox();
        private readonly TextBox _user = new TextBox();
        private readonly TextBox _password = new TextBox();
        private readonly Button _connect = new Button();
        private readonly Button _disconnect = new Button();
        private readonly Label _status = new Label();
        private readonly TextBox _log = new TextBox();

        private readonly TunnelPipeServer _pipe = new TunnelPipeServer();
        private TunnelManager _tunnel;
        private RdpLauncher _rdp;
        private string _serverIpv4;
        private bool _tunnelStarting;

        public MainForm()
        {
            Text = "RDP Tunnel";
            Width = 720;
            Height = 560;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;

            int y = 20;
            AddField("Server", _server, ref y, "155.212.167.12");
            AddField("Port", _port, ref y, "3389");
            AddField("Username", _user, ref y, "rdptun");
            AddField("Password", _password, ref y, "");
            _password.UseSystemPasswordChar = true;

            _connect.Text = "Connect";
            _connect.SetBounds(150, y, 130, 34);
            _connect.Click += ConnectClicked;
            Controls.Add(_connect);

            _disconnect.Text = "Disconnect";
            _disconnect.SetBounds(290, y, 130, 34);
            _disconnect.Enabled = false;
            _disconnect.Click += DisconnectClicked;
            Controls.Add(_disconnect);
            y += 46;

            _status.SetBounds(20, y, 660, 28);
            _status.Text = "Idle";
            _status.Font = new Font(_status.Font, FontStyle.Bold);
            Controls.Add(_status);
            y += 32;

            _log.SetBounds(20, y, 660, 280);
            _log.Multiline = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.ReadOnly = true;
            _log.Font = new Font("Consolas", 9F);
            Controls.Add(_log);

            _pipe.StatusMessage += s => Log("PLUGIN: " + s);
            _pipe.DvcOpened += PipeDvcOpened;
            _pipe.DvcClosed += () => BeginInvokeSafe(() =>
            {
                Log("DVC rdptun closed");
                StopTunnelOnly();
                SetStatus("RDP carrier connected, DVC closed");
            });
            _pipe.PacketFromDvc += packet =>
            {
                TunnelManager t = _tunnel;
                if (t != null)
                    t.InjectPacket(packet);
            };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            TunnelManager.CleanupStale(Log);
            _pipe.Start();
            Log("Waiting for RDP DVC plugin process...");
        }

        private void AddField(string caption, TextBox box, ref int y, string value)
        {
            Label label = new Label { Text = caption, Left = 20, Top = y + 4, Width = 120 };
            box.SetBounds(150, y, 530, 26);
            box.Text = value;
            Controls.Add(label);
            Controls.Add(box);
            y += 36;
        }

        private void ConnectClicked(object sender, EventArgs e)
        {
            string host = _server.Text.Trim();
            string user = _user.Text.Trim();
            string password = _password.Text;
            int port;
            if (!int.TryParse(_port.Text.Trim(), out port) || port < 1 || port > 65535)
                port = 3389;

            if (host.Length == 0 || user.Length == 0 || password.Length == 0)
            {
                MessageBox.Show(this, "Server, username and password are required.", "RDP Tunnel", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _connect.Enabled = false;
            _disconnect.Enabled = true;
            SetStatus("Preparing hidden RDP carrier...");

            Task.Run(() =>
            {
                try
                {
                    _serverIpv4 = ResolveIpv4(host);
                    Log("Server IPv4: " + _serverIpv4);

                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    RdpLauncher.EnsurePluginRegistered(baseDir, Log);

                    _rdp = new RdpLauncher();
                    _rdp.Exited += () => BeginInvokeSafe(() =>
                    {
                        Log("RDP carrier exited");
                        StopTunnelOnly();
                        SetStatus("Carrier disconnected");
                        _connect.Enabled = true;
                        _disconnect.Enabled = false;
                    });
                    _rdp.Launch(_serverIpv4, port, user, password, Log);
                    SetStatus("Hidden RDP carrier started; waiting for DVC rdptun...");
                }
                catch (Exception ex)
                {
                    Log("CONNECT ERROR: " + ex);
                    SetStatus("Failed: " + ex.Message);
                    BeginInvokeSafe(() => { _connect.Enabled = true; _disconnect.Enabled = false; });
                }
            });
        }

        private void PipeDvcOpened()
        {
            BeginInvokeSafe(() =>
            {
                Log("DVC rdptun opened");
                if (_tunnel != null || _tunnelStarting)
                    return;
                _tunnelStarting = true;
                SetStatus("DVC opened; configuring Wintun...");

                Task.Run(() =>
                {
                    try
                    {
                        TunnelManager tunnel = new TunnelManager(Log);
                        tunnel.Start(_serverIpv4, packet => _pipe.SendPacketToDvc(packet));
                        _tunnel = tunnel;
                        SetStatus("Connected — IPv4 through RDP/DVC");
                    }
                    catch (Exception ex)
                    {
                        Log("TUNNEL ERROR: " + ex);
                        SetStatus("Tunnel setup failed: " + ex.Message);
                    }
                    finally
                    {
                        _tunnelStarting = false;
                    }
                });
            });
        }

        private void DisconnectClicked(object sender, EventArgs e)
        {
            _disconnect.Enabled = false;
            Task.Run(() =>
            {
                StopTunnelOnly();
                RdpLauncher rdp = _rdp;
                _rdp = null;
                if (rdp != null)
                    rdp.Dispose();
                SetStatus("Disconnected");
                BeginInvokeSafe(() => { _connect.Enabled = true; _disconnect.Enabled = false; });
            });
        }

        private void StopTunnelOnly()
        {
            TunnelManager tunnel = _tunnel;
            _tunnel = null;
            if (tunnel != null)
            {
                try { tunnel.Dispose(); }
                catch (Exception ex) { Log("Tunnel cleanup: " + ex.Message); }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { StopTunnelOnly(); } catch { }
            try { if (_rdp != null) _rdp.Dispose(); } catch { }
            try { _pipe.Dispose(); } catch { }
            base.OnFormClosing(e);
        }

        private static string ResolveIpv4(string host)
        {
            IPAddress[] addresses = Dns.GetHostAddresses(host);
            foreach (IPAddress address in addresses)
                if (address.AddressFamily == AddressFamily.InterNetwork)
                    return address.ToString();
            throw new InvalidOperationException("Server has no IPv4 address");
        }

        private void SetStatus(string text)
        {
            BeginInvokeSafe(() => _status.Text = text);
            Log(text);
        }

        private void Log(string text)
        {
            BeginInvokeSafe(() =>
            {
                _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine);
            });
        }

        private void BeginInvokeSafe(Action action)
        {
            if (IsDisposed || Disposing)
                return;
            if (InvokeRequired)
                BeginInvoke(action);
            else
                action();
        }
    }
}
