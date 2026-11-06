using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AsarSharp.AsarFileSystem;
using Newtonsoft.Json;
using WandEnhancer.Core.Patching.Shared;
using WandEnhancer.Core.Patching.Strategies.Static;
using WandEnhancer.Models;
using WandEnhancer.Utils;

namespace WandEnhancer.Core.Patching.Content
{
    public sealed class PatchVerificationResult
    {
        public bool Verified { get; set; }
        public string Message { get; set; }
        public int FilesChecked { get; set; }
    }

    public static class PatchVerification
    {
        internal const string ConfigFileName = ".applied-patch.json";

        public static PatchConfig ReadConfig(string root)
        {
            string path = Path.Combine(root, "resources", ConfigFileName);
            try { return File.Exists(path) ? JsonConvert.DeserializeObject<PatchConfig>(File.ReadAllText(path)) : null; }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException)
            { return null; }
        }

        public static PatchVerificationResult Verify(WeModConfig install, PatchConfig config)
            => Check(install, config, false);

        internal static PatchVerificationResult Check(WeModConfig install, PatchConfig config, bool duringPatch)
        {
            string scratch = Path.Combine(Path.GetTempPath(), "wand-verify-" + Guid.NewGuid().ToString("N"));
            var result = new PatchVerificationResult();
            try
            {
                if (install == null) throw new InvalidDataException("Installation not found.");
                if (config == null) throw new InvalidDataException("No verified patch selection recorded; apply the selected options first.");
                if (config.PatchTypes == null || config.PatchTypes.Any(type => !Enum.IsDefined(typeof(EPatchType), type))
                    || !Enum.IsDefined(typeof(EPatchStrategy), config.Strategy))
                    throw new InvalidDataException("Invalid patch selection.");
                string resources = Path.Combine(install.RootDirectory, "resources");
                if (!Enhancer.HasBackup(install.RootDirectory)) throw new InvalidDataException("Pristine backups are missing.");
                if (!duringPatch && File.Exists(Path.Combine(resources, ".incomplete-patch")))
                    throw new InvalidDataException("The previous patch did not complete.");

                Directory.CreateDirectory(scratch);
                var original = new Archive(Path.Combine(resources, "app.asar.backup"), Path.Combine(resources, "app.asar.unpacked.backup"));
                var actual = new Archive(Path.Combine(resources, "app.asar"), Path.Combine(resources, "app.asar.unpacked"));
                // Rebuild expectations from pristine bytes, never from the extracted staging copy.
                foreach (string name in original.FileSystem.GetHeader().Files.Keys.Where(AsarContentPatcher.IsCandidateBundleFile))
                {
                    if (Path.GetFileName(name) != name) throw new InvalidDataException("Invalid top-level bundle name.");
                    File.WriteAllBytes(Path.Combine(scratch, name), original.Read(name));
                }
                Action<string, ELogType> quiet = (message, level) => { };
                new AsarContentPatcher(scratch, quiet, new JavaScriptPatchApplier(quiet)).Patch(config.PatchTypes);
                new RemotePanelInjector(scratch, quiet).Inject(config.PatchTypes, config.CustomScriptPaths);
                foreach (string expected in Directory.EnumerateFiles(scratch, "*", SearchOption.AllDirectories))
                {
                    string name = expected.Substring(scratch.Length + 1);
                    if (!File.ReadAllBytes(expected).SequenceEqual(actual.Read(name)))
                        throw new InvalidDataException("Applied content mismatch: " + name);
                    result.FilesChecked++;
                }

                string auxiliaryRoot = Path.Combine(resources, @"app.asar.unpacked.backup\static\unpacked\auxiliary");
                if (Directory.Exists(auxiliaryRoot))
                    foreach (string source in Directory.EnumerateFiles(auxiliaryRoot, "*AuxiliaryService.exe"))
                    {
                        string expected = Path.Combine(scratch, "verify-auxiliary.exe");
                        File.Copy(source, expected, true);
                        if (AuxTrustNeutralizer.Neutralize(expected, quiet) < 0)
                            throw new InvalidDataException("Unsupported auxiliary service.");
                        string target = Path.Combine(resources, @"app.asar.unpacked\static\unpacked\auxiliary", Path.GetFileName(source));
                        if (!File.ReadAllBytes(expected).SequenceEqual(File.ReadAllBytes(target)))
                            throw new InvalidDataException("Auxiliary compatibility content mismatch: " + Path.GetFileName(source));
                        result.FilesChecked++;
                    }

                long fuse = ElectronFuseWire.FindStateFileOffset(install.ExecutablePath);
                if (fuse < 0) throw new InvalidDataException("Electron fuse block missing from the launch target.");
                if (config.Strategy == EPatchStrategy.Static)
                    using (var stream = File.OpenRead(install.ExecutablePath))
                    {
                        stream.Position = fuse;
                        if (stream.ReadByte() != ElectronFuseWire.StateRemoved)
                            throw new InvalidDataException("Static launch target has not been enabled.");
                    }
                else
                {
                    string launcher = Path.Combine(LauncherDeployment.SquirrelRootOf(install), install.ExecutableName);
                    if (AssemblyName.GetAssemblyName(launcher).FullName != Assembly.GetExecutingAssembly().GetName().FullName)
                        throw new InvalidDataException("Supervised launcher is missing or was replaced.");
                }
                result.Verified = true;
                result.Message = "Verified actual archive content and launch enablement (" + result.FilesChecked + " files).";
            }
            catch (Exception error)
            {
                result.Message = error.Message;
            }
            finally
            {
                if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
            }
            return result;
        }

        private sealed class Archive
        {
            private readonly string path;
            private readonly string unpacked;
            internal readonly Filesystem FileSystem;

            internal Archive(string path, string unpacked)
            {
                this.path = path;
                this.unpacked = unpacked;
                FileSystem = Disk.ReadFilesystemSync(path);
            }

            internal byte[] Read(string name)
            {
                var entry = FileSystem.GetFile(name, false);
                if (entry == null || !entry.IsFile || entry.IsLink)
                    throw new InvalidDataException("Missing archive file: " + name);
                if (entry.Unpacked == true)
                {
                    string target = Path.Combine(unpacked, name);
                    if (!AsarSharp.Utils.Extensions.IsPathInside(unpacked, target)) throw new InvalidDataException("Invalid archive path.");
                    return File.ReadAllBytes(target);
                }
                long size = entry.Size ?? 0;
                if (size < 0 || size > 128 * 1024 * 1024) throw new InvalidDataException("Invalid archive entry size.");
                using (var stream = File.OpenRead(path))
                {
                    long offset = checked(8L + FileSystem.GetHeaderSize() + long.Parse(entry.Offset));
                    if (offset < 0 || offset > stream.Length - size) throw new InvalidDataException("Archive truncated: " + name);
                    stream.Position = offset;
                    byte[] bytes = new byte[(int)size];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int count = stream.Read(bytes, read, bytes.Length - read);
                        if (count == 0) throw new EndOfStreamException();
                        read += count;
                    }
                    return bytes;
                }
            }
        }
    }
}
