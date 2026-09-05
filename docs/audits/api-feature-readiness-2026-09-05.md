# API feature readiness audit — 2026-09-05

## Scope and result

Reviewed `feature/api-batch-automation` after the API repair commits through
`e4e686e`, then added queue persistence and a final candidate-authority guard.
The feature uses a staged, non-destructive pipeline: provider output becomes a
candidate first and a request becomes Done only after the existing durable Main
commit succeeds.

## Changes made from audit findings

- Added an atomically written `request-queue-state.json` snapshot, with strict
  request-key/fingerprint revalidation before restore.
- Added Clear Queue, deliberately blocked during API mutation and active
  reference sessions. It preserves generation-job records and staged output.
- Rejected Ready candidates without a raw candidate path and hash authority.
- Corrected README SAC guidance: `0x800711C7` is an environment block even when
  the executable was started through `dotnet`.

## Known operational boundary

If local durable storage fails after a Direct API response is received, the program
records an uncertain state instead of silently retrying a potentially billable call.
The user-facing Retry / Resolve workflow requires an explicit acknowledgement. This
is intentional fail-closed behaviour, not a recoverable remote-download capability.

## Verification recorded for this change

- Release `-warnaserror` build: pass.
- Targeted request-queue persistence/clear tests: pass.
- Targeted API suite was previously run through the SAC-aware runner with no
  `0x800711C7` block. Final full clean-tree validation is required before release.
