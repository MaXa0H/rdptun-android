using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace Rdptun.App
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            int pid = Process.GetCurrentProcess().Id;
            string token = Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable("RDPTUN_PIPE", "rdptun-control-" + pid + "-" + token);
            Environment.SetEnvironmentVariable(
                "RDPTUN_TRACE",
                Path.Combine(Path.GetTempPath(), "rdptun-plugin-" + pid + "-" + token + ".log"));

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
