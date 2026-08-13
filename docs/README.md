# B1 Chat — documentation index

[`../CLAUDE.md`](../CLAUDE.md) is the entry point and holds the project-wide
rules. Each document below is authoritative for its own area and must be updated
in the same commit as the behavior it describes.

| Document | Owns | Read when |
| --- | --- | --- |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Firmware module map, console structure, storage locations | Finding where a responsibility lives, or adding/removing a module |
| [PROTOCOL-REFERENCE.md](PROTOCOL-REFERENCE.md) | Current mesh message types and USB JSON bridge | Checking a message type or JSON command |
| [FIRMWARE-CONTRACT.md](FIRMWARE-CONTRACT.md) | Historical console ↔ firmware contract and why fields were renamed | Understanding the rationale behind a protocol decision |
| [SEQUENCER-BEHAVIOR.md](SEQUENCER-BEHAVIOR.md) | What the Sequencer currently does at runtime | Changing transport, scheduling, editing or Scene behavior |
| [SEQUENCER-HARDENING.md](SEQUENCER-HARDENING.md) | The 35 actionable Sequencer items, dashboard, decision log, execution order | Picking up Sequencer work or checking what is still open |
| [SEQUENCER-DONE.md](SEQUENCER-DONE.md) | The 41 closed items with acceptance criteria and evidence log | Proving an item shipped, or preparing the SEQ-H08 release gate |
| [SEQUENCER-IDEAS.md](SEQUENCER-IDEAS.md) | EPIC I and K: 30 deferred design ideas | Designing something structural that those ideas must stay compatible with |
| [KNOWN-PITFALLS.md](KNOWN-PITFALLS.md) | Implementation traps: flash/NVS, OTA, concurrency, timing, WPF | Touching firmware storage, OTA, timing, or WPF layout and input |
| [TEST-PROTOCOL.md](TEST-PROTOCOL.md) | Validation scope, what is covered and what is deliberately excluded | Running or adding tests |
| [PROGRESS-ARCHIVE.md](PROGRESS-ARCHIVE.md) | Chronological history, incident narratives, superseded designs | Investigating why something ended up this way |
| [hardware/](hardware/) | Carrier board and servo-hub PCB concepts | Working on the hardware |

Two rules keep this set usable:

- **Behavior lives in one document.** If a fact belongs to an area above, it goes
  there rather than into `CLAUDE.md`, which stays short because it is reloaded as
  context on every turn.
- **History is separate from state.** `PROGRESS-ARCHIVE.md` records what
  happened; the other documents describe what is true now.
