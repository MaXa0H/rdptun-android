using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rdptun.Common;

namespace Rdptun.App
{
    internal sealed class RdpLauncher : IDisposable
    {
        private Process _mstsc;
        private static Process _pluginHost;
        private static readonly object PluginHostLock = new object();
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

            lock (PluginHostLock)
            {
                if (_pluginHost != null)
                {
                    try
                    {
                        if (_pluginHost.HasExited)
                        {
                            _pluginHost.Dispose();
                            _pluginHost = null;
                        }
                    }
                    catch
                    {
                        _pluginHost = null;
                    }
                }

                if (_pluginHost == null)
                {
                    try { File.Delete(PipeProtocol.TracePath); } catch { }

                    ProcessStartInfo hostPsi = new ProcessStartInfo(plugin)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = baseDir
                    };
                    _pluginHost = Process.Start(hostPsi);
                    if (_pluginHost == null)
                        throw new InvalidOperationException("Could not pre-start DVC COM LocalServer");
                    Thread.Sleep(500);
                    if (_pluginHost.HasExited)
                    {
                        int code = _pluginHost.ExitCode;
                        _pluginHost.Dispose();
                        _pluginHost = null;
                        throw new InvalidOperationException("DVC COM LocalServer exited early with code " + code);
                    }
                    if (log != null)
                    {
                        log("DVC COM LocalServer pre-started, pid=" + _pluginHost.Id);
                        log("IPC session: " + PipeProtocol.PipeName);
                    }
                }
            }
        }

        public void Launch(string serverIp, int port, string username, string password, Action<string> log)
        {
            _disposed = false;
            string pluginTrace = PipeProtocol.TracePath;

            _credentialTarget = "TERMSRV/" + serverIp;
            RunCmdKey("/generic:" + _credentialTarget + " /user:\"" + username + "\" /pass:\"" + password.Replace("\"", "\\\"") + "\"");

            _rdpFile = Path.Combine(Path.GetTempPath(), "rdptun-" + Guid.NewGuid().ToString("N") + ".rdp");
            string fullAddress = serverIp + (port == 3389 ? string.Empty : ":" + port);

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
                StopPluginHost(null);
                Action handler = Exited;
                if (handler != null) handler();
            };

            Task.Run(() => HideWindowLoop(log));
            Task.Run(() => PluginActivationDiagnostic(pluginTrace, log));

            if (log != null)
                log("Headless RDP transport started for " + fullAddress + "; waiting for rdptun DVC");
        }

        private void PluginActivationDiagnostic(string tracePath, Action<string> log)
        {
            string lastText = string.Empty;
            for (int i = 0; i < 32 && !_disposed; i++)
            {
                Thread.Sleep(250);
                try
                {
                    if (!File.Exists(tracePath))
                        continue;

                    string text = File.ReadAllText(tracePath);
                    lastText = text;
                    bool factoryCalled = text.IndexOf("CLASSFACTORY CreateInstance", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool initializeCalled = text.IndexOf("IWTSPlugin.Initialize called", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!factoryCalled && !initializeCalled)
                        continue;

                    if (log != null)
                    {
                        log("mstsc reached DVC COM activation path");
                        string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        int start = Math.Max(0, lines.Length - 12);
                        for (int n = start; n < lines.Length; n++)
                            log("PLUGIN TRACE: " + lines[n]);
                    }
                    return;
                }
                catch { }
            }

            if (!_disposed && log != null)
            {
                log("DIAGNOSTIC: mstsc did not reach DVC COM activation within 8 seconds");
                if (!string.IsNullOrWhiteSpace(lastText))
                {
                    string[] lines = lastText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    int start = Math.Max(0, lines.Length - 8);
                    for (int n = start; n < lines.Length; n++)
                        log("PLUGIN TRACE: " + lines[n]);
                }
            }
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

        private static void StopPluginHost(Action<string> log)
        {
            lock (PluginHostLock)
            {
                Process host = _pluginHost;
                _pluginHost = null;
                if (host == null)
                    return;
                try
                {
                    int pid = host.Id;
                    if (!host.HasExited)
                    {
                        host.Kill();
                        host.WaitForExit(2000);
                    }
                    if (log != null) log("DVC COM LocalServer stopped, pid=" + pid);
                }
                catch (Exception ex)
                {
                    if (log != null) log("DVC COM LocalServer cleanup: " + ex.Message);
                }
                finally
                {
                    try { host.Dispose(); } catch { }
                }
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
                    _mstsc.Kill();
                    _mstsc.WaitForExit(2000);
                }
            }
            catch { }

            StopPluginHost(null);
            CleanupCredential();
            CleanupFile();
            if (_mstsc != null) _mstsc.Dispose();
            _mstsc = null;
        }
    }
}
