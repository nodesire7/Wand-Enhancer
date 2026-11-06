using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using WandEnhancer.Core;
using WandEnhancer.Core.Patching.Content;
using WandEnhancer.Core.Patching.Shared;
using WandEnhancer.Core.Patching.Strategies;
using WandEnhancer.Models;
using WandEnhancer.Utils;

namespace WandEnhancer.Fork
{
    internal static class Cli
    {
        [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);

        public static int Run(string[] args)
        {
            // Preserve redirected pipe handles. A WinExe has no console when called interactively.
            IntPtr outputHandle = GetStdHandle(-11);
            if (outputHandle == IntPtr.Zero || outputHandle == new IntPtr(-1)) AttachConsole(-1);
            // Console.OutputEncoding also changes stderr's code page, which throws when a
            // WinExe caller redirects only stdout. Write UTF-8 directly to the inherited pipe.
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            string command = args.Length > 1 ? args[1].ToLowerInvariant() : "help";
            var logs = new List<object>();
            Action<string, ELogType> log = (message, level) => logs.Add(new { level = level.ToString(), message });
            try
            {
                if (!new[] { "help", "version", "status", "verify", "patch", "restore", "launch" }.Contains(command))
                    return Reply(2, command, "Unknown command. Use --cli help.", null, logs);

                var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 2; i < args.Length; i++)
                {
                    string name = args[i];
                    if (!new[] { "--path", "--patches", "--strategy", "--expected-version", "--auto-apply", "--config" }.Contains(name) || options.ContainsKey(name))
                        return Reply(2, command, "Unknown or duplicate option: " + name, null, logs);
                    if (name == "--auto-apply") options.Add(name, "true");
                    else if (++i < args.Length && !args[i].StartsWith("--")) options.Add(name, args[i]);
                    else return Reply(2, command, "Missing value for " + name, null, logs);
                }
                if (options.Keys.Any(k => (command != "patch" && command != "verify" && (k == "--patches" || k == "--auto-apply" || k == "--config")) ||
                    (command != "patch" && command != "verify" && command != "launch" && k == "--strategy") ||
                    ((command == "help" || command == "version") && options.Count > 0)))
                    return Reply(2, command, "Option does not apply to this command.", null, logs);

                if (command == "help") return Reply(0, command, "Headless CLI; every response is one JSON object.", new {
                    commands = new[] { "version", "status", "verify", "patch", "restore", "launch" },
                    options = "--path <install root|app folder|exe> --expected-version <version>; patch/verify: --config <PatchConfig.json> OR --patches pro,disable-updates,devtools,remote-panel --strategy static|supervised --auto-apply; launch: --strategy static|supervised",
                    exitCodes = "0 success; 2 invalid arguments; 3 no install; 4 version mismatch; 5 operation failed; 6 busy"
                }, logs);
                if (command == "version") return Reply(0, command, "WandEnhancer CLI", new { cliVersion = 1 }, logs);

                var patchConfig = new PatchConfig { Strategy = EPatchStrategy.Static, PatchTypes = new HashSet<EPatchType>() };
                if (options.TryGetValue("--strategy", out string strategy))
                {
                    if (strategy != "static" && strategy != "supervised") return Reply(2, command, "Invalid strategy.", null, logs);
                    patchConfig.Strategy = strategy == "static" ? EPatchStrategy.Static : EPatchStrategy.Supervised;
                }
                if (command == "patch" || command == "verify")
                {
                    if (options.TryGetValue("--config", out string configPath))
                    {
                        if (options.ContainsKey("--patches") || options.ContainsKey("--strategy") || options.ContainsKey("--auto-apply"))
                            return Reply(2, command, "--config cannot be combined with patch selection options.", null, logs);
                        try
                        {
                            patchConfig = JsonConvert.DeserializeObject<PatchConfig>(File.ReadAllText(configPath));
                            PatchConfigs.Validate(patchConfig);
                        }
                        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException || error is ArgumentException)
                        { return Reply(2, command, error.Message, null, logs); }
                    }
                    else
                    {
                    string selection = options.TryGetValue("--patches", out string requested) ? requested : "pro,disable-updates";
                    var patches = new Dictionary<string, EPatchType> { { "pro", EPatchType.ActivatePro }, { "disable-updates", EPatchType.DisableUpdates }, { "devtools", EPatchType.DevToolsOnF12 }, { "remote-panel", EPatchType.RemoteWebPanelPreview } };
                    foreach (string name in selection.Split(','))
                    {
                        if (!patches.TryGetValue(name.Trim(), out EPatchType patch)) return Reply(2, command, "Unknown patch: " + name, null, logs);
                        patchConfig.PatchTypes.Add(patch);
                    }
                    patchConfig.AutoApplyAfterUpdate = options.ContainsKey("--auto-apply");
                    if (patchConfig.AutoApplyAfterUpdate && patchConfig.PatchTypes.Contains(EPatchType.DisableUpdates))
                        return Reply(2, command, "Choose either disable-updates or --auto-apply.", null, logs);
                    }
                }

