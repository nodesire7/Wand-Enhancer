using System;

namespace WandEnhancer.Fork
{
    internal static class ForkEntry
    {
        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Length > 0 && string.Equals(args[0], "--cli", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = Cli.Run(args);
                return;
            }
            Program.Main(args);
        }
    }
}
