using System;
using System.IO;
using System.Threading;

namespace WandEnhancer.Fork.Installer
{
    internal static class SilentRemoval
    {
        public static void Remove(string local, string roaming, string brand, Action<string> stopProcesses,
            Action cleanShortcuts, Action cleanRegistry)
        {
            string root = Path.Combine(local, brand);
            InstallEngine.GuardTree(root, local, brand);
            InstallEngine.GuardTree(Path.Combine(roaming, brand), roaming, brand);
            stopProcesses(root);
            // The vendor updater may show confirmation UI even with --silent. Never launch
            // it or its client uninstall hooks; remove only the prevalidated per-user install.
            cleanShortcuts();
            foreach (string parent in new[] { local, roaming })
            {
                string path = Path.Combine(parent, brand);
                InstallEngine.GuardTree(path, parent, brand);
                if (!Directory.Exists(path)) continue;
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                foreach (string directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                for (int attempt = 0; ; attempt++)
                    try { Directory.Delete(path, true); break; }
                    catch (IOException) when (attempt < 5) { Thread.Sleep(500); }
            }
            cleanRegistry();
        }
    }
}
