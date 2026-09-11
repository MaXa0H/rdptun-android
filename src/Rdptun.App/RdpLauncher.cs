using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Rdptun.App
{
    internal sealed class RdpLauncher : IDisposable
    {
        private Process _mstsc;
        private string _credentialTarget;
        private string _rdpFile;

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
            _credentialTarget = "TERMSRV/" + serverIp;
            RunCmdKey("/generic:" + _credentialTarget + " /user:\"" + username + "\" /pass:\"" + password.Replace("\"", "\\\"") + "\"");

            _rdpFile = Path.Combine(Path.GetTempPath(), "rdptun-" + Guid.NewGuid().ToString("N") + ".rdp");
            string fullAddress = serverIp + (port == 3389 ? string.Empty : ":" + port);
            StringBuilder rdp = new StringBuilder();
            rdp.AppendLine("screen mode id:i:1");
            rdp.AppendLine("desktopwidth:i:900");
            rdp.AppendLine("desktopheight:i:650");
            rdp.AppendLine("session bpp:i:24");
            rdp.AppendLine("full address:s:" + fullAddress);
            rdp.AppendLine("username:s:" + username);
            rdp.AppendLine("prompt for credentials on client:i:0");
            rdp.AppendLine("authentication level:i:0");
            rdp.AppendLine("enablecredsspsupport:i:1");
            rdp.AppendLine("redirectclipboard:i:0");
            rdp.AppendLine("redirectprinters:i:0");
            rdp.AppendLine("redirectcomports:i:0");
            rdp.AppendLine("redirectsmartcards:i:0");
            rdp.AppendLine("drivestoredirect:s:");
            rdp.AppendLine("audiomode:i:2");
            rdp.AppendLine("autoreconnection enabled:i:1");
            File.WriteAllText(_rdpFile, rdp.ToString(), Encoding.Unicode);

            ProcessStartInfo psi = new ProcessStartInfo("mstsc.exe", "\"" + _rdpFile + "\"")
            {
                UseShellExecute = true
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
            if (log != null) log("mstsc.exe started for " + fullAddress);
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
            try
            {
                if (_mstsc != null && !_mstsc.HasExited)
                {
                    _mstsc.CloseMainWindow();
                    if (!_mstsc.WaitForExit(2000))
                        _mstsc.Kill();
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
