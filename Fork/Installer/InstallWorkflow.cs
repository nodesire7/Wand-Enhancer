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
        void SaveState();
        void Launch();
    }

    internal interface IExistingInstallSteps
    {
        void ValidateExisting();
        void PrepareExisting();
        void Patch();
        bool Verify();
        void SaveState();
        void Launch();
    }

    internal static class ExistingInstallWorkflow
    {
        public static void Run(IExistingInstallSteps steps, bool launch, Action<int, string> report)
        {
            report(0, "检查已安装的 Wand 版本与文件…"); steps.ValidateExisting();
            report(1, "恢复所选增强设置…");
            report(2, "保留当前安装和用户配置，不执行卸载或重装。");
            report(3, "准备内置启动组件…"); steps.PrepareExisting();
            report(4, "应用并验证所选功能…"); steps.Patch();
            if (!steps.Verify()) throw new InvalidOperationException("未确认功能应用成功，已停止启动；不会重新安装 Wand。");
            steps.SaveState();
            report(5, launch ? "正在启动 Wand…" : "功能已应用，已按选择跳过启动。");
            if (launch) steps.Launch();
            report(6, launch ? "Wand 已启动。" : "功能已应用，可以再次使用启动器启动。");
        }
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
            steps.SaveState();
            report(5, launch ? "修改完成，正在启动 Wand…" : "修改完成，已按选择跳过启动。");
            if (launch) steps.Launch();
            report(6, launch ? "安装完成，Wand 已启动。" : "安装完成，可以随时启动 Wand。");
        }
    }
}
