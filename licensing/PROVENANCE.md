# P4.4 integration candidate

SDK sources copied verbatim from ljjnb666-nb/chezhi-license commit 57369b9097534530db1b018e2ff9adbaa24c6578, CI 46011524283 (10/10; actual Windows TLS fixture interop). Files: AuthorizationState.cs, WindowsDeviceKey.cs, DeviceEnrollment.cs, NativeLicenseClient.cs, LicenseCoordinator.cs.

This branch is a non-release candidate. The legacy default build is unchanged, and does not contain this integration. A separate explicit x64 candidate build with CHEZHI_LICENSED and generated CandidateTrust is required. No production trust values have been supplied. Do not ship either the legacy test build or this candidate as a licensed release. Card persistence, actual PostgreSQL interoperability, secure release distribution, existing-session renewal policy, full campus tests and elevated-helper admission review remain open.

GUI candidate guards shared manual/retry preflight, post-handoff launch and prepare. Restore/stop/exit/recovery unchanged. Direct privileged helper and core extraction threat boundaries require independent review before release. No offline grace; planned handoff can make authorization unavailable. User may recover official access. Current card is only held for this process and same-key activation is used for recovery; replacement UI not yet implemented. No actual network access tests in this commit.

## Card persistence increment (pending CI)
LicenseCardStore stores one separate current-user DPAPI record, domain-separated
by SID/registry path, device fingerprint and deployment scope. Protected DACL
and owner verification; serialized cooperating cross-session operations.
Explicit save after verified activation only; normal startup/reopen never
enrolls, never deletes corrupt data. Replacement resets the previous grant;
prior saved record is retained unless the new card verifies and user opts
to save. Explicit forget removes only the local card record, not CNG identity
or server binding. Same-user/admin and OS rollback outside local guarantees.
No offline credential, no card logs or command-line card. .NET strings and
native copies cannot be promised securely erased. Failed save does not
invalidate an already verified current grant. Retry after an initial failed
activation does not automatically persist the card; user can explicitly
replace/save via dialog. State/replace/forget tray entries are gated against
active task/service handoff. Existing-session periodic renewal policy remains
open. Runtime/UI tests still required.
Prior CI 46012569784: candidate x64 compile success, Linux/macOS audits
success; synthetic SBOM failed due to GitHub public API rate limit at
schema fetch; Windows job still running at verification time.

## Renewal and consent retry candidate
Prior 46013225140: actual x64 compile and DPAPI test steps passed; original
offline UI and lifecycle/stress (100 each), Linux/macOS and synthetic SBOM
passed; Windows full job incomplete at inspection. Existing UI tests exercise
the legacy build, not the licensed candidate.

Provisional policy: renew an established active session every 120 seconds,
temporary failure retry no sooner than 60 seconds, denial/security/local/
recovery/stopped states disable periodic retries. Scheduler is not a grant.
No automatic activation or enrollment from timer. Existing running session is
NOT automatically terminated on loss/denial/expiry; warning only and all new
connection/preparation/retry attempts need fresh online proof. This is a
non-release policy choice pending owner review, actual campus recovery tests
and a decision about ongoing-session enforcement. Server denial is generic,
not proof of a specific revocation. Network/power/stop invalidates outstanding
grant and result; re-observe local handoff after online prelaunch await.
Separate tray status is available even while connected.

Remember consent and card/fingerprint remain in memory for the same fixed
context after unsuccessful initial activation; next verified success saves.
Reset/replacement/forget/dispose clear pending buffer. Save already initiated
after verified success can finish during shutdown/invalidation; independent
worker-owned copy is wiped afterward. No claim of secure erasure of all
managed/native copies. No card persistence on failed verification. A failed
save retries only on later verified success. Current Windows test is a
scheduler unit regression, not full licensed GUI or server acceptance.
