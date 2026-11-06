using System.IO;
using WandEnhancer.Models;

namespace WandEnhancer.Fork.Installer
{
    internal static class PatchScriptStore
    {
        public static PatchConfig Copy(PatchConfig source, string destination)
        {
            PatchConfigs.Validate(source);
            var result = PatchConfigs.Copy(source);
            result.CustomScriptPaths.Clear();
            int index = 0;
            foreach (string path in source.CustomScriptPaths ?? new System.Collections.Generic.List<string>())
            {
                string directory = Path.Combine(destination, (index++).ToString("D3"));
                Directory.CreateDirectory(directory);
                string saved = Path.Combine(directory, Path.GetFileName(path));
                File.Copy(path, saved, true);
                result.CustomScriptPaths.Add(saved);
            }
            return result;
        }
    }
}
