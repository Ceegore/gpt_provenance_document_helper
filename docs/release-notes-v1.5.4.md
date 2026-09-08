# AI Asset Provenance Helper v1.5.4

A screenshot-driven audit of every screen, screen state and window size. Each
defect below renders wrong without throwing, so no behavioural test could have
caught it; each now has a numeric guard in `MainFormLayoutRegressionTests`.

## Main window layout

- **The prompt buttons were drawn through the prompt text.** The Final Prompt
  block lived in a nested table inside a percent-sized row. When that row was
  smaller than the block needed, a `TableLayoutPanel` overlaps its AutoSize rows
  instead of clipping them, so **Paste Clipboard** and **Clear** were painted
  over the prompt, and at smaller window sizes they collapsed to zero height and
  vanished entirely. The prompt title, preview, box and buttons are now flat
  rows of one table, so a single layout arbitrates the whole column.
- **The Current Asset group wasted 88px.** It and its Fill-docked child each
  sized themselves from the other and settled at a stale height, measured as
  rows `[26,111]` against a preferred `49`. Because the image cards are the only
  percent-sized row beneath it, every one of those pixels came out of the
  workspace — which is what squeezed the drop box and the **Main Image** button
  flat. The group's height is now derived from its content.
- **The image drop box and Main Image button are usable again** at every window
  size from 1040x640 up, as a direct result of the two fixes above.

## Help

- **The Help button opened an empty strip.** The overlay was docked `Fill`
  beside a `Top`-docked workspace, so it only ever received the 61px left over
  below it — against a 560px content panel. Every line of help text was clipped
  away, leaving just the title bar and Close button. The overlay is no longer
  docked; it covers the client area and tracks window resizes.

## Request Queue

- **Asset names were unreadable.** A 380px queue column left the Asset column
  ~150px, so every row truncated to the same shared prefix
  (`asset_card_bureau_field_...`). The queue is now 460px, the Asset column
  absorbs whatever space is left after the three fixed columns, and each row
  carries its full asset name as a tooltip.

## Release documentation

- `AGENTS.md` advertised a self-contained `-win-x64.zip` and a
  `-framework-dependent.zip` long after the pipeline had been reduced to one
  apphost-free archive — the self-contained package was dropped precisely
  because its unsigned apphost is what Smart App Control blocks. The table now
  matches the four shipped releases, and `scripts/verify_release_assets.ps1`
  compares it against `release.yml` in CI so it cannot drift again.

## Verification

- 1285 passed / 1 skipped / 0 failed.
- New `MainFormLayoutRegressionTests` guard each defect above, several across
  three window sizes; every one was confirmed to fail before its fix.
- An opt-in capture (`APH_SHOT_DIR`) reproduces the visual audit on demand and
  is skipped in CI.
