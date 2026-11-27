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

## Security boundary audit (candidate hardening)
Reviewed exact 7544bed (CI 46013818561 all four jobs passed). Confirmed
the original --admin-prepare entry calls ChangeService(true) independently
of the GUI coordinator, and the current core is embedded/extracted as an
independent executable. The licensed candidate now denies privileged prepare
at both CLI and service helper entry (28) and refuses service stop in the
GUI before confirmation/journal/mutation. No new bypass ticket or unchecked
IPC. Read-only ready observation still possible; restore unchanged.
This temporarily removes the licensed candidate's ability to stop an active
official service. Do not ship until privileged handoff and connectivity
policy are resolved. Original stable legacy build unaffected. A separate
resource-less compiled candidate executes exact deny/unknown CLI probes in
CI, never real service changes. Direct execution of an extracted core or
a modified/old binary is NOT solved by client-side licensing; server-side
control of a proprietary service or a different product boundary would be
necessary for strong enforcement against local owners. No DRM guarantee.

Card input now matches the 25-character issuer format and is canonicalized
before enrollment. Explicit replacement cancellation leaves previous
context intact; confirmed valid replacement resets it. Stale load-error
messages no longer override current online results. Connection/preparation
refuse concurrent license management; restore remains available. New
candidate compile/CLI probes and card format tests pending. Remaining:
real licensed UI/PG/TPM/campus acceptance, ongoing-session enforcement,
authenticated distribution, P5 capacity. No production release.

## Release boundary and negative runtime increment
Prior exact ce74666 CI 46016109429 4/4 PASS. Privileged prepare remains
disabled in licensed build. Restore remains subject to original SCM safety
checks, not license. No privileged handoff protocol or network bootstrap
implemented here.

Licensed production-shaped candidate now rejects all offline self-test
CLI commands, except the existing restore path; separate
CHEZHI_LICENSED_TEST compilation is CI-only and never packaged. The
negative-only fixture checks actual low-level core launch refusal without
authorization, real licensed WinForms launch refusal, cancellation, restore
button availability, tray and disposal, with no embedded core, DLL,
registry enrollment or service/network operation. The low-level LaunchCore
adds a second admission check for live auth before creating the process
and just before Process.Start. This is defense in depth inside the GUI,
not independent core enforcement, a cross-process proof, or DRM.

Candidate CI still builds and tests the legacy offline binaries but no
longer uploads them; only explicitly labeled non-executable evidence is
uploaded. Existing SBOM and manifest describe the legacy offline build,
not a licensed release. No historical artifacts removed, no stable main
change. Full positive licensed UI, actual server, TPM, elevated handoff
and bootstrap, existing-session policy, installation trust and direct
core threat model remain release blockers. Latest tests pending.

## Positive desktop licensing fixture
Exact prior 00f908d CI 46017274967: 4/4 PASS, including actual negative
WinForms fixture and non-executable artifact upload. New independent
Windows job checks out public SDK 57369b9097534530db1b018e2ff9adbaa24c6578,
verifies all five vendored source digests against it, rebuilds the native
DLL and loopback fixture, compiles actual DesktopLicenseGate plus modal
ActivationDialog, and runs real CNG/DPAPI/HTTPS tests. Fixture factories,
dialog automation and forced renewal scheduling compile only with
CHEZHI_LICENSE_UI_TEST (and SDK/card test flags); production-shaped
candidate build has none of those flags. No fake grant. Tests cover
activation on a pre-enrolled fixture, user-consented save, cancellation
of replacement, fresh validation after invalidation, actual renewal,
denied replacement preserves saved record, new gate instance reload
and validation, explicit forget and unchanged device. Hosted runner only,
random isolated registry profile, cleanup; no production trust/root
store changes, no core or SCM calls. No executable artifact. Exact
new CI pending. This is not full MainWindow positive authentication,
first enrollment confirmation dialog, actual PostgreSQL, reboot, TPM,
or real campus handoff. Privileged prepare stays disabled; production
release gates unchanged.

