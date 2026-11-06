using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WandEnhancer.Fork.Installer;

internal static class InstallerTests
{
    private static int passed;
    private static string output;
    private static readonly string[] Order = { "validate", "backup", "remove", "install", "patch", "verify", "launch" };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--echo")
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
            Console.WriteLine(InstallEngine.Json.Serialize(args.Skip(1).ToArray())); return 0;
        }
        output = args[0];
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
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); }
    }

    private static void TestWindow()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new InstallerWindow();
        window.Show();
        try
        {
            var model = (InstallerViewModel)window.DataContext;
            Equal(((TextBlock)window.FindName("AuthorText")).Text, "作者  A-FEI-");
            Equal(((Run)window.FindName("ForumText")).Text, "bbs.gbtgame.me");
            Equal(((Hyperlink)window.FindName("ForumLink")).NavigateUri.AbsoluteUri, "https://bbs.gbtgame.me/");
            Capture(window, "ready", 960, 720, 96);
            model.Begin(); model.Report(3, "正在安装 Wand 12.68.0…"); window.UpdateLayout();
            Check(!((Button)window.FindName("InstallButton")).IsEnabled, "Busy install button enabled");
            Check(!((CheckBox)window.FindName("Launch")).IsEnabled, "Busy launch checkbox enabled");
            window.Close(); Check(window.IsVisible, "Busy window closed");
            Capture(window, "busy", 960, 720, 96);
            model.Fail("测试错误：无法写入目标目录。请检查磁盘空间和文件权限，查看日志后重试。\n" + new string('路', 400));
            Check(((Button)window.FindName("InstallButton")).IsEnabled, "Retry unavailable");
            ((Expander)window.FindName("LogExpander")).IsExpanded = true;
            Capture(window, "error-minimum", 880, 680, 96);
            model.Begin(); model.Succeed(true);
            ((Expander)window.FindName("LogExpander")).IsExpanded = false;
            Capture(window, "success", 960, 720, 96);
            Capture(window, "dpi-150", 960, 720, 144);
            Capture(window, "dpi-200", 960, 720, 192);
        }
        finally { window.Close(); app.Shutdown(); }
    }

    private static void Capture(InstallerWindow window, string name, double width, double height, double dpi)
    {
        window.Width = width; window.Height = height; window.UpdateLayout();
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => {}));
        window.UpdateLayout();
        var message = (TextBlock)window.FindName("MessageText");
        Check(message.ActualHeight >= 15, "Status message lost its layout space: " + name);
        foreach (string controlName in new[] { "InstallButton", "Launch", "AuthorText" })
        {
            var control = (FrameworkElement)window.FindName(controlName);
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
