using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using WandEnhancer.Models;

namespace WandEnhancer.Fork.Installer
{
    internal sealed class LauncherSnapshot
    {
        public bool Exists { get; set; }
        public string Error { get; set; }
        public string Message { get; set; }
        public string Version { get; set; }
        public PatchConfig Config { get; set; }
        public bool CanRun => string.IsNullOrEmpty(Error);
    }

    internal sealed class StoredPatchSelection
    {
        public List<EPatchType> PatchTypes { get; set; }
        public EPatchStrategy Strategy { get; set; }
        public bool AutoApplyAfterUpdate { get; set; }
        public List<string> CustomScriptPaths { get; set; }
        public PatchConfig ToConfig() => new PatchConfig { Strategy = Strategy, AutoApplyAfterUpdate = AutoApplyAfterUpdate,
            PatchTypes = PatchTypes == null ? null : new HashSet<EPatchType>(PatchTypes), CustomScriptPaths = CustomScriptPaths ?? new List<string>() };
        public static StoredPatchSelection From(PatchConfig config) => new StoredPatchSelection { Strategy = config.Strategy, AutoApplyAfterUpdate = config.AutoApplyAfterUpdate,
            PatchTypes = config.PatchTypes.ToList(), CustomScriptPaths = (config.CustomScriptPaths ?? new List<string>()).ToList() };
    }

    internal sealed class InstallationRecord
    {
        public int SchemaVersion { get; set; }
        public string InstallRoot { get; set; }
        public string Version { get; set; }
        public string VerifiedUtc { get; set; }
        public StoredPatchSelection Config { get; set; }
    }

    internal sealed class LauncherStateStore
    {
        public string InstallRoot { get; }
        public string StatePath { get; }
        private readonly string expected;
        private readonly Func<string, string> versionReader;
        internal LauncherStateStore() : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wand"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GBTGAME", "WandSetup", "installation.json"),
            InstallEngine.LoadPackage().version, file => FileVersionInfo.GetVersionInfo(file).ProductVersion) { }
        internal LauncherStateStore(string root, string statePath, string expected, Func<string, string> versionReader)
        {
            InstallRoot = Path.GetFullPath(root); StatePath = Path.GetFullPath(statePath);
            this.expected = expected; this.versionReader = versionReader;
        }
        private static void ValidateSelection(PatchConfig config)
        {
            var shape = PatchConfigs.Copy(config); shape.CustomScriptPaths.Clear(); PatchConfigs.Validate(shape);
            foreach (string path in config.CustomScriptPaths ?? new System.Collections.Generic.List<string>())
                if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetExtension(Path.GetFullPath(path)), ".js", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("已保存的脚本路径无效。");
        }
        private PatchConfig ReadSelection(out string warning)
        {
            warning = null;
            try
            {
                if (File.Exists(StatePath))
                {
                    if (new FileInfo(StatePath).Length > 1024 * 1024) throw new InvalidDataException("安装记录过大。");
                    var saved = InstallEngine.Json.Deserialize<InstallationRecord>(File.ReadAllText(StatePath));
                    if (saved == null || saved.SchemaVersion != 1 || !SameVersion(saved.Version, expected) ||
                        !string.Equals(Path.GetFullPath(saved.InstallRoot), InstallRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("安装记录无效。");
                    var config = saved.Config?.ToConfig();
                    ValidateSelection(config);
                    return config;
                }
                string legacy = Path.Combine(InstallRoot, "enhancer.json");
                if (File.Exists(legacy))
                {
                    var config = InstallEngine.Json.Deserialize<StoredPatchSelection>(File.ReadAllText(legacy))?.ToConfig();
                    ValidateSelection(config); return config;
                }
            }
            catch (Exception error) { warning = "无法读取已保存的功能设置，请检查本次选项。" + error.Message; }
            return null;
        }
        internal static bool SameVersion(string a, string b)
        {
            Version first, second;
            return Version.TryParse(a, out first) && Version.TryParse(b, out second) && first.Major == second.Major && first.Minor == second.Minor && Math.Max(0, first.Build) == Math.Max(0, second.Build) && Math.Max(0, first.Revision) == Math.Max(0, second.Revision);
        }
        public LauncherSnapshot Inspect()
        {
            try
            {
                BackupStore.Guard(InstallRoot);
                var candidates = Directory.Exists(InstallRoot) ? Directory.GetDirectories(InstallRoot, "app-*") : new string[0];
                var latest = candidates.Select(path => new { Path = path, Text = Path.GetFileName(path).Substring(4) })
                    .Select(item => new { item.Path, Version = Version.TryParse(item.Text, out Version parsed) ? parsed : null })
                    .Where(item => item.Version != null).OrderByDescending(item => item.Version).FirstOrDefault();
                string warning; var config = ReadSelection(out warning);
                if (latest == null) return new LauncherSnapshot { Config = config, Message = "未找到 Wand，首次使用请点击安装。" + warning };
                string executable = Path.Combine(latest.Path, "Wand.exe");
                if (!File.Exists(executable) || !File.Exists(Path.Combine(latest.Path, "resources", "app.asar")) || !Directory.Exists(Path.Combine(latest.Path, "resources", "app.asar.unpacked")))
                    return new LauncherSnapshot { Exists = true, Config = config, Error = "现有 Wand 安装不完整，已停止启动；不会自动卸载或重装。" };
                string actual = versionReader(executable);
                if (!SameVersion(latest.Version.ToString(), expected) || !SameVersion(actual, expected))
                    return new LauncherSnapshot { Exists = true, Config = config, Version = actual, Error = "检测到 Wand " + actual + "，当前启动器仅支持 " + expected + "，不会自动重装或降级。" };
                return new LauncherSnapshot { Exists = true, Config = config, Version = actual,
                    Message = "已检测到 Wand " + actual + "，启动前将应用并验证所选功能，无需重新安装。" + (config == null ? "首次使用新启动器请确认功能设置，以后会自动保存。" : "") + warning + ((config?.CustomScriptPaths?.Any(path => !File.Exists(path)) ?? false) ? " 部分脚本不存在，请先移除或重新选择。" : "") };
            }
            catch (Exception error) { return new LauncherSnapshot { Exists = true, Error = "无法检查现有安装：" + error.Message + "；不会自动重装。" }; }
        }
        public void Save(PatchConfig config)
        {
            PatchConfigs.Validate(config);
            var snapshot = Inspect();
            if (!snapshot.Exists || !snapshot.CanRun) throw new InvalidOperationException(snapshot.Error ?? "没有可记录的有效安装。");
            string directory = Path.GetDirectoryName(StatePath); BackupStore.Guard(directory, false); Directory.CreateDirectory(directory);
            if (File.Exists(StatePath) && (File.GetAttributes(StatePath) & FileAttributes.ReparsePoint) != 0) throw new IOException("安装记录不能是文件链接。");
            string temporary = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, InstallEngine.Json.Serialize(new InstallationRecord { SchemaVersion = 1, InstallRoot = InstallRoot, Version = expected, VerifiedUtc = DateTime.UtcNow.ToString("O"), Config = StoredPatchSelection.From(config) }));
                if (File.Exists(StatePath)) File.Replace(temporary, StatePath, null); else File.Move(temporary, StatePath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
