# Claude Code 启动器

[English](README.md) · [한국어](README.ko.md) · **简体中文** · [日本語](README.ja.md)

1. **安装和修复 Claude Code CLI。** 首次使用时可安装 CLI。如果无法启动，可以运行八步诊断，查看原因和修复方法。启动器会先列出要做的更改，等你确认后再执行。
2. **在图形界面中管理 Claude 桌面应用的 Code 会话。** 只要这台电脑保存了对应的 Claude Code 记录，就能按项目文件夹查找会话。选中会话后，启动器会在原工作目录打开 PowerShell，让你在 CLI 中继续对话。

## 安装和修复

点击主窗口顶部或侧边栏左下角的**安装和修复**。弹窗会先快速检查 PowerShell 在所选目录找到的 `claude` 命令、可执行文件的发布者签名、版本和登录状态。点击**运行八步完整诊断**，展开各步骤即可查看检查结果、判断依据、可能原因和可用的修复操作。

选择修复操作后，确认窗口会列出涉及的文件、进程或设置。点击**应用此操作**开始修复，或取消并返回结果页。如果目标在检查后发生变化，请查看新结果，再选择要执行的操作。

| 步骤 | 检查内容 | 处理方法 |
|---|---|---|
| 1. 电脑环境 | Windows 内部版本和体系结构、Windows PowerShell 的版本和路径，以及用户目录中的空格和非 ASCII 字符。 | 确认系统支持 x64 并检查 PowerShell 路径。用户目录中的空格或中文等字符仅作提示。 |
| 2. 可执行文件 | 已知安装位置、发布者签名、版本、更新留下的 `.new.*` 和 `.old.*` 备份，以及旧版本文件。 | 可从已验证的备份恢复，也可用 WinGet 安装或重装官方软件包。如果需要关闭 Claude 才能替换文件，请先核对进程 PID。结束进程会中断该会话。 |
| 3. PATH | 对比启动器当前使用的 PATH、Windows 保存的用户和系统 PATH，以及 CLI 的实际位置。 | 将已验证的安装目录添加到用户 PATH。如果已经添加，刷新启动器的 PATH，让新打开的 PowerShell 使用该路径。 |
| 4. 重复安装与命令冲突 | 同名可执行文件、npm 全局包、WindowsApps 别名和旧版安装目录 `.claude\local`。 | 调整用户 PATH 的顺序，让可用版本优先启动。卸载重复的 npm 包和清理旧文件需分别确认。清理旧版安装时，只会删除确认窗口列出的普通文件。 |
| 5. PowerShell 执行策略 | 当前策略是否阻止 npm 安装的 PowerShell 脚本，以及组织是否管理这些策略。 | 如果仅修改用户设置就能解决，可以将 `CurrentUser` 的执行策略设为 `RemoteSigned`。如果组织策略生效，请联系管理员或使用原生 CLI。 |
| 6. 身份验证、证书、Git 与代理 | 检查 `ANTHROPIC_API_KEY`、`ANTHROPIC_AUTH_TOKEN`、`ANTHROPIC_BASE_URL` 的作用域，`NODE_EXTRA_CA_CERTS` 指向的证书文件，`CLAUDE_CODE_GIT_BASH_PATH` 指向的程序，以及 Git 安装和代理设置。界面会隐藏凭据和代理地址的具体值。 | 先确认公司或课程对设置的要求。可以逐个删除多余的用户变量，也可以把失效的 Git Bash 路径改为检查时找到的路径。系统变量和组织管理的设置请向管理员确认。 |
| 7. 网络 | 分别连接下载和更新服务器、Claude API 与登录服务，并检查下载响应的内容。 | 根据出错的服务和错误信息检查证书、代理、防火墙或网络连接。收到 API 的 HTTP 响应说明服务器可达，登录状态由快速检查单独确认。 |
| 8. 更新清理 | 检查残留的 `claude.exe.old.*`、`.new.*`、`.broken.*` 文件、`.claude\downloads` 缓存和当前用户的自动更新设置。 | 分别核对残留文件和下载缓存的清单后再删除。清理时会保留最新的 old 备份。如果因原生安装更新损坏而关闭自动更新，今后需要手动更新。 |

