using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WandEnhancer.Fork.Installer
{
    internal static class UserShellCleanup
    {
        internal static IEnumerable<string> AssociatedProtocolKeys(RegistryKey user, string root)
        {
            foreach (string key in new[] { @"Software\Classes\wand", @"Software\Classes\wemod", @"Software\Classes\Applications\Wand.exe", @"Software\Classes\Applications\WeMod.exe" })
                using (var command = user.OpenSubKey(key + @"\shell\open\command"))
                    if (command != null && InstallEngine.CommandIsUnder(command.GetValue(null) as string, root)) yield return key;
        }

        public static void RemoveShortcuts(string root, string backup)
        {
            var folders = new Dictionary<string, string> {
                { "Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) },
                { "Programs", Environment.GetFolderPath(Environment.SpecialFolder.Programs) },
                { "Startup", Environment.GetFolderPath(Environment.SpecialFolder.Startup) }
            };
            RemoveShortcuts(root, backup, folders);
        }

        internal static void RemoveShortcuts(string root, string backup, IDictionary<string, string> folders)
        {
            object shell = null;
            try
            {
                foreach (var folder in folders)
                    foreach (string path in ShortcutFiles(folder.Value, folder.Key == "Programs").ToArray())
                    {
                        if (shell == null) shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                        object link = null;
                        try
                        {
                            link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                            string target = link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string;
                            if (string.IsNullOrWhiteSpace(target) || !Path.IsPathRooted(target) || !InstallEngine.IsUnder(Environment.ExpandEnvironmentVariables(target), root)) continue;
                            string relative = Path.GetFullPath(path).Substring(Path.GetFullPath(folder.Value).TrimEnd('\\').Length).TrimStart('\\');
                            string saved = Path.Combine(backup, folder.Key, relative);
                            Directory.CreateDirectory(Path.GetDirectoryName(saved));
                            File.Copy(path, saved, true);
                            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                            File.Delete(path);
                            string parent = Path.GetDirectoryName(path);
                            string name = Path.GetFileName(root.TrimEnd('\\'));
                            if (folder.Key == "Programs" && !string.Equals(parent.TrimEnd('\\'), folder.Value.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                                && string.Equals(Path.GetFileName(parent), name, StringComparison.OrdinalIgnoreCase)
                                && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
                        }
                        finally { if (link != null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link); }
                    }
            }
            finally { if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
        }

        private static IEnumerable<string> ShortcutFiles(string folder, bool recursive)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) yield break;
            foreach (string file in Directory.EnumerateFiles(folder, "*.lnk"))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file;
            if (!recursive) yield break;
            foreach (string child in Directory.EnumerateDirectories(folder))
                foreach (string file in ShortcutFiles(child, true)) yield return file;
        }
    }
}
