using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Windows;

namespace WandEnhancer.Fork.Installer
{
    internal static class InstallerProgram
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--cli") return EmbeddedCli.Run(args);
            bool check = args.Length == 1 && args[0] == "--check";
            bool install = (args.Length == 1 || (args.Length == 2 && args[1] == "--no-launch")) && args[0] == "--install";
            bool start = (args.Length == 1 || (args.Length == 2 && args[1] == "--no-launch")) && args[0] == "--start";
            if (check || install || start)
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
                using (var mutex = new Mutex(true, "Local\\GBTGAME.WandSetup", out bool first))
                {
                    if (!first)
                    {
                        Console.WriteLine("{\"schemaVersion\":1,\"success\":false,\"code\":6,\"message\":\"Installer is busy.\"}");
                        return 6;
                    }
                try
                {
                    var reports = new List<object>();
                    var saved = start ? new LauncherStateStore().Inspect() : null;
                    if (start && (!saved.Exists || !saved.CanRun)) throw new InvalidOperationException(saved.Error ?? "未找到 Wand，请先首次安装；启动命令不会自动重装。");
                    using (var engine = new InstallEngine((step, text) => reports.Add(new { step, message = text }), saved?.Config))
                    {
                        try
                        {
                            if (check) engine.Preflight();
                            else if (start) engine.StartExisting(args.Length == 1);
                            else engine.Install(args.Length == 1);
                        }
                        catch (Exception error)
                        {
                            engine.RecordFailure(error);
                            Console.WriteLine(InstallEngine.Json.Serialize(new { schemaVersion = 1, success = false, code = 1,
                                message = error.Message, logPath = engine.LogPath, backupPath = engine.BackupPath, reports }));
                            return 1;
                        }
                        Console.WriteLine(InstallEngine.Json.Serialize(new { schemaVersion = 1, success = true, code = 0,
                            message = check ? "Payloads verified; no installation changed." : start ? "Existing installation prepared; no reinstall performed." : "Installation completed.",
                            logPath = engine.LogPath, backupPath = engine.BackupPath, reports }));
                        return 0;
                    }
                }
                catch (Exception error)
                {
                    Console.WriteLine(InstallEngine.Json.Serialize(new { schemaVersion = 1, success = false, code = 1, message = error.Message }));
                    return 1;
                }
                }
            }
            if (args.Length != 0) return 2;
            using (var mutex = new Mutex(true, "Local\\GBTGAME.WandSetup", out bool first))
            {
                if (!first) { MessageBox.Show("安装工具已在运行。", "GBTGAME"); return 6; }
                new Application().Run(new InstallerWindow());
            }
            return 0;
        }
    }
}
