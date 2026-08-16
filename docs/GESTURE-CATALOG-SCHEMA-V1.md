# Gesture Catalog and Scene schema V1

This document owns the strict V2 data boundary introduced in Stage 3A and made
the only Scene persistence format in Stage 3B. The current firmware motion
engine is now generated from this source catalog. The console dispatches the
three initial catalog keys directly; numeric values are never persisted in a
Scene.

## Catalog

`catalog/gesture-catalog-v1.json` is the initial source artifact. Its root is:

```json
{
  "type": "b1-gesture-catalog",
  "version": 1,
  "catalogId": "b1.core",
  "revision": "v1",
  "hash": "sha256:<64 lowercase hexadecimal characters>",
  "gestures": []
}
```

`hash` is an announced, content-derived catalog identity. It hashes UTF-8
catalog text after normalizing line endings and replacing the hash value itself
with 64 zeroes. This avoids a self-reference while making every other byte
significant. After editing the catalog, run:

```powershell
.\tools\generate-gesture-catalog.ps1 -UpdateHash
```

Then commit both the catalog and generated firmware header. `-Verify` rejects a
stale generated header or an invalid catalog hash.
The source format has no `wireId` or `animId`; generated wire IDs are a build
artifact and are never hand-authored or persisted in Scenes.

Each gesture requires a lowercase dotted `key`, display text, family and tags,
one execution kind (`immediate`, `finite`, or `continuous`), `returnToCenter`
end policy, safety flags and a minimum motion-engine generation. It also lists:

- `tempos`: one to three named exact durations. `normal` is mandatory;
  `slow` and `fast` are permitted only after the motion implementation proves
  them safe. Immediate gestures use a zero duration.
- `intensities`: allowed `soft`, `normal`, and/or `strong`. `normal` is
  mandatory. Intensity never changes the planned duration.
- `variants`: permitted variant tokens. `default` is mandatory.
- `seedPolicy`: `required` or `ignored`.

The three initial catalog entries contain normalized trajectory tables and
generate the current firmware header. The authoring library exposes Center, Nod
and Talk until the catalog grows.

## Scene

A V2 Scene has root `{ "type": "b1-scene", "version": 1 }`. It contains a
name, loop/end settings, the exact catalog identity, physical tracks, audio
lanes and `gestureClips`. An old `b1-sequence` file is not a V2 Scene and is
rejected without migration.

Every gesture clip persists:

```json
{
  "id": "D-format GUID",
  "gestureKey": "communicate.nod",
  "target": { "mode": "droid", "id": 43140 },
  "startMs": 1200,
  "intensity": "normal",
  "tempo": "normal",
  "variant": "default",
  "seed": 1847293,
  "holdMs": null
}
```

The explicit target is either one physical droid or `{ "mode": "all" }`.
Continuous gestures require a bounded `holdMs`; other gestures require it to be
`null`. Validation checks the clip against the bound catalog: key, tempo,
intensity, variant, continuous-hold rule and non-truncating Scene end must all
agree.

Unknown and duplicate fields are rejected at every object level. This prevents a
future or misspelled setting from being silently discarded.

## Validation fixtures

`console.tests/Fixtures/V2/scene-v1.json` and the linked source catalog are
parsed in `GestureSceneV2SchemaTests`. They cover named identity, normal-only
tempo, continuous hold, catalog mismatch, unknown numeric identities and timing
boundary failures.
