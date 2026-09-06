# AI Asset Provenance Helper v1.5.2

## Pixel-Exact review and queue navigation

- Before a manual Pixel-Exact collection writes anything, the helper now shows a
  confirmation preview of the detected oldest-to-newest source-file order and
  its exact target asset for every phase. Cancelling leaves Downloads, staging,
  queue state, and asset folders unchanged.
- Request Queue now offers **Show: Open Pixel series**. It shows every row of
  incomplete canonical `FLOWMETA.SERIE` groups, including completed phases that
  provide useful context for the remaining work.
- The queue footer reports overall canonical Pixel-series completion, outstanding
  series count, and the selected series' per-phase progress.

## Verification

- Added focused regression coverage for phase-preview content, canonical series
  progress, and the queue filter.
