# 发布检查清单（RELEASE_CHECKLIST.md）

> 当前状态：**V1.2.0 开发候选（RC）**。以下条件全部满足前不得正式发布。

## 发布前必查

- [ ] 全部离线自测通过（logic/credentials/core/stop/ui）。
- [ ] CI 绿（全新环境构建 + 自检 + 扫描 + 产物上传）。
- [ ] 构建依赖版本漂移门禁、产物清单与源码级 CycloneDX SBOM 验证通过；独立核验二进制链接依赖及供应链来源证据。
- [ ] cJSON 1.7.19 本地修订版公告复审（含 CVE-2026-16554、67215、67216），以实际可达路径与独立回归确定适用性；其余依赖与工具链漏洞审计完毕。
- [ ] 真实环境验证清单执行完毕（TESTING.md 第 10 项，需用户操作）。
- [ ] 24/72 小时长跑稳定性确认。
- [ ] 多档 DPI 实机验证（100%–200%）。
- [ ] 桌面界面源码补充许可声明（当前缺失，正式分发的前置条件）。
- [ ] 凭据兼容性：用 V1.1 保存的凭据在 V1.2 读取正常（格式未变，理论兼容，需实测一次）。
- [ ] 升级路径：旧版退出→新版启动→识别旧凭据/服务事务记录。
- [ ] 卸载检查：先退出程序并核对服务事务，按用户授权恢复服务；仅删除明确归属本程序且已确认无待恢复责任的文件。不要通配删除 %TEMP%\Chezhi-* 或整个 %LOCALAPPDATA%\CheZhiTech。
- [ ] 发布包内容审查：不含开发者个人信息、测试凭据、内部日志、无关文件。
- [ ] 发布包含：澈知科技.exe + 澈知科技.exe.config + 认证核心.exe（可选，Build.ps1 可重建）+ Licenses.txt + 说明文档。

## 构建证据与复现性（待验收）

- [ ] PE 导入报告与原始 EXE SHA-256 一致；直接导入检查通过，不以此代替传递或运行时依赖审计。
- [ ] CycloneDX 官方 Schema 校验及负向测试通过。
- [ ] 工具链证据记录 20 个已安装包版本、可获得的安装包 SHA-256 与 8 个工具可执行文件摘要；缺少的归档摘要明确标记，不把观测摘要当作预先独立审核的锁。
- [ ] 同一 runner 两次独立输出目录的构建结果完成比较，差异有分析；即使字节一致，也须独立环境复现后才可宣称可复现。
- [ ] 独立核验归档签名与摘要锁、编译器依赖及完整二进制组成；unsigned build-evidence.json 仅作追溯索引，不能代替签名来源证明。
- [ ] cJSON 公告适用性复审完成。CI #280 的 CJSON_BOUNDS_REGRESSION_PASS、CJSON_UNUSED_API_SOURCE_GATE_PASS 已通过，但完整 32 位构建、实际二进制可达性与所有公告覆盖仍需核实。

### 历史验收基线（不是当前 HEAD 的验收声明）

CI #302（45623785237，`64ed1b5`）Windows/Linux 均成功。已实测通过 PE 导入基线、三个官方 Schema blob 校验及正反例、cJSON 边界、加密 sanitizer、工具链版本与仓库声明摘要、构建证据索引。20 个包缓存均无实际归档字节可核查。

同 runner 双重构建：核心 EXE FBF3D0DE / 5B74DABD，GUI EXE 10DBBAFA / 79F5FC4E，均不一致；配置 D2454CE8 / D2454CE8 一致。以上为摘要前缀，完整值见 CI 日志。复现性保持 BLOCKED。

后续仅对 GNU PE 链接时间戳采用 --no-insert-timestamp，并增加只读 COFF 时间戳与原始节区哈希对比；不修改已生成产物，不把归一化比较当作真实字节一致。下一次 CI 必须确认实际效果，旧版 C# 编译器不预设支持确定性编译。

### CI #314 更新（历史验收基线）

- `3ed64a5` / [45627343926](https://github.com/ljjnb666-nb/campus-network-auth/actions/runs/45627343926)：Windows 与 Linux 全绿；NATIVE_NET_RESOURCE_PASS、APP_LIFECYCLE_PASS cycles=100、UI_STRESS_PASS measured=100、CYCLONEDX_SCHEMA_VERIFY_PASS、BUILD_EVIDENCE_INDEX_PASS。
- 原生核心同 runner 两次完整 SHA-256 均为 2DD357146E74BBD2336E8B9196E65482F8A5DA5380F81C6DDBCD8DC0994717CC，COFF 时间戳均为 0，原始节区一致；链接时剥离整改获得验证。GUI C749D11F / 8D5BE4C0 不一致（.text、时间戳），配置一致。项目级可复现性仍 BLOCKED；不得用原生核心通过代替 GUI 或独立环境验证。
- 统一复审：网速 JSON 构造原先未完整检查分配和所有权转移，并在采样锁内分配。已调整为锁内快照、锁外事务式构造、失败清理和 20 个合成失败点（macOS 21 个）；待新 CI 验证。合成短路故障不是实际内存分配器 OOM 注入或完整泄漏证明。
- 原有发布阻断不变：签名证书、独立包归档与签名锁、完整第三方公告及动态依赖覆盖、独立复现、真实环境测试、长跑与升级卸载。不得以历史 CI 代替当前 HEAD 验收。

## 代码签名（当前 BLOCKED：无证书）

- [ ] 取得代码签名证书后对两个 exe 做 Authenticode 签名（时间戳服务器启用）。
- [ ] 无证书期间如实声明"未签名"，不得宣称已签名。

## 已知发布限制（如实携带）

- 认证协议明文 HTTP（UPSTREAM_LIMITATION）——README/SECURITY.md 已声明。
- libcurl 8.23.0：已验证的基线 6ad2b93（CI 45574796280）官方版本公告与区间解析交叉核查为 0 条已知影响记录；原 42 条为旧解析误判，不能当作当前漏洞数量。发布时重新核查全部依赖与实际构建配置；0 条不代表没有漏洞。
- Program.cs 完整模块拆分延后（A16，不影响功能）。

## 发布动作（用户授权后）

1. 合并 fix/v1.2.0-stability-security → main（PR 审查）。
2. 打 tag `v1.2.0`，GitHub Release 附产物与哈希清单（build-manifest.json）。
3. CHANGELOG 定稿。
