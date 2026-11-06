using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace WandEnhancer.Fork.Installer
{
    internal sealed class Package
    {
        public string version { get; set; }
        public string fileName { get; set; }
        public string sha256 { get; set; }
        public string arguments { get; set; }
    }

    internal sealed class InstallEngine : IDisposable, IInstallSteps
    {
        internal static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        private readonly Action<int, string> report;
        private readonly string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private readonly string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private readonly string scratch;
        private readonly Package package;
        private string enhancer;
        private string setup;
        private string backup;
        private string logFile;
        private StreamWriter log;
        public string BackupPath => backup;
        public string LogPath => logFile;

        public InstallEngine(Action<int, string> report)
        {
            this.report = report;
            scratch = Path.Combine(Path.GetTempPath(), "GBTGAME-Wand-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            package = LoadPackage();
        }

        internal static Package LoadPackage()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WandPackage"))
            using (var reader = new StreamReader(stream)) return Json.Deserialize<Package>(reader.ReadToEnd());
        }

        public void RecordFailure(Exception error) => log?.WriteLine("FAILED: " + error);

        private void Report(int step, string text)
        {
            log?.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text);
            report(step, text);
        }

        public void Preflight()
        {
            Report(0, "校验安装包与内置 CLI…");
            if (Path.GetFileName(package.fileName) != package.fileName || package.arguments != "--silent" ||
                !Version.TryParse(package.version, out Version ignored)) throw new InvalidDataException("安装清单无效。");
            enhancer = Path.Combine(scratch, "WandEnhancer.exe");
            Extract("WandEnhancerPayload", enhancer);
            setup = Path.Combine(scratch, package.fileName);
            if (Assembly.GetExecutingAssembly().GetManifestResourceInfo("WandInstallerPayload") != null)
                Extract("WandInstallerPayload", setup);
            else
            {
                string adjacent = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, package.fileName);
                if (!File.Exists(adjacent)) throw new FileNotFoundException("请把 " + package.fileName + " 放到安装工具旁边后重试。");
                File.Copy(adjacent, setup);
            }
            using (var input = File.OpenRead(setup))
            using (var sha = SHA256.Create())
            {
                string hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
                if (hash != package.sha256) throw new InvalidDataException("安装包 SHA256 不匹配，请使用随附的原版 " + package.fileName + "。");
            }
            if (FileVersionInfo.GetVersionInfo(setup).ProductVersion != package.version)
                throw new InvalidDataException("安装包版本与清单不匹配。");
            var version = Cli("version");
            var data = version["data"] as Dictionary<string, object>;
            if (data == null || Convert.ToInt32(data["cliVersion"]) != 1) throw new InvalidDataException("CLI 协议不兼容。");
            ValidateKnownPaths();
            CheckRegistrations();
        }

        public void Install(bool launchAfter = true)
        {
            string logDir = Path.Combine(local, "GBTGAME", "WandSetup", "logs");
            Directory.CreateDirectory(logDir);
            logFile = Path.Combine(logDir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".log");
            log = new StreamWriter(logFile) { AutoFlush = true };
            InstallWorkflow.Run(this, launchAfter, package.version, Report);
        }

        void IInstallSteps.Validate() => Preflight();

        void IInstallSteps.Backup()
        {
            foreach (string brand in new[] { "Wand", "WeMod" }) StopUnder(Path.Combine(local, brand));
            BackupSettings();
        }

        void IInstallSteps.Remove()
        {
            foreach (string brand in new[] { "Wand", "WeMod" }) RemoveOld(brand);
        }

        void IInstallSteps.Install()
        {
            Run(setup, package.arguments, 600000);
            WaitForInstall();
            string root = Path.Combine(local, "Wand");
            StopUnder(root);
        }

        void IInstallSteps.Patch()
        {
            string root = Path.Combine(local, "Wand");
            Cli("patch", "--path", root, "--expected-version", package.version,
                "--patches", "pro,disable-updates", "--strategy", "static");
        }

        bool IInstallSteps.Verify()
        {
            string root = Path.Combine(local, "Wand");
            var state = Cli("status", "--path", root, "--expected-version", package.version);
            var data = state["data"] as Dictionary<string, object>;
            return data != null && Convert.ToBoolean(data["patched"]);
        }

        void IInstallSteps.Launch() => Cli("launch", "--path", Path.Combine(local, "Wand"), "--expected-version", package.version, "--strategy", "static");

        private void ValidateKnownPaths()
        {
            foreach (string brand in new[] { "Wand", "WeMod" })
                foreach (string parent in new[] { local, roaming }) GuardTree(Path.Combine(parent, brand), parent, brand);
        }

        internal static void GuardTree(string path, string parent, string brand)
        {
            string expected = Path.GetFullPath(Path.Combine(parent, brand));
            if ((brand != "Wand" && brand != "WeMod") || !string.Equals(Path.GetFullPath(path).TrimEnd('\\'), expected.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒绝清理非 Wand 专属目录：" + path);
            // Check ancestors as well: a redirected AppData parent must not escape this allowlist.
            for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("目录包含链接，请手动处理后重试：" + directory.FullName);
            if (Directory.Exists(path)) CheckTree(path);
        }

        private static void CheckTree(string root)
        {
            foreach (string item in Directory.EnumerateFileSystemEntries(root))
            {
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("检测到目录链接，停止自动清理：" + item);
                if ((attributes & FileAttributes.Directory) != 0) CheckTree(item);
            }
        }

        private void CheckRegistrations()
        {
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                    using (var root = RegistryKey.OpenBaseKey(hive, view))
                    using (var uninstall = root.OpenSubKey(UninstallKey))
                    {
                        if (uninstall == null) continue;
                        foreach (string name in uninstall.GetSubKeyNames())
                            using (var entry = uninstall.OpenSubKey(name))
                            {
                                string display = entry?.GetValue("DisplayName") as string;
                                if (!new[] { "Wand", "WeMod" }.Contains(display)) continue;
                                string location = entry.GetValue("InstallLocation") as string;
                                if (hive == RegistryHive.LocalMachine || name != display || string.IsNullOrEmpty(location) ||
                                    !string.Equals(Path.GetFullPath(location).TrimEnd('\\'), Path.Combine(local, display), StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidOperationException("检测到自定义位置或系统级旧版：" + display + "。请先使用其卸载程序移除后重试。");
                            }
                    }
        }

        private void BackupSettings()
        {
            backup = Path.Combine(local, "GBTGAME", "WandSetup", "backups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            foreach (string brand in new[] { "Wand", "WeMod" })
            {
                string settings = Path.Combine(roaming, brand);
                if (Directory.Exists(settings)) CopyTree(settings, Path.Combine(backup, "Roaming", brand));
                foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                    using (var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                    {
                        foreach (string key in new[] { @"Software\" + brand, UninstallKey + "\\" + brand })
                        {
                            using (var entry = root.OpenSubKey(key))
                                if (entry != null) File.WriteAllText(Path.Combine(backup, brand + "-" + view + "-" + (key.StartsWith(UninstallKey) ? "uninstall" : "settings") + ".json"), Json.Serialize(Snapshot(entry)));
                        }
                        using (var run = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                            File.WriteAllText(Path.Combine(backup, brand + "-" + view + "-startup.json"), Json.Serialize(StartupValues(run, Path.Combine(local, brand))));
                        int protocol = 0;
                        foreach (string key in UserShellCleanup.AssociatedProtocolKeys(root, Path.Combine(local, brand)))
                            using (var entry = root.OpenSubKey(key))
                                File.WriteAllText(Path.Combine(backup, brand + "-" + view + "-protocol-" + protocol++ + ".json"), Json.Serialize(Snapshot(entry)));
                    }
            }
            File.WriteAllText(Path.Combine(backup, "说明.txt"), "此目录保存安装前的 Roaming 配置和相关 HKCU 注册表快照（含值类型）。日志位于相邻 logs 目录。旧安装程序文件不在备份内。注册表 JSON 仅供人工检查和恢复。\r\n");
            Report(1, "配置备份：" + backup);
        }

        internal static object Snapshot(RegistryKey key)
        {
            var values = key.GetValueNames().ToDictionary(name => name, name => (object)new {
                kind = key.GetValueKind(name).ToString(), value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            });
            var children = new Dictionary<string, object>();
            foreach (string name in key.GetSubKeyNames()) using (var child = key.OpenSubKey(name)) children.Add(name, Snapshot(child));
            return new { path = key.Name, values, children };
        }

        internal static Dictionary<string, object> StartupValues(RegistryKey run, string root)
        {
            var values = new Dictionary<string, object>();
            if (run == null) return values;
            foreach (string name in run.GetValueNames())
            {
                string value = run.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (!CommandIsUnder(value, root)) continue;
                values.Add(name, new { kind = run.GetValueKind(name).ToString(), value });
            }
            return values;
        }

        internal static bool CommandIsUnder(string command, string root)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            command = Environment.ExpandEnvironmentVariables(command.Trim());
            string executable = command.StartsWith("\"") ? command.Split('"').ElementAtOrDefault(1) : command.Split(' ')[0];
            try { return !string.IsNullOrEmpty(executable) && Path.IsPathRooted(executable) && IsUnder(executable, root); }
            catch (ArgumentException) { return false; }
        }

        private void RemoveOld(string brand)
        {
            SilentRemoval.Remove(local, roaming, brand, StopUnder,
                () => UserShellCleanup.RemoveShortcuts(Path.Combine(local, brand), Path.Combine(backup, "Shortcuts", brand)),
                () => CleanRegistry(brand));
        }

        private void CleanRegistry(string brand)
        {
            string root = Path.Combine(local, brand);
            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                using (var registry = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                {
                    foreach (string key in UserShellCleanup.AssociatedProtocolKeys(registry, root).ToArray()) registry.DeleteSubKeyTree(key, false);
                    registry.DeleteSubKeyTree(@"Software\" + brand, false);
                    registry.DeleteSubKeyTree(UninstallKey + "\\" + brand, false);
                    using (var run = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                    {
                        if (run == null) continue;
                        foreach (string name in StartupValues(run, root).Keys) run.DeleteValue(name, false);
                    }
                }
        }

        internal static bool IsUnder(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        private static void StopUnder(string root)
        {
            foreach (var process in Process.GetProcesses())
                using (process)
                {
                    string path;
                    try { path = process.MainModule?.FileName; }
                    catch { continue; }
                    if (path == null || !IsUnder(path, root)) continue;
                    try { process.Kill(); if (!process.WaitForExit(10000)) throw new IOException("等待进程退出超时。"); }
                    catch (InvalidOperationException) { }
                }
        }

        private void WaitForInstall()
        {
            string root = Path.Combine(local, "Wand");
            string target = Path.Combine(root, "app-" + package.version);
            var timer = Stopwatch.StartNew();
            long previousSize = -1;
            int stable = 0;
            while (timer.Elapsed < TimeSpan.FromMinutes(10))
            {
                string asar = Path.Combine(target, "resources", "app.asar");
                bool worker = HasInstallWorker(root);
                if (File.Exists(Path.Combine(target, "Wand.exe")) && File.Exists(asar) && Directory.Exists(Path.Combine(target, "resources", "app.asar.unpacked")) && !worker)
                {
                    long size = new FileInfo(asar).Length;
                    stable = size > 0 && size == previousSize ? stable + 1 : 0;
                    previousSize = size;
                    if (stable >= 3) { Cli("status", "--path", target, "--expected-version", package.version); return; }
                }
                else stable = 0;
                Thread.Sleep(1000);
            }
            throw new TimeoutException("等待 Wand 安装完成超时。请查看 SquirrelSetup.log 和安装日志后重试。");
        }

        private bool HasInstallWorker(string root)
        {
            foreach (string name in new[] { "Update", Path.GetFileNameWithoutExtension(package.fileName) })
                foreach (var process in Process.GetProcessesByName(name))
                    using (process)
                    {
                        try
                        {
                            string path = process.MainModule?.FileName;
                            if (path != null && (IsUnder(path, root) || IsUnder(path, scratch) || IsUnder(path, Path.Combine(local, "SquirrelTemp")))) return true;
                        }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception) { throw new IOException("无法确认安装进程已退出，请关闭其他安装程序后重试。"); }
                    }
            return false;
        }

        private Dictionary<string, object> Cli(params string[] args)
        {
            string output = Run(enhancer, Arguments.Join(new[] { "--cli" }.Concat(args)), 600000, true);
            var result = Json.Deserialize<Dictionary<string, object>>(output);
            if (result == null || !result.ContainsKey("schemaVersion") || Convert.ToInt32(result["schemaVersion"]) != 1 ||
                !Convert.ToBoolean(result["success"])) throw new InvalidOperationException("CLI 返回了无效或失败的结果：" + output);
            return result;
        }

        private string Run(string file, string args, int timeout, bool capture = false)
        {
            using (var process = new Process { StartInfo = new ProcessStartInfo(file, args) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = capture,
                RedirectStandardError = capture, WorkingDirectory = Path.GetDirectoryName(file)
            } })
            {
                if (capture)
                {
                    process.StartInfo.StandardOutputEncoding = new System.Text.UTF8Encoding(false);
                    process.StartInfo.StandardErrorEncoding = new System.Text.UTF8Encoding(false);
                }
                process.Start();
                Task<string> output = capture ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
                Task<string> error = capture ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
                if (!process.WaitForExit(timeout))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException(Path.GetFileName(file) + " 执行超时；已停止后续步骤。");
                }
                if (!Task.WaitAll(new Task[] { output, error }, 10000)) throw new TimeoutException("读取子进程输出超时；已停止后续步骤。");
                log?.WriteLine(Path.GetFileName(file) + " " + args + "\r\n" + output.Result + error.Result);
                if (process.ExitCode != 0) throw new InvalidOperationException(Path.GetFileName(file) + " 失败（退出码 " + process.ExitCode + "）：" + output.Result + error.Result);
                return output.Result;
            }
        }

        private static void Extract(string name, string path)
        {
            using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var output = File.Create(path)) source.CopyTo(output);
        }

        private static void CopyTree(string source, string destination)
        {
            CheckTree(source);
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            foreach (string directory in Directory.GetDirectories(source)) CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        public void Dispose()
        {
            log?.Dispose();
            try
            {
                string expected = Path.Combine(Path.GetTempPath(), Path.GetFileName(scratch));
                if (Path.GetFullPath(scratch) == Path.GetFullPath(expected) && Path.GetFileName(scratch).StartsWith("GBTGAME-Wand-"))
                { CheckTree(scratch); Directory.Delete(scratch, true); }
            }
            catch { }
        }
    }
}
