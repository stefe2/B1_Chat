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
- `console/Services/ProtocolClient.cs` hardcodes the expected
  `RequiredGestureCatalogId/Revision/Hash`. Editing
  `catalog/gesture-catalog-v1.json` and running
  `generate-gesture-catalog.ps1 -UpdateHash` changes the firmware's announced
  hash but not this console constant; the console then fails closed exactly as
  designed and silently refuses every gesture dispatch as `WRITE FAIL`, with no
  further explanation in the UI. Update `RequiredGestureCatalogHash` (and the
  id/revision if those changed) in the same change, and remember the
  `console.tests` V2 scene fixtures embed their own catalog hash string too.

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
- `ServoEngine::setLimits()` only updates the calibrated range/center; it never
  moves the current-position state. Any call site that loads calibration
  (boot, mesh `MSG_CALIB`, console `calib`) must follow it with `head.center()`
  the way `applyCalib()` does. Boot once skipped this: a droid that persisted
  `servosEnabled=true` would snap to the generic pre-calibration default
  (`SERVO_PAN_CENTER`/`SERVO_TILT_CENTER`) instead of its real calibrated
  center on every restart.
- A catalog gesture frame's declared `moveMs` must be at least the travel it
  requests divided by `SERVO_MAX_DEGREES_PER_SECOND` (180°/s), in real
  calibrated degrees for that frame's percent delta — not just a duration that
  feels right. `MotionEngine::updateChannel()` advances to the next frame on a
  fixed timer (`frame.moveMs + frame.holdMs`) regardless of whether
  `ServoEngine` actually finished the move; if `moveMs` is too short for the
  ceiling-clamped physical travel, the next frame retargets mid-flight,
  restarting the ease from an unexpected intermediate position. This reads as
  a stutter/kink even though each individual segment has a correct
  ease-in-out curve. Tuned 2026-08-23/24: `communicate.nod`,
  `dialogue.talk` and the four `attention.look-*` gestures had their frame
  `moveMs`/`tempos.normal.durationMs` increased (they were riding close to the
  180°/s ceiling in a too-short window) specifically to make the existing
  easing visible instead of reading as a snap to target.
- `MotionEngine::stop()`'s `applyComposedTarget()` duration (currently 550ms)
  is not catalog-driven — `idle.center` has no trajectory frames — and it is
  also the glide used by `applySafeStop()` in `main.cpp` for every Safe Stop.
  Slowing it for a smoother-looking Center clip also slows the physical
  response of Safe Stop; keep it well under the expressive-gesture durations
  above (currently 700-1400ms) so Safe Stop stays visibly faster than a normal
  gesture.
- `SERVO_PAN_MIN/MAX` and `SERVO_TILT_MIN/MAX` in `config.h` (currently a
  conservative ±30° from center on both axes) are only the virgin/uncalibrated
  board fallback used until `setLimits()` loads a real per-droid calibration
  from NVS; changing them does not affect the physical range of an
  already-calibrated droid.

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
- Same `ItemsControl`/`Canvas` pitfall applies to `Panel.ZIndex`: the outer
  `Canvas` only reads it from its own direct children (the generated
  `ContentPresenter`s), never from anything inside the item `DataTemplate`.
  Set it via `ItemContainerStyle` targeting `ContentPresenter`, not on an
  element nested inside the template — the Sequencer timeline silently fell
  back to Steps insertion order for stacked/overlapping clips until this was
  caught (see SEQUENCER-BEHAVIOR.md, Timeline clip editing).
