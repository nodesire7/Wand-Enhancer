# Installer interface

Scope: `Fork/Installer/InstallerForm.cs`, Windows desktop, operate mode.

This companion utility inherits the existing enhancer's dark zinc palette (`WandEnhancer/Style/ColorScheme.xaml`) and GBTGAME identity. It uses native Windows buttons, progress bar, check box and read-only log with Microsoft YaHei UI for simplified Chinese. The task is a sequential installation, so the six numbered steps express actual execution order.

The first screen states target version, destructive effects and backup behavior before the Install button. The large title, six text steps and live progress establish hierarchy without decorative panels. White primary action on near-black background matches the existing app; muted text remains readable. Secondary actions open the actual log and backup after a run. Keyboard activation and focus use native Windows behavior. Layout uses DPI scaling, docking and a minimum window size.

During execution, Install and closing are disabled; status and logs remain visible. Failure exposes its reason and retry; success changes the action to Finish. The optional launch check box is checked initially. No marketing claims, generated imagery or motion are needed for this installation task.
