# Codex Instructions

Start every task by reading [`CLAUDE.md`](CLAUDE.md). It is the project bootstrap
document: it holds the project-wide rules that always apply — architecture
essentials, non-negotiable safety and compatibility rules, commands, and git and
handoff rules — and it is deliberately short.

`CLAUDE.md` is **not** the whole documentation. It opens with a *Source of
truth* routing table, and each linked document under `docs/` is authoritative
for its own area. Follow that table before touching a subsystem instead of
working from `CLAUDE.md` alone:

| Area | Read before touching |
| --- | --- |
| Firmware, console or storage structure | [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) |
| Serial or mesh protocol | [`docs/PROTOCOL-REFERENCE.md`](docs/PROTOCOL-REFERENCE.md), then [`docs/FIRMWARE-CONTRACT.md`](docs/FIRMWARE-CONTRACT.md) for the rationale |
| Console Sequencer | [`docs/SEQUENCER-BEHAVIOR.md`](docs/SEQUENCER-BEHAVIOR.md), and [`docs/SEQUENCER-HARDENING.md`](docs/SEQUENCER-HARDENING.md) to pick up tracked work |
| Firmware storage, OTA, timing, or WPF layout and input | [`docs/KNOWN-PITFALLS.md`](docs/KNOWN-PITFALLS.md) |
| Tests and validation | [`docs/TEST-PROTOCOL.md`](docs/TEST-PROTOCOL.md) |
| Why something is the way it is | [`docs/PROGRESS-ARCHIVE.md`](docs/PROGRESS-ARCHIVE.md) |

When behavior or a decision changes, update the document that owns that area in
the same commit — not `CLAUDE.md`, unless a project-wide rule itself changed.
[`docs/README.md`](docs/README.md) lists every document and what it owns.
