# B1 Chat — Known pitfalls

Detailed implementation traps that must be checked when touching the affected
area. Keep this file updated with the code change that fixes or introduces a
relevant behavior.

## Firmware, flash and persistence

- Never rewrite the partition table on a board whose NVS must survive. A
  partition-table change is safe only together with an intentional full chip
  erase; otherwise NVS can silently appear lost or resurrect stale data.
- App-only flashing writes `0x10000` and is the default update path. Full flash
  is only for a new/erased board and writes bootloader (`0x1000`), partitions
  (`0x8000`) and app (`0x10000`) after chip erase. Keep this destructive option
  under Firmware's Advanced options, behind the themed confirmation dialog;
  completion must remain visible outside the scrolling log.
- A board that has completed OTA needs the full-erase USB path before the next
  app flash, because otadata may still select the other OTA partition.
- Support images must be present and verified with the 64-character SHA-256
  from `firmware_manifest.json`; never label an unverified download as valid.
- The startup fleet updater is supervised, not unattended: wait for a stable
  online roster, ask once per console session, stop Sequencer playback before
  writing, update slaves sequentially through OTA, and update the USB master
  last. Only semantic upgrades are automatic; never downgrade, overwrite a
  same-version custom Build ID, include an offline/pending droid, or select full
  erase. Stop the batch at the first failed transfer or identity verification.
  The master republishes its roster more frequently than the stabilization
  delay; debounce only update-relevant roster fingerprints, never every
  `evt:droids` telemetry refresh, or the startup prompt is postponed forever.
- `IS_MASTER` in `config.h` controls local `[env:b1]` flashing. CI release
  environments `[env:b1_master]` and `[env:b1_slave]` override it with build
  flags. Check the role before every flash.
- `OtaGuard::earlyCheck()` must remain the first line of `setup()`.
- `Update.begin/write/end` must run from `loop()`, never from the ESP-NOW
  callback or under `portENTER_CRITICAL`. The OTA callback only fills the
  mailbox.
- `OtaSlave::processChunk()` is append-only: a retransmitted chunk is re-acked
  but must never be written twice.
- `OTA_CHUNK_DATA_MAX` in `mesh_comm.h` is authoritative; the C# client must
  use the size announced by `evt:otaReady.chunkSize`.

## Protocol and compatibility

- The current runtime preserves the dual current/legacy heartbeat decoder until
  the coordinated V2 fleet cutover. After every board is aligned, the approved
  breaking redesign may remove it instead of carrying permanent compatibility.
  The sequential OTA transition itself must still have an explicitly tested
  identity/heartbeat path.
- Unknown JSON command fields are ignored. Responses use the stable `evt`
  discriminator and the serial line limit announced as `lineMax` (4 KB).
- `requestId` is mapped to the existing mesh sequence; do not widen the
  byte-compatible animation payload just to correlate console commands.
- Before the V2 cutover, current additive behavior such as `safeStop`, animation
  leases and execution telemetry must retain its existing safe fallback. V2 may
  instead require one uniform protocol/catalog generation and fail closed on a
  mismatch after the complete fleet has been updated.
- The console's line handler must catch malformed input per line so one bad
  firmware message cannot kill the serial read loop.
- The two release trains share one GitHub repository. Never use
  `/releases/latest`; filter app tags (`v...`) and firmware tags (`fw-v...`)
  separately, and select the semantic maximum rather than trusting list order.

## Concurrency, timing and storage

- Neighbor recording in `handleRaw()` must happen before the self-message early
  return and deduplication; relayed echoes still prove a direct radio link.
- Process normal mesh messages from the bounded loop inbox. Keep NVS/flash work
  outside critical sections and outside the ESP-NOW callback.
- `Registry::at()` returns a copy. `Config.isAdopted()` and other NVS access
  must stay outside the registry lock.
- Timestamps written after a loop-start `now` can be newer than `now`; compare
  differences as signed values or clamp them before timeout/age decisions.
- A persisted field whose meaning changes needs a new field name and migration,
  not a lenient reader that silently interprets old data under new semantics.

## Firmware animation and hardware

- Use native LEDC PWM; `ESP32Servo` was abandoned because of its double-attach
  behavior.
- Keep animation duration jitter signed until it has been bounded. Use
  `clampMoveDurationMs()` before narrowing to an unsigned type.
- Animation keyframes are offsets and must use
  `ServoEngine::setTargetOffset()`, so persisted calibration centers are kept.
- Audio is console-side only; the DFPlayer is retired from firmware.

## WPF layout and input

- Do not combine `SystemParameters.WorkArea` with `WindowStartupLocation="CenterScreen"`:
  the former can describe the primary monitor while WPF centers the window on a
  different one, pushing its title bar off a smaller display. Once the HWND exists,
  select its monitor with `MonitorFromWindow`, use that monitor's `rcWork`, and keep
  the clamp/centering calculation in native pixels. Scale only the desired DIP margin
  with `GetDpiForWindow`; this also handles different DPI settings and monitors with
  negative or vertically offset desktop coordinates.
- In a repeated `DataTemplate`, animate named `Transform`s from
  `DataTemplate.Triggers`; do not animate an unnamed compound property path.
  `Style.Triggers` has no usable template `NameScope` for this case.
- In an `ItemsControl` using a `Canvas`, position a child inside a template-root
  `Canvas`; `Canvas.Left/Top` on the template root are swallowed by the
  `ContentPresenter` wrapper.
- WPF `Transparent` is transparent white. Use explicit `#00RRGGBB` values in
  dark gradients to avoid grey haze.
- `Setter.TargetName` cannot target a nested `Freezable`; replace the parent
  property instead.
- `DockPanel.LastChildFill` defaults to `True`; set it to `False` when every
  child must respect its own `Dock`.
- A nested `ButtonBase` handles its click before an ancestor `MouseBinding`;
  use a real `Button` for an intentional second click target.
- Treat tooltips as part of the interaction contract, not decorative copy. Every
  operator-facing interactive control needs a concise action/consequence tooltip;
  keep safety, persistence and timing claims synchronized with the command path.
  Shared control styles set `ToolTipService.ShowOnDisabled=True` so a disabled
  action can still explain itself; custom or unstyled disabled controls must do
  the same explicitly.
- `DarkComboBoxStyle` displays the closed selection through `ToString()`, so
  every model used as a `ComboBox.ItemsSource` needs an appropriate override.
- Debounced sliders must snapshot target ID and values and cancel on selection
  changes; programmatic loads must suppress write-back hooks.
- WPF `MediaPlayer` teardown is dispatcher-affine. A probe may await without a
  synchronization context, but `Stop`, `Close` and event detachment must be
  marshalled back to the player's owning dispatcher; catching the cross-thread
  exception only hides a live native media resource.

## Historical decisions retained for safety

- The old web page at `console/wwwroot/index.html` is a frozen French design
  reference and is not rendered at runtime.
- KyberEditor is UX inspiration and the source of `tools/espflash.exe`; its
  firmware and bootloader are not used.
- Droid names are persisted on the targeted droid through `MSG_NAME`, while
  the master's commit draft remains a display concern. Older slaves may ignore
  this additive message.
