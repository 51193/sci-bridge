# SciBridge — 机构服务器科研数据桥（默认只读）

给 Codex（或其它本机 agent）用的科研数据通道。默认**只读**；用户可以在连接前
按需开启“仅新建文件/目录”的写权限。程序在本机回环上开 HTTP 接口，并把一段
"交接文字"直接送到剪贴板，粘进 Codex 即可开始。

**目标平台：Windows 11 x64。交付物是单个自包含 exe，除 Windows 自身组件外无任何依赖。**

---

## 快速开始（给使用者）

1. 把 `SciBridge.exe` 放到一个固定文件夹。
2. 双击，填服务器信息，点"连接"。
3. 交接文字会自动复制；粘进 Codex 即可。文字里有本机 HTTP 地址和短期令牌。

默认只读。需要让 agent 新建文件或目录时，在连接前勾选“允许写入”及对应子项；
本版本**不能覆盖、重命名或删除**服务器上的任何内容。

程序不附带 skill、独立说明文档或测试服务器；使用说明就在窗口和交接文字里。

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
- `SciBridge.exe` 只负责 GUI 和可选的自检，不再被 Codex 调用；
- 写能力同样只走这个 HTTP 接口，且仅限新建文件/目录。

Codex 的沙箱仍可能禁止子进程访问回环网络（表现为 `EACCES` / 连接被拒）。
交接文字会提示 agent：**向用户申请“允许本机网络访问”**，不要关闭防火墙。

---

## 与参考包（InstitutionReadOnlySFTP）的区别

| 参考包的问题 | 本项目的做法 |
| --- | --- |
| 需要预装 Python 3.10+（含 Tcl/Tk、pip、venv） | 自包含单文件 exe，**不需要 Python** |
| 安装期必须联网访问 PyPI | **运行期完全不需要联网**（除连接服务器本身） |
| venv + 原生 wheel 易碎、icacls 改 ACL | 无安装过程，无 ACL 改动 |
| agent 必须自己"猜"安装目录 | 交接文字直接给本机 HTTP 地址，不需要找 exe |
| 要求 agent 读 `session.json` 却不许打印令牌 | 不落盘会话；令牌直接在交接文字里，本机有效、默认只读 |
| GUI 不能改 IP / 账号（只有端口和密码可改） | 地址、端口、账号、密码、**可读根目录**全部可填 |
| 主机公钥写死在某台服务器 | TOFU + 指纹确认 + 本地固定；变化时硬停并给指引 |
| 没有访问范围限制 | 默认只读 + 根目录约束 + 符号链接默认不跟随；写仅限新建 |
| ZIP + 安装器 + 快捷方式 + 5 页 PDF | 一个 exe；不附带 skill/说明/测试服务器；默认只读 |

---

## 架构

