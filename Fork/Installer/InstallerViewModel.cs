using System;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace WandEnhancer.Fork.Installer
{
    internal class NotifyModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    internal sealed class InstallStage : NotifyModel
    {
        public int Number { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
        private string state = "等待";
        public string State { get => state; set { state = value; Changed(nameof(State)); } }
    }

    internal sealed class InstallerViewModel : NotifyModel
    {
        public ObservableCollection<InstallStage> Stages { get; } = new ObservableCollection<InstallStage>();
        public string Version { get; }
        public bool UseExisting { get; private set; }
        public bool CanRun { get; private set; } = true;
        public string ShellName => UseExisting ? "Wand 启动器" : "Wand 安装助手";
        public PatchOptionsViewModel Options { get; } = new PatchOptionsViewModel();
        private string state = "Ready";
        private int progress;
        private string message = "准备好开始安装";
        public string State { get => state; private set { state = value; Changed(nameof(State)); Changed(nameof(IsBusy)); Changed(nameof(ActionText)); Changed(nameof(Heading)); } }
        public bool IsBusy => State == "Running";
        public string ActionText => !CanRun ? "无法启动" : State == "Success" ? (UseExisting ? "关闭启动器" : "完成") : State == "Error" ? (UseExisting ? "重试启动" : "重试安装") : IsBusy ? (UseExisting ? "正在启动…" : "正在安装…") : UseExisting ? "启动 Wand" : "安装";
        public string Heading => State == "Success" ? (UseExisting ? "启动完成" : "安装完成") : State == "Error" ? (UseExisting ? "启动已停止" : "安装已停止") : IsBusy ? (UseExisting ? "正在启动 Wand" : "正在安装 Wand") : UseExisting ? "启动 Wand" : "安装 Wand";
        public int Progress { get => progress; private set { progress = value; Changed(nameof(Progress)); Changed(nameof(ProgressLabel)); } }
        public string ProgressLabel => Progress + " / 6 步完成";
        public string Message { get => message; private set { message = value; Changed(nameof(Message)); } }

        public InstallerViewModel(string version)
        {
            Version = version;
            BuildStages();
        }
        private void BuildStages()
        {
            Stages.Clear();
            string[] titles = UseExisting ? new[] { "检查安装", "恢复设置", "保留配置", "准备组件", "应用功能", "启动应用" } : new[] { "校验文件", "备份配置", "清理旧版", "安装 Wand", "应用修改", "启动应用" };
            string[] details = UseExisting ? new[] { "核对版本与完整性", "使用已保存的功能选择", "不卸载、不重新安装", "准备内置 CLI", "重新应用并验证功能", "按你的选择启动" } : new[] { "检查版本与完整性", "保留原有配置备份", "卸载并清理默认目录", "安装 " + Version, "调用 CLI 并验证结果", "按你的选择启动" };
            for (int i = 0; i < titles.Length; i++) Stages.Add(new InstallStage { Number = i + 1, Title = titles[i], Detail = details[i] });
        }
        public void Configure(LauncherSnapshot snapshot, bool restoreOptions = true)
        {
            if (IsBusy) return;
            UseExisting = snapshot.Exists; CanRun = snapshot.CanRun;
            if (restoreOptions && snapshot.Config != null) Options.Load(snapshot.Config);
            BuildStages(); Progress = 0;
            Message = snapshot.Error ?? snapshot.Message ?? (UseExisting ? "已安装 Wand，可直接启动。" : "准备好开始安装");
            State = CanRun ? "Ready" : "Error";
            if (!CanRun) Stages[0].State = "失败";
            Changed(nameof(UseExisting)); Changed(nameof(CanRun)); Changed(nameof(ShellName));
        }

        public bool Begin()
        {
            if (IsBusy || State == "Success" || !CanRun) return false;
            State = "Running";
            Progress = 0;
            Message = UseExisting ? "正在检查现有安装…" : "正在校验安装文件…";
            foreach (var stage in Stages) stage.State = "等待";
            Stages[0].State = "进行中";
            return true;
        }

        public void Report(int step, string text)
        {
            if (!IsBusy) return;
            Progress = Math.Max(0, Math.Min(step, 6));
            Message = text;
            for (int i = 0; i < Stages.Count; i++) Stages[i].State = i < Progress ? "已完成" : i == Progress ? "进行中" : "等待";
        }

        public void Succeed(bool launched)
        {
            if (!IsBusy) return;
            Progress = 6;
            foreach (var stage in Stages) stage.State = "已完成";
            if (!launched) Stages[5].State = "已跳过";
            Message = UseExisting ? (launched ? "功能已应用并启动 Wand。以后继续使用此启动器，无需重新安装。" : "功能已应用，安装与用户配置已保留。") : (launched ? "Wand 已启动，安装状态和功能选择已保存。以后使用此工具直接启动。" : "安装与修改已完成，安装状态和功能选择已保存。");
            State = "Success";
        }

        public void Fail(string text)
        {
            if (!IsBusy) return;
            Stages[Math.Min(Progress, 5)].State = "失败";
            Message = text;
            State = "Error";
        }
    }
}
