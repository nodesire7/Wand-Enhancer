using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using WandEnhancer.Models;

namespace WandEnhancer.Fork.Installer
{
    internal sealed class SelectedScript
    {
        public string FullPath { get; }
        public string FileName => Path.GetFileName(FullPath);
        public SelectedScript(string path) { FullPath = Path.GetFullPath(path); }
    }

    internal sealed class PatchOptionsViewModel : NotifyModel
    {
        private EPatchStrategy strategy = EPatchStrategy.Static;
        private bool pro = true, devTools, disableUpdates = true, remote, autoApply;
        public EPatchStrategy Strategy { get => strategy; set { strategy = value; Changed(nameof(Strategy)); } }
        public bool ActivatePro { get => pro; set { pro = value; Changed(nameof(ActivatePro)); } }
        public bool DevTools { get => devTools; set { devTools = value; Changed(nameof(DevTools)); } }
        public bool DisableUpdates { get => disableUpdates; set { disableUpdates = value; if (value) AutoApply = false; Changed(nameof(DisableUpdates)); } }
        public bool RemotePanel { get => remote; set { remote = value; Changed(nameof(RemotePanel)); } }
        public bool AutoApply { get => autoApply; set { autoApply = value; if (value) DisableUpdates = false; Changed(nameof(AutoApply)); } }
        public ObservableCollection<SelectedScript> Scripts { get; } = new ObservableCollection<SelectedScript>();
        public bool HasScripts => Scripts.Count > 0;
        public PatchOptionsViewModel() { Scripts.CollectionChanged += (sender, args) => Changed(nameof(HasScripts)); }

        public void AddScript(string path)
        {
            string full = Path.GetFullPath(path);
            if (!File.Exists(full) || !string.Equals(Path.GetExtension(full), ".js", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择现有的 .js 文件。");
            if (!Scripts.Any(item => string.Equals(item.FullPath, full, StringComparison.OrdinalIgnoreCase))) Scripts.Add(new SelectedScript(full));
            RemotePanel = true;
        }

        public PatchConfig Snapshot()
        {
            var types = new HashSet<EPatchType>();
            if (ActivatePro) types.Add(EPatchType.ActivatePro);
            if (DevTools) types.Add(EPatchType.DevToolsOnF12);
            if (DisableUpdates) types.Add(EPatchType.DisableUpdates);
            if (RemotePanel) types.Add(EPatchType.RemoteWebPanelPreview);
            var config = new PatchConfig { Strategy = Strategy, PatchTypes = types, AutoApplyAfterUpdate = AutoApply, CustomScriptPaths = Scripts.Select(item => item.FullPath).ToList() };
            PatchConfigs.Validate(config);
            return config;
        }
    }
}
