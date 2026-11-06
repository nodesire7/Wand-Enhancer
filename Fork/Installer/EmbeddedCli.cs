using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace WandEnhancer.Fork.Installer
{
    internal static class EmbeddedCli
    {
        public static int Run(string[] args)
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            string scratch = Path.Combine(Path.GetTempPath(), "GBTGAME-WandCli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                string exe = Path.Combine(scratch, "WandEnhancer.exe");
                using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("WandEnhancerPayload"))
                using (var output = File.Create(exe)) source.CopyTo(output);
                using (var process = new Process { StartInfo = new ProcessStartInfo(exe, Arguments.Join(args)) {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.CurrentDirectory,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
                } })
                {
                    process.Start();
                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    Task<string> error = process.StandardError.ReadToEndAsync();
                    process.WaitForExit();
                    Task.WaitAll(output, error);
                    Console.Write(output.Result);
                    if (!string.IsNullOrWhiteSpace(error.Result)) Console.Error.Write(error.Result);
                    return process.ExitCode;
                }
            }
            catch (Exception error)
            {
                Console.WriteLine(InstallEngine.Json.Serialize(new { schemaVersion = 1, success = false, code = 5, message = error.Message }));
                return 5;
            }
            finally
            {
                string prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
                if (Path.GetFullPath(scratch).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(scratch).StartsWith("GBTGAME-WandCli-"))
                    try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
