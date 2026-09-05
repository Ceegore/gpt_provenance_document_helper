# AI Asset Provenance Helper v1.5.1

## Request manifests

- Request manifests produced from animation specifications may use the documented
  `{frame:03d}` sequence token. The queue now imports those entries as a safe
  `_frames` Windows asset name while retaining the complete sequence prompt.
- Other invalid Windows filename characters are still rejected; this is a narrow
  compatibility rule, not broad filename sanitisation.

## Usability

- Queue actions wrap at normal button height, so all actions remain reachable.
- The status history is substantially taller and can show several complete rows.
- The window scrolls rather than hiding controls at reduced height; mode controls
  also wrap below the asset name at reduced width.
- Removed the obsolete full-prompt hover popup. The editable Final Prompt field
  remains the single full-prompt view.
