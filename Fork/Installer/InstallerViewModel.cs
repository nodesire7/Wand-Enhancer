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
        private string state = "Ready";
        private int progress;
        private string message = "准备好开始安装";
        public string State { get => state; private set { state = value; Changed(nameof(State)); Changed(nameof(IsBusy)); Changed(nameof(ActionText)); Changed(nameof(Heading)); } }
        public bool IsBusy => State == "Running";
        public string ActionText => State == "Success" ? "完成" : State == "Error" ? "重试安装" : IsBusy ? "正在安装…" : "安装";
        public string Heading => State == "Success" ? "一切就绪，祝游玩愉快" : State == "Error" ? "安装已停止" : IsBusy ? "正在为你准备 Wand" : "安装 Wand";
        public int Progress { get => progress; private set { progress = value; Changed(nameof(Progress)); Changed(nameof(ProgressLabel)); } }
        public string ProgressLabel => Progress + " / 6 步完成";
        public string Message { get => message; private set { message = value; Changed(nameof(Message)); } }

        public InstallerViewModel(string version)
        {
            Version = version;
            string[] titles = { "校验文件", "备份配置", "清理旧版", "安装 Wand", "应用修改", "启动应用" };
            string[] details = { "检查版本与完整性", "保留原有配置备份", "卸载并清理默认目录", "安装 " + version, "调用 CLI 并验证结果", "按你的选择启动" };
            for (int i = 0; i < titles.Length; i++) Stages.Add(new InstallStage { Number = i + 1, Title = titles[i], Detail = details[i] });
        }

        public bool Begin()
        {
            if (IsBusy || State == "Success") return false;
            State = "Running";
            Progress = 0;
            Message = "正在校验安装文件…";
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
            Message = launched ? "Wand 已启动。原配置备份与本次日志均已保存。" : "安装与修改已完成，可以随时手动启动 Wand。";
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
