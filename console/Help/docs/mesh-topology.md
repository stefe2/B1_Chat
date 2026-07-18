# Reading the Radar

The Mesh Topology card shows the live shape of the ESP-NOW mesh as a
green-phosphor radar display — which droid can hear which, how strong each link
is, and what's currently traveling across it.

## Nodes

- The **master** sits pinned at the center.
- Each **slave** sits at a fixed bearing (evenly spaced around the disc,
  assigned by ID so a droid keeps the same angular position across sessions) —
  only its **radius** moves, and it moves with signal strength: a strong link
  pulls a node in close, a weak one lets it drift toward the rim.
- A droid with no path back to the master at all sits at the rim.
- Radius changes glide smoothly rather than jumping, so ordinary RSSI jitter
  doesn't make the display twitchy.

## Links

Lines between nodes are real direct radio links (each droid periodically
reports its own direct neighbors). A **multi-hop** droid — one the master can't
hear directly — is drawn connected through whichever droid is relaying for it;
moving that droid out of range makes its **direct** link vanish while a relayed
path, if one still exists, remains visible through another droid.

Link color/thickness encodes signal strength, from a strong short green line to
a faint one near the rim.

## Live traffic

Small colored dots ride along the links in real time, one per kind of message
the console can actually observe:

- Outgoing commands (gesture, servo toggle, config, calibration, preview,
  **Locate**) — one dot per command, colored by kind (see the legend row under
  the disc).
- OTA chunks — one dot per acknowledged fragment, riding the OTA progress line
  (see [OTA over the Mesh](firmware/ota.md)).
- Inbound heartbeat/neighbor-report traffic — a generic droid→master dot,
  standing in for the periodic housekeeping messages the console doesn't see
  individually.

This is a faithful visualization of what the **master reports over serial** —
it is not a full packet capture, and it can't show inter-slave relay traffic the
master itself never surfaces to the console.

## Legend

The row below the disc explains the master/slave node styling and each traffic
dot color.
