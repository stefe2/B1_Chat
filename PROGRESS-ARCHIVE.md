# B1 Chat — Progress archive

Full chronological history of completed steps, moved out of `CLAUDE.md` on
2026-07-19 to keep that file lighter (it's reloaded as context on every
Claude Code turn in this project). Nothing here is deleted — this is purely
an archival split. See `CLAUDE.md`'s own "Progress" section for the
still-open items and a short recent-highlights summary.

## Progress (archived entries)

- [x] Steps 1-5, 7-10: servo_engine, mesh_comm (HMAC, relay), animation (18
      gestures), audio, config_store + registry, serial_console, dashboard,
      sequence_store + standalone playback.
- [x] Sequencer overhaul: catalog by name (hidden slots), real durations
      (`getAnimDurations`), live progress (`seqState`), seqSaved/seqDeleted acks,
      `anim.isPlaying()` guard for steps targeting the master.
- [x] Per-droid "Auto anims" pause (MSG_AUTOANIM, heartbeat bit1, UI column).
- [x] Mesh topology (MSG_NEIGHBORS, mesh_topology module, SVG graph card).
- [x] WPF console v0.8.0: native serial port, auto-reconnect, integrated flashing,
      local library, backup/restore, timeline, Rehearse,
      export/import. `index.html` replaces `web/dashboard_V7.html`.
- [x] "Ecosystem" phase (2026-07-07), firmware 1.0.0: 4 KB serial buffer +
      explicit `err`, enriched handshake (fw/proto/lineMax/caps/dirty), `getAll`
      + `allDone`, contract §3 (`evt:config`), §1 (per-sequence audio track, played
      standalone, per-gesture sounds suppressed when a track exists), §5 (`seqRun
      from`, pause/resume with DFPlayer pause), §4 (atomic `setMulti`),
      KyberEditor-style commit/revert (volume/params/names). Page + C# (GitHub
      checkUpdates, download+install, firmware and console release scripts) shipped.
- [x] Repo merge (2026-07-13): the console (`b1-chat-console`, a
      local repo never pushed) is brought into `console/` in this repo (plain
      copy, one commit, the 6-commit console history not kept — nothing
      was published). Only one GitHub repo now (`stefe2/B1_Chat`), two
      tag trains (`vX.Y.Z` app, `fw-vX.Y.Z` firmware); `MainWindow.xaml.cs`
      adapted (lists the repo's releases and filters by tag prefix instead
      of `/releases/latest`, which would have mixed the two trains).
