# 发布检查清单（RELEASE_CHECKLIST.md）

> 当前状态：**V1.2.0 开发候选（RC）**。以下条件全部满足前不得正式发布。

## 发布前必查

- [ ] 全部离线自测通过（logic/credentials/core/stop/ui）。
- [ ] CI 绿（全新环境构建 + 自检 + 扫描 + 产物上传）。
- [ ] 真实环境验证清单执行完毕（TESTING.md 第 10 项，需用户操作）。
- [ ] 24/72 小时长跑稳定性确认。
- [ ] 多档 DPI 实机验证（100%–200%）。
- [ ] 桌面界面源码补充许可声明（当前缺失，正式分发的前置条件）。
- [ ] 凭据兼容性：用 V1.1 保存的凭据在 V1.2 读取正常（格式未变，理论兼容，需实测一次）。
- [ ] 升级路径：旧版退出→新版启动→识别旧凭据/服务事务记录。
- [ ] 卸载检查：删除程序文件 + %LOCALAPPDATA%\CheZhiTech + %TEMP%\Chezhi-* 无残留；天翼服务处于运行状态。
- [ ] 发布包内容审查：不含开发者个人信息、测试凭据、内部日志、无关文件。
- [ ] 发布包含：澈知科技.exe + 澈知科技.exe.config + 认证核心.exe（可选，Build.ps1 可重建）+ Licenses.txt + 说明文档。

## 代码签名（当前 BLOCKED：无证书）

- [ ] 取得代码签名证书后对两个 exe 做 Authenticode 签名（时间戳服务器启用）。
- [ ] 无证书期间如实声明"未签名"，不得宣称已签名。

## 已知发布限制（如实携带）

- 认证协议明文 HTTP（UPSTREAM_LIMITATION）——README/SECURITY.md 已声明。
- libcurl 8.23.0 官方漏洞库仍有 42 条披露未修复条目（最新版所能达到的水平；对本项目使用面的实际影响待人工复核结论）。
- Program.cs 完整模块拆分延后（A16，不影响功能）。

## 发布动作（用户授权后）

1. 合并 fix/v1.2.0-stability-security → main（PR 审查）。
2. 打 tag `v1.2.0`，GitHub Release 附产物与哈希清单（build-manifest.json）。
3. CHANGELOG 定稿。
