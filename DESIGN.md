# Installer interface

Scope: `Fork/Installer/InstallerWindow.xaml`, Windows desktop, operate mode.

The WPF utility preserves the enhancer's dark palette and GBTGAME identity. A slate sidebar separates six sequential installation steps from the main task. Mint indicates the active action and completed work; warm red identifies a failed step. Microsoft YaHei UI follows the Windows desktop environment. Native window chrome retains resizing and window management, with a dark title bar on supported Windows versions.

The main surface presents the fixed Wand version, current task, completed-step progress, launch preference and one Install action. Configuration removal and login effects remain visible before installation. Execution records expand on demand and automatically on failure. Secondary controls open actual saved logs and backups. Author A-FEI- and the clickable bbs.gbtgame.me address stay in the sidebar footer.

Buttons, the launch check box and execution-record toggle use consistent focus outlines and hover/disabled feedback. The launch preference starts checked. During execution, duplicate activation and closing are prevented. Failure retains the current step, reason and retry action; success labels all completed stages and identifies a skipped launch. Status text remains visible at the minimum window size, even with logs expanded, and exposes its actual message to accessibility tools. No decorative motion is needed.

Validated states include default, busy, long failure, success, 880×680 minimum size, and 150%/200% render scaling. Standalone-process smoke testing complements view construction to exercise WPF's asynchronous dispatcher startup.