- [x] Complete rewrite of the console in native WPF (2026-07-13, `index.html`
      kept intact as a reference, no longer rendered at runtime): 8 cards
      ported to XAML/MVVM (`CommunityToolkit.Mvvm`), C# `ProtocolClient` as the
      new central state (native equivalent of JS's `sendCmd()`/`handleEvent()`),
      auto-reconnecting serial, sequencer undo/redo, mesh topology (Canvas +
      circular layout ported as-is).
- [x] Visual polish + rearrangement (2026-07-13): Servo Calibration card taken
      as the reference model (metal slider, value pills, dark
      ComboBox) then propagated to the other cards; header redesigned (logo, status,
      accent CTA); Mesh Topology card redesigned (glossy nodes, radar
      rings, links colored by signal strength) and moved between Calibration and
      Animation; Activity card removed; Firmware card taken out of the grid
      into a separate window (`FirmwareWindow`, dedicated header button);
      Animation and Audio placed side by side above the Sequencer.
- [x] Console firmware-flow overhaul (Master/Slave role chosen explicitly
      before the source, self-sufficient GitHub source with verification, address
      relegated to advanced options) + automatic firmware release via CI
      (`.github/workflows/firmware-release.yml`, triggered by a `FW_VERSION`
      bump; `IS_MASTER` made overridable via `#ifndef` for the
      new `b1_master`/`b1_slave` PlatformIO environments).
- [x] Droid adoption (2026-07-13, fw 1.1.0): a droid never seen before (or
      "forgotten"/declined) is no longer automatically added to the list — the
      console offers Adopt/Ignore (`cmd adopt`/`forget`, status persisted in
      NVS via `config_store`, never in the ephemeral RAM `registry`). "Forget"
      button to remove an already-adopted droid. Fixed along the way:
      `ProtocolClient.HandleDroids` never removed an entry that had disappeared from
      `evt:droids` (latent bug, no visible effect before this feature).
- [x] Per-droid firmware version (2026-07-13, fw 1.2.0): each slave
      reports its version in its heartbeat (3 bytes major/minor/patch),
      stored in the `registry` and exposed via `evt:droids.fw`; new
      FW column in the Droids card. Breaks heartbeat binary
      compatibility (see pitfalls) — the whole fleet must be reflashed together.
- [x] Droids card polish (2026-07-13): fixed-width NAME column, fixed-width
      centered-text STATE/ROLE badges, RSSI shown as `-` (instead of the
      last frozen value) when a droid is "lost", "lost" threshold
      lowered to 4s (`DROID_TIMEOUT_MS` + console-side threshold), sliding
      on/off switches (`OnOffSwitchStyle`) for Servos/Auto anims.
- [x] Automatic firmware release operational (`fw-v1.0.0`, `fw-v1.1.0`
      published via CI). Console: still manual (`gh auth login` once,
      then `console\installer\release.ps1 -Publish`).
- [x] Firmware OTA relayed by the ESP-NOW mesh (2026-07-14, fw 1.3.0): an
      adopted slave can be reflashed without USB from the Droids card
      (`MSG_OTA_START/CHUNK/ACK/END/ABORT`, stop-and-wait, one session at a
      time) + `ota_guard` (manual anti-brick rollback, see the OTA section
      above). So far only tested on a dedicated spare board (see
      Verification) — not yet validated on a real fleet droid.
- [x] Nominal OTA path **validated at the bench** (2026-07-14, fw 1.3.11): full
      transfer 1.3.9 → 1.3.11 (~4 min, 5111 chunks at ~22/s, single hop),
      clean reboot, correct `otaResult{ok:true}` verdict, FW column up to date.
      Required a long bug hunt (fw 1.3.2 → 1.3.11 + console):
      flash access under `portENTER_CRITICAL` in the ESP-NOW callback (freeze at
      chunk 21 → callback→loop mailbox), unsigned overflow in
      `now - timestamp` (instant timeouts, false `age` ~4e9 that crashed
      `HandleDroids` and killed the read loop — the "freezes" at chunks
      716/848/859), serial segment with no retry (console watchdog 3s ×5 +
      master re-ack + 2 KB UART buffers + slave 60s timeout > master's 45s),
      false `rolledBack` (verdict rendered on a pre-reboot heartbeat → 5s
      grace window), and lexicographic sorting of the GitHub `/releases` API (the
      console flashed 1.3.9 thinking it was the latest → semantic
      max). Permanent serial trace added
      (`%LOCALAPPDATA%\B1ChatConsole\serial-trace.log`).
      Still to do (Verification pt 7): corrupted `.bin`, forced rollback,
      console abort mid-transfer, anti-double-session guard,
      multi-hop.
- [x] OTA robustness validated at the bench (2026-07-14 night, fw 1.3.12) via
      `tools/ota-test.ps1` (scriptable serial test bench: replays the
      console protocol and injects faults — corrupted chunk, abort,
      double otaStart):
      · corrupted chunk in flight → `ERR_MD5` (err 7) at END, **no reboot**,
        the slave stays on its image;
      · double `otaStart` → `otaError "busy"`, the in-progress session
        continues undisturbed;
      · console abort at chunk 1200 → master aborts at exactly 45s (reason 11),
        the slave purges the session, the next one starts cleanly;
      · anti-brick rollback (`-D OTA_TEST_FORCE_CRASH` build, inert hook
        kept in main.cpp): 3 boot panics → automatic partition switch
        on the 4th boot → old image recovered in **3.6s**, genuine
        `rolledBack` verdict on the master's side;
      · nominal regression 1.3.11 → 1.3.12 (with the Registry synchronized
        on both sides) → `otaResult{ok:true}` ~1s post-reboot.
      Remaining: multi-hop (3rd board required) and a first OTA on a real
      fleet droid.
- [x] Console v0.9.0 (2026-07-14): OTA hardening cycle (permanent serial
      trace, chunk watchdog, more reliable verdict, semantic release
      max, colored FW column, OTA from GitHub). NSIS installer
      published on GitHub (tag `v0.9.0`, `console\installer\release.ps1 -Publish`).
- [x] Console/firmware English pass + icon + Droids/Mesh-Topology/header
      overhaul (2026-07-14): app icon designed and wired in everywhere
      (csproj, windows, installer); Droids card reworked (disambiguated
      VERSION/UPDATE columns, flash buttons hidden once a droid's firmware is
      confirmed up to date, "Up to date ✓" badge); Mesh Topology card given
      the full treatment — ambient effects (rotating radar sweep, spinning
      ring on the master, starfield, master/slave legend, auto-scaling
      radius) and live-telemetry effects (per-node signal halo,
      heartbeat-flash pulse, OTA travel indicator, hop-wave ripple on
      broadcast, TALK-sync pulse, mini-stats readout); header bar
      regrouped into three visually separated clusters. Entire codebase
      (console C#/XAML, firmware `src/*.h/.cpp` comments, docs, build
      scripts) switched from French to English — see the updated
      convention below. `console/wwwroot/index.html` deliberately left
      untouched (frozen design reference, never rendered).
      **Post-translation crash found and fixed**: connecting to a real
      master with ≥1 droid crashed the console immediately
      (`XamlParseException` → `Cannot animate '(0).(1)' on an object
      instance that cannot be modified`) — see the new Known pitfalls
      entry below for the root cause and fix.
- [x] Mesh Topology: force-directed layout + live packet visualization
      (2026-07-14): node placement replaced the old fixed circular layout
      with a force-directed simulation (`MeshTopologyViewModel.ComputeForceLayout`,
      120 iterations per render, warm-started from the previous frame's
      positions) — master pinned at the canvas center, repulsion between
      all nodes, springs along real links whose rest length now encodes
      RSSI (strong signal pulls a node in, weak signal lets it drift out,
      on top of the existing thickness/opacity encoding), weak gravity so
      unreachable nodes don't drift off-canvas. Added small colored dots
      (`Packets` collection, ~30 fps ticker, `MeshPacketVisual`) that
      travel hop-by-hop along the real BFS-derived master↔node path for
      every mesh frame the console can actually observe: outgoing
      `anim`/`servo`/`autoAnim`/`config`/`calib`/`preview` (new
      `ProtocolClient.PacketSent` event), OTA chunks (one dot per
      `evt:otaChunkAck`, riding the existing dashed travel line), and an
      inbound droid→master dot standing in for heartbeat/neighbor-report
      refreshes (the console never sees individual `MSG_HEARTBEAT`/
      `MSG_NEIGHBORS` frames or any inter-slave relay traffic — only
      what the master reports over serial — so this is a faithful
      visualization of what's observable, not a packet capture). Legend
      row added below the Master/Slave legend. Colors registered in
      `Converters/PacketKindToBrushConverter.cs`.
- [x] Mesh Topology: classic green radar screen skin (2026-07-14, from a
      user-provided `radar.html` mockup): the old dark-square recess +
      orange rings + white starfield background was replaced with a
      circular green-phosphor radar disc (dark green→black radial
      gradient, glowing `#2DFF6F` rim, 3 concentric range rings, 12
      angular spokes, a faster 4s rotating sweep beam, plus a subtle
      vignette + CRT scanline overlay — both implemented as circular
      `Ellipse` fills so they self-clip without an explicit
      `Canvas.Clip`). Scoped entirely to this card via local XAML
      resources, not the global orange `AccentBrush` theme. Node
      placement is now radially clamped (`MeshTopologyViewModel.
      ComputeForceLayout`, `MaxNodeRadius`) instead of per-axis, so a
      node never renders outside the visible disc. Functional accent
      indicators (master ring, heartbeat pulse, hop-wave ripple, TALK
      pulse) were deliberately left orange for contrast against the
      green backdrop. Verified visually with temporary seeded test data
      (canvas is otherwise hidden until droids are present) then reverted.
- [x] Mesh Topology: radar skin brought to full radar.html fidelity
      (2026-07-15, user-approved via an HTML preview artifact before any code
      change): range rings made solid (dashes removed), crosshair brightened
      to .22 vs the .11 diagonal spokes (hierarchy matching the mockup's
      conic ticks), and the old narrow ~13° sweep triangle replaced by a
      wide ~42° beam — WPF has no conic gradient, so the falloff is stepped
      into twelve 3.5° pie slices of increasing alpha plus a glowing
      full-brightness leading-edge radius, all rotating together (4s) in one
      Canvas. Second fidelity pass after user comparison against the HTML
      preview: disc base made an opaque phosphor gradient (#0A3016 →
      #021006 at 62% → black, exactly radar.html's bottom layer) instead
      of a translucent glow over black (read grey, not green), scanline
      overlay dropped 0.06 → 0.02, and the three 25/50/75 % rings replaced
      by two at r ≈ 45/91 px — the CSS color-stop percentages resolve
      against the farthest-corner ray (130·√2), so the mockup really shows
      two rings, the third falling outside the disc. Verified visually
      (seeded test data + automated window screenshot, then reverted):
      smooth falloff, no visible banding, green phosphor look matching the
      approved HTML preview.
- [x] Mesh Topology: fixed-bearing polar layout + packet-dot fix + 300 px
      canvas (2026-07-15): the force-directed simulation was replaced by a
      deterministic polar layout — master pinned at center, each slave at a
      fixed evenly-spaced bearing (3 slaves = 120° apart, assigned by
      ascending id, first at 12 o'clock, reshuffled only when the slave set
      changes) and **only the radius moves with RSSI** (strong ≈ 42 px,
      weak ≈ 122 px; multi-hop child = parent radius + RSSI-scaled hop
      segment; unreachable = rim). Radius changes are eased by a ~30 fps
      exponential lerp (`LayoutTick`, `RadiusLerpRate` 3 s⁻¹) so RSSI
      jitter glides instead of jumping — `MeshNodeVisual`/`MeshEdgeVisual`
      became mutable ObservableObjects so the ticker drags nodes, edges,
      packet paths and the OTA line without rebuilding collections. Fixed
      along the way: the traveling packet dots had NEVER rendered on links
      — they all piled up at the canvas origin (see the new ItemsControl/
      Canvas pitfall below); verified live on the real 4-droid fleet
      (heartbeat dot caught mid-flight on a link). Drawing canvas enlarged
      260 → 300 (disc unchanged at 260, offset inner canvas at 20,20) so
      the rim's green glow and edge labels are no longer clipped.
- [x] Droids card: "Locate" button (2026-07-15, fw 1.4.0, `MSG_LOCATE` = 15):
      a per-droid toggle overrides the onboard LED's normal execution-indicator
      blink with solid on/off, so a physical droid can be matched to its row
      — a mesh round-trip (console → master → relayed to the targeted slave),
      not a local-only master feature, same target/broadcast semantics as
      `MSG_SERVO`/`MSG_AUTOANIM` (`applyLocate()`, `gLocateOn` in
      `main.cpp`, checked first in the life-LED block of `loop()`). Ephemeral
      by design — not persisted in NVS, not carried in the heartbeat: a
      reboot or console restart silently drops back to the normal blink,
      consistent with `MSG_PREVIEW`'s "transient, not persisted" precedent.
      Console: `Droid.LocateOn`, `ProtocolClient.SetLocate`,
      `DroidsViewModel.ToggleLocateCommand`, styled as a `HaloToggleButtonStyle`
      `ToggleButton` (red border off / green on, matching the Master/Slave
      role selector in the Firmware card) rather than the sliding
      `OnOffSwitchStyle` used for Servos/Auto anims, since it's a momentary
      "identify" action, not a persistent setting. Also wired into the mesh
      topology's live packet-dot visualization (`PacketSent` event, lime
      `#C6FF4D`, legend row) for consistency with every other targeted
      command. Same Droids-card pass: the ID column was dropped (RSSI now
      follows VERSION directly — the hex id was rarely load-bearing once a
      droid has a name), the Servos/Auto anims columns were narrowed
      (90/90 → 60/70 px) to sit closer together, and the row-end "✕"
      (forget) button — briefly moved next to Auto anims in an earlier pass
      — was moved back after Update, at the row's end.
- [x] Sequencer: absolute-time timeline model (2026-07-16, fw 1.5.0, proto 3,
      `caps: seqTimeline`), replacing the old chained relay-only player.
      `SeqStep.delayMs` (relative, chained) → `startMs` (absolute offset from
      the sequence's own t=0); `StoredSequence` gained `totalMs` (explicit
      loop/end boundary — no longer implied by "the last step") and
      `audioStartMs` (the audio track's own cue point, previously implicit
      at step 0). `main.cpp`'s player (`sortStepsByStart` + `gSeqStartMs`
      anchor + `gSeqNextFireIdx`) can now fire several steps in the same
      `loop()` pass — the actual point of the rework: droids can start
      **together**, not just relayed one after another. Traded away:
      `gSeqWaitLocal` (the one guarantee that the master's own gesture in a
      running sequence never got cut short) — a later local step now
      interrupts it exactly like any live `MSG_ANIM`, matching what already
      happened to remote slaves (no mesh ack, never had that guarantee).
      Protocol: `seqSave`/`seqData` steps carry `start` (was `delay`) plus
      top-level `totalMs`/`audioStartMs`; `seqRun` takes `fromMs` (was
      `from`, a step index — now a scrub offset); `seqState` reports
      `elapsedMs`/`totalMs` (was `index`/`total`). Fields renamed, not just
      reinterpreted, specifically so an old console/firmware pairing drops
      an unrecognized field instead of misreading it (see Known pitfalls).
      **Breaking, on purpose**: `SequenceStore`'s NVS blob now requires an
      exact size match, so a sequence saved before this rework reads back
      as "not found" rather than replaying with the wrong timing — the 8
      slots on any master already in service need re-saving from the
      console after the update. Console (`SequenceStep.StartMs`,
      `ProtocolClient.SeqSave` computing `totalMs` server-side so every save
      path — slot save, library push — stays consistent, local rehearsal
      rewritten from one chained timer to one timer per step so steps
      sharing a `StartMs` actually fire together) adapted to match; the flat
      step-list editor itself (raw numeric fields, no visual timeline) was
      superseded shortly after by the real multi-track timeline below.
- [x] Sequencer: real multi-track visual timeline (2026-07-16), replacing the
      flat step-list editor entirely. New `Views/SequenceTimelineView`
      (embedded in `SequencerCardView`, `SequencerViewModel` as `DataContext`,
      no new ViewModel): one horizontal track per droid + a synthetic
      "All droids" broadcast row (`Models/TimelineTrack`), a ruler with
      zoomable ticks (`Models/TimelineTick`, 20-300 px/s slider), draggable
      colored gesture clips positioned/sized via `Converters/
      TimelineGeometryConverter` (one converter, `Left`/`Width`/`Top`/
      `Duration` `ConverterParameter` modes) and colored by family via
      `Converters/AnimFamilyToBrushConverter`, an inspector panel
      (gesture/target/start-time with ±0.1s nudge, duplicate/delete), and a
      click-to-insert gesture library row. Clip dragging is raw mouse capture
      (`SequenceTimelineView.xaml.cs` — first mouse-interaction code in this
      app, no `Thumb`/native `DragDrop` precedent existed) snapping to 100ms,
      one `PushHistory()` per drag gesture so Undo restores it in one step.
      Playhead: local scrub via the ruler when idle, or synced to real
      hardware playback via `ProtocolClient.SeqStateReceived` (previously
      unconsumed) — a 30ms `DispatcherTimer` computes position directly from
      an elapsed-time anchor (no easing, unlike Mesh Topology's telemetry
      tickers) so it can't be scrubbed while `IsLiveTracking`. Also closed a
      second previously-flagged gap: `ProtocolClient.AnimDurationMs` (fetched
      via `getAnimDurations`, previously unconsumed by any UI) now drives
      clip width, self-correcting once real durations arrive post-handshake
      (`AnimDurationsReceived` event, new). Verified live against the real
      4-droid fleet (COM3): correct per-track/per-time placement, drag +
      snap, zoom rescaling, gesture insertion on the armed track, and the
      inspector — the last found and fixed three WPF-specific bugs along the
      way (`ComboBox.SelectedValue`/`SelectedValuePath` unreliable against
      `DarkComboBoxStyle`'s fully-replaced `ControlTemplate` → replaced with
      a `SelectedItem`-bound `SelectedStepTrack` wrapper property; that
      wrapper then went stale across a `DroidsChanged`-triggered track
      rebuild until explicitly re-`OnPropertyChanged`'d; `TimelineTrack`
      needed its own `ToString()` since `DarkComboBoxStyle` renders
      `SelectedItem` via `SelectionBoxItem`, which falls back to
      `ToString()` rather than `DisplayMemberPath`). Out of scope, parked for
      a later phase: a local "My Sequences" project bin (distinct from the
      existing per-item local library) and a live gesture recorder.
- [x] Sequencer: console-side audio, DFPlayer set aside "for now" (2026-07-16):
      per explicit decision, a sequence's audio no longer comes from the
      master's DFPlayer (`AudioTrack`/track-number field kept internally for
      wire-protocol compatibility — `SeqSave` still sends it, always 0 now —
      but no longer surfaced in the UI). Instead the **console** plays a
      local audio file directly during `Rehearse (local)`, via the new
      `Services/AudioPlaybackService` (thin `System.Windows.Media.MediaPlayer`
      wrapper, no new NuGet dependency — also used to probe a picked file's
      exact duration, `ProbeDurationMsAsync`, opening the file just long
      enough to read `NaturalDuration`). `SequencerViewModel` gained
      `AudioFilePath`/`AudioDurationMs` (browse/clear commands, undo/redo,
      export/import, local-library round-trip); `TotalDurationMs()` (ruler
      extent, rehearsal end timer) now takes `Math.Max` of the steps' span
      and the audio's real duration, so a long audio-only sequence still
      gets a correct ruler/loop boundary. The timeline gained a dedicated,
      non-arm-able "♪ AUDIO" row showing the file as a bar from t=0 (fixed,
      not draggable — rehearsal always starts it at the pass's own t=0, no
      offset UI yet). Because the master's 8 NVS slots have no room for a
      filesystem path, the slot↔file association lives **client-side only**,
      in the new `Services/SequenceAudioStore` (`slot-audio.json`, see
      Storage table) — a slot pulled/pushed/deleted from a different console
      install or after a local cache wipe simply has no audio attached
      (same "no worse than before" fallback as a missing local-library
      item). Real-hardware playback sync (starting the file in sync with a
      live `Play` on the mesh, using the already-wired `audioStartMs`) was
      explicitly deferred — confirmed with the user as scope for a later
      pass, current scope is `Rehearse (local)` only. Verified live against
      the real fleet (COM3): file picked via the new "…"/"✕" controls next
      to the (now-unused) Loop checkbox, `AudioDurationMs` probed correctly
      and reflected on the ruler/audio bar, `Rehearse (local)` starts the
      file (`AudioPlaybackService.Play`) together with the gesture timers and
      auto-stops it cleanly at the end of the pass with no exceptions.
      **Superseded the same day** by the multi-lane/multi-clip rework below —
      the single `AudioFilePath`/`AudioDurationMs` fields and the toolbar's
      "…"/"✕" picker no longer exist.
- [x] Sequencer: multi-lane/multi-clip audio, gesture drag-and-drop, per-track
      mute (2026-07-16), replacing the single-audio-file model above the same
      day, plus three more editing features requested in the same batch —
      confirmed via two `AskUserQuestion` rounds and a written plan before
      any code changed, per this project's confirm-before-coding rule.
      **Audio**: `AudioFilePath`/`AudioDurationMs` → `Models/AudioLane`
      (named row, e.g. "AUDIO"/"AMBIENT") each holding an
      `ObservableCollection<Models/AudioClip>` (`FilePath`, `DurationMs`,
      `StartMs`, `Loop`). Clips are independently draggable in time and may
      freely overlap **within** a lane (no collision handling — layered
      rendering only); a lane's "+" adds a clip via file picker, a clip's
      right-click `ContextMenu` (new `Themes/Effects.xaml` dark
      `ContextMenuStyle`/`MenuItemStyle` — first `ContextMenu` use in this
      app) offers Replace file…/Loop/Delete. `AudioPlaybackService` now
      tracks a `List<MediaPlayer>` instead of one, so several clips (e.g. an
      SFX plus a looping ambient bed) play concurrently; `Loop` wires
      `MediaEnded` to restart the player, torn down by `StopAll()` at the end
      of a rehearsal pass (never outlives `IsRehearsing`). Persistence
      (`SequenceAudioStore` → `slot-audio.json`, `SequenceLibraryItem`,
      `SequenceSnapshot`, export/import) all moved from a single path+
      duration pair to `List<AudioLaneDto>` (nested `AudioClipDto` list) —
      breaking, on purpose, same day the singular shape shipped, so no
      migration path was written (see the sequence-timeline-rework pitfall
      below on reinterpreting a stored field's meaning — this instead
      replaced the shape outright before anything depended on the old one).
      **Gesture retargeting**: dragging an existing clip now also updates
      `SequenceStep.Target` from the drag's Y position (new
      `SequencerViewModel.TrackAtY(double)`, `TimelineTrack.RowHeight`/
      `RowGap` math), alongside the existing horizontal `StartMs` drag —
      one `Undo` restores both axes together (still a single
      `BeginStepDrag()`/`PushHistory()` per gesture, not per pixel).
      **Gesture-library drag-and-drop**: chips gained a click-vs-drag
      threshold (5px) in `SequenceTimelineView.xaml.cs` — under threshold
      falls back to the existing click-inserts-on-armed-track-at-playhead
      behavior unchanged; past it, a floating ghost (`DragGhostCanvas`, a
      `Panel.ZIndex="999"` overlay Canvas spanning the whole card, positioned
      every `MouseMove` via `Canvas.SetLeft/Top`) follows the cursor and
      dropping over `TracksCanvas` calls the new `SequencerViewModel.
      InsertGestureAt(animId, track, startMs)` with the drop's actual
      track+time instead of the armed-track/playhead defaults — still no
      native `DragDrop.DoDragDrop` anywhere in this app, same raw-mouse-
      capture idiom as every other drag here, just driving a manually
      positioned ghost element instead of repositioning a real item.
      **Per-track mute**: `TimelineTrack` gained `[ObservableProperty] bool
      Muted` (the class itself went from a plain POCO to an `ObservableObject`
      for this one property) plus a small `Button` in each gutter row
      (`ToggleMuteCommand`) — deliberately a real `Button`, not a `Border`+
      `MouseBinding` like the row's own arm-click, because `ButtonBase`
      marks its `Click` handled and stops it bubbling to the row's
      `MouseBinding`; a `Border`+`MouseBinding` mute toggle would have also
      armed the track underneath it on every click. `RebuildTracks()` (fired
      on every heartbeat-driven `DroidsChanged`, wholesale-replacing
      `Tracks`) now carries `Muted` forward by `Id` from the previous
      generation, same pattern already used for `ArmedTrack`, so a mute
      doesn't silently reset a few seconds later. **Mute only ever affects
      `Rehearse (local)`** (`ScheduleRehearsalPass()` skips arming a timer
      for a muted target) — a real hardware `Play` cannot honor it: `seqRun`
      just starts the master, which then replays its own NVS-stored steps
      from its own `loop()`, and the console has no per-step veto over that
      once it's been sent. Mute state is not saved with the sequence (not in
      `Snapshot()`/export/slot data) — it's a live editing/audition aid, reset
      whenever a sequence is loaded/created. Verified live against the real
      4-droid fleet (COM3): two overlapping SFX clips on "AUDIO" dragged
      independently, a looping clip added to "AMBIENT", an existing gesture
      clip dragged from one droid's row to another's (retargeted correctly,
      confirmed in the inspector), a library chip dragged directly onto a
      specific droid+time cell (landed exactly there, distinct from a plain
      click), a track muted and dimmed, and a full `Rehearse (local)` pass
      with all of the above active at once — ran cleanly, stopped exactly at
      the computed total duration, no exceptions.
