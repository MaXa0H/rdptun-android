using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Rdptun.Common;

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
            string plugin = Path.Combine(baseDir, "Rdptun.Plugin.dll");
            if (!File.Exists(plugin))
                throw new FileNotFoundException("Rdptun.Plugin.dll is missing", plugin);

            string clsidText = Environment.GetEnvironmentVariable("RDPTUN_CLSID");
            Guid clsid;
            if (string.IsNullOrWhiteSpace(clsidText) || !Guid.TryParse(clsidText, out clsid))
                throw new InvalidOperationException("RDPTUN_CLSID is missing or invalid");
            clsidText = clsid.ToString("B");

            string addInsPath = @"Software\Microsoft\Terminal Server Client\Default\AddIns\Rdptun";
            string clsidPath = @"Software\Classes\CLSID\" + clsidText;
            string inprocPath = clsidPath + @"\InprocServer32";
            string localServerPath = clsidPath + @"\LocalServer32";

            try { Registry.CurrentUser.DeleteSubKeyTree(localServerPath, false); } catch { }

            using (RegistryKey addIn = Registry.CurrentUser.CreateSubKey(addInsPath))
            {
                if (addIn == null) throw new InvalidOperationException("Cannot create RDP AddIns registry key");
                addIn.SetValue("Name", clsidText, RegistryValueKind.String);
            }
            using (RegistryKey clsidKey = Registry.CurrentUser.CreateSubKey(clsidPath))
            {
                if (clsidKey == null) throw new InvalidOperationException("Cannot create COM CLSID registry key");
                clsidKey.SetValue(null, "RDP Tunnel DVC Plugin", RegistryValueKind.String);
            }
            using (RegistryKey inproc = Registry.CurrentUser.CreateSubKey(inprocPath))
            {
                if (inproc == null) throw new InvalidOperationException("Cannot create COM InprocServer32 registry key");
                inproc.SetValue(null, plugin, RegistryValueKind.String);
                inproc.SetValue("ThreadingModel", "Both", RegistryValueKind.String);
            }

            try { File.Delete(PipeProtocol.TracePath); } catch { }

            if (log != null)
            {
                log("DVC native in-process COM plugin registered for current user");
                log("Plugin DLL: " + plugin);
                log("IPC session: " + PipeProtocol.PipeName);
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
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Minimized
            };
            CopyEnvironment(psi, "RDPTUN_PIPE");
            CopyEnvironment(psi, "RDPTUN_TRACE");
            CopyEnvironment(psi, "RDPTUN_CLSID");

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

            Task.Run(() => HideWindowLoop(log));
            Task.Run(() => PluginActivationDiagnostic(pluginTrace, log));

            if (log != null)
                log("Headless RDP transport started for " + fullAddress + "; waiting for rdptun DVC");
        }

        private static void CopyEnvironment(ProcessStartInfo psi, string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
                psi.EnvironmentVariables[name] = value;
        }

        private void PluginActivationDiagnostic(string tracePath, Action<string> log)
        {
            string lastText = string.Empty;
            for (int i = 0; i < 40 && !_disposed; i++)
            {
                Thread.Sleep(250);
                try
                {
                    if (!File.Exists(tracePath))
                        continue;

                    string text = File.ReadAllText(tracePath);
                    lastText = text;
                    bool factoryCalled = text.IndexOf("CLASSFACTORY CreateInstance", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool initializeCalled = text.IndexOf("IWTSPlugin.Initialize ENTER", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!factoryCalled && !initializeCalled)
                        continue;

                    if (log != null)
                    {
                        log("mstsc reached DVC in-process COM activation path");
                        string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        int start = Math.Max(0, lines.Length - 16);
                        for (int n = start; n < lines.Length; n++)
                            log("PLUGIN TRACE: " + lines[n]);
                    }
                    return;
                }
                catch { }
            }

            if (!_disposed && log != null)
            {
                log("DIAGNOSTIC: mstsc did not reach DVC in-process COM activation within 10 seconds");
                if (!string.IsNullOrWhiteSpace(lastText))
                {
                    string[] lines = lastText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    int start = Math.Max(0, lines.Length - 12);
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
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                    throw new InvalidOperationException("cmdkey failed: " + output);
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

            CleanupCredential();
            CleanupFile();
            if (_mstsc != null) _mstsc.Dispose();
            _mstsc = null;
        }
    }
}
