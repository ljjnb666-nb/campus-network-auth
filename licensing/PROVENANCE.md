# P4.4 integration candidate

SDK sources copied verbatim from ljjnb666-nb/chezhi-license commit 57369b9097534530db1b018e2ff9adbaa24c6578, CI 46011524283 (10/10; actual Windows TLS fixture interop). Files: AuthorizationState.cs, WindowsDeviceKey.cs, DeviceEnrollment.cs, NativeLicenseClient.cs, LicenseCoordinator.cs.

This branch is a non-release candidate. The legacy default build is unchanged, and does not contain this integration. A separate explicit x64 candidate build with CHEZHI_LICENSED and generated CandidateTrust is required. No production trust values have been supplied. Do not ship either the legacy test build or this candidate as a licensed release. Card persistence, actual PostgreSQL interoperability, secure release distribution, existing-session renewal policy, full campus tests and elevated-helper admission review remain open.

GUI candidate guards shared manual/retry preflight, post-handoff launch and prepare. Restore/stop/exit/recovery unchanged. Direct privileged helper and core extraction threat boundaries require independent review before release. No offline grace; planned handoff can make authorization unavailable. User may recover official access. Current card is only held for this process and same-key activation is used for recovery; replacement UI not yet implemented. No actual network access tests in this commit.
