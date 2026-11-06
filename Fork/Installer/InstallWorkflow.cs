using System;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("WandInstaller.Tests")]

namespace WandEnhancer.Fork.Installer
{
    internal interface IInstallSteps
    {
        void Validate();
        void Backup();
        void Remove();
        void Install();
        void Patch();
        bool Verify();
        void Launch();
    }

    internal static class InstallWorkflow
    {
        public static void Run(IInstallSteps steps, bool launch, string version, Action<int, string> report)
        {
            report(0, "正在校验安装包与内置 CLI…");
            steps.Validate();
            report(1, "关闭 Wand 并备份配置与注册表…");
            steps.Backup();
            report(2, "卸载旧版并清理残留…");
            steps.Remove();
            report(3, "安装 Wand " + version + "，等待文件写入完成…");
            steps.Install();
            report(4, "正在应用修改并验证结果…");
            steps.Patch();
            if (!steps.Verify()) throw new InvalidOperationException("未确认修改成功，已停止启动 Wand。请查看日志后重试。");
            report(5, launch ? "修改完成，正在启动 Wand…" : "修改完成，已按选择跳过启动。");
            if (launch) steps.Launch();
            report(6, launch ? "安装完成，Wand 已启动。" : "安装完成，可以随时启动 Wand。");
        }
    }
}
