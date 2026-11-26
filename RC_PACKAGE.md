# V1.2.0 RC 内部构建归档说明

本归档为开发和离线审计候选，不是正式发布，不构成对桌面源码或二进制的额外许可授权。不得将未签名、尚未完成发布核查的构建误称为正式版本。实际网络环境验证仅限合法授权范围，目前延期。

## 内容与证据

| 文件 | 说明 |
| --- | --- |
| 澈知科技-源码重建.exe | Windows 桌面构建产物 |
| 澈知科技-源码重建.exe.config | 与桌面产物配套的配置 |
| 认证核心-源码重建.exe | 原生构建产物 |
| build-manifest.json | 构建版本、checkout 与三个产物摘要 |
| sbom.cdx.json | 不完整源码级 CycloneDX 物料清单 |
| pe-imports.json | 直接 PE 导入报告 |
| repeatability.json | 同 runner 两次构建比较，不是独立复现证明 |
| build-toolchain-evidence.json | 工具链观测记录 |
| gui-compiler-candidate.json | runner 已安装候选编译器的隔离双编译诊断；非默认产物、非运行时验收 |
| build-evidence.json | 无签名证据索引，绑定其他归档文件的长度与 SHA-256 |
| Licenses.txt | 已有第三方许可全文与声明 |
| THIRD_PARTY_NOTICES.md | 第三方组件、来源及尚未关闭的适用性审查 |
| RC_PACKAGE.md | 本说明 |
| RELEASE_CHECKLIST.md | 仍有效的正式发布门禁 |

归档完整性以 GitHub Actions 指定 run、artifact ID、ZIP SHA-256 及内部逐文件摘要核对为准。PR 工作流可能检出临时合并提交，应核对其父提交和 tree，而不是假设 manifest 中的 checkout 等于分支 HEAD。不得将摘要一致解释为签名、完整供应链来源、真实网络功能或发布授权已获得验证。

## 未关闭的正式发布条件

桌面许可由权利人确认，不由打包脚本推定。实际代码签名、独立依赖来源和链接组成核查、GUI 确定性与独立环境复现、升级卸载、实机和长跑依照 RELEASE_CHECKLIST.md 保留。不要改写已构建二进制以制造摘要一致；不要通过跳过验证来取得绿色 CI。

## GUI 编译器迁移验收

当前保持已验证的 Framework csc 构建路径。CI #433 已观察到旧编译器拒绝 /deterministic+（CS2007），因此不将原生核心字节一致泛化到 GUI。候选编译器须另行核验来源、版本及摘要锁，保持 Framework 4.x 目标引用与现有资源、清单、平台设置；在独立输出目录完成两次完整构建，检查原始字节与现有离线 UI、生命周期、资源和完整性门禁，之后再决定是否替换默认工具链。未经验证不添加自动下载、自动回退或关闭现有门禁。

CI #435 的四份 RC 文档打包步骤已成功。新增实验仅在已有 Visual Studio Roslyn 编译器自身签名验证通过时进行两次完整 GUI 编译（复用已校验的核心、独立源码目录、显式 Framework 引用及确定性参数）。缺候选、签名未确认、编译失败或摘要不一致均如实记录状态，不等于迁移验收。候选 EXE 不运行、不上传、不替换默认构建，独立来源锁、完整双构建及运行时兼容性仍待验证。
