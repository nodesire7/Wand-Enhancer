using System;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using WandEnhancer.Models;

namespace WandEnhancer.Fork.Installer
{
    internal interface IInstallerSession : IDisposable
    {
        string LogPath { get; }
        string BackupPath { get; }
        void Install(bool launch);
        void StartExisting(bool launch);
        void RecordFailure(Exception error);
    }

    internal interface IInstallerInteraction
    {
        LauncherSnapshot InspectInstallation();
        string[] SelectScripts(Window owner);
        void Open(string path);
        IInstallerSession CreateSession(Action<int, string> report, PatchConfig config);
    }

    internal sealed class NativeInstallerInteraction : IInstallerInteraction
    {
        public LauncherSnapshot InspectInstallation() => new LauncherStateStore().Inspect();
        public string[] SelectScripts(Window owner)
        {
            var dialog = new OpenFileDialog { Filter = "JavaScript 文件 (*.js)|*.js", Multiselect = true, CheckFileExists = true };
            return dialog.ShowDialog(owner) == true ? dialog.FileNames : new string[0];
        }

        public void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        public IInstallerSession CreateSession(Action<int, string> report, PatchConfig config) => new InstallEngine(report, config);
    }
}
