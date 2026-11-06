# Installer interface

Scope: `Fork/Installer/InstallerWindow.xaml`, Windows desktop, operate mode.

The installer compiles the original WandEnhancer Style/ColorScheme.xaml and Style/Styles.xaml into its resources. Buttons, check boxes and combo boxes use those styles, preserving the black/zinc/white palette and three-pixel corners. The logs and settings hide scrollbars while retaining wheel scrolling, matching the enhancer's log view. The GBTGAME sidebar does not repeat the native title-bar logo. Native window management and dark title bar remain.

The status block is a compact automatic-height strip, capped by validation below 145 logical pixels for tested long-error states. The main area contains the original strategy and patch choices plus custom script selection. A scrollable body holds settings and expandable execution records, while the Install action and configuration-removal notice remain fixed. Author A-FEI- and bbs.gbtgame.me stay in the sidebar footer.

Buttons, the launch check box and execution-record toggle use consistent focus outlines and hover/disabled feedback. The launch preference starts checked. During execution, duplicate activation and closing are prevented. Failure retains the current step, reason and retry action; success labels all completed stages and identifies a skipped launch. Status text remains visible at the minimum window size, even with logs expanded, and exposes its actual message to accessibility tools. No decorative motion is needed.

Validated states include default, busy, long failure, success, 880×680 minimum size, and 150%/200% render scaling. Standalone-process smoke testing complements view construction to exercise WPF's asynchronous dispatcher startup.
