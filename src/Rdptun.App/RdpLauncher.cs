using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Rdptun.App
{
    internal sealed class RdpLauncher : IDisposable
    {
        private Process _mstsc;
        private string _credentialTarget;
        private string _rdpFile;
        private volatile bool _disposed;

        private const int SW_HIDE = 0;

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        public event Action Exited;

        public static void EnsurePluginRegistered(string baseDir, Action<string> log)
        {
            string plugin = Path.Combine(baseDir, "Rdptun.Plugin.exe");
            if (!File.Exists(plugin))
                throw new FileNotFoundException("Rdptun.Plugin.exe is missing", plugin);

            ProcessStartInfo psi = new ProcessStartInfo(plugin, "/register")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit();
                if (p.ExitCode != 0)
                    throw new InvalidOperationException("DVC plugin registration failed with exit code " + p.ExitCode);
            }
            if (log != null) log("DVC COM plugin registered for current user");
        }

        public void Launch(string serverIp, int port, string username, string password, Action<string> log)
        {
            _disposed = false;
            _credentialTarget = "TERMSRV/" + serverIp;
            RunCmdKey("/generic:" + _credentialTarget + " /user:\"" + username + "\" /pass:\"" + password.Replace("\"", "\\\"") + "\"");

            _rdpFile = Path.Combine(Path.GetTempPath(), "rdptun-" + Guid.NewGuid().ToString("N") + ".rdp");
            string fullAddress = serverIp + (port == 3389 ? string.Empty : ":" + port);

            // This is a real RDP session because DVC/drdynvc only exists inside RDP.
            // Keep the graphical side deliberately tiny and feature-poor: the session is
            // only a transport carrier for the rdptun DVC, not a user-facing desktop.
            StringBuilder rdp = new StringBuilder();
            rdp.AppendLine("screen mode id:i:1");
            rdp.AppendLine("desktopwidth:i:320");
            rdp.AppendLine("desktopheight:i:240");
            rdp.AppendLine("session bpp:i:16");
            rdp.AppendLine("full address:s:" + fullAddress);
            rdp.AppendLine("username:s:" + username);
            rdp.AppendLine("prompt for credentials on client:i:0");
            rdp.AppendLine("authentication level:i:0");
            rdp.AppendLine("enablecredsspsupport:i:1");

            // Minimise non-DVC traffic and redirections.
            rdp.AppendLine("connection type:i:1");
            rdp.AppendLine("networkautodetect:i:0");
            rdp.AppendLine("bandwidthautodetect:i:0");
            rdp.AppendLine("compression:i:1");
            rdp.AppendLine("disable wallpaper:i:1");
            rdp.AppendLine("allow font smoothing:i:0");
            rdp.AppendLine("allow desktop composition:i:0");
            rdp.AppendLine("disable full window drag:i:1");
            rdp.AppendLine("disable menu anims:i:1");
            rdp.AppendLine("disable themes:i:1");
            rdp.AppendLine("bitmapcachepersistenable:i:0");
            rdp.AppendLine("displayconnectionbar:i:0");
            rdp.AppendLine("smart sizing:i:0");
            rdp.AppendLine("use multimon:i:0");
            rdp.AppendLine("videoplaybackmode:i:0");

            rdp.AppendLine("redirectclipboard:i:0");
            rdp.AppendLine("redirectprinters:i:0");
            rdp.AppendLine("redirectcomports:i:0");
            rdp.AppendLine("redirectsmartcards:i:0");
            rdp.AppendLine("redirectposdevices:i:0");
            rdp.AppendLine("redirectdirectx:i:0");
            rdp.AppendLine("drivestoredirect:s:");
            rdp.AppendLine("audiomode:i:2");
            rdp.AppendLine("autoreconnection enabled:i:1");

            File.WriteAllText(_rdpFile, rdp.ToString(), Encoding.Unicode);

            ProcessStartInfo psi = new ProcessStartInfo("mstsc.exe", "\"" + _rdpFile + "\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized
            };

            _mstsc = Process.Start(psi);
            if (_mstsc == null)
                throw new InvalidOperationException("Could not start mstsc.exe");

            _mstsc.EnableRaisingEvents = true;
            _mstsc.Exited += (s, e) =>
            {
                CleanupCredential();
                CleanupFile();
                Action handler = Exited;
                if (handler != null) handler();
            };

            // mstsc creates/recreates its top-level window during negotiation. Keep hiding
            // it for the lifetime of the transport so no remote desktop UI is presented.
            Task.Run(() => HideWindowLoop(log));

            if (log != null)
                log("Headless RDP transport started for " + fullAddress + "; waiting for rdptun DVC");
        }

        private void HideWindowLoop(Action<string> log)
        {
            bool logged = false;
            while (!_disposed)
            {
                Process p = _mstsc;
                if (p == null)
                    return;

                try
                {
                    if (p.HasExited)
                        return;

                    p.Refresh();
                    IntPtr hwnd = p.MainWindowHandle;
                    if (hwnd != IntPtr.Zero)
                    {
                        ShowWindowAsync(hwnd, SW_HIDE);
                        if (!logged)
                        {
                            logged = true;
                            if (log != null) log("RDP UI hidden; session is transport-only");
                        }
                    }
                }
                catch
                {
                    return;
                }

                Thread.Sleep(250);
            }
        }

        private static void RunCmdKey(string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo("cmdkey.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                    throw new InvalidOperationException("cmdkey failed: " + o);
            }
        }

        private void CleanupCredential()
        {
            if (string.IsNullOrEmpty(_credentialTarget)) return;
            try { RunCmdKey("/delete:" + _credentialTarget); } catch { }
            _credentialTarget = null;
        }

        private void CleanupFile()
        {
            if (string.IsNullOrEmpty(_rdpFile)) return;
            try { File.Delete(_rdpFile); } catch { }
            _rdpFile = null;
        }

        public void Dispose()
        {
            _disposed = true;
            try
            {
                if (_mstsc != null && !_mstsc.HasExited)
                {
                    // Hidden mstsc has no useful UI to close; terminate the carrier process.
                    _mstsc.Kill();
                    _mstsc.WaitForExit(2000);
                }
            }
            catch { }

            CleanupCredential();
            CleanupFile();
            if (_mstsc != null) _mstsc.Dispose();
            _mstsc = null;
        }
    }
}
