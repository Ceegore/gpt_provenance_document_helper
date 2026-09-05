# AI Asset Provenance Helper v1.5.0

## API and queue reliability

- The request queue now survives application restarts, including its original order
  and completed rows. A private, atomically written snapshot is revalidated before
  it can be restored; the original manifest file may have been moved or deleted.
- Added **Clear Queue**. It deliberately removes the visible queue snapshot and
  completion display, but preserves generation-job and staging evidence so paid API
  outputs and remote batch recovery remain auditable.
- Ready API candidates now require verified raw-file/hash authority before they can
  be loaded into the normal Main commit workflow.

## Documentation and validation

- Documented API-queue usage, durable-commit semantics, and SAC-aware local testing.
- Updated release version to 1.5.0.