- A `Style` with a `ContentTemplate` of `<TextBlock Text="{Binding}"/>` applied
  to every `ToolTip` (e.g. for consistent wrapping/MaxWidth) breaks any
  `ToolTip.Content` that is a rich `UIElement` (a `StackPanel`, say): `{Binding}`
  on the string-typed `Text` property calls `.ToString()` on it, literally
  rendering the type name (e.g. "System.Windows.Controls.StackPanel"). Use a
  `ContentPresenter` instead, with the wrapping style applied through its own
  `.Resources` — WPF's default string template still picks it up, and a rich
  element is hosted as itself instead of being stringified.
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
  `TimelineTrack` and `Droid` already had it; `GestureLibraryEntry` did not and
  showed its full type name in the Sequencer inspector's GESTURE combo until
  fixed. The closed box's rendered text has no independently reachable UIA
  surface for this custom, non-editable `ControlTemplate` (confirmed: `Value`
  pattern unsupported, `Name`/`LegacyIAccessible` blank, no descendant tree
  while collapsed, `FromPoint` resolves no deeper than the `ComboBox` itself)
  — a live UI test can only guard this class of bug at the model's `ToString()`
  level, or by asserting the live `SelectionItem` pattern and dropdown-item
  names never contain a type/namespace fragment (see
  `console.tests/SequenceStepModelTests.cs` and
  `console.tests/UiAutomationSmokeTests2.cs`'s Category A remarks).
- Debounced sliders must snapshot target ID and values and cancel on selection
  changes; programmatic loads must suppress write-back hooks.
- WPF `MediaPlayer` teardown is dispatcher-affine. A probe may await without a
  synchronization context, but `Stop`, `Close` and event detachment must be
  marshalled back to the player's owning dispatcher; catching the cross-thread
  exception only hides a live native media resource.

## UI test automation (FlaUI/UIA3)

- FlaUI's `BoundingRectangle`/`Mouse` coordinates are real physical screen
  pixels; WPF's own layout values (`PxPerSecond`/`PxPerMs`, and anything
  bound to them like the Sequencer zoom slider) are device-independent
  (96-DPI logical) pixels. The two only match 1:1 at 100% display scaling.
  Computing a click point by adding a distance derived from a logical
  px/ms rate onto a physical-pixel base (read from a `BoundingRectangle`)
  silently lands short on any scaled display — a real, reproducible failure
  found this session in `OverlappingClips_TheShorterTopmostClipReceivesTheClick`.
  Multiply the logical distance by the real DPI scale first
  (`GetDpiForWindow(hwnd) / 96.0`, `hwnd` from
  `AutomationElement.Properties.NativeWindowHandle`) before combining it with
  a physical-pixel coordinate. A small fixed physical-pixel margin (e.g. "20px
  from this element's edge") needs no such conversion and is the safer choice
  wherever a precise logical distance isn't actually required.
- A shared-instance UI-automation test collection
  (`console.tests/UiAutomationFixture.cs`) drives one real running window with
  timed synthetic input. Letting it run in parallel with the rest of the
  test assembly (xUnit's default across different collections) was observed
  to starve that window's UI thread of CPU under full-suite load and cause
  synthetic clicks to silently miss — 100% reliable in isolation, reproducibly
  flaky only alongside the ~330 other tests. Fixed with
  `[assembly: CollectionBehavior(DisableTestParallelization = true)]`
  (`console.tests/AssemblyInfo.cs`) rather than papering over the race with
  more retries.
- An `ItemsControl`-generated container whose `DataTemplate` root has no
  explicit `Width`/`Height` (e.g. a bare `Canvas` positioning a `Border` via
  `Canvas.Left`) does not itself report a `BoundingRectangle` spanning the
  visible content — only its descendant elements with real rendered size
  (a `TextBlock` label, say) do. Compute click points from a reliable
  descendant's rectangle, not the item container's own.
- A gesture clip's own accessible `Name` for a `TextBlock` bound to a model
  object without a `ToString()` override is that model's full type name
  (e.g. `"b1_chat_console.Models.SequenceStep"`) — useful as a stable,
  content-independent selector for "find every inserted clip container"
  (`FindStepItems()`), distinct from each clip's own visible gesture-name
  label.
- Every owned window this app creates (`SceneDecisionWindow`,
  `SceneNameWindow`, `SceneBrowserWindow`, `FirmwareWindow`, `HelpWindow` —
  modal and non-modal alike) has its content exposed by UIA as part of
  `MainWindow`'s own automation subtree in this environment, not as an
  independent entry in `Application.GetAllTopLevelWindows()` or even
  `Automation.GetDesktop()`. Search `MainWindow`'s own descendants for their
  content instead of treating them as separate windows; closing one needs a
  raw `WM_CLOSE` posted to its own HWND (found via `EnumWindows` scoped to
  the app's process ID), since neither Alt+F4 (depends on real OS keyboard
  focus, and was observed to hit `MainWindow` instead under load) nor a FlaUI
  `Window.Close()` reaches it.
- A synthetic click that silently misses under load can be indistinguishable
  from "correctly did nothing" unless verified by the actual expected
  outcome (e.g. the newly-inserted clip's own label name), not merely a
  count change — a stale retry that re-clicks on every observed miss risks a
  genuine duplicate action if the original actually did land, just late. Only
  retry once the relevant count has proven nothing happened at all.

## Historical decisions retained for safety

- The old web page at `console/wwwroot/index.html` is a frozen French design
  reference and is not rendered at runtime.
- KyberEditor is UX inspiration and the source of `tools/espflash.exe`; its
  firmware and bootloader are not used.
- Droid names are persisted on the targeted droid through `MSG_NAME`, while
  the master's commit draft remains a display concern. Older slaves may ignore
  this additive message.