## P4.4 consolidated evidence review — 2026-11-27

Disposition: **CONDITIONAL_PASS for isolated engineering evidence;
HOLD for complete integration acceptance, merge and distribution.**
This is a source/log review, not a separate Windows rerun or an external
independent security certification. The review covers exact candidate
`1a163e1f5c9f907ba5ef73919818edcdd1f08933`, base
`bf7eb8aaf523c953a6b232290eb1adaa546d9ec8`. CI
[46027343488](https://github.com/ljjnb666-nb/campus-network-auth/actions/runs/46027343488)
completed 5/5; logs identify the PR merge checkout (35e96a8).
Earlier sections describe historical increments; this section supersedes
their pending-test and pre-enrollment statements, not release restrictions.

### Evidence ledger

- Windows build job 140602033984: resource-less x64 licensed compilation,
  denied privileged prepare/self-test commands, negative MainWindow/core
  admission, cancellation, restore control availability, tray/disposal,
  DPAPI roundtrip/tamper/identity/scope mismatch/forget, card format,
  injected-clock expiry/rollback/revision/denial and retry scheduling.
- Online job 140602034489: actual modal dialog plus pinned real Go DLL,
  CNG software provider, DPAPI, signed HTTPS loopback fixture. First
  enrollment through the dialog, test-only confirmation response, duplicate
  rejection, save/cancel/reload/forget/manual activation, fresh validation,
  renewal, denied replacement, dispatch-boundary invalidation/disposal,
  no stale-result save, pending intent/key exception faults, damaged record,
  lost/replaced key, same-process concurrent first enrollment.
  The five vendored source SHA256 comparisons remain mandatory; SDK pin
  57369b9097534530db1b018e2ff9adbaa24c6578 remains unchanged.
- Linux 140602034664, macOS 140602034279, synthetic SBOM 140602034436:
  success. Raw schema retrieval pins upstream commit and retains Git blob
  checks and negative controls. Windows schema verification also passed.
- Legacy offline UI/lifecycle suites: 11/11, 100 lifecycle cycles,
  100 measured UI stress cycles, 256 operation-matrix cases. These are
  regression evidence for the legacy build, NOT licensed positive UI tests.
- Workflow artifact 15212807435: upload allowlist is eight non-executable
  notice/evidence files; legacy source SBOM explicitly incomplete. No
  licensed release executable or signed release attestation produced.

### Remaining gates (do not waive on CI success)

| Gate | Required closure evidence |
| --- | --- |
| P4.4 positive MainWindow integration | Real authorization gate wired into actual main-window lifecycle with inert test core boundary; no real service mutation; observe positive admission, cancellation and stale-result denial. |
| P4.5 privileged boundary | Preserve denial of licensed privileged prepare until an independently reviewed authorized cross-process design exists. Restore/cleanup remain available. |
| Connectivity bootstrap and session policy | Explicit owner decision and authorized environment tests; no unauthorized access or automatic disruption. Current established sessions warning-only on expiry/denial; new operations require fresh online proof. |
| Production trust and distribution | Authenticated trust manifest, protected installation, pinned DLL/dependencies, licensed artifact SBOM, signing and reproducible package validation. Compile-only trust is unusable. |
| Real service integration | Approved live PostgreSQL-backed licensing environment, actual revocation/expiry, TPM-required failure without downgrade, process restart and recovery; loopback memory fixture is not evidence of these. |
| Local enforcement scope | Old/modified binaries, extracted core and hostile same-user/admin are outside current client-side boundary; do not claim tamper-proof DRM. |
| P5/P6 | Capacity, recovery, monitoring and authorized field acceptance before release. |

No code change in this review entry. No merge, release, service action,
production credentials or real campus test. Preserve Draft until remaining
gates are explicitly resolved. Subsequent changes require a new exact-head
CI/evidence review; the above evidence must not be attributed to future SHAs.
