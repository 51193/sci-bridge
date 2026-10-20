# SciBridge — 机构服务器只读数据桥

给 Codex（或其它本机 agent）用的**只读**科研数据通道。用户双击一个自包含 exe，
填入服务器地址、端口、账号、密码和可读根目录，程序在本机回环上开一个只读 HTTP
接口，并把一段"交接文字"直接送到剪贴板。把这段文字粘进 Codex，就可以开始读数据。

**目标平台：Windows 11 x64。交付物是单个自包含 exe，除 Windows 自身组件外无任何依赖。**

---

## 快速开始（给使用者）

1. 把 `SciBridge.exe` 放到一个固定文件夹。
2. 双击，填服务器信息，点"连接"。
3. 交接文字会自动复制；粘进 Codex 即可。文字里有本机 HTTP 地址和短期令牌。

面向无编程背景用户的完整说明见 [`dist/使用说明.txt`](dist/使用说明.txt)。

---

## 为什么 agent 只走 HTTP

Codex 的 Windows 沙箱（`workspace-write`）默认只允许读/执行固定系统目录和
workspace，**不允许读/执行用户目录里的 exe，也不允许读 `%LOCALAPPDATA%` 里的
会话文件**。早期版本的 CLI 路线因此会在沙箱里失败：

- `SciBridge.exe` 放在下载/桌面/`D:\` → 沙箱报 `Access denied`；
- 即使 exe 能跑，`session.json` 在用户目录 → CLI 报"未找到连接信息"；
- 命名管道跨用户 → 回落也失败。

HTTP 路线不需要执行 exe、不需要读文件，只需要 `curl.exe`（系统自带）和
`127.0.0.1` 回环。因此本版本把 agent 访问面收敛为**单一 HTTP 接口**：

- 交接文字直接给出 `http://127.0.0.1:<port>/<token>/`；
- 不写 `session.json`，没有命名管道，没有面向 agent 的 CLI；
- `SciBridge.exe` 只负责 GUI 和可选的自检，不再被 Codex 调用。

Codex 的沙箱仍可能禁止子进程访问回环网络（表现为 `EACCES` / 连接被拒）。
交接文字和 skill 都会提示 agent：**向用户申请“允许本机网络访问”**，不要关闭防火墙。

---

## 与参考包（InstitutionReadOnlySFTP）的区别

| 参考包的问题 | 本项目的做法 |
| --- | --- |
| 需要预装 Python 3.10+（含 Tcl/Tk、pip、venv） | 自包含单文件 exe，**不需要 Python** |
| 安装期必须联网访问 PyPI | **运行期完全不需要联网**（除连接服务器本身） |
| venv + 原生 wheel 易碎、icacls 改 ACL | 无安装过程，无 ACL 改动 |
| agent 必须自己"猜"安装目录 | 交接文字直接给本机 HTTP 地址，不需要找 exe |
| 要求 agent 读 `session.json` 却不许打印令牌 | 不落盘会话；令牌直接在交接文字里，但只对本机只读有效 |
| GUI 不能改 IP / 账号（只有端口和密码可改） | 地址、端口、账号、密码、**可读根目录**全部可填 |
| 主机公钥写死在某台服务器 | TOFU + 指纹确认 + 本地固定；变化时硬停并给指引 |
| 没有访问范围限制 | 三层只读保证 + 可读根目录约束 + 符号链接默认不跟随 |
| ZIP + 安装器 + 快捷方式 + 5 页 PDF | 一个 exe + 一页说明 |

---

## 架构

