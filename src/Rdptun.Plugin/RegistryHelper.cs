using System;
using Microsoft.Win32;

namespace Rdptun.Plugin
{
    internal static class RegistryHelper
    {
        private const string AddInName = "Rdptun";

        public static void RegisterLocalServer(Guid clsid, string exePath)
        {
            string addInsPath = @"Software\Microsoft\Terminal Server Client\Default\AddIns\" + AddInName;
            string localServerPath = @"Software\Classes\CLSID\" + clsid.ToString("B") + @"\LocalServer32";

            using (RegistryKey addIn = Registry.CurrentUser.CreateSubKey(addInsPath))
            {
                if (addIn == null) throw new InvalidOperationException("Cannot create RDP AddIns registry key");
                addIn.SetValue("Name", clsid.ToString("B"), RegistryValueKind.String);
            }
            using (RegistryKey server = Registry.CurrentUser.CreateSubKey(localServerPath))
            {
                if (server == null) throw new InvalidOperationException("Cannot create COM LocalServer32 registry key");
                server.SetValue(null, "\"" + exePath + "\"", RegistryValueKind.String);
            }
        }

        public static void UnregisterLocalServer(Guid clsid)
        {
            string addInsPath = @"Software\Microsoft\Terminal Server Client\Default\AddIns\" + AddInName;
            string clsidPath = @"Software\Classes\CLSID\" + clsid.ToString("B");
            try { Registry.CurrentUser.DeleteSubKeyTree(addInsPath, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(clsidPath, false); } catch { }
        }
    }
}
