# Claude Code Launcher

**English** · [한국어](README.ko.md) · [简体中文](README.zh-CN.md) · [日本語](README.ja.md)

This Windows launcher helps you install or repair the Claude Code CLI and pick up where you left off in a previous session.

1. **Install and repair the Claude Code CLI.** Install the CLI on first use or run eight diagnostic checks when it stops working. The launcher shows what it found and asks for approval before making changes.
2. **Find and resume local Claude Code sessions.** Browse your Claude Code history by project folder in the launcher. Select a session to open PowerShell in its working folder and pick up where you left off in the CLI.

## Install and repair

Open **Setup and repair** at the top of the main window or at the bottom left of the sidebar. The scan starts automatically. The result is **Installation needed**, **Issue found** or **No issues found**. Issues are marked in red.

If the launcher can fix the problem, it explains what happened, recommends one action and asks whether to proceed. Choose **Yes** to run that action or **No** to leave things as they are. If you need to sign in or contact an administrator, the result tells you what to do. Expand **Full diagnostic results** to see all eight checks and their evidence.

| Step | What the launcher checks | What you can do |
|---|---|---|
| 1. PC environment | Windows build and architecture, Windows PowerShell version and path, spaces and non-ASCII characters in the user profile path. | Check x64 compatibility and the PowerShell path. Spaces and non-ASCII characters are informational. |
| 2. Executable | Known install paths, publisher signature, version, `.new.*` and `.old.*` update backups, and saved versions. | Restore a verified backup first. If no usable backup exists, the launcher offers the official PowerShell installer. If a running Claude process blocks replacement, check its PID before closing it. Closing it interrupts that session. |
| 3. PATH | The launcher’s current PATH, the user and system PATH saved by Windows, and the location of the CLI. | Add a verified installation folder to the user PATH. If already registered, refresh the launcher’s PATH for new PowerShell windows. |
| 4. Duplicate installs and command conflicts | Executables with the same name, the global npm package, WindowsApps aliases and the legacy `.claude\local` folder. | Reorder the user PATH so the working installation runs first. Removing a duplicate npm package and deleting legacy files are separate actions. Legacy cleanup deletes only the regular files listed for approval. |
| 5. PowerShell execution policy | Whether policy blocks the npm PowerShell script and whether an organization manages the policy. | Set `CurrentUser` to `RemoteSigned` when a user-level change can resolve the issue. For an organization policy, contact the administrator or use the native CLI. |
| 6. Authentication, certificates, Git and proxies | The scope of `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN` and `ANTHROPIC_BASE_URL`, the certificate file in `NODE_EXTRA_CA_CERTS`, the shell path in `CLAUDE_CODE_GIT_BASH_PATH`, Git and proxy settings. Credential values and proxy addresses stay hidden. | Check what your workplace or course requires. Remove obsolete user variables individually or point Git Bash to a detected shell. Ask your administrator about system variables and managed settings. |
| 7. Network | Connections to the download and update server, API and sign-in service, plus the contents of the download response. | Use the failing endpoint and error to check certificates, proxy, firewall or connectivity. An HTTP response from the API confirms reachability. The quick check verifies sign-in separately. |
| 8. Update cleanup | Leftover `claude.exe.old.*`, `.new.*` and `.broken.*` files, the `.claude\downloads` cache and the user’s automatic update setting. | Review leftover files and the download cache separately before deleting them. Cleanup keeps the newest old backup. If a damaged native update leads you to disable automatic updates, install future updates manually. |

**If `~/.local/bin/claude.exe` disappears**, the launcher looks for another working installation and checks its PATH if it finds one. If the executable is missing and a verified backup is available for restoration, the launcher recommends one backup and asks whether to restore it to the original path. If there is no usable backup, it offers to reinstall the CLI with Anthropic’s PowerShell installer.

The network check contacts `downloads.claude.ai`, `api.anthropic.com` and `claude.ai`. If your organization uses a proxy or a private certificate authority, review the results with your network administrator. [Anthropic network configuration](https://code.claude.com/docs/en/network-config)

### Installation and sign-in

- Before you choose **Yes**, the launcher explains that it will use Anthropic’s PowerShell installer first and when it may switch to WinGet. Your approval covers that installation attempt, including the fallback.
- If sign-in is required, run `claude auth login` in PowerShell and check again. The launcher distinguishes failure to start from an error after startup and shows the exit code when available.
- If you opened setup while launching a conversation, a successful quick check after repair lets you return to the original folder and session.

| Symptom | Next step |
|---|---|
| The working folder cannot be opened | Check whether it was moved or deleted. Use **Browse folders** to select its current location. |
| PowerShell finds another installation | Run `Get-Command claude -All` in that folder to inspect command order. The launcher checks the native executable’s Authenticode signature and publisher (`Anthropic, PBC`) before requesting its version. |
| The installer seems idle | The download may run without printing anything. The launcher watches both installer output and changes to download files. Only after five consecutive minutes with neither signal, and after safely stopping the original installer, can it try WinGet. An installer error, cancellation or failed verification ends the attempt without switching to WinGet. |
| The diagnostic PowerShell process stops responding | Each quick PowerShell probe waits up to 25 seconds. Read the error and exit code in the dialog, then open PowerShell in the affected folder. |

The launcher runs this official Windows installation command in PowerShell when you approve a new installation or reinstallation. [Anthropic’s installation guide](https://code.claude.com/docs/en/setup#install-claude-code)

```powershell
irm https://claude.ai/install.ps1 | iex
```

## Manage sessions

1. Open `ClaudeLauncher.exe` on Windows x64. The executable includes the .NET desktop runtime.
2. Under **Running now**, choose **Switch** to return to an open terminal. **Recently run** lists up to five sessions.
3. Expand a project under **Folders and sessions** or search by session name. Selecting a session runs `claude --resume <session-id>` in its folder. To begin a new conversation, choose **Start a new session** or enter another folder path.
4. If a folder’s history cannot be read, choose **Open this folder in the CLI picker** to run `claude -r` in PowerShell. Right-click the tree to specify a folder yourself.

Session titles and folders come from JSONL files under `projects` in the Claude configuration directory. For a session with no launcher run history, the launcher uses the file’s last modification time to sort it by recency.

The sort menu beside **Folders and sessions** orders folders by last use, name or full path in either direction. The default is newest first. Your selection is saved for the next launch. In the sidebar, each folder shows its path on a second line. Hover over a shortened path to see it in full.

## Appearance

Dock the window to the left or right edge to use it as a sidebar. In **Appearance**, choose dark mode, adjust text size (85–125%) or change window opacity (60–100%). The main window and sidebar share these settings across launches. The interface supports English, Korean, Simplified Chinese and Japanese.

## Build from source

On Windows with the .NET 10 SDK installed, run this command from the repository root.

```powershell
dotnet publish src/ClaudeLauncher.csproj -c Release -r win-x64 -p:PublishProfile=Windows-x64
```

The executable is written to `artifacts/publish/win-x64/ClaudeLauncher.exe`.

[Workflow and diagnostic decisions (Korean)](docs/workflow.md) · [Development plan and acceptance criteria (Korean)](docs/development-plan.md)
