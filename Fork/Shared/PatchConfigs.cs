using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WandEnhancer.Models;

namespace WandEnhancer.Fork
{
    internal static class PatchConfigs
    {
        public static PatchConfig Default() => new PatchConfig {
            Strategy = EPatchStrategy.Static,
            PatchTypes = new HashSet<EPatchType> { EPatchType.ActivatePro, EPatchType.DisableUpdates }
        };

        public static PatchConfig Copy(PatchConfig config) => new PatchConfig {
            Strategy = config.Strategy, AutoApplyAfterUpdate = config.AutoApplyAfterUpdate,
            PatchTypes = config.PatchTypes == null ? null : new HashSet<EPatchType>(config.PatchTypes),
            CustomScriptPaths = config.CustomScriptPaths == null ? new List<string>() : new List<string>(config.CustomScriptPaths)
        };

        public static void Validate(PatchConfig config)
        {
            if (config == null || !Enum.IsDefined(typeof(EPatchStrategy), config.Strategy)) throw new ArgumentException("请选择有效的补丁方式。");
            if (config.PatchTypes == null || config.PatchTypes.Count == 0) throw new ArgumentException("请至少选择一项增强功能。");
            if (config.PatchTypes.Any(type => !Enum.IsDefined(typeof(EPatchType), type))) throw new ArgumentException("配置包含不支持的增强功能。");
            if (config.AutoApplyAfterUpdate && config.PatchTypes.Contains(EPatchType.DisableUpdates)) throw new ArgumentException("禁用更新与更新后自动应用不能同时启用。");
            var scripts = config.CustomScriptPaths ?? new List<string>();
            if (scripts.Count > 0 && !config.PatchTypes.Contains(EPatchType.RemoteWebPanelPreview)) throw new ArgumentException("自定义脚本需要启用远程访问。");
            foreach (string path in scripts)
                if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetExtension(path), ".js", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                    throw new ArgumentException("自定义脚本不存在或不是 .js 文件：" + path);
        }
    }
}