```
src/
├─ SciBridge.Core/          跨平台核心库（net10.0），不含任何 Windows 专属代码
│   ├─ SshConnector.cs      SSH 连接、TOFU 主机公钥、键盘交互（2FA）
│   ├─ RemoteFs.cs          只读操作 + 新建文件/目录 + 根目录约束
│   ├─ BoundedReadStream.cs 把 HTTP 请求体流式交给 SFTP（不整包进内存）
│   ├─ PathGuard.cs         路径规范化与越界拒绝
│   ├─ BridgeService.cs     读写操作分发、权限检查与审计
│   ├─ HttpBridgeServer.cs  手写最小 HTTP/1.1，只绑 127.0.0.1
│   ├─ BridgeHost.cs        组装 HTTP 服务 + 内存会话
│   ├─ AuditLog.cs          每次连接一份时间戳命名的操作日志
│   ├─ BridgeClient.cs      内部 HTTP 客户端（自检用，不面向 agent）
│   ├─ HandoffText.cs       生成交接文字（URL + 令牌 + curl 示例）
│   ├─ TextRenderer.cs      文本输出（ls -l 风格）
│   ├─ HostKeyStore.cs      指纹固定存储
│   ├─ Settings.cs          非机密偏好 + DPAPI blob 占位
│   ├─ BuildInfo.cs         从程序集读取内嵌版本
│   ├─ UpdateConfig.cs      固定更新仓库与资源名
│   ├─ ReleaseVersion.cs    稳定版 SemVer 解析/比较
│   └─ UpdateService.cs     GitHub Release 检查、下载、SHA-256 校验
├─ SciBridge.App/           Windows GUI 外壳（net10.0-windows，WinForms）
│   ├─ Program.cs           GUI / 诊断入口；不再提供 ls/cat CLI
│   ├─ MainForm.cs          主窗口 + 更新检查/底部下载进度 + 写选项折叠
│   ├─ BracePanel.cs        折叠写选项的缩进引导线
│   ├─ UpdateApplier.cs     隐藏 helper：等待退出、替换 exe、重启
│   ├─ Dialogs.cs           主机公钥确认、2FA
│   ├─ SecretStore.cs       DPAPI 加解密（"记住密码"）
│   ├─ SelfTest.cs          --self-test：真实连接 + 真实 HTTP 只读检查
│   └─ UiDump.cs            --dump-ui：导出真实版面几何用于自动验证
└─ SciBridge.TestHost/      Linux 下的跨平台主机，供自动化测试复用同一套 Core

tools/testserver/          仅开发测试用的本机测试服务器；不随发布包分发
tests/
├─ sftp_server.py           基于 paramiko 的真实 SSH/SFTP 服务端（默认只读，可测试写、记录所有请求）
├─ bridge_client.py         测试共享的 HTTP 客户端/Bridge 进程辅助
├─ e2e.py                   79 项端到端断言（真实 SSH 服务端 + 真实 HTTP 接口）
├─ go_server_check.py       20 项断言：验证 Go 测试服务器是合格的替身
├─ wine_check.py            8 项：用 Wine 验证交付的 Windows exe（自检/HTTP 路径）
└─ wine_selftest.py         14 项断言：Windows 测试服务器 + Windows SciBridge 全链路

tools/make_icon.py          生成 app.ico（纯标准库，附 ASCII 预览）
```

设计上的关键选择：

- **HTTP 是 agent 的唯一接口**。没有 CLI、命名管道或会话文件需要沙箱去读/执行。
- **HTTP 服务手写在 `TcpListener` 上**，不用 `HttpListener`：后者在 Windows 上需要
  管理员 URL ACL 预留，会直接毁掉"双击即用"。
- **令牌放进交接文字**。它是本机回环、会话级的能力凭证；默认只读，开启写后仅多出新建权；关闭窗口即失效。
  代价是令牌会进入对话上下文，因此交接文字明确要求不要外发、不要截图。
- **不落盘 session**。GUI 只在内存里保存 URL/令牌；没有 agent 可读的会话文件。
- **写能力默认关闭且只做加法**。勾选后仅允许上传新文件、创建一级目录；没有覆盖、
  重命名、删除、chmod 或远程命令执行。写路径仍受读根目录和符号链接策略约束。
  上传流式写入 SFTP，单次上限 2 GiB。
- **审计日志**。每次连接在 `%LOCALAPPDATA%\SciBridge\audit` 生成
  `audit-<时间戳>-<pid>.log`，记录操作、路径、字节数、成功/失败；不记录令牌、
  密码或文件内容。GUI 的“打开审计日志”可随时查看。
- **exe 是控制台子系统（不是 WinExe）**，`--self-test` / `--version` 仍能被
  cmd/PowerShell 等待并捕获输出。GUI 模式启动时调用 `FreeConsole()`：双击产生的
  控制台/pwsh 窗口会被关闭；从已有 cmd/PowerShell 启动时只脱离控制台，不会把
  用户自己的终端窗口隐藏或关掉。
- **内置更新检查**：GUI 显示后异步查询 GitHub 最新稳定 Release；发现新版本才
  弹窗询问，不阻塞启动。更新只下载并替换 `SciBridge.exe`，校验 SHA-256 后用临时
  helper 完成替换，失败保留旧版本。选择“稍后”只跳过本次，下次启动会再次提醒。
