# AI Asset Provenance Helper v1.5.3

## Pixel-Exact series split across several manifest parts

A large asset document is commonly exported as several manifest parts, and a
canonical series may begin in one part while its last `AusRefN` target rows sit
in the next. The helper previously rejected the whole collection with *"The
Pixel-Exact series metadata does not contain exactly one target row for output
N"* and wrote nothing at all — discarding the phases it could have committed.

- Output indexes with no matching row in the imported manifest are now
  **deferred** instead of failing the batch. Every phase that does have a row is
  committed normally, with its own asset folder and provenance.
- The phase-order confirmation marks deferred phases explicitly, and the
  completion summary names them together with the download files they map to.
  Those downloads are left untouched in the Image Download Folder.
- A deferred phase is terminal for its batch journal, so the pending-collection
  slot is released and the next series in the queue is no longer blocked.
- After importing the continuation manifest, a leftover `AusRefN` row can be
  committed as a single asset behind one confirmation, because no `RefN`
  collection row for that series is importable there. While the series' `RefN`
  row is present and still open, that manual path stays blocked.
- Two rows claiming the same `OUTPUT_INDEX` remains corrupt metadata and still
  fails closed without touching the filesystem.
- Staged source images are now bound to their queue targets **by output index**
  rather than by position, so a deferred phase can never shift a later image onto
  the wrong asset.

## Queue usability

- Finishing a Pixel-Exact collection now selects the next open Request and copies
  its prompt to the clipboard, instead of leaving the queue with no active row.
- The Request Queue keeps its scroll position and selection when a row is
  activated or completed. Long queues no longer jump back to the first row after
  every action.

## Fixes found by the pre-release audit

- A legacy `AusRefN` queue row (a `PROZESSMARKER` with no `FLOWMETA` clause) has
  no series identity, so the new continuation path could not identify it and
  returned without doing anything. Clicking **Main Image** on such a row was a
  dead click with no feedback. It now shows the same guidance it always did.
- Finishing a collection moved the form, prompt and clipboard to the next open
  Request but left the previous row highlighted, because the queue rebuild
  restores whatever was selected when it started. A following Enter then acted
  on the stale row. The hand-off now moves the selection with it.
- The auto-advance searched the whole manifest but activated through the
  rendered rows, so with **Show: Open Pixel series** active it silently did
  nothing whenever the next open Request was filtered out. It now searches the
  rows the operator can actually see.
- Changing the queue filter no longer restores the previous scroll position. The
  new row set has no meaningful position for the old top row, so the clamped
  index scrolled to an unrelated request.
- The status line no longer claims the prompt was copied to the clipboard; the
  copy reports its own failure separately and that claim could be false.

## Verification

- New regression coverage for the split-series commit path, index-based source
  binding, deferred journal completion, the confirmed continuation commit, the
  still-blocked manual path, ambiguous series metadata, clipboard/auto-advance,
  and queue scroll retention.
- The test suite no longer touches the real per-user state directory. A MainForm
  built without the optional state services previously fell back to
  `%LOCALAPPDATA%\Ceegore\AssetProvenanceHelper`, so the suite read the live
  Pixel-Exact journal, ran generation-job and candidate recovery against the real
  store, wrote staged candidates into the real `generated/` folder, and could
  discard a pending batch. A full run is now verified to leave that folder
  byte-for-byte unchanged.
- Two API-generation test classes installed only one of the four dialog seams and
  depended on execution order for the rest; an unseamed message box blocked their
  UI thread until the test timed out. They now install and reset the full set.