```
src/
├─ SciBridge.Core/          跨平台核心库（net10.0），不含任何 Windows 专属代码
│   ├─ SshConnector.cs      SSH 连接、TOFU 主机公钥、键盘交互（2FA）
│   ├─ RemoteFs.cs          只读文件操作 + 根目录约束 + find 上限
│   ├─ PathGuard.cs         路径规范化与越界拒绝
│   ├─ BridgeService.cs     五个操作的唯一分发点（health 会真实探测 root）
│   ├─ HttpBridgeServer.cs  手写最小 HTTP/1.1，只绑 127.0.0.1
│   ├─ BridgeHost.cs        组装 HTTP 服务 + 内存会话
│   ├─ BridgeClient.cs      内部 HTTP 客户端（自检用，不面向 agent）
│   ├─ HandoffText.cs       生成交接文字（URL + 令牌 + curl 示例）
│   ├─ TextRenderer.cs      文本输出（ls -l 风格）
│   ├─ HostKeyStore.cs      指纹固定存储
│   └─ Settings.cs          非机密偏好 + DPAPI blob 占位
├─ SciBridge.App/           Windows GUI 外壳（net10.0-windows，WinForms）
│   ├─ Program.cs           GUI / 诊断入口；不再提供 ls/cat CLI
│   ├─ MainForm.cs          主窗口
│   ├─ Dialogs.cs           主机公钥确认、2FA
│   ├─ SecretStore.cs       DPAPI 加解密（"记住密码"）
│   ├─ SelfTest.cs          --self-test：真实连接 + 真实 HTTP 只读检查
│   └─ UiDump.cs            --dump-ui：导出真实版面几何用于自动验证
└─ SciBridge.TestHost/      Linux 下的跨平台主机，供自动化测试复用同一套 Core

tools/testserver/          交付给用户的“本机测试服务器”（Go，零依赖静态 exe）
tests/
├─ sftp_server.py           基于 paramiko 的真实 SSH/SFTP 服务端（只读、记录所有请求）
├─ bridge_client.py         测试共享的 HTTP 客户端/Bridge 进程辅助
├─ e2e.py                   54 项端到端断言（真实 SSH 服务端 + 真实 HTTP 接口）
├─ go_server_check.py       20 项断言：验证 Go 测试服务器是合格的替身
├─ wine_check.py            8 项：用 Wine 验证交付的 Windows exe（自检/HTTP 路径）
└─ wine_selftest.py         14 项断言：Windows 测试服务器 + Windows SciBridge 全链路

skills/sci-data-bridge/     给 Codex 的 skill（含 references 与 agents/openai.yaml）
tools/make_icon.py          生成 app.ico（纯标准库，附 ASCII 预览）
```

设计上的关键选择：

- **HTTP 是 agent 的唯一接口**。没有 CLI、命名管道或会话文件需要沙箱去读/执行。
- **HTTP 服务手写在 `TcpListener` 上**，不用 `HttpListener`：后者在 Windows 上需要
  管理员 URL ACL 预留，会直接毁掉"双击即用"。
- **令牌放进交接文字**。它是本机回环、只读、会话级的能力凭证；关闭窗口即失效。
  代价是令牌会进入对话上下文，因此交接文字明确要求不要外发、不要截图。
- **不落盘 session**。GUI 只在内存里保存 URL/令牌；没有 agent 可读的会话文件。
- **exe 是控制台子系统（不是 WinExe）**，`--self-test` / `--version` 仍能被
  cmd/PowerShell 等待并捕获输出。GUI 模式启动时调用 `FreeConsole()`：双击产生的
  控制台/pwsh 窗口会被关闭；从已有 cmd/PowerShell 启动时只脱离控制台，不会把
  用户自己的终端窗口隐藏或关掉。
- **不开单文件压缩**：压缩过的 .NET 单文件很像"加壳程序"，容易被杀软误报，
  而目标用户没有能力排查误报。代价只是体积（约 110 MB）。

---

## 只读的三层保证

1. **协议层**：`BridgeService` 只分发 `health / list / stat / read / find`。
   代码中不存在任何写入、删除、改名、chmod 或远程命令执行的调用点。
   `health` 额外做一次 `stat(".")`，所以它能反映 SSH/root 是否真的可用。
2. **调用层**：`RemoteFs` 只使用 SSH.NET 的 `ListDirectory` / `GetAttributes` /
   `OpenRead`，没有任何写路径。
3. **范围层**：所有路径先经 `PathGuard` 规范化，限制在用户设定的"可读根目录"内；
   `..` 越界、盘符与前缀一律拒绝。由于 SSH.NET 的 `GetAttributes` 实际发送的是
   **LSTAT**，属性不会跟随符号链接，因此"检查但不跟随符号链接"的策略可以被可靠执行；
   中间路径上的符号链接默认直接拒绝。

---

## 构建

需要 .NET SDK 10（本仓库在 Ubuntu 上用交叉编译验证过）。

```bash
source env.sh                      # 把 NuGet 缓存限制在仓库内，便于沙箱环境
dotnet publish src/SciBridge.App/SciBridge.App.csproj -c Release
# 产物：src/SciBridge.App/bin/Release/net10.0-windows/win-x64/publish/SciBridge.exe
```