- **不开单文件压缩**：压缩过的 .NET 单文件很像"加壳程序"，容易被杀软误报，
  而目标用户没有能力排查误报。代价只是体积（约 110 MB）。

---

## 读写边界

1. **默认只读**：不勾选写权限时，协议层只接受 `health / list / stat / read / find`，
   代码中不存在任何写入、删除、改名、chmod 或远程命令执行的调用点。
2. **写只做加法**：开启后仅新增 `PUT /upload`（新文件）和 `POST /mkdir`（一级新目录）。
   覆盖已有文件、重命名、删除、chmod、远程执行都不存在；目标已存在时直接拒绝。
3. **范围层**：所有路径先经 `PathGuard` 规范化，限制在用户设定的根目录内；
   `..` 越界、盘符与前缀一律拒绝。由于 SSH.NET 的 `GetAttributes` 实际发送的是
   **LSTAT**，属性不会跟随符号链接，因此"检查但不跟随符号链接"的策略可以被可靠执行；
   中间路径上的符号链接默认直接拒绝。上传先写临时文件，成功后 rename，失败清理。

写接口（仅当用户勾选对应权限）：

```text
PUT  /<token>/upload?path=<新文件路径>    body=原始字节（上限 2 GiB，不覆盖）
POST /<token>/mkdir?path=<新目录>         父目录必须已存在
```

目标已存在、路径越界、父目录是符号链接等情况一律返回 400。

---

## 构建

需要 .NET SDK 10（本仓库在 Ubuntu 上用交叉编译验证过）。

```bash
source env.sh                      # 把 NuGet 缓存限制在仓库内，便于沙箱环境
dotnet publish src/SciBridge.App/SciBridge.App.csproj -c Release
# 产物：src/SciBridge.App/bin/Release/net10.0-windows/win-x64/publish/SciBridge.exe
```

本地构建没有 release tag 时会显示 `0.0.0-dev`。要模拟某个发布版本：

```bash
dotnet publish src/SciBridge.App/SciBridge.App.csproj -c Release -p:Version=1.0.0
```

### 版本号

- **唯一的发布版本权威源是 Release workflow 的 git tag**（例如 `v1.2.3` → `1.2.3`）；
  workflow 通过 `-p:Version="$VERSION"` 注入，程序集把它内嵌为
  `AssemblyInformationalVersion`。
- C# 代码里没有版本常量；`BuildInfo` 从程序集元数据读取版本，并写入内部
  `SessionInfo.AppVersion` / `health` 返回值。
- GUI 右下角显示这个版本。非 tag 的本地构建显示 `0.0.0-dev`，不会被误认为发布版。
- GUI 左下角显示 GitHub 仓库链接；URL 同样来自程序集元数据
  （`AssemblyMetadata("RepositoryUrl", ...)`），不在 C# 里硬编码。
- 写入子项默认折叠，只有勾选“允许写入”才展开；左侧有缩进引导线。
- 更新下载时在窗口底部展开进度条和取消按钮，不阻塞主界面。

### 更新

- GUI 显示后异步请求 GitHub `releases/latest`，只接受稳定版；`0.0.0-dev` 不自动更新。
- 发现新版本才弹窗询问；“稍后”只跳过本次，下次启动会再次提醒。
- 只下载稳定文件名 `SciBridge-win11.exe`，并用同 Release 的 `SHA256SUMS.txt` 校验。
- 下载完成后启动隐藏 helper；它等待旧进程退出，备份旧 exe、替换、重启。失败会保留/恢复旧版本。
- 更新包不包含 skill、说明或测试服务器；也不做代码签名（见安全模型）。

`env.sh` 只是把 `NUGET_PACKAGES` 等指向仓库内部；在普通机器上可以不用。

## 发布

