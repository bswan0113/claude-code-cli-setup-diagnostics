# Claude Code Launcher

**English** · [한국어](README.ko.md) · [简体中文](README.zh-CN.md) · [日本語](README.ja.md)

1. **Install and repair the Claude Code CLI.** Install the CLI on first use or run eight diagnostic checks when it stops working. The launcher shows what it found and asks for approval before making changes.
2. **Manage Claude Desktop Code sessions in a GUI.** Browse conversations by project folder when their Claude Code history is saved on this PC. Select a session to open PowerShell in its working folder and continue the conversation in the CLI.

## Install and repair

Open **Setup and repair** at the top of the main window or at the bottom left of the sidebar. The quick check shows which `claude` command PowerShell finds in the selected folder, verifies the executable’s publisher and version, and checks sign-in status. Select **Run full 8-step diagnosis** and expand a step to see the evidence, likely cause and available actions.

Select a repair to see the files, processes or settings it would change. Choose **Apply this action** to proceed or cancel to return to the results. If the target changes after the scan, review the new result before approving it.

| Step | What the launcher checks | What you can do |
|---|---|---|
| 1. PC environment | Windows build and architecture, Windows PowerShell version and path, spaces and non-ASCII characters in the user profile path. | Check x64 compatibility and the PowerShell path. Spaces and non-ASCII characters are informational. |
| 2. Executable | Known install paths, publisher signature, version, `.new.*` and `.old.*` update backups, and saved versions. | Restore a verified backup or install or reinstall the official package with WinGet. If a running Claude process blocks replacement, check its PID before closing it. Closing it interrupts that session. |
| 3. PATH | The launcher’s current PATH, the user and system PATH saved by Windows, and the location of the CLI. | Add a verified installation folder to the user PATH. If already registered, refresh the launcher’s PATH for new PowerShell windows. |
| 4. Duplicate installs and command conflicts | Executables with the same name, the global npm package, WindowsApps aliases and the legacy `.claude\local` folder. | Reorder the user PATH so the working installation runs first. Removing a duplicate npm package and deleting legacy files are separate actions. Legacy cleanup deletes only the regular files listed for approval. |
| 5. PowerShell execution policy | Whether policy blocks the npm PowerShell script and whether an organization manages the policy. | Set `CurrentUser` to `RemoteSigned` when a user-level change can resolve the issue. For an organization policy, contact the administrator or use the native CLI. |
| 6. Authentication, certificates, Git and proxies | The scope of `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN` and `ANTHROPIC_BASE_URL`, the certificate file in `NODE_EXTRA_CA_CERTS`, the shell path in `CLAUDE_CODE_GIT_BASH_PATH`, Git and proxy settings. Credential values and proxy addresses stay hidden. | Check what your workplace or course requires. Remove obsolete user variables individually or point Git Bash to a detected shell. Ask your administrator about system variables and managed settings. |
| 7. Network | Connections to the download and update server, API and sign-in service, plus the contents of the download response. | Use the failing endpoint and error to check certificates, proxy, firewall or connectivity. An HTTP response from the API confirms reachability. The quick check verifies sign-in separately. |
| 8. Update cleanup | Leftover `claude.exe.old.*`, `.new.*` and `.broken.*` files, the `.claude\downloads` cache and the user’s automatic update setting. | Review leftover files and the download cache separately before deleting them. Cleanup keeps the newest old backup. If a damaged native update leads you to disable automatic updates, install future updates manually. |

**If `~/.local/bin/claude.exe` disappears**, the quick check may report traces of a previous install. Open step 2 of the full diagnosis. A verified backup can restore the file to its original path. WinGet can reinstall the CLI in its own package directory. If another verified installation is available, the launcher checks its PATH.

The network check contacts `downloads.claude.ai`, `api.anthropic.com` and `claude.ai`. If your organization uses a proxy or a private certificate authority, review the results with your network administrator. [Anthropic network configuration](https://code.claude.com/docs/en/network-config)

### Quick check results

- Installing Claude Code uses the `winget` source and the portable `Anthropic.ClaudeCode` package for the current Windows user. If WinGet asks you to accept its terms, run the displayed command in PowerShell.
- If sign-in is required, run `claude auth login` in PowerShell and check again. The launcher distinguishes failure to start from an error after startup and shows the exit code when available.
- If you opened setup while launching a conversation, a successful quick check after repair lets you return to the original folder and session.

| Symptom | Next step |
|---|---|
| The working folder cannot be opened | Check whether it was moved or deleted. Use **Browse folders** to select its current location. |
| PowerShell finds another installation | Run `Get-Command claude -All` in that folder to inspect command order. The launcher checks the native executable’s Authenticode signature and publisher (`Anthropic, PBC`) before requesting its version. |
| WinGet installation stalls | Check for another running WinGet job. Choose **Check again** if installation exceeds five minutes or the CLI is still missing afterward. If WinGet cannot start, open the official Windows installation help from the launcher. |
| The diagnostic PowerShell process stops responding | Each quick PowerShell probe waits up to 25 seconds. Read the error and exit code in the dialog, then open PowerShell in the affected folder. |

Quick installation runs the following command. Reinstalling the official package from the full diagnosis changes `--no-upgrade` to `--force`.

```powershell
winget install --id Anthropic.ClaudeCode --exact --source winget --scope user --installer-type portable --no-upgrade --disable-interactivity
```

## Manage sessions

1. Open `ClaudeLauncher.exe` on Windows x64. The executable includes the .NET desktop runtime.
2. Under **Running now**, choose **Switch** to return to an open terminal. **Recently run** lists up to five sessions.
3. Expand a project under **Folders and sessions** or search by session name. Selecting a session runs `claude --resume <session-id>` in its folder. To begin a new conversation, choose **Start a new session** or enter another folder path.
4. If a folder’s history cannot be read, choose **Open this folder in the CLI picker** to run `claude -r` in PowerShell. Right-click the tree to specify a folder yourself.

Session titles and folders come from JSONL files under `projects` in the Claude configuration directory. For a session with no launcher run history, the launcher uses the file’s last modification time to sort it by recency.

## Appearance

Dock the window to the left or right edge to use it as a sidebar. In **Appearance**, choose dark mode, adjust text size (85–125%) or change window opacity (60–100%). The main window and sidebar share these settings across launches. The interface supports English, Korean, Simplified Chinese and Japanese.

## Build from source

On Windows with the .NET 10 SDK installed, run this command from the repository root.

```powershell
dotnet publish src/ClaudeLauncher.csproj -c Release -r win-x64 -p:PublishProfile=Windows-x64
```

The executable is written to `artifacts/publish/win-x64/ClaudeLauncher.exe`.

[Workflow and diagnostic decisions (Korean)](docs/workflow.md) · [Development plan and acceptance criteria (Korean)](docs/development-plan.md)