`env.sh` 只是把 `NUGET_PACKAGES` 等指向仓库内部；在普通机器上可以不用。

## 发布

交付二进制不进入 git 历史（`dist/SciBridge.exe` 和根目录的 `*.zip` 已在 `.gitignore` 中），
由 GitHub Actions 构建并附加到 Release：

- 在 Actions 页面手动运行 **Release**，填入 tag（如 `v1.0.0`）；或
- 推送 tag 触发：`git tag v1.0.0 && git push origin v1.0.0`

工作流会交叉编译 `SciBridge.exe`、构建 Go 测试服务器，生成
`SciBridge-<版本>-win11.zip`、同名独立 exe 和 `SHA256SUMS.txt`，
并创建或更新对应的 GitHub Release。

## 测试

```bash
python3 -m venv .venv && .venv/bin/pip install paramiko pyyaml

.venv/bin/python tests/e2e.py             # 54 项：真实 SSH 服务端 + 真实 HTTP
.venv/bin/python tests/go_server_check.py # 20 项：Go 测试服务器是否合格
.venv/bin/python tests/wine_check.py      # 8 项：交付的 Windows exe（自检/HTTP 路径）
.venv/bin/python tests/wine_selftest.py   # 14 项：Windows 测试服务器 + Windows 自检
```

构建本机测试服务器（需要 Go；依赖已 vendored 在 `third_party/`，构建时不需要联网）：

```bash
export PATH=$PWD/.build/go/bin:$PATH GOFLAGS=-mod=mod GOPROXY=off GOSUMDB=off
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build -C tools/testserver \
  -ldflags="-s -w" -o ../../.build/SciBridgeTestServer.exe ./...
```

`tests/e2e.py` 覆盖：认证与指纹固定、错误密码只尝试一次、health 的真实探测、
文本与二进制读取的字节一致性、分段读取、Unicode 路径、find 深度与上限、
越界与符号链接逃逸拒绝、HTTP 令牌/GET-only/浏览器来源拒绝、交接文字含 URL/令牌、
无写入审计，以及"桥关闭后 HTTP 连接失败"。

---

## 安全模型（如实说明）

- 密码只在用户本机输入，**不会**进入 agent 的对话上下文；
  默认不落盘；勾选"记住密码"后用 Windows DPAPI（当前用户作用域）加密保存。
- 令牌是**本机回环、只读、会话级**的能力凭证，进程退出即失效。
- 令牌现在会出现在交接文字里（否则 HTTP-only 无法工作）；请勿外发、截图或写进仓库。
- 接口只绑定 `127.0.0.1`，不触发 Windows 防火墙弹窗，局域网无法访问。
- 带 `Origin`/`Referer`/`Sec-Fetch-*` 的请求一律 403，防止网页借浏览器探测。
- 不写 `session.json`；没有命名管道；agent 不需要读取任何本机文件。
- 已知限制：
  - 勾选"允许跟随符号链接"后，符号链接**可以**指向可读根目录之外——这是用户显式选择的放宽。
  - 服务器端账号自身的权限不受本工具限制；本工具只是不提供任何写操作。
  - 不做代码签名，首次运行可能出现 SmartScreen 提示（说明文档中已写明处理方式）。
  - agent 必须运行在同一台 Windows 电脑上；不支持跨机或云端访问，也不提供隧道。
  - Codex 沙箱若禁止回环网络，需要用户授予本机网络访问权限；不要用关闭防火墙来绕过。

## 未能在此环境自动验证的部分

开发环境是 Ubuntu，因此以下内容只做了间接验证，需要在真实 Windows 11 上确认：

- SmartScreen / 杀毒软件的实际表现；
- DPAPI"记住密码"的读写往返（代码路径已实现，但只在 Windows 上有效）；
- 服务器要求 2FA 时键盘交互对话框的实际交互体验。
  密码认证路径已端到端验证；键盘交互因 paramiko 服务端限制未做自动化覆盖。
- Codex 各版本的 Windows 沙箱是否放行本机回环网络；HTTP-only 已把失败模式
  收敛为"申请本机联网权限"，但仍建议在目标机器上用交接文字里的 `health` 做一次确认。

GUI 版面不是靠肉眼确认的：`SciBridge.exe --dump-ui` 会在 Windows 运行时导出每个控件的
真实坐标与尺寸，已验证所有控件都在客户区内、无零尺寸、无文字裁切。
