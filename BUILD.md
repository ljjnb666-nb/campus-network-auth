# 澈知科技桌面版源码

本包包括桌面界面源码、图标、第三方许可、修改后的 ESurfingClient-CVersion 源码、已构建的认证核心以及该核心使用的 libcurl 源码包。认证核心上游版本：`ffdfaf13965661ff1c5e298c2c9d9cc6f9194687`。

在 Windows 且已安装 .NET Framework 的系统上，可在本目录 PowerShell 中运行 `./Build.ps1`，从所附认证核心重新构建桌面 EXE。脚本验证核心 SHA-256，并把认证核心和许可说明作为资源嵌入，不读取或写入任何校园网账号。程序使用 Windows DPAPI 为当前用户加密保存账号密码；关闭窗口会隐藏到托盘，退出需使用托盘菜单或主窗口的“退出程序”。

若需要修改或重新编译认证核心，请参阅 `认证核心源码/docs/Compile.md`。本次核心使用 UCRT GCC、CMake/Ninja 和静态构建的 curl 8.20.0（Schannel）；核心修改记录见 `认证核心源码/docs/EphemeralExperimental.md`。重新构建核心后，须审计、测试并更新 `Program.cs` 中的 `CoreHash`。协议使用 HTTP，不因启用 Schannel 而自动升级为 HTTPS。

网络认证、服务切换和长期保活依赖学校网络环境，不会在构建或自检时执行。请保留 `Licenses.txt` 中列出的第三方许可与源代码。

安全须知：认证协议为明文 HTTP，密码虽按官方协议做对称加密，但加密密钥内嵌于所有客户端中——同一校园网内的窃听者抓到登录报文即可离线还原密码，且无法验证服务器身份。请勿在本校园网之外的场合复用该密码。此为协议固有形态，客户端无法单方面修复。

许可说明：认证核心声明为 Apache-2.0（修改版已移除 mongoose 及其 Web 界面，不再链接 GPL 组件）。桌面界面源码（Program.cs/CredentialStore.cs）未附带许可声明，仅限自用时无碍，对外分发前需补充。
