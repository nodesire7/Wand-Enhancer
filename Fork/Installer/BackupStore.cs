using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WandEnhancer.Fork.Installer
{
    internal sealed class BackupEntry
    {
        public string Path { get; set; }
        public string Label { get; set; }
        public string Summary { get; set; }
        public bool CanRestore { get; set; }
    }

    internal interface IBackupStore
    {
        string Root { get; }
        IList<BackupEntry> List();
        string Restore(string path);
        void Delete(string path);
    }

    internal interface ISettingsRegistry
    {
        string Capture(string brand, RegistryView view);
        void Validate(string json, string brand);
        void Restore(string json, string brand, RegistryView view);
    }

    internal sealed class SettingsRegistry : ISettingsRegistry
    {
        private readonly string parent;
        internal SettingsRegistry(string parent = "Software") { this.parent = parent; }
        internal sealed class Value { public string kind { get; set; } public object value { get; set; } }
        internal sealed class Node
        {
            public string path { get; set; }
            public Dictionary<string, Value> values { get; set; }
            public Dictionary<string, Node> children { get; set; }
        }
        public string Capture(string brand, RegistryView view)
        {
            using (var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
            using (var key = root.OpenSubKey(parent + "\\" + brand))
                return key == null ? "null" : InstallEngine.Json.Serialize(InstallEngine.Snapshot(key));
        }
        public void Validate(string json, string brand)
        {
            if (brand != "Wand" && brand != "WeMod") throw new InvalidDataException("无效的配置名称。");
            var node = InstallEngine.Json.Deserialize<Node>(json);
            if (node != null) ValidateNode(node, "HKEY_CURRENT_USER\\" + parent + "\\" + brand);
        }
        private static object ConvertValue(Value value)
        {
            if (value == null || value.value == null) throw new InvalidDataException("注册表值缺失。");
            switch (value.kind)
            {
                case "String": case "ExpandString":
                    if (!(value.value is string)) throw new InvalidDataException("注册表字符串无效。"); return value.value;
                case "DWord": return Convert.ToInt32(value.value);
                case "QWord": return Convert.ToInt64(value.value);
                case "Binary": case "None": return InstallEngine.Json.ConvertToType<byte[]>(value.value);
                case "MultiString": return InstallEngine.Json.ConvertToType<string[]>(value.value);
                default: throw new InvalidDataException("不支持的注册表值类型。");
            }
        }
        private static void ValidateNode(Node node, string expected)
        {
            if (!string.Equals(node.path, expected, StringComparison.OrdinalIgnoreCase) || node.values == null || node.children == null)
                throw new InvalidDataException("注册表快照不属于所选应用的用户配置。");
            foreach (var value in node.values.Values) ConvertValue(value);
            foreach (var child in node.children)
            {
                if (string.IsNullOrEmpty(child.Key) || child.Key.IndexOfAny(new[] { '\\', '/', '\0' }) >= 0) throw new InvalidDataException("无效的注册表子键。");
                ValidateNode(child.Value, expected + "\\" + child.Key);
            }
        }
        public void Restore(string json, string brand, RegistryView view)
        {
            Validate(json, brand);
            var node = InstallEngine.Json.Deserialize<Node>(json);
            using (var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
            {
                string path = parent + "\\" + brand;
                root.DeleteSubKeyTree(path, false);
                if (node != null) using (var key = root.CreateSubKey(path)) Write(key, node);
            }
        }
        private static void Write(RegistryKey key, Node node)
        {
            foreach (var value in node.values) key.SetValue(value.Key, ConvertValue(value.Value), (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), value.Value.kind));
            foreach (var child in node.children) using (var nested = key.CreateSubKey(child.Key)) Write(nested, child.Value);
        }
    }

    internal sealed class BackupStore : IBackupStore
    {
        private static readonly string[] Brands = { "Wand", "WeMod" };
        private static readonly RegistryView[] Views = { RegistryView.Registry32, RegistryView.Registry64 };
        private readonly string roaming, local;
        private readonly ISettingsRegistry registry;
        private readonly Action<string> stop;
        public string Root { get; }
        public BackupStore() : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GBTGAME", "WandSetup", "backups"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            new SettingsRegistry(), InstallEngine.StopUnder) { }
        internal BackupStore(string root, string roaming, string local, ISettingsRegistry registry, Action<string> stop)
        {
            Root = Path.GetFullPath(root); this.roaming = Path.GetFullPath(roaming); this.local = Path.GetFullPath(local);
            this.registry = registry; this.stop = stop;
        }
        internal static void Guard(string path, bool recursive = true)
        {
            for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("路径包含链接，已停止操作：" + current.FullName);
            if (!recursive || !Directory.Exists(path)) return;
            foreach (string item in Directory.EnumerateFileSystemEntries(path))
            {
                var attr = File.GetAttributes(item);
                if ((attr & FileAttributes.ReparsePoint) != 0) throw new IOException("备份包含链接，已停止操作：" + item);
                if ((attr & FileAttributes.Directory) != 0) Guard(item);
            }
        }
        private string Selected(string path)
        {
            string full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(full), Root, StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(Path.GetFileName(full), @"^\d{8}-\d{6}-[a-fA-F0-9]{32}$"))
                throw new InvalidOperationException("只能操作备份目录中的有效备份。");
            Guard(Root, false); Guard(full);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException("该备份已不存在，请刷新列表。");
            return full;
        }
        private string[] Plan(string path)
        {
            if (!File.Exists(Path.Combine(path, "说明.txt")) && !File.Exists(Path.Combine(path, "恢复前备份.txt"))) throw new InvalidDataException("备份未完整创建，不能恢复。");
            var brands = new List<string>();
            foreach (string brand in Brands)
            {
                bool has = Directory.Exists(Path.Combine(path, "Roaming", brand)) || File.Exists(Path.Combine(path, brand + "-roaming.absent"));
                foreach (var view in Views)
                {
                    string file = Path.Combine(path, brand + "-" + view + "-settings.json");
                    if (!File.Exists(file)) continue;
                    registry.Validate(File.ReadAllText(file), brand); has = true;
                }
                if (has) brands.Add(brand);
            }
            return brands.ToArray();
        }
        public IList<BackupEntry> List()
        {
            Guard(Root, false);
            var list = new List<BackupEntry>();
            if (!Directory.Exists(Root)) return list;
            foreach (string path in Directory.EnumerateDirectories(Root).OrderByDescending(p => p))
            {
                if (!Regex.IsMatch(Path.GetFileName(path), @"^\d{8}-\d{6}-[a-fA-F0-9]{32}$")) continue;
                try
                {
                    Selected(path); var brands = Plan(path);
                    DateTime date; string name = Path.GetFileName(path);
                    DateTime.TryParseExact(name.Substring(0, 15), "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.None, out date);
                    list.Add(new BackupEntry { Path = path, Label = date.ToString("yyyy-MM-dd HH:mm:ss") + (File.Exists(Path.Combine(path, "恢复前备份.txt")) ? " · 恢复前自动备份" : " · 安装前备份"),
                        Summary = brands.Length == 0 ? "没有可恢复的用户配置" : string.Join(" / ", brands) + " 用户配置", CanRestore = brands.Length > 0 });
                }
                catch (Exception error) { list.Add(new BackupEntry { Path = path, Label = Path.GetFileName(path), Summary = "无法读取：" + error.Message }); }
            }
            return list;
        }
        public void Delete(string path) { RemoveTree(Selected(path)); }
        private static void RemoveTree(string path)
        {
            Guard(path);
            if (!Directory.Exists(path)) return;
            foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            foreach (string dir in Directory.GetDirectories(path, "*", SearchOption.AllDirectories)) File.SetAttributes(dir, File.GetAttributes(dir) & ~FileAttributes.ReadOnly);
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly); Directory.Delete(path, true);
        }
        private static void Copy(string source, string destination)
        {
            Guard(source); Guard(destination);
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
            foreach (string dir in Directory.GetDirectories(source)) Copy(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
        public string Restore(string path)
        {
            path = Selected(path); var brands = Plan(path);
            if (brands.Length == 0) throw new InvalidOperationException("此备份没有可恢复的用户配置。");
            foreach (string brand in brands)
            {
                InstallEngine.GuardTree(Path.Combine(roaming, brand), roaming, brand);
                InstallEngine.GuardTree(Path.Combine(local, brand), local, brand);
            }
            foreach (string brand in brands) stop(Path.Combine(local, brand));
            string recovery = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Guard(recovery); Directory.CreateDirectory(recovery);
            foreach (string brand in brands)
            {
                string current = Path.Combine(roaming, brand);
                if (Directory.Exists(current)) Copy(current, Path.Combine(recovery, "Roaming", brand));
                else File.WriteAllText(Path.Combine(recovery, brand + "-roaming.absent"), "");
                foreach (var view in Views) File.WriteAllText(Path.Combine(recovery, brand + "-" + view + "-settings.json"), registry.Capture(brand, view));
            }
            File.WriteAllText(Path.Combine(recovery, "恢复前备份.txt"), "恢复前的用户配置，允许在备份列表中恢复。不会恢复旧安装注册、启动项或协议。");
            Plan(recovery);
            try { Apply(path, brands); }
            catch (Exception error)
            {
                try { Apply(recovery, brands); }
                catch (Exception rollback) { throw new IOException("恢复未完成，回退也失败。恢复前备份：" + recovery + "；回退原因：" + rollback.Message, error); }
                throw new IOException("恢复未完成，已回退到恢复前配置。备份：" + recovery + "；原因：" + error.Message, error);
            }
            return recovery;
        }
        private void Apply(string path, string[] brands)
        {
            foreach (string brand in brands)
            {
                string saved = Path.Combine(path, "Roaming", brand), target = Path.Combine(roaming, brand);
                if (Directory.Exists(saved) || File.Exists(Path.Combine(path, brand + "-roaming.absent")))
                {
                    InstallEngine.GuardTree(target, roaming, brand); RemoveTree(target);
                    if (Directory.Exists(saved)) Copy(saved, target);
                }
                foreach (var view in Views)
                {
                    string file = Path.Combine(path, brand + "-" + view + "-settings.json");
                    if (File.Exists(file)) registry.Restore(File.ReadAllText(file), brand, view);
                }
            }
        }
    }
}
