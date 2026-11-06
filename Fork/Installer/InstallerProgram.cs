using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WandEnhancer.Fork.Installer
{
    internal static class InstallerProgram
    {
        [DllImport("kernel32.dll")] private static extern bool SetProcessDPIAware();

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--check")
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
                try
                {
                    using (var engine = new InstallEngine((step, text) => { }))
                    {
                        engine.Preflight();
                        Console.WriteLine("{\"success\":true,\"message\":\"Payloads verified; no installation changed.\"}");
                        return 0;
                    }
                }
                catch (Exception error)
                {
                    Console.WriteLine(InstallEngine.Json.Serialize(new { success = false, message = error.Message }));
                    return 1;
                }
            }
            if (args.Length != 0) return 2;
            using (var mutex = new Mutex(true, "Local\\GBTGAME.WandSetup", out bool first))
            {
                if (!first) { MessageBox.Show("安装工具已在运行。", "GBTGAME"); return 6; }
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new InstallerForm());
            }
            return 0;
        }
    }
}