**如果 `~/.local/bin/claude.exe` 丢失**，快速检查可能会提示发现旧的安装记录。请打开完整诊断的第 2 步。可以用已验证的备份将文件恢复到原路径，也可以用 WinGet 将 CLI 重装到其软件包目录。如果已找到其他可用且通过验证的安装，启动器会检查其 PATH。

网络检查会访问 `downloads.claude.ai`、`api.anthropic.com` 和 `claude.ai`。如果公司使用代理或内部 CA 证书，请和网络管理员一起核对结果。[Anthropic 网络配置说明](https://code.claude.com/docs/en/network-config)

### 常见快速检查结果

- 安装 Claude Code 时，启动器会从 `winget` 源为当前 Windows 用户安装 `Anthropic.ClaudeCode` 便携软件包。如果 WinGet 提示接受条款，请在 PowerShell 中运行界面显示的命令。
- 如果提示需要登录，请在 PowerShell 中运行 `claude auth login`，然后重新检查。启动器会区分 CLI 未能启动和启动后异常退出，并在能获取退出代码时显示该代码。
- 如果你是在打开会话时进入修复界面的，修复后的快速检查通过后，可以返回原来的目录和会话。

| 遇到的问题 | 下一步 |
|---|---|
| 无法打开工作目录 | 确认目录是否已移动或删除，用**浏览文件夹**选择当前位置。 |
| PowerShell 找到了其他安装版本 | 在同一目录运行 `Get-Command claude -All` 查看命令顺序。启动器会先验证原生可执行文件的 Authenticode 签名和发布者 `Anthropic, PBC`，再查询版本。 |
| WinGet 安装未完成 | 检查是否有其他 WinGet 任务正在运行。如果超过 5 分钟或安装结束后仍找不到 CLI，点击**重新检查**。如果 WinGet 本身无法启动，请从启动器打开官方帮助，查看 Windows 安装方法。 |
| 诊断用的 PowerShell 无响应 | 快速检查中的每次 PowerShell 检测最多等待 25 秒。查看弹窗中的错误和退出代码，然后在受影响的目录打开 PowerShell。 |

快速安装使用以下命令。从完整诊断中选择重装官方软件包时，启动器会将 `--no-upgrade` 换为 `--force`。

```powershell
winget install --id Anthropic.ClaudeCode --exact --source winget --scope user --installer-type portable --no-upgrade --disable-interactivity
```

## 管理会话

1. 在 Windows x64 上打开 `ClaudeLauncher.exe`。可执行文件已包含 .NET 桌面运行时。
2. 在**正在运行**中点击**切换**即可返回已打开的终端。**最近运行的会话**最多显示五个会话。
3. 在**文件夹与会话**中展开项目目录，或按会话名称搜索。选中会话后，启动器会在对应目录运行 `claude --resume <session-id>`。要开始新的对话，可以点击**开始新会话**，也可以输入其他目录的路径。
4. 如果某个目录的记录无法读取，点击**在 CLI 中选择此文件夹的会话**，即可在 PowerShell 中通过 `claude -r` 选择会话。右键点击目录树可手动指定文件夹。

会话标题和目录来自 Claude 配置目录下 `projects` 中的 JSONL 文件。如果启动器没有该会话的运行记录，就按文件最后修改时间排序。

## 外观设置

将窗口停靠在屏幕左侧或右侧即可作为侧边栏使用。在**外观设置**中可以启用深色模式、调整字号（85% 至 125%）和窗口不透明度（60% 至 100%）。主窗口和侧边栏共用这些设置，下次启动时会保留。界面支持英语、韩语、简体中文和日语。

## 从源码构建

在安装了 .NET 10 SDK 的 Windows 上，从仓库根目录运行以下命令。

```powershell
dotnet publish src/ClaudeLauncher.csproj -c Release -r win-x64 -p:PublishProfile=Windows-x64
```

生成的可执行文件位于 `artifacts/publish/win-x64/ClaudeLauncher.exe`。

[操作流程与诊断分支（韩语）](docs/workflow.md) · [开发计划与验收标准（韩语）](docs/development-plan.md)
