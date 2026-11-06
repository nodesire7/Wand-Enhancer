# Installer interface

Scope: `Fork/Installer/InstallerWindow.xaml`, Windows desktop, operate mode.

The installer compiles the original WandEnhancer Style/ColorScheme.xaml and Style/Styles.xaml into its resources. Buttons, check boxes and combo boxes use those styles, preserving the black/zinc/white palette and three-pixel corners. The user requires complete pages without scrolling. Settings and script/backup/log views disable scrolling and use paging for unbounded content. The GBTGAME sidebar does not repeat the native title-bar logo. Native window management and dark title bar remain.

The status block is a compact automatic-height strip, capped by validation below 145 logical pixels for tested long-error states. The main area contains the original strategy and patch choices plus custom script selection. An accordion with fixed headers switches between installation options, execution records and configuration backups; exactly one section stays open, with shared 40-DIP headers, borders, padding and equal body dimensions, while the Install action and configuration-removal notice remain fixed. Author A-FEI- and bbs.gbtgame.me stay in the sidebar footer.

Buttons, the launch check box and execution-record toggle use consistent focus outlines and hover/disabled feedback. The launch preference starts checked. During execution, duplicate activation and closing are prevented. Failure retains the current step, reason and retry action; success labels all completed stages and identifies a skipped launch. Status text remains visible at the minimum window size, even with logs expanded, and exposes its actual message to accessibility tools. Occasional mouse-driven section changes use a reversible 200ms ease-out height/fade/chevron transition. Keyboard input and disabled Windows client-area animations use direct state changes.

Validated states include default, busy, long failure, success, 900×860 minimum size, long script lists and logs, and 125%/150%/200% render scaling. Standalone-process smoke testing complements view construction to exercise WPF's asynchronous dispatcher startup.

The first focus target is the strategy picker. Keyboard focus uses palette-based outlines, primary-button hover preserves geometry, and opening execution records folds the options without scrolling the status or footer away. File-picker and log-writing errors remain in the window and do not interrupt recovery. UI regression tests inject only native interactions and installation sessions; production retains the actual file dialog, shell opener and installation engine.

Configuration backups always have a visible section header with a count. The section displays the root and selected full paths, list, restore/delete/open/refresh controls, empty state and local confirmation overlay. Confirmation preserves list geometry. Restore first saves current user configuration and rolls back on failure; legacy uninstall/startup/protocol snapshots are never replayed. Backup operations prevent concurrent installation and closing.

Header clicks A/B/C select that section and collapse both others. Re-clicking the selected header keeps it open. Script pages show two complete filenames, backups show one full record and wrapped paths, and logs split at Unicode text-element boundaries into bounded pages. Full configuration selections and saved log data remain intact. Page controls replace wheel scrolling; empty/first/last pages disable invalid navigation.

All three page controls share 26-DIP buttons, 11-DIP type, a 52-DIP minimum width and the same previous/next order and spacing.
