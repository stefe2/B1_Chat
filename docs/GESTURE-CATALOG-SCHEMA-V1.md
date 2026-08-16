# Gesture Catalog and Scene schema V1

This document owns the strict V2 data boundary introduced in Stage 3A and made
the only Scene persistence format in Stage 3B. The current firmware motion
engine is still transitional until Stage 4; a narrow console-only execution
adapter maps the three initial catalog keys at dispatch time. Numeric values are
never persisted in a Scene.

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

`hash` is an announced catalog identity in this contract. Stage 4's generator
will calculate and replace the fixture placeholder before runtime adoption.
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

The three initial catalog entries are intentionally metadata-only: trajectory
tables and generated firmware code are introduced in Stage 4/5, not guessed in
the schema layer. The 3B adapter supports only Center, Nod and Talk, so the
authoring library exposes only those three gestures until the catalog grows.

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