- [x] Sequencer: waveform preview + cross-lane audio drag (2026-07-16), two
      more additions to the same batch above. **Waveform**: new `NAudio`
      NuGet dependency (`console/Services/WaveformService.cs`) — the only
      decoder in this app with raw sample access, `AudioPlaybackService`'s
      `MediaPlayer` has none. Decodes a clip's file to a fixed 120-point
      min/max-style peak envelope (0..1) off the UI thread, cached by file
      path (`ConcurrentDictionary`, shared across every clip that happens to
      reference the same file) so re-showing or duplicating a clip never
      re-decodes. `AudioClip.Peaks` (new, nullable `float[]`) is populated
      asynchronously from every clip-creation path (`AddAudioClip`,
      `ReplaceAudioClip`, `ApplyAudioLanesFromDto` on load/import/library
      pull). Rendered via `Converters/WaveformToGeometryConverter.cs`
      (`Peaks` → a filled `PathGeometry` in a fixed `x:[0,119] y:[0,2]`
      domain) hosted inside a `Viewbox Stretch="Fill"` layered behind the
      clip's label — the fixed-domain geometry rescales to whatever pixel
      width the clip currently has (zoom, drag) without ever recomputing
      peaks. Not persisted (`Peaks` is derived, not part of `AudioClipDto`)
      — reloading a sequence just re-decodes from the stored file path.
      **Cross-lane drag**: audio clips previously only moved horizontally
      within their own lane's `Canvas` — a live re-parent into another
      lane's `Canvas` mid-drag isn't practical the way it is for gesture
      clips (which all share one flat `TracksCanvas`, only `Canvas.Top`
      changes). Reused the app's existing ghost-drag idiom instead (same
      `DragGhostCanvas`/`GhostBorder` already built for the gesture-library
      drop): `SequencerViewModel.AudioLaneAtY(double)` (mirrors `TrackAtY`,
      new `AudioLane.RowHeight`/`RowGap` consts alongside the pre-existing
      `TimelineTrack` ones) is checked on every `AudioClip_MouseMove`; once
      the cursor crosses into a different lane's row, the real clip dims
      (new transient `AudioClip.Dragging` bool, not persisted) and a ghost
      follows the cursor instead, snapping back the moment it re-enters its
      origin lane. The actual move (`SequencerViewModel.
      MoveAudioClipToLane` — remove from the source lane's `Clips`, add to
      the target's, `StartMs` carried over from the live horizontal drag)
      only happens once, at `MouseUp`, inside the same `BeginAudioClipDrag()`
      `PushHistory()` already armed at `MouseDown` — one Undo restores both
      the lane and the time position together. **Also**: the two default
      seeded lanes were reordered to `AMBIENT` then `AUDIO` (was the
      reverse) per direct request — cosmetic, no data-shape change.
      Verified live (2026-07-16, once the foreground window matched on a
      retry): added a real `.wav` file to the AMBIENT lane, waveform
      rendered correctly; dragged the same clip from AMBIENT into AUDIO,
      it re-parented cleanly (source lane emptied, waveform intact in the
      new lane). `dotnet build` clean throughout.
- [x] Sequencer timeline: visual pass toward the "Sequencer v2" concept mockup
      (2026-07-16), first batch of 4 purely-visual items picked from the mockup
      by the user (screenshots + a short back-and-forth, confirmed before
      coding per this project's standing rule) — no protocol/behavior changes.
      **Track-gutter rows** (`TimelineTrack.Role`, new "MASTER"/"SLAVE"/
      "BROADCAST" caption set in `RebuildTracks()`): each row now shows the
      droid name plus a small caps role line underneath, and the old 🔇/🔊
      text `Button` was replaced by a real `OnOffSwitchStyle` `ToggleButton`
      (glossy green/red slider, already used for Servos/Auto anims/Locate) —
      `IsChecked` bound through the new `Converters/BoolInvertConverter.cs`
      so ON/green reads as "audible" rather than showing green for "muted",
      keeping the same green=active convention as everywhere else; still a
      real `ToggleButton` (not `Border`+`MouseBinding`) for the same
      click-bubbling reason as the button it replaced. **Timeline grid**:
      two new `ItemsControl`s added as the first (bottommost-rendered)
      children of `TracksCanvas` and of each audio lane's `Canvas` — one
      bound to `Tracks`/`AudioLanes` drawing a full-width row background +
      1px bottom separator (`#59000000`) per row, one bound to the *same*
      `RulerTicks` collection the ruler itself uses (so grid lines can never
      drift out of sync with the time labels) drawing 1px vertical
      `Rectangle`s at each tick, dimmer for minor ticks than major —
      broadcast row and audio lanes get a faint accent tint (`#FF9D2E` at
      3-4% opacity) matching the mockup's `.track-lane.audio`/`.broadcast`.
      **Transport bar**: the two previously-separate button rows
      (Undo/Redo/New/Save/→Library above the timeline, and
      Play/Stop/Pause/Resume/Rehearse/Export/Import below it, both in
      `SequencerCardView.xaml`) are consolidated into *one* recessed dark
      bar now living in `SequenceTimelineView.xaml`'s own toolbar row —
      round icon buttons (new `IconTransportButtonStyle`, same beveled
      chrome as every other button, just square+glyph) for Play/Stop/
      Pause/Resume, a `⟲ Loop` `HaloToggleButtonStyle` toggle (moved off
      the Name row, which now just has the name field), a live timecode
      pill (new `SequencerViewModel.TimecodeText`, "mm:ss.mmm /
      mm:ss.mmm", refreshed on every `PlayheadMs` change and on every
      ruler-tick rebuild since total duration can change), the existing
      zoom/snap, a new **Fit** button (`SequenceTimelineView.xaml.cs`,
      code-behind rather than a ViewModel command since it needs the
      `ScrollViewer`'s actual pixel width — a view concern —
      `TotalDurationMs()` made `public` for this one caller), then
      Undo/Redo/Rehearse/Export/Import/Add-audio-lane/Live-badge. `New`/
      `Save (ESP32)`/`→ Library` are the only buttons left in
      `SequencerCardView.xaml` — they don't have a mockup equivalent, so
      weren't moved. **Gutter header**: the empty 24px spacer above the
      track rows now reads "TRACKS" (small bold caps, muted gray,
      bottom-bordered), matching the mockup's `.ruler-spacer`, aligned
      with the ruler's own 24px height. Verified live (2026-07-16, once the
      foreground window matched on a later retry) against the real 4-droid
      fleet: gutter shows correct MASTER/SLAVE/BROADCAST roles and glossy
      toggles, transport bar renders and its Fit button correctly rescales
      zoom to the sequence length, "TRACKS" header aligned with the ruler.
      `dotnet build` clean throughout.
- [x] Sequencer timeline: 3 follow-up fixes from direct user feedback after
      seeing the above live (2026-07-16), found and fixed with a temporary
      red debug-`Rectangle` at canvas origin to pin down the first one
      empirically rather than guessing from a screenshot:
      **(1) Pre-existing centering bug** (not caused by this session's other
      changes — reproduced back through earlier screenshots too): the
      `ScrollViewer`'s content `StackPanel` (ruler + audio lanes + tracks)
      had no explicit `HorizontalAlignment`, so whenever the sequence is
      short/zoomed out (content narrower than the viewport), WPF's default
      content stretching centered it inside the scroll viewport instead of
      pinning it flush against the gutter — the ruler's "0.0s" origin then
      floated away from x=0 with a wide gap that scaled with window width
      (confirmed via the debug rectangle landing exactly on the gap's far
      edge, matching the ruler/playhead exactly, not just visually near it).
      Fixed with one `HorizontalAlignment="Left"` on that `StackPanel`.
      **(2) Gutter toggles at 50%**: the new `OnOffSwitchStyle` track-row
      switches (from the batch above) are now wrapped in a `LayoutTransform
      ScaleTransform 0.5/0.5` — `LayoutTransform`, not `RenderTransform`, so
      the row's `Auto`-sized column shrinks along with the visual instead of
      leaving dead space; scoped to just this one `ToggleButton` instance so
      the shared `OnOffSwitchStyle` (Servos/Auto anims/Locate in the Droids
      card) stays full-size everywhere else. **(3) Active-row highlight**: a
      new `DataTrigger` on the row `Border` (`Muted=False` → soft green
      `DropShadowEffect`, `#3DDC84` at 0.45 opacity) makes an audible/active
      track's whole row glow, not just its small switch — mirrors the same
      green already used for the toggle's own on-state. Verified live
      (screenshot comparison before/after): gap gone (ruler origin flush
      against the gutter), switches visibly smaller, "All droids" row
      glowing green while un-muted. `dotnet build` clean.
- [x] Sequencer: gesture library grouped by family + card header badge
      (2026-07-16), continuing the same visual pass as the batch above.
      **Gesture library**: the flat wrapped row of 18 chips became labeled
      rows (`SequencerViewModel.GestureFamilies`, `Models/GestureFamily.cs`)
      — "IDLE & REST", "LOOK & CURIOSITY", "AFFIRMATION", "SCAN & TRACK",
      "ALERT & GLITCH", "TALK (AUDIO-SYNCED, LOOPS)". Grouping/labels come
      from a new `AnimFamilyToBrushConverter.Families` static table (single
      source of truth, reused by the ViewModel so the grouping can never
      drift from the colors every clip/chip already uses) instead of
      duplicating the animId→family mapping a second time. The flat
      `GestureLibrary` list stays too (used by the drag-ghost's name lookup
      in `SequenceTimelineView.xaml.cs`). **Card header**: `SequencerCardView`
      gained the same icon-box + subtitle + right-aligned badge treatment
      already used by Droids/Mesh Topology — a small vector "steps" glyph,
      "Multi-track timeline — parallel per-droid lanes, absolute start
      times" subtitle, and a live badge (`SequencerViewModel.
      SequenceBadgeText`, updates on `CurrentSlot`/`Name` change) reading
      `SLOT 2 · "PARADE"` or `UNSAVED · NEW SEQUENCE`. `dotnet build` clean.
- [x] DFPlayer retired everywhere, firmware and console (fw 1.6.0,
      2026-07-16) — the console already owned multi-track audio playback
      client-side (see the Sequencer-audio entries above); this removes the
      master's own DFPlayer entirely rather than just leaving it unused.
      **Firmware**: `src/audio.{h,cpp}` deleted outright (the
      `AudioPlayer`/`Audio` wrapper, anim→track-range table); every
      `Audio.*` call site in `main.cpp` removed (`setup()`'s init, the
      per-gesture sound on `playLocalAnim`/incoming `MSG_ANIM`/the master's
      own idle-anim draw, the stored-sequence player's audio-cue block and
      its DFPlayer-only-plays-one-track-at-a-time gesture-sound
      suppression, `onSeqPauseCmd`'s `Audio.pause()/resume()`) — the
      sequence *player* itself (step firing via `anim.play()`/mesh
      broadcast) is untouched, only the DFPlayer calls interleaved with it
      are gone. `src/serial_console.{h,cpp}`: `playTrack`/`volume` command
      handlers and their `onVolume`/`onTrack` hook plumbing removed;
      `track`/`audioStartMs` dropped from `seqSave`/`seqData`/`seqList`/
      `seqState` (`pushSeqState` lost its `track` parameter entirely);
      `trackCount` dropped from the `hello` ack; the now-meaningless
      `seqTrack` capability flag removed from `caps[]`.
      `src/config_store.{h,cpp}`: `volume()`/`setVolume()` and the `"vol"`
      NVS key removed, along with their branches in `refreshDirty()`/
      `commitPending()`/`revertPending()` (volume was part of the
      commit/revert draft, same machinery as anim params/names).
      `src/sequence_store.{h,cpp}`: `track`/`audioStartMs` dropped from
      `StoredSequence`/`SequenceBlob` — the blob shrinks, and the existing
      **exact-size-match** guard (same one added for the fw 1.5.0 timeline
      rework) means every slot saved before this update reads back as "not
      found" rather than being misread — **all 8 slots need re-saving from
      the console** after updating a master past fw 1.6.0. `platformio.ini`
      lost the `dfrobot/DFRobotDFPlayerMini` dependency.
      `FIRMWARE-CONTRACT.md` §1 (audio track) and §2 (`getTrackDurations`,
      already deferred/unimplemented) marked removed. **Console**:
      `Views/AudioCardView.xaml(.cs)` + `ViewModels/AudioViewModel.cs`
      deleted outright (the whole "AUDIO (MASTER)" card — volume slider,
      test-track button); `MainWindow.xaml`'s Animation/Audio 2-column row
      collapsed to just `AnimationCardView` at full width;
      `ProtocolClient.SetVolume`/`PlayTrack`/`LastVolume`/`TrackCount` and
      the `evt:config` volume parse / `hello` trackCount parse removed;
      `SeqSave`'s `track`/`audioStartMs` parameters dropped (every caller
      updated). `SequencerViewModel.AudioTrack` (already vestigial —
      always 0, no UI control bound to it since the client-side multi-lane
      audio rework, see above) removed along with every read/write site
      (`Snapshot`/`Apply`/`NewSequence`/`OnSeqData`/`SaveToSlot`/
      `SaveToLibrary`/`LoadFromLibrary`/`PushToMaster`/`Export`/`Import`);
      `SequenceSlotMeta.Track`/`SequenceLibraryItem.AudioTrack`/
      `SequenceSnapshot.Track` dropped from their model records.
      `DroidsViewModel`'s Backup/Restore no longer reads/writes a `"volume"`
      key (old backup files with one are simply ignored on restore, same
      safe-additive-removal precedent as everywhere else this session).
      Physically unplugging the DFPlayer/amp is a separate, optional
      hardware task — out of scope here, the firmware just stops calling
      into it. Verified: `pio run` clean on `b1`/`b1_master`/`b1_slave`;
      `dotnet build` clean.
- [x] Sequencer: Play and Rehearse merged into one console-driven Play, with
      real Pause/Resume (fw unaffected, console-only — 2026-07-16). Until
      now the transport had two disconnected paths: hardware `Play`/`Stop`/
      `Pause`/`Resume` (sent `seqRun`/`seqStop`/`seqPause`/`seqResume` to
      the master, which replayed its own NVS-stored sequence — required a
      prior "Save (ESP32)") and a separate "Rehearse (local)" toggle (the
      console scheduled its own timers straight from the editor — no save
      needed — firing real per-step `anim` mesh commands plus local audio,
      but no pause/resume and no playhead feedback). Per direct request
      ("quand je presse sur Play l'animation joue dans la console avec le
      son et déclenche en même temps les animations sur les droids"): the
      old hardware-backed Play/Stop/Pause/Resume are gone, and Play now
      always works the way Rehearse did — console-driven, no slot required.
      `SequencerViewModel`: `IsRehearsing` → `IsPlaying`/`IsPaused`;
      `ScheduleRehearsalPass()` → `ScheduleTimers(int fromMs)`, now taking
      an offset so only steps/clips whose `StartMs >= fromMs` get armed
      (delay `StartMs - fromMs`) — this is what makes `Resume()` skip
      everything that already fired before a pause instead of replaying it.
      `Pause()` computes elapsed from the same anchor the playhead ticker
      already used, disposes pending timers (nothing to "pause" about a
      one-shot trigger that hasn't fired yet) and calls the new
      `AudioPlaybackService.PauseAll()` (clips already mid-playback keep
      their position natively via `MediaPlayer.Pause()` — no manual seek
      math); `Resume()` calls `PauseAll()`'s counterpart `ResumeAll()` and
      re-arms the remaining timers via `ScheduleTimers(elapsedAtPause)`.
      The hardware-`seqState`-driven playhead/LIVE-badge logic
      (`OnSeqState`) and the new console-driven path now share one
      `StartPlayheadTicker(fromElapsedMs)` helper instead of duplicating
      the anchor/`DispatcherTimer` setup, so both sources animate the
      playhead identically. Mute's documented limitation ("only affects
      Rehearse, a real hardware Play can't honor it") is now moot — Play
      *is* the console-driven mechanism, so mute applies unconditionally.
      **Accepted trade-off, confirmed explicitly**: the console no longer
      has any way to trigger the firmware's own onboard `seqRun` playback
      (the tested "sequence saved → master reboot → Play works without a
      PC" path) — the firmware keeps that capability fully intact and
      working (see fw Verification above), just nothing in the Sequencer UI
      calls it anymore. `Views/SequenceTimelineView.xaml`: the standalone
      "Rehearse (local)" button removed; the existing 4 icon buttons (▶ ■
      ⏸ ⏵) rebound to the new commands, Pause/Resume auto-disable via
      `[RelayCommand(CanExecute=...)]` (`CanPause`/`CanResume`, same pattern
      already used for Undo/Redo). Verified: `dotnet build` clean; not yet
      re-verified live against the real fleet this pass (see the DFPlayer
      entry above for the build verification already done) — needs a
      hands-on Play/Pause/Resume/Stop/Loop pass against real hardware.
- [x] Sequencer: full visual polish to the "Sequencer v2" mockup's look
      (2026-07-16, second batch — look only, no new features, plan confirmed
      before coding; reference = the same approved concept artifact, fetched
      and mirrored CSS-value-for-CSS-value). **Body**: rail + timeline +
      inspector unified into one recessed rounded container (mockup `.body`)
      — the gutter's old floating 110px boxes became a flush 150px rail
      (`BgBrush`, contiguous rows with bottom separators, 3px accent left
      bar + orange tint when armed, glowing status dot, 📡 broadcast row,
      accent-tinted "♪ LANE" audio rows with an AUDIO LANE caption).
      **Geometry**: `TimelineTrack`/`AudioLane` `RowHeight` 48→52,
      `RowGap` 4→0 (contiguous mockup rows); clips float inside their row
      via a new `TimelineGeometryConverter` "ClipTop" mode (`ClipInsetY` 5,
      clip height 42) so row backgrounds still use raw "Top"; `TrackAtY`
      switched Round→Floor to match. `TimelineWidthPx` now also floors at
      the visible viewport width (`ViewportWidthPx`, pushed by the view on
      `ScrollViewer.SizeChanged`) so row backgrounds/gridlines fill the body
      even for a short/empty sequence (mockup `max(content, viewport)`).
      **Ruler**: 30px on new `Recess2Brush` (#26282B, added to Theme.xaml),
      bottom-anchored short/tall ticks, labels up top, bottom hairline, and
      a glowing pentagon playhead flag (mockup `.playhead .flag`); the
      tracks-canvas playhead line gained the matching accent glow.
      **Gesture clips**: vertical family gradient (new "Gradient" parameter
      on `AnimFamilyToBrushConverter`, cached frozen brushes, `Lift(+43)` =
      mockup's `lighten(18)`), rounded 5px, drop shadow, two-line content —
      name + real duration ("2.00s", new `AnimDurationTextConverter` sharing
      the Width mode's 800ms fallback so label always matches drawn width) —
      and a ⟲ badge on the two looping gestures (16/17). **Audio clips**:
      translucent orange gradient + orange border (was opaque accent),
      waveform recolored light orange (#8CFFB766), file label top-left in
      #FFD9A3. **Transport**: timecode split into `TimecodeNowText` (accent)
      + `TimecodeTotalText` (muted gray) rendered as two `Run`s (must be
      `Mode=OneWay` — `Run.Text` defaults TwoWay and the sources are
      get-only), 🔍 glyph for zoom, mockup ordering. **Gesture library**:
      drawer-style bordered container ("GESTURES" header + right-aligned
      hint), family labels tinted with their family color (new
      `GestureFamily.ColorAnimId`), chips restyled from solid family-color
      pills to neutral panel pills with a 3px family-colored left edge +
      mono `#id` + ⟲ on loops (mockup `.chip`); the drag ghost now takes the
      full family color from the converter instead of copying the (now
      neutral) chip background. **Inspector**: flush right panel inside the
      body (BgBrush, left separator), family color strip under the gesture
      picker, red-tinted Delete text (BeveledButtonStyle's template
      hardcodes its BorderBrush, so only Foreground can signal danger).
      **Removed on request: the "Save (ESP32)" button** (SaveToSlotCommand
      still exists in the VM; pushing to a slot remains possible via the
      Local Library's "→ ESP32"). Catalog/Local Library/name field/New
      left as-is — explicitly deferred to a later pass. Verified:
      `dotnet build` clean, launched + full-window screenshots (empty
      state) matching the mockup; a live pass with real droids/sequence
      data still pending.
- [x] Sequencer timeline: 6 fixes from direct user feedback on the polish
      pass above, seen live with the real 4-droid fleet (2026-07-16,
      confirmed before coding): **(1+2 — one root cause)** row backgrounds
      and separators were piling on row 0 and every vertical gridline was
      stacking at x=0 — the ItemsControl/Canvas pitfall again, this time
      disproving the old note's "works on the template root" claim (see the
      amended pitfall below); the gridline templates got a Canvas root, and
      the row-background ItemsControl dropped Canvas positioning entirely
      (plain vertical StackPanel — Tracks is already in row order and rows
      are contiguous RowHeight, so stacking IS the layout). **(3)** every
      droid row now gets the same accent-tinted background the broadcast
      row appeared to have (per direct request — what the user saw was
      actually 5 stacked 1.5% white layers), uniform, no IsBroadcast
      trigger. **(4)** gridline opacities raised (minor .05→.08, major
      .12→.18) so the second markers visibly run down to the last droid.
      **(5)** right-click ContextMenu on gesture clips (Duplicate/Delete,
      same Tag=ViewModel trick and dark menu styles as the audio clips'
      menu). **(6)** transport zoom glyph switched from the 🔍 emoji (color
      font, ignores Foreground, rendered dark) to Segoe MDL2 Assets E71E in
      theme text color; and the rail's broadcast row shows the same status
      dot as every other row (📡 special case removed, per direct request).
- [x] Sequences made 100% console-side + fourth Sequencer feedback batch
      (2026-07-16, fw 1.7.0 / proto 4, console build 225 — every item
      confirmed before coding, including "retirer les slots du firmware
      aussi" via an explicit disambiguation question). **Firmware 1.7.0**:
      `src/sequence_store.{h,cpp}` deleted; the whole `seq*` command/event
      family removed from `serial_console.{h,cpp}` (handlers, `push*`
      emitters, `hello.seqSlots`, `seqTimeline`/`seqPause` caps, the
      seqSave/seqDelete `setMulti` ops — an old backup carrying them is now
      rejected with an explicit reason, atomically); `main.cpp` lost the
      onboard player (gSeq* state machine, sortStepsByStart, onSeq* hooks)
      — the master keeps relaying live `anim` commands, its idle draw no
      longer checks gSeqPlaying. `FW_PROTO` 3→4 (commands removed =
      breaking); builds clean on b1/b1_master/b1_slave; **not yet
      released/flashed** — bump is in `config.h`, CI will release on push.
      **Console**: all slot plumbing stripped end-to-end (`ProtocolClient`
      `seq*` helpers/events/catalog + `SequencerViewModel` LoadSlot/SaveToSlot/
      DeleteSlot/PushToMaster/PullFromMaster/CurrentSlot +
      `SequenceAudioStore` service deleted + `SequenceSlotMeta` record);
      Local Library's "→ ESP32" button removed; header badge is name-only.
      **Sequence files carry the droid roster** (new `tracks` array in
      `.b1seq.json` v4 + `SequenceLibraryItem.Tracks`): on load/import with
      droids unplugged, each saved droid keeps its own named row (role
      "OFFLINE", `RebuildTracks` appends them after live rows, plus hex-id
      rows for pre-roster files' step targets) instead of everything
      collapsing onto one line; a droid coming online takes its row over.
      **Fixes/UX from direct feedback**: Pause button fixed — root cause:
      `[RelayCommand(CanExecute=...)]` never re-evaluates without
      `[NotifyCanExecuteChangedFor]` on the properties it reads, so Pause
      stayed disabled forever (same latent bug fixed on Undo/Redo);
      `TotalDurationMs()` now uses real gesture durations (was flat
      +1500 ms — the reason "Fit" kept cutting the last clip's tail) and
      "Fit" also scrolls back to t=0; ruler ticks/gridlines now span the
      whole drawn width incl. viewport ("la trame reste en pleine
      longueur"), minor gridlines raised to 0.20; smooth drag — clips move
      freely at pixel level while held, Snap (100 ms grid) applies at
      release only, gestures and audio clips alike; audio clips use the
      same SizeAll cursor as gestures; audio lanes renamable in place
      (borderless TextBox in the rail; `AudioLane` became an
      ObservableObject); clicking empty timeline space deselects; name
      field + New + "→ Library" buttons removed from the card (consequence
      accepted explicitly: no way to ADD to the Local Library anymore —
      Export/Import files remain the save path until a future "My
      Sequences" pass). Verified: console build clean, firmware builds
      clean ×3, full-width grid confirmed by screenshot; hands-on pass
      (Pause fix, offline-roster import, lane rename/delete) still to do —
      and the fleet still runs fw 1.6.0, which is fine (the console simply
      ignores the extra hello fields and never sends `seq*`).
- [x] Sequencer: third feedback batch (2026-07-16, 9 items + 3 amendments,
      confirmed before coding). **Visual**: major gridline opacity .18→.30
      (seconds markers readable); audio clips get the same SizeAll move
      cursor as gesture clips; a gesture clip dims to 55% while held/dragged
      (new transient `SequenceStep.Dragging`, set by the view for the mouse
      capture's duration — never serialized) and the selected clip gains a
      white halo on top of its outline (trigger placed last so it wins over
      active/muted). **Behavior**: `Fit` (zooms so the whole sequence fits
      the visible width) now also scrolls back to t=0 — previously a
      scrolled view could sit past the fitted content; `Duplicate` nudges
      the clone +200 ms right and selects it (was landing exactly on top of
      the original, invisible); new `DeleteAudioLane` (right-click a lane's
      rail row → dark ContextMenu), any lane deletable including the two
      seeded ones, asks via MessageBox when the lane still holds clips;
      new `Clear` transport button after Import (`ClearTimeline`: empties
      steps + all lanes' clips, keeps the lanes and the name, asks first
      when Dirty) — both undoable via the existing history. **Removed**:
      the "CATALOG (8 ESP32 SLOTS)" section from SequencerCardView (per
      direct request) — the master's 8 NVS slots and the VM plumbing
      (Catalog/LoadSlot/DeleteSlot) remain, the only UI path into them is
      now the Local Library's "→ ESP32". Verified: build clean, screenshot
      (Catalog gone, Clear present); the interactive behaviors (drag dim,
      duplicate offset, confirmations, lane delete) still need a hands-on
      pass.
- [x] Sequencer: fifth feedback batch (2026-07-17, console build 227,
      confirmed before coding). **Droid row background** made a neutral
      light tint (#FFFFFF at 3%, was the same #FF9D2E accent tint as the
      audio lanes) so the orange tint now reads as "audio row" only.
      **Resume button removed** — Play doubles as Resume: pressed while
      paused it resumes from the pause point (`ResumeCommand`/`CanResume`
      deleted, logic folded into `Play()`), otherwise it (re)starts from
      t=0; a Play pressed mid-playback now also calls
      `AudioPlaybackService.StopAll()` first (previously it re-armed timers
      without stopping in-flight audio → overlapped playback, latent).
      **Major gridlines** raised .30→.40 (both the tracks canvas and the
      audio lanes). **Truly smooth 2-axis drag** — root cause of the
      "jumps droid to droid" complaint: the previous drag updated
      `SequenceStep.Target` live on every MouseMove, so the clip teleported
      row-by-row (52px) the instant the cursor crossed a boundary (and an
      audio clip stayed dimmed in place with only a small ghost following
      the cursor, teleporting lanes at release). Now both clip kinds glide
      with the cursor at pixel level on BOTH axes via a new transient
      `DragOffsetY` (`SequenceStep`/`AudioClip`, drives a bound
      `TranslateTransform` in the clip templates — a Canvas doesn't clip
      children, so an audio clip stays visible outside its own lane's row)
      and the row/lane, like the 100ms time snap, only settles at mouse-up
      (released outside the tracks/lanes area vertically → snaps back to
      its original row/lane). The audio drag's ghost-border mechanism is
      gone (the ghost remains for gesture-library chip drags, which have no
      real element to move); audio clips dim to the same 55% "in hand" as
      gesture clips (was 25% cross-lane-only). One Undo still restores
      time + row/lane together. Verified: build clean (0 warnings),
      screenshot (⏵ gone, neutral vs orange row tints, brighter second
      markers); the drag feel itself still needs the user's hands-on pass.
- [x] Header: manual Save/Revert replaced by a debounced auto-commit (2026-07-17,
      fw 1.8.0/proto 5), plus a firmware+console dead-code sweep in the same
      pass. The header's "unsaved" badge (`MainWindow.xaml`) now shows on its
      own — the Save/Revert buttons and `MainViewModel.CommitChangesCommand`/
      `RevertChangesCommand` are gone. `ProtocolClient.SetName`/`SetConfig`
      (the only two setters that dirty the master's draft) now arm a 2s
      `System.Threading.Timer` (`ScheduleAutoCommit`, same dispatcher-remarshal
      pattern as the existing keepalive timer), re-armed on every call so it
      only fires once, 2s after the *last* edit, and only commits if `Dirty &&
      HasCap("commit")` — avoids writing to the master's NVS on every
      keystroke/slider tick. Since nothing sends `cmd:"revert"` anymore, it was
      removed end-to-end rather than left dormant: `ConfigStore::revertPending()`
      + the `serial_console.cpp` handler deleted, `FW_PROTO` 4→5, `FW_VERSION`
      1.8.0. **Dead-code sweep** (two `Explore` agents audited `src/` and
      `console/` independently, findings reviewed before deleting): firmware —
      `ServoEngine::isEnabled/pan/tilt`, `AnimationPlayer::current()`,
      `SerialConsole::isClientReady()` (zero callers), `MESH_DEDUP_CACHE`/
      `IDLE_ANIM_MIN_MS`/`IDLE_ANIM_MAX_MS` (config.h constants nobody read —
      the real values are hardcoded literals elsewhere, and no longer even
      matched what these claimed), two stale "volume" comments (DFPlayer-era
      leftovers). Console — `SequencerViewModel.SaveToLibrary()`/
      `CurrentTrackDtos()` (button removed earlier, method forgotten),
      `ProtocolClient.SeqSlotMax`/`AnimCount` (write-only) and
      `AllDoneReceived` (event with no subscriber), `Effects.xaml`'s
      `MeshRadarRingStyle` (superseded by a locally-scoped style, never
      referenced), `Theme.xaml`'s `OkDimBrush`/`WarnDimBrush`/`SansFont`
      (unused), two unused `App.xaml.cs` usings, five stale comments
      referencing removed concepts (DFPlayer, the old slot catalog, the old
      "Rehearse (local)" vs hardware-Play split that Play/Rehearse-unification
      already erased). `Models/SequenceSlotMeta.cs` renamed to
      `SequenceLibraryModels.cs` (the class it was named for no longer exists
      in the file). Deliberately left alone: `UpdateInfo.Notes` (GitHub release
      body, captured but not yet shown in any UI) — not dead, parked for a
      future "what's new" block in `FirmwareCardView`, now documented inline
      as such; `DroidsViewModel.AnyOtaActive` (has a real internal reader, just
      no XAML binding); `droid.{h,cpp}` and the anim freq/amp/speed
      params/`onConfig` hook (both already documented above as deliberately
      incomplete, not dead). Verified: `pio run -e b1` and `dotnet build` both
      clean throughout.
- [x] In-app Help window (2026-07-17, console build 228), modeled after
      KyberEditor's own Help viewer (`C:\Program Files\KyberEditor\Help`) —
      same **content** shape (`manifest.json` sections→pages, Markdown pages
      under `docs/**`) but a **native WPF** rendering instead of Kyber's
      WebView2 mini-browser, deliberately: this console dropped WebView2
      entirely on 2026-07-13 and re-adding it just for Help would have been a
      step backward. New `Markdig.Wpf` NuGet dependency
      (`Markdig.Wpf.Markdown.ToFlowDocument`) converts a page's Markdown text
      straight to a `FlowDocument` for a `FlowDocumentScrollViewer` — no
      `MarkdownViewer` control, no JS. `Themes/HelpStyles.xaml` overrides
      every `Markdig.Wpf.Styles.*StyleKey` (headings/links/code/quotes/tables)
      to match this app's dark palette instead of the library's default
      black-on-white; scoped to `HelpWindow` only (merges `Theme.xaml` itself
      so it doesn't depend on merge order at the call site) since nothing
      else in the app renders Markdown. `HelpViewModel` loads
      `Help/manifest.json` once, exposes the current page's raw Markdown, and
      resolves relative links between pages (`droids.md`, `../firmware/ota.md`,
      etc.) the same way the old `index.html`'s `app.js` did — `HelpWindow`'s
      code-behind binds a `CommandBinding` on `Markdig.Wpf.Commands.Hyperlink`
      (the library routes link clicks through a command rather than
      `Hyperlink.RequestNavigate`) to navigate internal links inside the same
      window and `Process.Start` external ones. New "Help" button in the
      header, next to "Firmware…", same singleton-reopen pattern as
      `OpenFirmwareWindow`. Content: 12 pages across 8 sections (Getting
      Started, Droids, Calibration, Mesh Topology, Animation, Sequencer ×3,
      Firmware ×2, Reference ×2) — screenshots and per-card "?" contextual
      entry points deliberately deferred to a later pass (text-only content
      for now). **Bug found and fixed during verification**: the static
      `Markdown.ToFlowDocument(markdown)` helper defaults to a bare pipeline
      with *no* extensions, unlike `MarkdownViewer`'s own default — Markdown
      tables silently rendered as raw `| a | b |` text instead of an actual
      `Table` until `MarkdownToFlowDocumentConverter` was made to pass an
      explicit `new MarkdownPipelineBuilder().UseSupportedExtensions().Build()`
      pipeline. Verified live end-to-end via a scripted UI-automation pass
      (real click simulation, not just a static screenshot): Help opens on
      Overview with the full 8-section sidebar, sidebar navigation between
      pages, an inline Markdown link clicked at its real screen coordinates
      correctly navigated within the same window (Overview → Droids Card, no
      browser opened), tables render as real grids after the fix, and
      re-clicking "Help" while already open refocuses the existing window
      instead of duplicating it. `dotnet build` clean (0 warnings).
- [x] Help window: relative image support + logo on the Overview page
      (2026-07-17, same day, follow-up to the Help window above). Markdig.Wpf's
      image renderer builds a `BitmapImage` straight from the Markdown `src`
      with no base URI, so a plain `images/x.png` never resolved on its own —
      `HelpViewModel.ResolveImagePaths()` now rewrites every image link in a
      page's Markdown (relative to that page's own folder, same rule
      `TryNavigateInternalLink` already used for page links) to the image's
      real path on disk, emitted as a `file://` URI (`Uri.AbsoluteUri`, not a
      raw Windows path — this repo's own path contains a space, "B1 Chat",
      which breaks unescaped Markdown link syntax). New
      `Themes/HelpStyles.xaml` `ImageStyleKey` override caps images at
      220×220 (the library's default binds Max Width/Height to the bitmap's
      own native pixel size — no scaling at all, would otherwise blow out the
      content column). `Help/docs/images/b1-chat-logo.png` (user-supplied
      artwork) added and referenced at the top of `getting-started.md`,
      mirroring KyberEditor's own banner-image treatment. Verified via the
      same scripted UI-automation screenshot pass as the Help window itself.
- [x] Droids card: "⚙ Configure" button opens Servo Calibration in a dedicated window,
      pre-targeted (2026-07-19), plus a matching main-grid reorg — three ideas from the same
      "the console feels cluttered" conversation, compared first via an HTML mockup artifact
      (four layout options: workspace tabs, fixed Droids + tabs, collapsible cards, Sequencer
      popped out) before any code changed, per this project's standing confirm-before-coding
      rule. **New `CalibrationWindow.xaml(.cs)`**: same singleton-reopen pattern as
      `FirmwareWindow`/`HelpWindow` (`MainWindow.xaml.cs`'s `OpenCalibrationWindow(Droid)` sets
      `vm.Calibration.SelectedTarget = target` *before* showing/activating the window, so it's
      already on the right droid). `DroidsViewModel` gained `OpenCalibrationRequested`
      (`Action<Droid>`) + `[RelayCommand] OpenCalibration(Droid)`, mirroring the existing
      `OpenFirmwareRequested` plumbing but per-droid instead of global.
      `DroidsCardView.xaml`: new ⚙ column inserted just before the existing ✕ (Forget) column
      (both grids — header and row template — needed the same `<ColumnDefinition Width="40"/>`
      appended, and the Forget button's `Grid.Column` shifted 9→10) — card `Width` 780→820 to
      absorb it, hidden while `IsPending` like the row's other per-droid actions. **Grid
      reorg** (`MainWindow.xaml`): `CalibrationCardView` removed from the grid entirely;
      `MeshTopologyCardView` promoted to its old spot (Row 0, Col 1, beside Droids); row count
      4→3 (`Animation` and `Sequencer` shift up one row each); `Droids` lost its `RowSpan="2"`
      (only one card next to it now). The "Main grid layout" paragraph in `CLAUDE.md` was also
      corrected in the same pass — it still described a since-removed Audio column from the
      fw-1.6.0 DFPlayer retirement, never updated at the time.
      **Bug found and fixed during verification**: `Droid` had no `ToString()` override, so
      `CalibrationCardView`'s `DROID` ComboBox (`DarkComboBoxStyle`) showed the literal CLR
      type name (`b1_chat_console.Models.Droid`) instead of the droid's name once opened
      standalone — same `SelectionBoxItem`-falls-back-to-`ToString()` pitfall already hit and
      fixed on `TimelineTrack` for the Sequencer (see Known pitfalls), just never surfaced
      before because Calibration always lived inline in the main grid. Fixed the same way:
      `Droid.ToString() => DisplayLabel`. Icon chosen for the button via a follow-up
      `AskUserQuestion` round (⚙ / ⛭ / ⚒ / a Segoe MDL2 Assets "Equalizer" glyph) → settled on
      ⛭ (gear without hub, thinner, sits better next to the ✕). Verified live end-to-end
      against the real fleet via UI Automation scripting (not just a screenshot): clicked ⚙ on
      B1-Bureau, confirmed the new window opened pre-targeted with B1-Bureau's real calibration
      values loaded, confirmed the `ToString()` fix rendered "B1-Bureau (0504)" instead of the
      type name, confirmed the reorganized grid (Droids | Mesh Topology, Animation full width,
      Sequencer full width). `dotnet build` clean (0 warnings) throughout.
- [x] Console startup: auto-connect with retry, last-sequence auto-reload, and a real bug fix
      for an unplugged master (2026-07-19), three items from one request, each verified live.
      **Auto-connect**: `RefreshPorts()` already ran at startup and pre-selected
      `SettingsService.LastPort`, but never actually connected — `MainViewModel` now also
      calls a new `TryAutoConnect()` once immediately and then every 3s via
      `_startupConnectTimer` (a `System.Threading.Timer`, same idiom as
      `SerialLinkService`'s own post-drop reconnect loop) until the last port reappears and
      opens successfully; the timer disposes itself as soon as `_link.Opened` fires (any
      connect, automatic or manual) or the user explicitly hits `Disconnect`, so it never
      fights a deliberate user action. **Last sequence**: `SettingsService` gained
      `LastSequencePath` (same load/save shape as `LastPort`, `settings.json`). Since the
      Sequencer's "→ Library" save path was removed back on 2026-07-17 (see above), "last
      saved" was clarified with the user to mean the last file explicitly Exported or
      Imported, not the (now largely dead) Local Library folder — confirmed via
      `AskUserQuestion` rather than assumed. `SequencerViewModel.Export()`/`Import()` now
      call `_settings.SetLastSequencePath(...)` after a successful write/read; the shared
      parsing logic was extracted from `Import()` into a new `ImportFrom(string path)` so a
      new `TryLoadLastSequence()` (called once from `MainViewModel`'s constructor, no dialog,
      silent on a missing/corrupt file) can reuse it without popping the file picker at
      startup. **Bug fix**: `ProtocolClient.OnClosed(bool unexpected)` only called
      `ClearLiveState()` (droids/mesh links) when the disconnect was voluntary
      (`!unexpected`) — an actual unplugged master left the Droids/Mesh Topology cards frozen
      on stale "online" data while `SerialLinkService`'s auto-reconnect loop quietly retried
      in the background, instead of visibly reflecting the loss like a manual Disconnect does.
      The `if (!unexpected)` guard was removed — `ClearLiveState()` now runs unconditionally,
      so both paths are now, deliberately, the exact same code path. Verified live: auto-connect
      confirmed by relaunching with a real `lastPort` already saved and seeing `Connected — fw
      1.9.0` with no manual click; last-sequence reload confirmed with a planted test
      `.b1seq.json` + `settings.json` entry, reopened to find the Sequencer badge and timeline
      already showing the test sequence. `dotnet build` clean throughout.
- [x] Main window: mouse-wheel scroll fix (2026-07-19). Reported as the page scroll
      "sticking"/going jerky specifically around the Animation card. Automated repro attempts
      (UI-Automation-driven synthetic wheel events over every control in that card — both
      ComboBoxes, all three sliders, even a ToolTip left open mid-scroll) never reproduced a
      stuck scroll, most likely because synthetic discrete wheel notches don't match a real
      mouse/trackpad's continuous delta stream — but the reported symptom is a well-known WPF
      class of bug regardless (a densely packed card increases the odds some child control
      intercepts the wheel before the outer `ScrollViewer` sees it). Fixed with the standard,
      low-risk countermeasure rather than chasing an unreproducible root cause further: the
      main `ScrollViewer` (`x:Name="MainScroll"`) now handles `PreviewMouseWheel` — which
      tunnels from the window down, arriving *before* any child gets a chance — and always
      performs the scroll itself (`ScrollToVerticalOffset` + `e.Handled = true`), making page
      scroll authoritative everywhere in the main window regardless of what's under the
      cursor. Confirmed safe for the two other scrollable regions in scope: ComboBox popups
      are separate top-level windows (routed events don't tunnel through `MainScroll` at all)
      and the Sequencer timeline's own `ScrollArea` is horizontal-only
      (`VerticalScrollBarVisibility="Disabled"`), so it was never competing for vertical wheel
      in the first place. Verified live via the same UI-Automation wheel-simulation harness:
      scroll now progresses monotonically to 100% across every previously-suspect control.
      `dotnet build` clean.
- [x] Native window chrome recolored to match the app's dark theme (2026-07-19), two related
      requests in the same session. **Title bar**: new `Services/DarkTitleBar.cs`
      (`DwmSetWindowAttribute`, Windows 11 22H2+) sets `DWMWA_USE_IMMERSIVE_DARK_MODE` (so the
      minimize/maximize/close glyphs switch to their light variant) plus
      `DWMWA_CAPTION_COLOR`/`DWMWA_BORDER_COLOR`/`DWMWA_TEXT_COLOR` to mirror `Theme.xaml`'s
      `Bg2Brush`/`TextBrush` — replacing the default white Windows title bar that previously
      sat, jarringly, right above the app's own dark header. `DarkTitleBar.Apply(Window)`
      defers to `SourceInitialized` if the window's HWND doesn't exist yet (a fresh `Window`
      right after `InitializeComponent()` has none), applies immediately otherwise; fails
      silently on pre-22H2 Windows (`DwmSetWindowAttribute`'s HRESULT is ignored) rather than
      throwing. Wired into all four windows (`MainWindow`, `FirmwareWindow`, `HelpWindow`,
      `CalibrationWindow`). **Scrollbars**: new implicit (`x:Key`-less) `ScrollBar` style in
      `Effects.xaml` — applies automatically to every `ScrollBar` app-wide, including the ones
      nested inside plain `ScrollViewer`s and the `DarkComboBoxStyle` popup, with no changes
      needed to any of the ~6 existing `ScrollViewer` usages. Floating rounded thumb only (no
      visible track box, no line-up/line-down arrow buttons — mouse wheel/drag/page-click
      cover it), `PanelHiBrush` at rest, `ButtonHoverBrush` on hover, `AccentBrush` while
      dragging (`Thumb.IsDragging`), matching the app's existing hover/press language.
      **Also same session**: the Droids-card ⚙ icon swapped for ⛭ (thinner, gear-without-hub)
      per direct request after comparing options. Verified live via window screenshots (title
      bar text/buttons legible on dark, scrollbar thumb visible-but-unobtrusive, no default
      light-chrome bleeding through anywhere) across the resized main window. `dotnet build`
      clean (0 warnings) throughout.
