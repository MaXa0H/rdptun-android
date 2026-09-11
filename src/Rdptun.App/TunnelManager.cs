using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace Rdptun.App
{
    internal sealed class TunnelManager : IDisposable
    {
        private const string AdapterName = "rdptun";
        private const string TunnelGateway = "10.77.0.1";
        private const string FirewallRule = "RDP Tunnel - Block IPv6";

        private readonly Action<string> _log;
        private WintunSession _wintun;
        private string _serverIp;
        private string _physicalGateway;
        private int _physicalIfIndex;
        private int _tunIfIndex;
        private bool _routesInstalled;

        public TunnelManager(Action<string> log)
        {
            _log = log;
        }

        public void Start(string serverIp, Action<byte[]> outboundPacket)
        {
            if (string.IsNullOrWhiteSpace(serverIp))
                throw new ArgumentNullException("serverIp");

            _serverIp = serverIp;
            GetDefaultRoute(out _physicalGateway, out _physicalIfIndex);
            _log("Physical route: gateway=" + _physicalGateway + " ifIndex=" + _physicalIfIndex);

            _wintun = new WintunSession(outboundPacket, _log);
            _wintun.Start();

            try
            {
                RunChecked("netsh.exe", "interface ipv4 set address name=\"" + AdapterName + "\" source=static address=10.77.0.2 mask=255.255.255.0 gateway=none store=active");
                RunChecked("netsh.exe", "interface ipv4 set subinterface \"" + AdapterName + "\" mtu=1200 store=active");
                RunChecked("netsh.exe", "interface ipv4 set interface \"" + AdapterName + "\" metric=5");
                RunChecked("netsh.exe", "interface ipv4 set dnsservers name=\"" + AdapterName + "\" source=static address=1.1.1.1 validate=no");

                _tunIfIndex = WaitForInterfaceIndex(AdapterName);
                _log("Wintun ifIndex=" + _tunIfIndex);

                // Keep the outer RDP TCP connection on the physical interface.
                // /32 is more specific than the /1 tunnel routes below.
                RunIgnore("route.exe", "DELETE " + _serverIp + " MASK 255.255.255.255 " + _physicalGateway);
                RunChecked("route.exe", "ADD " + _serverIp + " MASK 255.255.255.255 " + _physicalGateway + " IF " + _physicalIfIndex + " METRIC 1");

                // Do not add another competing 0/0 default route. Windows can keep using
                // the physical default depending on effective interface metrics. Two /1 routes
                // cover all IPv4 addresses and always beat any ordinary /0 route by prefix length.
                DeleteSplitRoutes();
                RunChecked("route.exe", "ADD 0.0.0.0 MASK 128.0.0.0 " + TunnelGateway + " IF " + _tunIfIndex + " METRIC 1");
                RunChecked("route.exe", "ADD 128.0.0.0 MASK 128.0.0.0 " + TunnelGateway + " IF " + _tunIfIndex + " METRIC 1");

                RunIgnore("netsh.exe", "advfirewall firewall delete rule name=\"" + FirewallRule + "\"");
                RunChecked("netsh.exe", "advfirewall firewall add rule name=\"" + FirewallRule + "\" dir=out action=block protocol=any remoteip=::/0");
                RunIgnore("ipconfig.exe", "/flushdns");

                _routesInstalled = true;
                VerifyRouteSelection();
                _log("IPv4 split-default routes are active through RDP DVC");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void InjectPacket(byte[] packet)
        {
            WintunSession w = _wintun;
            if (w != null)
                w.Inject(packet);
        }

        public void Dispose()
        {
            if (_routesInstalled || _serverIp != null)
            {
                DeleteSplitRoutes();
                // Cleanup route from older builds too.
                RunIgnore("route.exe", "DELETE 0.0.0.0 MASK 0.0.0.0 " + TunnelGateway);
                if (!string.IsNullOrEmpty(_serverIp) && !string.IsNullOrEmpty(_physicalGateway))
                    RunIgnore("route.exe", "DELETE " + _serverIp + " MASK 255.255.255.255 " + _physicalGateway);
                RunIgnore("netsh.exe", "advfirewall firewall delete rule name=\"" + FirewallRule + "\"");
                RunIgnore("ipconfig.exe", "/flushdns");
            }
            _routesInstalled = false;
            if (_wintun != null)
            {
                _wintun.Dispose();
                _wintun = null;
            }
            _log("Tunnel routes cleaned up");
        }

        public static void CleanupStale(Action<string> log)
        {
            try { RunProcess("route.exe", "DELETE 0.0.0.0 MASK 128.0.0.0 " + TunnelGateway, false); } catch { }
            try { RunProcess("route.exe", "DELETE 128.0.0.0 MASK 128.0.0.0 " + TunnelGateway, false); } catch { }
            try { RunProcess("route.exe", "DELETE 0.0.0.0 MASK 0.0.0.0 " + TunnelGateway, false); } catch { }
            try { RunProcess("netsh.exe", "advfirewall firewall delete rule name=\"" + FirewallRule + "\"", false); } catch { }
            if (log != null) log("Stale tunnel route/firewall cleanup complete");
        }

        private void DeleteSplitRoutes()
        {
            RunIgnore("route.exe", "DELETE 0.0.0.0 MASK 128.0.0.0 " + TunnelGateway);
            RunIgnore("route.exe", "DELETE 128.0.0.0 MASK 128.0.0.0 " + TunnelGateway);
        }

        private void VerifyRouteSelection()
        {
            string script = "$r=Find-NetRoute -RemoteIPAddress '1.1.1.1' -ErrorAction Stop | Select-Object -First 1; Write-Output ($r.InterfaceIndex.ToString()+'|'+$r.NextHop)";
            string output = RunProcess("powershell.exe", "-NoProfile -NonInteractive -Command \"" + script.Replace("\"", "\\\"") + "\"", true).Trim();
            _log("Route check 1.1.1.1 => " + output + " (expected ifIndex=" + _tunIfIndex + ")");

            string[] parts = output.Split('|');
            int selectedIf;
            if (parts.Length < 1 || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out selectedIf) || selectedIf != _tunIfIndex)
                throw new InvalidOperationException("Windows is not selecting the Wintun route for IPv4 traffic: " + output);
        }

        private void GetDefaultRoute(out string gateway, out int ifIndex)
        {
            string script = "$r=Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' | Where-Object {$_.NextHop -ne '0.0.0.0'} | Sort-Object RouteMetric | Select-Object -First 1; if($null -eq $r){exit 2}; Write-Output ($r.NextHop+'|'+$r.ifIndex)";
            string output = RunProcess("powershell.exe", "-NoProfile -NonInteractive -Command \"" + script.Replace("\"", "\\\"") + "\"", true).Trim();
            string[] parts = output.Split('|');
            if (parts.Length != 2 || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ifIndex))
                throw new InvalidOperationException("Could not determine physical default route: " + output);
            gateway = parts[0].Trim();
        }

        private int WaitForInterfaceIndex(string name)
        {
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    string script = "(Get-NetAdapter -Name '" + name.Replace("'", "''") + "' -ErrorAction Stop).ifIndex";
                    string output = RunProcess("powershell.exe", "-NoProfile -NonInteractive -Command \"" + script + "\"", true).Trim();
                    int index;
                    if (int.TryParse(output, out index))
                        return index;
                }
                catch { }
                Thread.Sleep(200);
            }
            throw new InvalidOperationException("Wintun interface index not found");
        }

        private void RunChecked(string file, string args)
        {
            _log(file + " " + args);
            RunProcess(file, args, true);
        }

        private void RunIgnore(string file, string args)
        {
            try { RunProcess(file, args, false); } catch { }
        }

        private static string RunProcess(string file, string args, bool throwOnError)
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (throwOnError && p.ExitCode != 0)
                    throw new InvalidOperationException(file + " failed (" + p.ExitCode + "): " + stderr + stdout);
                return stdout;
            }
        }
    }
}
