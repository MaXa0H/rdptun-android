using System;
using System.Diagnostics;
using System.Reflection;

namespace Rdptun.Plugin
{
    internal static class Program
    {
        [MTAThread]
        private static int Main(string[] args)
        {
            try
            {
                foreach (string arg in args)
                {
                    string a = arg.ToLowerInvariant();
                    if (a == "/register" || a == "-register")
                    {
                        RegistryHelper.RegisterLocalServer(RdpPlugin.PluginClsid, GetExePath());
                        return 0;
                    }
                    if (a == "/unregister" || a == "-unregister")
                    {
                        RegistryHelper.UnregisterLocalServer(RdpPlugin.PluginClsid);
                        return 0;
                    }
                }

                using (var server = new COMRegistration.LocalServer())
                {
                    server.RegisterClass<RdpPlugin>(RdpPlugin.PluginClsid);
                    server.Run();
                }
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        private static string GetExePath()
        {
            Process p = Process.GetCurrentProcess();
            if (p.MainModule != null && !string.IsNullOrEmpty(p.MainModule.FileName))
                return p.MainModule.FileName;
            return Assembly.GetExecutingAssembly().Location;
        }
    }
}
