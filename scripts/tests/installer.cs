using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Windows.Threading;
using System.Threading;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WandEnhancer.Fork.Installer;
using WandEnhancer.Models;

internal static class InstallerTests
{
    private static int passed;
    private static string output;
    private static string corePath;
    private static readonly string[] Order = { "validate", "backup", "remove", "install", "patch", "verify", "launch" };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--watch-updates")
        {
            File.WriteAllText(Path.Combine(args[1], "watcher.pid"), System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
            System.Threading.Thread.Sleep(30000); return 0;
        }
        if (args.Length > 0 && args[0] == "--watch-parent")
        {
            var core = System.Reflection.Assembly.LoadFrom(args[1]);
            core.GetType("WandEnhancer.Core.Services.WatcherAutostart", true).GetMethod("StartDetached", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { args[2], args[3] });
            Console.WriteLine("started"); return 0;
        }
        if (args.Length > 0 && args[0] == "--uninstall")
        {
            File.WriteAllText(Environment.GetEnvironmentVariable("WAND_REMOVAL_TEST_MARKER"), "confirmation-required");
            return 0;
        }
        if (args.Length > 0 && args[0] == "--echo")
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
            Console.WriteLine(InstallEngine.Json.Serialize(args.Skip(1).ToArray())); return 0;
        }
        output = args[0];
        corePath = args[1];
        Directory.CreateDirectory(output);
        try
        {
            Test("install performs validation, backup, removal, installation, patch, verification, launch in order", () => {
                var fake = new FakeSteps();
                var reports = new List<int>();
                InstallWorkflow.Run(fake, true, "12.68.0", (step, text) => reports.Add(step));
                Equal(string.Join(",", fake.Calls), string.Join(",", Order));
                Equal(reports.Last(), 6);
            });
            foreach (string operation in Order)
            {
                string failure = operation;
                Test("failure in " + failure + " stops all subsequent actions", () => {
                    var fake = new FakeSteps { FailAt = failure };
                    bool complete = false;
                    Throws(() => InstallWorkflow.Run(fake, true, "12.68.0", (step, text) => complete |= step == 6));
                    Equal(fake.Calls.Count, Array.IndexOf(Order, failure) + 1);
                    Check(!complete, "Failure must never report completion");
                });
            }
            Test("false verification prevents launch", () => {
                var fake = new FakeSteps { Verified = false };
                Throws(() => InstallWorkflow.Run(fake, true, "12.68.0", (step, text) => {}));
                Check(!fake.Calls.Contains("launch"), "Unverified patch launched");
            });
            Test("launch opt-out still verifies the patch", () => {
                var fake = new FakeSteps();
                InstallWorkflow.Run(fake, false, "12.68.0", (step, text) => {});
                Equal(string.Join(",", fake.Calls), string.Join(",", Order.Take(6)));
            });
            Test("busy state rejects double activation and retry resets failed steps", () => {
                var model = new InstallerViewModel("12.68.0");
                Check(model.Begin(), "Did not start"); Check(!model.Begin(), "Double start accepted");
                model.Report(3, "installing"); model.Fail("disk full");
                Equal(model.Stages[3].State, "失败"); Equal(model.ActionText, "重试安装");
                model.Report(6, "stale success"); Equal(model.State, "Error"); Equal(model.Message, "disk full");
                Check(model.Begin(), "Retry rejected"); Equal(model.Progress, 0); Equal(model.Stages[3].State, "等待");
            });
            Test("success preserves launch opt-out and ignores stale updates", () => {
                var model = new InstallerViewModel("12.68.0"); model.Begin(); model.Succeed(false);
                Equal(model.Stages[5].State, "已跳过"); Equal(model.ActionText, "完成"); Check(!model.Begin(), "Success restarted installation");
                model.Report(0, "late callback"); model.Fail("late error"); Equal(model.State, "Success"); Equal(model.Progress, 6);
            });
            Test("progress inputs are bounded", () => {
                var model = new InstallerViewModel("12.68.0"); model.Begin(); model.Report(-1, ""); Equal(model.Progress, 0);
                model.Report(99, ""); Equal(model.Progress, 6);
            });
            Test("startup cleanup matches only commands inside the exact application directory", () => {
                Check(InstallEngine.CommandIsUnder("\"C:\\My Apps\\Wand\\Wand.exe\" --minimized", @"C:\My Apps\Wand"), "Quoted path rejected");
                Check(!InstallEngine.CommandIsUnder(@"C:\Apps\WandOther\Other.exe", @"C:\Apps\Wand"), "Sibling accepted");
                Check(!InstallEngine.CommandIsUnder(@"C:\Apps\Wand\..\Other.exe", @"C:\Apps\Wand"), "Traversal accepted");
                Check(!InstallEngine.CommandIsUnder("\"", @"C:\Apps\Wand"), "Broken quote accepted");
                Check(!InstallEngine.CommandIsUnder("relative.exe", @"C:\Apps\Wand"), "Relative command accepted");
                Environment.SetEnvironmentVariable("WAND_INSTALLER_TEST_ROOT", @"C:\My Apps\Wand");
                try { Check(InstallEngine.CommandIsUnder("\"%WAND_INSTALLER_TEST_ROOT%\\Wand.exe\"", @"C:\My Apps\Wand"), "Expanded command rejected"); }
                finally { Environment.SetEnvironmentVariable("WAND_INSTALLER_TEST_ROOT", null); }
            });
            Test("registry backup preserves kinds, binary values, strings and subkeys", TestRegistry);
            Test("all enhancer options reach an isolated PatchConfig snapshot", () => {
                var options = new PatchOptionsViewModel { Strategy = EPatchStrategy.Supervised, ActivatePro = false, DevTools = true, RemotePanel = true, AutoApply = true };
                var snapshot = options.Snapshot();
                Equal(snapshot.Strategy, EPatchStrategy.Supervised); Check(snapshot.AutoApplyAfterUpdate, "Auto-apply lost");
                Check(snapshot.PatchTypes.SetEquals(new[] { EPatchType.DevToolsOnF12, EPatchType.RemoteWebPanelPreview }), "Patch choices lost");
                options.ActivatePro = true; Check(!snapshot.PatchTypes.Contains(EPatchType.ActivatePro), "Snapshot changed with UI");
                options.DisableUpdates = true; Check(!options.AutoApply, "Conflicting update options enabled");
                options.AutoApply = true; Check(!options.DisableUpdates, "Auto-apply did not allow updates");
            });
            Test("script selections are deduplicated, enable remote access and survive old installation removal", TestScriptOptions);
            Test("auto-apply watcher does not inherit the CLI output pipe", TestWatcherPipe);
            Test("silent removal never launches the original interactive uninstaller", TestSilentRemoval);
            Test("shortcut cleanup backs up only links targeting the removed installation", TestShortcuts);
            Test("silent removal rejects unsafe targets before any stop or cleanup action", () => {
                bool touched = false;
                Throws(() => SilentRemoval.Remove(Path.GetTempPath(), Path.GetTempPath(), "Other", path => touched = true, () => touched = true, () => touched = true));
                Check(!touched, "Unsafe removal had side effects");
            });
            Test("Windows command argument quoting survives a real subprocess round-trip", () => {
                string[] values = { "", "plain", "中文 路径", "a\tb", "embedded\"quote", @"C:\folder with space\", "\\\\\"", "line\nbreak", "--flag=value" };
                var start = new System.Diagnostics.ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,
                    WandEnhancer.Fork.Arguments.Join(new[] { "--echo" }.Concat(values))) { UseShellExecute = false, RedirectStandardOutput = true, StandardOutputEncoding = new System.Text.UTF8Encoding(false) };
                using (var process = System.Diagnostics.Process.Start(start))
                {
                    var read = process.StandardOutput.ReadToEndAsync(); Check(process.WaitForExit(5000), "Echo timed out"); Check(read.Wait(5000), "Echo output not closed");
                    string[] actual = InstallEngine.Json.Deserialize<string[]>(read.Result);
                    Check(values.SequenceEqual(actual), "Quoted arguments were changed by Windows parsing");
                }
            });
            Test("standalone GUI process survives its dispatcher startup", () => {
                using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(typeof(InstallerWindow).Assembly.Location) { UseShellExecute = false }))
                try
                {
                    Check(!process.WaitForExit(3000), "Standalone GUI exited during startup; code " + (process.HasExited ? process.ExitCode.ToString() : "unknown"));
                    process.Refresh(); Check(process.MainWindowHandle != IntPtr.Zero, "Standalone GUI has no window");
                }
                finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
            });
            Test("WPF default, busy, failure, success, minimum size and DPI layouts", TestWindow);
            Console.WriteLine("PASS: " + passed + " installer test groups");
            File.WriteAllText(Path.Combine(output, "result.json"), InstallEngine.Json.Serialize(new { success = true, groups = passed }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            File.WriteAllText(Path.Combine(output, "result.json"), InstallEngine.Json.Serialize(new { success = false, groups = passed, error = error.ToString() }));
            return 1;
        }
    }

    private static void TestRegistry()
    {
        string keyPath = @"Software\GBTGAME\WandInstaller.Tests\" + Guid.NewGuid().ToString("N");
        using (var root = Registry.CurrentUser.CreateSubKey(keyPath))
        try
        {
            root.SetValue("text", "中文", RegistryValueKind.String);
            root.SetValue("count", 42, RegistryValueKind.DWord);
            root.SetValue("binary", new byte[] { 0, 127, 255 }, RegistryValueKind.Binary);
            root.SetValue("expand", "%TEMP%\\fixture", RegistryValueKind.ExpandString);
            root.SetValue("multi", new[] { "one", "two" }, RegistryValueKind.MultiString);
            using (var child = root.CreateSubKey("Nested")) child.SetValue("saved", "yes");
            string snapshot = InstallEngine.Json.Serialize(InstallEngine.Snapshot(root));
            foreach (string expected in new[] { "中文", "DWord", "Binary", "ExpandString", "%TEMP%", "MultiString", "Nested", "saved" }) Check(snapshot.Contains(expected), "Missing snapshot value " + expected);
            using (var run = root.CreateSubKey("Run"))
            {
                run.SetValue("own", "\"C:\\Apps\\Wand\\Wand.exe\" --start");
                run.SetValue("other", "\"C:\\Apps\\WandOther\\Other.exe\"");
                var values = InstallEngine.StartupValues(run, @"C:\Apps\Wand");
                Equal(values.Count, 1); Check(values.ContainsKey("own"), "Own startup missing");
            }
            using (var command = root.CreateSubKey(@"Software\Classes\wemod\shell\open\command")) command.SetValue("", "\"C:\\Apps\\Wand\\Wand.exe\" \"%1\"");
            using (var command = root.CreateSubKey(@"Software\Classes\wand\shell\open\command")) command.SetValue("", "\"C:\\Apps\\Other\\Other.exe\" \"%1\"");
            var protocols = UserShellCleanup.AssociatedProtocolKeys(root, @"C:\Apps\Wand").ToArray();
            Equal(protocols.Length, 1); Equal(protocols[0], @"Software\Classes\wemod");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); }
    }

    private static void TestWatcherPipe()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "wand-watch-pipe-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        string child = Path.Combine(scratch, "WandEnhancerWatcher.exe");
        string pidFile = Path.Combine(scratch, "watcher.pid");
        File.Copy(System.Reflection.Assembly.GetExecutingAssembly().Location, child);
        File.Copy(typeof(InstallerWindow).Assembly.Location, Path.Combine(scratch, "WandSetup.exe"));
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,
                WandEnhancer.Fork.Arguments.Join(new[] { "--watch-parent", corePath, child, scratch })) { UseShellExecute = false, RedirectStandardOutput = true };
            using (var parent = System.Diagnostics.Process.Start(start))
            {
                var read = parent.StandardOutput.ReadToEndAsync(); Check(parent.WaitForExit(5000), "Watcher starter did not exit");
                Check(read.Wait(1500), "Long-running watcher kept CLI output open");
                Equal(parent.ExitCode, 0);
            }
            for (int i = 0; i < 50 && !File.Exists(pidFile); i++) System.Threading.Thread.Sleep(100);
            Check(File.Exists(pidFile), "Watcher did not start");
        }
        finally
        {
            if (File.Exists(pidFile)) using (var process = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(pidFile))))
                if (!process.HasExited && string.Equals(process.MainModule.FileName, child, StringComparison.OrdinalIgnoreCase)) { process.Kill(); process.WaitForExit(5000); }
            Directory.Delete(scratch, true);
        }
    }

    private static void TestScriptOptions()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "wand-script-test-" + Guid.NewGuid().ToString("N"));
        string old = Path.Combine(scratch, "old"); Directory.CreateDirectory(old);
        string script = Path.Combine(old, "中文 script.js"); File.WriteAllText(script, "globalThis.fixture = true;");
        try
        {
            var options = new PatchOptionsViewModel(); options.AddScript(script); options.AddScript(script);
            Equal(options.Scripts.Count, 1); Check(options.RemotePanel, "Script did not enable remote access");
            options.Strategy = EPatchStrategy.Supervised; options.AutoApply = true;
            var cached = PatchScriptStore.Copy(options.Snapshot(), Path.Combine(scratch, "cache"));
            File.Delete(script); Directory.Delete(old);
            var saved = PatchScriptStore.Copy(cached, Path.Combine(scratch, "installed", "WandEnhancerScripts"));
            Equal(saved.Strategy, EPatchStrategy.Supervised); Check(saved.AutoApplyAfterUpdate, "Persisted config lost auto-apply");
            Equal(File.ReadAllText(saved.CustomScriptPaths.Single()), "globalThis.fixture = true;");
            Equal(Path.GetFileName(saved.CustomScriptPaths.Single()), "中文 script.js");
            Directory.Delete(Path.Combine(scratch, "cache"), true);
            Check(File.Exists(saved.CustomScriptPaths.Single()), "Auto-apply script depends on temporary cache");
            string configFile = Path.Combine(scratch, "selected.json");
            File.WriteAllText(configFile, InstallEngine.Json.Serialize(saved));
            var start = new System.Diagnostics.ProcessStartInfo(typeof(InstallerWindow).Assembly.Location,
                WandEnhancer.Fork.Arguments.Join(new[] { "--cli", "patch", "--path", Path.Combine(scratch, "missing-install"), "--config", configFile })) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = new System.Text.UTF8Encoding(false)
                };
            using (var process = System.Diagnostics.Process.Start(start))
            {
                var read = process.StandardOutput.ReadToEndAsync(); Check(process.WaitForExit(15000), "Config round-trip timed out"); Check(read.Wait(5000), "Config output not closed");
                Equal(process.ExitCode, 3);
                var reply = InstallEngine.Json.Deserialize<Dictionary<string, object>>(read.Result);
                Equal(Convert.ToInt32(reply["code"]), 3);
            }
            options.Scripts.Clear(); Check(!options.HasScripts, "Script empty state is stale");
        }
        finally { Directory.Delete(scratch, true); }
    }

    private static void TestSilentRemoval()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "wand-removal-test-" + Guid.NewGuid().ToString("N"));
        string local = Path.Combine(scratch, "Local");
        string roaming = Path.Combine(scratch, "Roaming");
        string root = Path.Combine(local, "Wand");
        string marker = Path.Combine(scratch, "confirmation.marker");
        Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(roaming, "Wand"));
        File.Copy(System.Reflection.Assembly.GetExecutingAssembly().Location, Path.Combine(root, "Update.exe"));
        File.Copy(typeof(InstallerWindow).Assembly.Location, Path.Combine(root, "WandSetup.exe"));
        File.WriteAllText(Path.Combine(roaming, "Wand", "old.txt"), "old settings");
        string readOnly = Path.Combine(root, "readonly.txt"); File.WriteAllText(readOnly, "read-only application file"); File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        string sibling = Path.Combine(local, "WandOther"); Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "keep.txt"), "unrelated");
        Environment.SetEnvironmentVariable("WAND_REMOVAL_TEST_MARKER", marker);
        try
        {
            bool stopped = false, registry = false, shortcuts = false;
            SilentRemoval.Remove(local, roaming, "Wand", path => { Equal(path, root); stopped = true; }, () => shortcuts = true, () => registry = true);
            Check(!File.Exists(marker), "Original uninstaller was launched and requested confirmation");
            Check(stopped && registry && shortcuts, "Cleanup steps were skipped");
            Check(!Directory.Exists(root) && !Directory.Exists(Path.Combine(roaming, "Wand")), "Old installation remains");
            Check(File.Exists(Path.Combine(sibling, "keep.txt")), "Unrelated directory was removed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("WAND_REMOVAL_TEST_MARKER", null);
            Check(Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase), "Invalid fixture cleanup path");
            Directory.Delete(scratch, true);
        }
    }

    private static void TestShortcuts()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "wand-shortcut-test-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(scratch, "Wand");
        string programs = Path.Combine(scratch, "Programs");
        string group = Path.Combine(programs, "Wand");
        string desktop = Path.Combine(scratch, "Desktop");
        string backup = Path.Combine(scratch, "Backup");
        Directory.CreateDirectory(root); Directory.CreateDirectory(group); Directory.CreateDirectory(desktop);
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
        Action<string, string> create = (path, target) => {
            object link = shell.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
            try
            {
                link.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { target });
                link.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); }
        };
        try
        {
            create(Path.Combine(desktop, "My Wand.lnk"), Path.Combine(root, "Wand.exe"));
            create(Path.Combine(group, "Wand.lnk"), Path.Combine(root, "Update.exe"));
            create(Path.Combine(desktop, "Wand.lnk"), Path.Combine(scratch, "Other", "Other.exe"));
            UserShellCleanup.RemoveShortcuts(root, backup, new Dictionary<string, string> { { "Desktop", desktop }, { "Programs", programs } });
            Check(!File.Exists(Path.Combine(desktop, "My Wand.lnk")), "Owned desktop shortcut remains");
            Check(!Directory.Exists(group), "Empty Wand start menu folder remains");
            Check(File.Exists(Path.Combine(desktop, "Wand.lnk")), "Same-name unrelated shortcut was deleted");
            Check(File.Exists(Path.Combine(backup, "Desktop", "My Wand.lnk")), "Desktop shortcut backup missing");
            Check(File.Exists(Path.Combine(backup, "Programs", "Wand", "Wand.lnk")), "Start menu shortcut backup missing");
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            Check(Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase), "Invalid shortcut fixture cleanup path");
            Directory.Delete(scratch, true);
        }
    }

    private static void TestWindow()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            var window = new InstallerWindow();
            window.Show();
            try
            {
                var model = (InstallerViewModel)window.DataContext;
                Equal(((TextBlock)window.FindName("AuthorText")).Text, "作者  A-FEI-");
                Equal(((Run)window.FindName("ForumText")).Text, "bbs.gbtgame.me");
                Equal(((Hyperlink)window.FindName("ForumLink")).NavigateUri.AbsoluteUri, "https://bbs.gbtgame.me/");
                Check(((ScrollViewer)window.FindName("SettingsScroll")).VerticalScrollBarVisibility == ScrollBarVisibility.Hidden, "Scroll behavior differs from enhancer");
                var originalPalette = new ResourceDictionary { Source = new Uri("/WandSetup;component/Style/ColorScheme.xaml", UriKind.Relative) };
                Equal(((SolidColorBrush)window.FindResource("Background")).Color, ((SolidColorBrush)originalPalette["Background"]).Color);
                Capture(window, "ready", 900, 760, 96);
                Test("button hover preserves geometry and disabled/pressed styles", () => TestButtonStates(window));
                model.Begin(); model.Report(3, "正在安装 Wand 12.68.0…"); window.UpdateLayout();
                Check(!((Button)window.FindName("InstallButton")).IsEnabled, "Busy install button enabled");
                Check(!((CheckBox)window.FindName("Launch")).IsEnabled, "Busy launch checkbox enabled");
                window.Close(); Check(window.IsVisible, "Busy window closed");
                Capture(window, "busy", 960, 720, 96);
                model.Fail("测试错误：无法写入目标目录。请检查磁盘空间和文件权限，查看日志后重试。\n" + new string('路', 400));
                Check(((Button)window.FindName("InstallButton")).IsEnabled, "Retry unavailable");
                ((Expander)window.FindName("LogExpander")).IsExpanded = true;
                Capture(window, "error-minimum", 820, 680, 96);
                model.Begin(); model.Succeed(true);
                ((Expander)window.FindName("LogExpander")).IsExpanded = false;
                Capture(window, "success", 960, 720, 96);
                Capture(window, "dpi-125", 900, 760, 120);
                Capture(window, "dpi-150", 900, 760, 144);
                Capture(window, "dpi-200", 960, 720, 192);
            }
            finally { window.Close(); }
            Test("all option controls bind, update choices exclude each other, keyboard focus is visible", TestOptionControls);
            Test("script dialog cancel, multiple, duplicate, invalid, failure and remove buttons", TestScriptControls);
            Test("install, busy guard, log, backup, forum, failure, retry and finish buttons", TestWorkflowControls);
            Test("invalid options never create a session and logging errors never crash recovery", TestRecoveryControls);
            Test("long scripts, scrolling and minimum/maximized layouts keep actions reachable", TestLongContent);
        }
        finally { SynchronizationContext.SetSynchronizationContext(null); app.Shutdown(); }
    }

    private static T Control<T>(InstallerWindow window, string name) where T : class { return (T)window.FindName(name); }
    private static void Click(ButtonBase button)
    {
        typeof(ButtonBase).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(button, null);
    }
    private static void Toggle(InstallerWindow window, string name, bool value)
    {
        var box = Control<CheckBox>(window, name);
        if (box.IsChecked != value) Click(box);
        Equal(box.IsChecked, (bool?)value);
    }
    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => {}));
            if (done()) { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => {})); return; }
            Thread.Sleep(10);
        } while (DateTime.UtcNow < deadline);
        throw new Exception("UI operation timed out");
    }
    private static void TestButtonStates(InstallerWindow window)
    {
        var button = Control<Button>(window, "InstallButton");
        window.UpdateLayout();
        var before = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
        var hoverKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
        var pressedKey = (DependencyPropertyKey)typeof(ButtonBase).GetField("IsPressedPropertyKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
        try
        {
            button.SetValue(hoverKey, true); window.UpdateLayout();
            Equal(button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize)), before);
            button.SetValue(pressedKey, true); Equal(button.Opacity, 0.75);
            button.ClearValue(pressedKey); button.IsEnabled = false;
            Equal(((SolidColorBrush)button.Background).Color, ((SolidColorBrush)window.FindResource("Muted")).Color);
        }
        finally { button.ClearValue(hoverKey); button.ClearValue(pressedKey); button.ClearValue(UIElement.IsEnabledProperty); }
    }
    private static void TestOptionControls()
    {
        var window = new InstallerWindow(new FakeInteraction()); window.Show();
        try
        {
            PumpUntil(() => window.IsLoaded);
            var model = (InstallerViewModel)window.DataContext;
            var strategy = Control<ComboBox>(window, "StrategyBox");
            Check(strategy.IsKeyboardFocusWithin, "Initial keyboard focus missing");
            Check(strategy.FocusVisualStyle != null, "Strategy keyboard focus invisible");
            Toggle(window, "ActivateProBox", false); Equal(model.Options.ActivatePro, false);
            Toggle(window, "ActivateProBox", true); Equal(model.Options.ActivatePro, true);
            Toggle(window, "DevToolsBox", true); Equal(model.Options.DevTools, true);
            Toggle(window, "DevToolsBox", false); Equal(model.Options.DevTools, false);
            Toggle(window, "AutoApplyBox", true); Equal(model.Options.AutoApply, true);
            Equal(Control<CheckBox>(window, "DisableUpdatesBox").IsChecked, (bool?)false);
            Toggle(window, "DisableUpdatesBox", true); Equal(model.Options.DisableUpdates, true);
            Equal(Control<CheckBox>(window, "AutoApplyBox").IsChecked, (bool?)false);
            Toggle(window, "RemotePanelBox", true); Equal(model.Options.RemotePanel, true);
            Toggle(window, "RemotePanelBox", false); Equal(model.Options.RemotePanel, false);
            strategy.SetCurrentValue(Selector.SelectedValueProperty, EPatchStrategy.Supervised);
            Equal(model.Options.Strategy, EPatchStrategy.Supervised);
            strategy.SetCurrentValue(Selector.SelectedValueProperty, EPatchStrategy.Static);
            Equal(model.Options.Strategy, EPatchStrategy.Static);
            foreach (string name in new[] { "ActivateProBox", "DevToolsBox", "DisableUpdatesBox", "RemotePanelBox", "AutoApplyBox", "Launch" })
                Check(Control<CheckBox>(window, name).FocusVisualStyle != null, "Missing keyboard outline: " + name);
            Click(Control<CheckBox>(window, "Launch")); Equal(Control<CheckBox>(window, "Launch").IsChecked, (bool?)false);
            strategy.Focus(); Check(strategy.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)), "Tab navigation stuck");
            Check(Control<CheckBox>(window, "ActivateProBox").IsKeyboardFocused, "Wrong next Tab target");
            var expander = Control<Expander>(window, "LogExpander"); expander.ApplyTemplate();
            Click(Descendants(expander).OfType<ToggleButton>().First()); Check(expander.IsExpanded, "Expand log failed");
            Click(Descendants(expander).OfType<ToggleButton>().First()); Check(!expander.IsExpanded, "Collapse log failed");
        }
        finally { window.Close(); }
    }
    private static void TestScriptControls()
    {
        var ui = new FakeInteraction(); var window = new InstallerWindow(ui); window.Show();
        string script = Path.Combine(output, "按钮测试 中文.js"); File.WriteAllText(script, "globalThis.uiTest = true;");
        string second = Path.Combine(output, "第二个脚本.js"); File.WriteAllText(second, "void 0;");
        try
        {
            var model = (InstallerViewModel)window.DataContext;
            var add = Control<Button>(window, "AddScriptButton");
            Click(add); Equal(model.Options.Scripts.Count, 0);
            ui.Files = new[] { script, second, script }; Click(add); window.UpdateLayout();
            Equal(model.Options.Scripts.Count, 2); Check(model.Options.RemotePanel, "Script did not enable remote panel");
            Equal(Control<TextBlock>(window, "NoScriptsText").Visibility, Visibility.Collapsed);
            ui.Files = new[] { Path.Combine(output, "missing.txt") }; Click(add);
            Equal(model.Options.Scripts.Count, 2); Check(Control<Expander>(window, "LogExpander").IsExpanded, "Invalid selection hidden");
            ui.SelectionError = true; Click(add);
            Check(Control<TextBox>(window, "Details").Text.Contains("dialog unavailable"), "File dialog failure was not shown");
            var remove = Descendants(Control<ItemsControl>(window, "ScriptList")).OfType<Button>().First();
            Equal(System.Windows.Automation.AutomationProperties.GetName(remove), "移除脚本 " + Path.GetFileName(script));
            Click(remove); window.UpdateLayout(); Equal(model.Options.Scripts.Count, 1);
            Click(Descendants(Control<ItemsControl>(window, "ScriptList")).OfType<Button>().Single()); window.UpdateLayout();
            Equal(model.Options.Scripts.Count, 0);
            Equal(Control<TextBlock>(window, "NoScriptsText").Visibility, Visibility.Visible);
            Check(File.Exists(script), "Remove button deleted the original file");
        }
        finally { window.Close(); }
    }
    private static void TestWorkflowControls()
    {
        var ui = new FakeInteraction { Hold = true, Fail = true }; var window = new InstallerWindow(ui); window.Show();
        var model = (InstallerViewModel)window.DataContext;
        try
        {
            Toggle(window, "Launch", false);
            ui.Files = new[] { Path.Combine(output, "busy-script.js") }; File.WriteAllText(ui.Files[0], "void 0;");
            Click(Control<Button>(window, "AddScriptButton")); window.UpdateLayout();
            var remove = Descendants(Control<ItemsControl>(window, "ScriptList")).OfType<Button>().Single();
            Click(Control<Button>(window, "InstallButton"));
            PumpUntil(() => ui.Session != null && ui.Session.Started);
            Check(model.IsBusy, "Install did not enter busy state");
            foreach (string name in new[] { "InstallButton", "Launch", "OptionsEditor", "AddScriptButton" })
                Check(!Control<UIElement>(window, name).IsEnabled, "Busy control enabled: " + name);
            Click(Control<Button>(window, "InstallButton")); Equal(ui.Created, 1);
            Click(Control<Button>(window, "AddScriptButton")); Equal(ui.Selected, 1);
            Click(remove); Equal(model.Options.Scripts.Count, 1);
            Equal(ui.Config.CustomScriptPaths.Single(), ui.Files.Single());
            window.Close(); Check(window.IsVisible, "Close interrupted an active installation");
            Capture(window, "buttons-busy", 820, 680, 96);
            ui.Session.Gate.Set(); PumpUntil(() => model.State == "Error" && ui.Session.Disposed);
            Check(ui.Session.FailureRecorded, "Failure not recorded");
            Equal(Control<Button>(window, "InstallButton").Content, "重试安装");
            Check(Control<Expander>(window, "LogExpander").IsExpanded, "Failure logs not expanded");
            foreach (string name in new[] { "OpenLog", "OpenBackup" })
            {
                Equal(Control<Button>(window, name).Visibility, Visibility.Visible);
                Click(Control<Button>(window, name));
            }
            Equal(ui.Opened[0], ui.Session.LogPath); Equal(ui.Opened[1], ui.Session.BackupPath);
            var forum = Control<Hyperlink>(window, "ForumLink");
            var navigate = new RequestNavigateEventArgs(forum.NavigateUri, "") { RoutedEvent = Hyperlink.RequestNavigateEvent };
            forum.RaiseEvent(navigate); Check(navigate.Handled, "Forum event not handled"); Equal(ui.Opened.Last(), "https://bbs.gbtgame.me/");
            Control<ScrollViewer>(window, "SettingsScroll").ScrollToTop(); window.UpdateLayout();
            ui.OpenError = true; Click(Control<Button>(window, "OpenLog"));
            Check(Control<TextBox>(window, "Details").Text.Contains("无法打开："), "Open failure invisible"); ui.OpenError = false;
            Capture(window, "buttons-error", 820, 680, 96);
            var logBounds = Control<TextBox>(window, "Details").TransformToAncestor(Control<ScrollViewer>(window, "SettingsScroll")).TransformBounds(new Rect(Control<TextBox>(window, "Details").RenderSize));
            Check(logBounds.Top >= 0 && logBounds.Bottom <= Control<ScrollViewer>(window, "SettingsScroll").ViewportHeight + 1, "Expanded failure log was left outside the viewport");
            ui.Hold = true; ui.Fail = false;
            Click(Control<Button>(window, "InstallButton")); Equal(ui.Created, 2);
            Equal(Control<Button>(window, "OpenLog").Visibility, Visibility.Collapsed);
            Equal(Control<Button>(window, "OpenBackup").Visibility, Visibility.Collapsed);
            ui.Session.Gate.Set(); PumpUntil(() => model.State == "Success" && ui.Session.Disposed);
            Check(!ui.Session.Launched, "Launch opt-out was lost"); Equal(model.Stages[5].State, "已跳过");
            Check(!Control<UIElement>(window, "OptionsEditor").IsEnabled, "Success options remained editable");
            Capture(window, "buttons-success", 820, 680, 96);
            Click(Control<Button>(window, "InstallButton")); Check(!window.IsVisible, "Finish did not close the window"); Equal(ui.Created, 2);
        }
        finally { if (ui.Session != null && !ui.Session.Disposed) ui.Session.Gate.Set(); PumpUntil(() => !model.IsBusy); window.Close(); }
    }
    private static void TestRecoveryControls()
    {
        var ui = new FakeInteraction { Fail = true, LogError = true }; var window = new InstallerWindow(ui); window.Show();
        var model = (InstallerViewModel)window.DataContext;
        try
        {
            Toggle(window, "ActivateProBox", false); Toggle(window, "DisableUpdatesBox", false);
            Click(Control<Button>(window, "InstallButton")); Equal(model.State, "Error"); Equal(ui.Created, 0);
            Check(Control<TextBox>(window, "Details").Text.Length > 0, "Invalid options produced no guidance");
            Toggle(window, "DevToolsBox", true);
            Click(Control<Button>(window, "InstallButton")); PumpUntil(() => model.State == "Error" && ui.Session.Disposed);
            Check(model.Message.Contains("fixture install failure"), "Log failure replaced the actual installation error");
            Check(Control<TextBox>(window, "Details").Text.Contains("无法保存错误日志："), "Write failure not handled");
            Check(Control<TextBox>(window, "Details").Text.Contains("无法关闭日志："), "Dispose failure not handled");
            ui.Fail = false; ui.LogError = false;
            Click(Control<Button>(window, "InstallButton")); PumpUntil(() => model.State == "Success" && ui.Session.Disposed);
            Check(ui.Session.Launched, "Default launch choice was lost");
            Check(ui.Config.PatchTypes.SetEquals(new[] { EPatchType.DevToolsOnF12 }), "GUI choices lost at session boundary");
        }
        finally { PumpUntil(() => !model.IsBusy); window.Close(); }
    }
    private static void TestLongContent()
    {
        var window = new InstallerWindow(new FakeInteraction()); window.Show();
        try
        {
            var model = (InstallerViewModel)window.DataContext;
            for (int i = 0; i < 18; i++)
            {
                string script = Path.Combine(output, "很长的自定义脚本名称_" + new string('测', 70) + i + ".js");
                File.WriteAllText(script, "void 0;"); model.Options.AddScript(script);
            }
            Control<Expander>(window, "LogExpander").IsExpanded = true;
            Control<TextBox>(window, "Details").Text = string.Join("\n", Enumerable.Repeat("用于检查滚动和长日志显示的测试记录。", 100));
            Capture(window, "long-content-minimum", 820, 680, 96);
            var scripts = Control<ScrollViewer>(window, "ScriptScroll"); var settings = Control<ScrollViewer>(window, "SettingsScroll");
            Check(scripts.ScrollableHeight > 0 && settings.ScrollableHeight > 0, "Long content cannot scroll");
            scripts.ScrollToEnd(); settings.ScrollToEnd(); window.UpdateLayout();
            Check(scripts.VerticalOffset > 0 && settings.VerticalOffset > 0, "Hidden scrollbar also disabled scrolling");
            Check(Descendants(Control<ItemsControl>(window, "ScriptList")).OfType<Button>().All(b => b.ActualWidth >= 36), "Long filename squeezed remove controls");
            Capture(window, "long-content-scrolled", 820, 680, 96);
            window.WindowState = WindowState.Maximized; window.UpdateLayout();
            Check(Control<Button>(window, "InstallButton").ActualWidth >= 118, "Maximized action collapsed");
            window.WindowState = WindowState.Normal;
        }
        finally { window.Close(); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private sealed class FakeInteraction : IInstallerInteraction
    {
        public string[] Files = new string[0];
        public bool SelectionError, OpenError, Hold, Fail, LogError;
        public int Created, Selected;
        public PatchConfig Config;
        public FakeSession Session;
        public List<string> Opened = new List<string>();
        public string[] SelectScripts(Window owner) { Selected++; if (SelectionError) throw new IOException("dialog unavailable"); return Files; }
        public void Open(string path) { if (OpenError) throw new IOException("file association unavailable"); Opened.Add(path); }
        public IInstallerSession CreateSession(Action<int, string> report, PatchConfig config)
        {
            Created++; Config = config; Session = new FakeSession(report, Hold, Fail, LogError); return Session;
        }
    }
    private sealed class FakeSession : IInstallerSession
    {
        private readonly Action<int, string> report;
        private readonly bool fail, logError;
        public readonly ManualResetEventSlim Gate = new ManualResetEventSlim(false);
        public volatile bool Started;
        public bool Disposed, FailureRecorded, Launched;
        public string LogPath { get; private set; }
        public string BackupPath { get; private set; }
        public FakeSession(Action<int, string> report, bool hold, bool fail, bool logError)
        {
            this.report = report; this.fail = fail; this.logError = logError;
            BackupPath = Path.Combine(output, "fake-backup"); Directory.CreateDirectory(BackupPath);
            LogPath = Path.Combine(BackupPath, "fixture.log"); File.WriteAllText(LogPath, "fixture only");
            if (!hold) Gate.Set();
        }
        public void Install(bool launch)
        {
            Started = true; Launched = launch; report(3, "测试安装进度，不会改动真实应用。");
            if (!Gate.Wait(10000)) throw new TimeoutException("Fixture release timed out");
            if (fail) throw new IOException("fixture install failure");
            report(6, "测试安装完成。");
        }
        public void RecordFailure(Exception error) { FailureRecorded = true; if (logError) throw new IOException("fixture log write failure"); }
        public void Dispose() { Disposed = true; Gate.Dispose(); if (logError) throw new IOException("fixture log close failure"); }
    }

    private static void Capture(InstallerWindow window, string name, double width, double height, double dpi)
    {
        window.Width = width; window.Height = height; window.UpdateLayout();
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => {}));
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => {}));
        window.UpdateLayout();
        var message = (TextBlock)window.FindName("MessageText");
        Check(((FrameworkElement)window.FindName("StatusPanel")).ActualHeight < 145, "Status block is too large: " + name);
        Check(message.ActualHeight >= 15, "Status message lost its layout space: " + name);
        foreach (string controlName in new[] { "InstallButton", "Launch", "AuthorText", "StatusPanel", "SettingsScroll", "OpenLog", "OpenBackup" })
        {
            var control = (FrameworkElement)window.FindName(controlName);
            if (control.Visibility != Visibility.Visible) continue;
            var point = control.TranslatePoint(new Point(), window);
            Check(control.ActualWidth > 0 && control.ActualHeight > 0 && point.X >= 0 && point.Y >= 0 && point.X + control.ActualWidth <= window.ActualWidth && point.Y + control.ActualHeight <= window.ActualHeight, "Control clipped: " + name + "/" + controlName);
        }
        var visual = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * dpi / 96), (int)Math.Ceiling(visual.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, name + ".png"))) encoder.Save(stream);
    }

    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T actual, T expected) { Check(Equals(actual, expected), "Expected " + expected + ", got " + actual); }
    private static void Throws(Action action) { bool threw = false; try { action(); } catch (InvalidOperationException) { threw = true; } Check(threw, "Expected operation failure"); }

    private sealed class FakeSteps : IInstallSteps
    {
        public List<string> Calls = new List<string>();
        public string FailAt;
        public bool Verified = true;
        private void Call(string name) { Calls.Add(name); if (name == FailAt) throw new InvalidOperationException("Injected " + name + " failure"); }
        public void Validate() { Call("validate"); }
        public void Backup() { Call("backup"); }
        public void Remove() { Call("remove"); }
        public void Install() { Call("install"); }
        public void Patch() { Call("patch"); }
        public bool Verify() { Call("verify"); return Verified; }
        public void Launch() { Call("launch"); }
    }
}