                WeModConfig install;
                if (options.TryGetValue("--path", out string path))
                {
                    path = Path.GetFullPath(path);
                    install = WeModInstalls.ResolveInstall(File.Exists(path) ? Path.GetDirectoryName(path) : path);
                }
                else install = WeModInstalls.FindWeMod();
                if (install == null) return Reply(3, command, "Wand installation not found.", null, logs);
                string version = FileVersionInfo.GetVersionInfo(install.ExecutablePath).ProductVersion;
                if (options.TryGetValue("--expected-version", out string expected))
                {
                    if (!Version.TryParse(expected, out Version expectedVersion)) return Reply(2, command, "Invalid expected version.", null, logs);
                    if (!Version.TryParse(version, out Version actualVersion) || Normalize(actualVersion) != Normalize(expectedVersion))
                        return Reply(4, command, "Installed version does not match " + expected, Describe(install, version), logs);
                }
                if (command == "status") return Reply(0, command, "Installation detected.", Describe(install, version), logs);
                string mutexName;
                using (var sha = SHA256.Create()) mutexName = "Local\\GBTGAME.WandCli." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(install.RootDirectory.ToUpperInvariant()))).Replace("-", "");
                using (var mutex = new Mutex(false, mutexName))
                {
                    bool acquired;
                    try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return Reply(6, command, "Another CLI operation is running.", null, logs);
                    try
                    {
                        if (command == "verify")
                        {
                            var verification = PatchVerification.Verify(install, patchConfig);
                            return Reply(verification.Verified ? 0 : 5, command, verification.Message,
                                new { verified = verification.Verified, verification.FilesChecked, path = install.RootDirectory,
                                    executable = install.ExecutablePath, strategy = patchConfig.Strategy.ToString(), patches = patchConfig.PatchTypes }, logs);
                        }
                        if (command == "patch") new Enhancer(install, log, patchConfig).Patch();
                        else if (command == "restore") new Enhancer(install, log).Restore();
                        else
                        {
                            var saved = PatchVerification.ReadConfig(install.RootDirectory);
                            var mode = options.ContainsKey("--strategy") ? patchConfig.Strategy : saved?.Strategy ?? EPatchStrategy.Static;
                            var verification = PatchVerification.Verify(install, saved);
                            if (!verification.Verified || saved.Strategy != mode)
                                return Reply(5, command, "Launch blocked: " + (!verification.Verified ? verification.Message : "Requested strategy differs from verified patch."),
                                    new { verified = false, path = install.RootDirectory }, logs);
                            if (!LaunchDetached(install, mode, log))
                                return Reply(5, command, "Launch failed.", Describe(install, version), logs);
                        }
                        return Reply(0, command, "Completed.", Describe(install, version), logs);
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception error) { return Reply(5, command, error.Message, null, logs); }
        }

        private static Version Normalize(Version value) => new Version(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));

        private static bool LaunchDetached(WeModConfig install, EPatchStrategy mode, Action<string, ELogType> log)
        {
            if (mode == EPatchStrategy.Supervised)
                return StrategyFactory.Create(mode).Launch(new PatchContext(install, log), null);

            // ShellExecute does not pass the CLI's inherited output pipes to the long-lived
            // client. Process.Start with UseShellExecute=false keeps the installer's read open.
            using (var process = Process.Start(new ProcessStartInfo(install.ExecutablePath)
            {
                UseShellExecute = true, WorkingDirectory = install.RootDirectory
            })) return true;
        }

        private static object Describe(WeModConfig install, string version)
        {
            PatchVerificationResult verification;
            try { verification = PatchVerification.Verify(install, PatchVerification.ReadConfig(install.RootDirectory)); }
            catch (Exception error) { verification = new PatchVerificationResult { Message = error.Message }; }
            return new {
                path = install.RootDirectory, executable = install.ExecutablePath, brand = install.BrandName, version,
                patched = verification.Verified, hasBackup = Enhancer.HasBackup(install.RootDirectory), verification
            };
        }

        private static int Reply(int code, string command, string message, object data, List<object> logs)
        {
            Console.WriteLine(JsonConvert.SerializeObject(new { schemaVersion = 1, success = code == 0, code, command, message, version = Constants.Version.ToString(), build = Constants.Build, data, logs }));
            return code;
        }
    }
}
