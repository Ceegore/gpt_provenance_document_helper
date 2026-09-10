# AI Asset Provenance Helper v1.5.5

Pixel-Exact series that are split across manifest parts could not be completed.
This release fixes the durable journal that caused it, and the two follow-on
defects the same design produced.

## The Pixel-Exact journal holds one receipt per series

The batch journal (`pixel-exact-batch-state.json`) had exactly **one** slot. That
was fine while a series' `Einzeln` master row and its `RefN`/`AusRefN` rows lived
in the same manifest part, and wrong as soon as they did not.

A real 250-row export ends with a run of master rows whose collections all live
in the next part. In that shape the first master claimed the only slot, and from
there:

- **Every following master row was refused** with *"Another Pixel-Exact batch is
  pending. Finish or discard it before committing this master image."* Its asset
  was never written, so the tail of that manifest part could not be finished at
  all — 11 rows in the reported run.
- **Every following collection failed in the next part** with *"Could not
  establish the durable Pixel-Exact collection receipt. The matching Pixel-Exact
  seed has not been committed and marked done."* — because that series' receipt
  had never been able to exist alongside the first one.

The journal is now keyed by series and holds one entry per series, so a manifest
part that ends with a dozen master rows can be processed straight through and
each collection in the next part finds its own master receipt. The on-disk format
moves to a `Batches` container; an existing single-batch file is read and
migrated on first use, so an in-flight run keeps its state.

Finished batches are still retained for the delete-and-retry path, bounded to the
16 most recently updated — except that a finished batch holding an unresolved
deferred phase is never dropped, because its receipt is the only record of which
download belongs to the queue row still waiting in a later part.

## A collection whose master row is elsewhere

Related, and previously a dead end: if a series' master receipt is genuinely
unavailable — the queue was cleared, or the master part was processed on another
machine — the collection now asks for one explicit confirmation and runs,
recording each queue row's own prompt. It refuses, as before, while the series'
own master row *is* importable and still open, and now names the row to process
first instead of reporting a receipt failure.

The confirmation is explicit that it does **not** produce the master asset: if
that master row is still open in a different manifest part, process it there
first, or it stays missing.

## Deferred phases hand themselves over

When a collection defers a phase because this manifest part carries no row for
it, it already freezes that phase's image and records which download it was. That
record used to be destroyed by the next series, so the operator had to identify
the right file among unrelated downloads by hand.

The continuation `AusRefN` row now names that exact file in its confirmation,
commits the frozen bytes (hash-verified against the collection's receipt), and
closes the phase out in the journal. Picking an image by hand still works when
those bytes are gone.

## Staging directory leak

Removing a batch deleted `pixel-exact/<series>/<batch-id>/` but never the now
empty `<series>/` directory, so the staging root accumulated one empty folder per
series forever (109 on the reporting machine). The series directory is removed
once its last batch is gone. Directories left behind by earlier versions are
inert and can be deleted by hand.

## Verification

- 1299 passed / 1 skipped / 0 failed, up from 1285 passed / 1 skipped in v1.5.4:
  14 new tests. `scripts/verify_like_ci.ps1` green on a clean tree
  (`--no-incremental`, `-warnaserror`, Debug + Release + RecoveryCritical).
- New `PixelExactMultiSeriesJournalTests` reproduce the production shape
  end-to-end: two master rows in one part, both collections in the next, plus the
  journal, migration, retention, confirmation, resumability and
  deferred-hand-off invariants. Each was confirmed to fail before its fix.