交付二进制不进入 git 历史（`dist/SciBridge.exe` 已在 `.gitignore` 中），
由 GitHub Actions 构建并附加到 Release：

- 在 Actions 页面手动运行 **Release**，填入 tag（如 `v1.0.0`）；或
- 推送 tag 触发：`git tag v1.0.0 && git push origin v1.0.0`

工作流只交叉编译 `SciBridge.exe`，生成
`SciBridge-win11.exe`（稳定文件名，不带版本）和 `SHA256SUMS.txt`，并创建或更新对应的
GitHub Release。不再生成 zip，也不打包 skill、说明或测试服务器。

## 测试

```bash
python3 -m venv .venv && .venv/bin/pip install paramiko pyyaml

.venv/bin/python tests/e2e.py             # 79 项：真实 SSH 服务端 + 真实 HTTP
.venv/bin/python tests/go_server_check.py # 20 项：Go 测试服务器是否合格
.venv/bin/python tests/wine_check.py      # 8 项：交付的 Windows exe（自检/HTTP 路径）
.venv/bin/python tests/wine_selftest.py   # 14 项：Windows 测试服务器 + Windows 自检
```

构建开发用的本机测试服务器（需要 Go；依赖已 vendored 在 `third_party/`，不随发布包分发）：

```bash
export PATH=$PWD/.build/go/bin:$PATH GOFLAGS=-mod=mod GOPROXY=off GOSUMDB=off
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build -C tools/testserver \
  -ldflags="-s -w" -o ../../.build/SciBridgeTestServer.exe ./...
```

`tests/e2e.py` 覆盖：认证与指纹固定、错误密码只尝试一次、health 的真实探测、
文本与二进制读取的字节一致性、分段读取、Unicode 路径、find 深度与上限、
越界与符号链接逃逸拒绝、HTTP 令牌/GET-only/浏览器来源拒绝、交接文字含 URL/令牌、
只读默认拒绝写入、写权限下的新文件/目录、不可覆盖、路径/符号链接约束、审计日志，
以及"桥关闭后 HTTP 连接失败"。

---

## 安全模型（如实说明）

- 密码只在用户本机输入，**不会**进入 agent 的对话上下文；
  默认不落盘；勾选"记住密码"后用 Windows DPAPI（当前用户作用域）加密保存。
- 令牌是**本机回环、会话级**的能力凭证，进程退出即失效；默认只读，开启写后仅多出新建权。
- 令牌现在会出现在交接文字里（否则 HTTP-only 无法工作）；请勿外发、截图或写进仓库。
- 开启写权限后，这个令牌同时具备**新建文件/目录**的能力；仍不能覆盖、删除或执行命令。
- 接口只绑定 `127.0.0.1`，不触发 Windows 防火墙弹窗，局域网无法访问。
- 带 `Origin`/`Referer`/`Sec-Fetch-*` 的请求一律 403，防止网页借浏览器探测。
- 不写 `session.json`；没有命名管道；agent 不需要读取任何本机文件。
- 已知限制：
  - 勾选"允许跟随符号链接"后，符号链接**可以**指向可读根目录之外——这是用户显式选择的放宽。
  - 服务器端账号自身的权限不受本工具限制；本工具只是不提供任何写操作。
  - 写权限只允许新增文件和一级目录；目标已存在会被拒绝，但仍可能占用服务器磁盘空间。
  - 不做代码签名，首次运行可能出现 SmartScreen 提示。
  - 更新器不做代码签名，只依赖 GitHub HTTPS + Release 的 SHA-256 校验；如果发布账号
    被攻破，校验值也可能被替换。这是当前接受的风险。
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
- 内置更新器的真实 GitHub Release 下载/替换链路需要在 Windows 11 上做一次手工验收
  （尤其关注杀软占用、只读目录和更新后重启）。

GUI 版面不是靠肉眼确认的：`SciBridge.exe --dump-ui` 会在 Windows 运行时导出每个控件的
真实坐标与尺寸，已验证所有控件都在客户区内、无零尺寸、无文字裁切。
