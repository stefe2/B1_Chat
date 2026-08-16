# Data, Backups & Moving to Another PC

B1 Chat stores different information in different places. Scene export protects
choreography, but no single action is a complete fleet backup.

## Storage map

| Information | Stored where | Survives app-only flash? | Portable export? |
| --- | --- | --- | --- |
| Droid name | Target droid NVS plus master cache | Yes | No; record it before a full erase |
| Servo calibration | Target droid NVS | Yes | No; record it before a full erase |
| Adoption | Master NVS | Yes on the master | No |
| Servos state | Target droid NVS after the operator changes it; virgin/full-erased boards default off | Yes | No |
| Locate state | Target droid runtime only | No; restart clears it | No |
| Scene gesture/audio layout | PC Local Library and exported `.b1scene.json` copies | Not applicable | Yes, `.b1scene.json` |
| Actual audio bytes | Original files on the PC | Not applicable | Copy separately |
| Last COM port and last Scene identity/external path | PC `settings.json` | Not applicable | No |

A full erase destroys all NVS on the board being flashed. An app-only flash is
designed to leave NVS untouched when the board already has the expected
partition layout.

## Scene Library and sequence export

**Save** is the normal working path. It atomically updates a versioned Scene in
the Local Library; **Save As** creates a new GUID-backed identity. The last
library Scene is restored at startup. Removing a Scene moves it to
`library\trash` after confirmation instead of permanently deleting it.

Export writes a `.b1scene.json` snapshot containing named gesture clips, target IDs,
offline track layout, audio-lane layout, and **paths** to audio files. It does
not embed or copy audio. Timeline edits after the export remain only in memory
until the next export.

Export writes and flushes a sibling temporary file before atomically replacing
the destination. A failed write or replacement preserves the previous file and
does not move the editor's saved checkpoint. Export clears Dirty for a new or
external-file document, but never claims that changes to a library-backed Scene
were saved into the library. Save, Import, and Local Library Load establish the
appropriate clean checkpoint; Undo/Redo compares actual document content with
that checkpoint.

For another PC, copy both the sequence JSON and every audio file. Import the JSON,
then use Replace file on any clip whose old absolute path is invalid.

Scene Import validates the complete V2 document and its bound gesture catalog
before replacing the editor. Older `.b1seq.json` files are deliberately
incompatible and are not migrated.
Unknown future versions and invalid fields are refused with a field-specific
error, leaving the open sequence unchanged. Very old DFPlayer track numbers do
not contain PC file paths and therefore cannot restore their original sound
automatically.

## Console-local files

The following directory is used under the signed-in Windows account:

`%LOCALAPPDATA%\B1ChatConsole`

- `settings.json` — last selected COM port and last Local Library Scene identity
  or imported/exported sequence path.
- `serial-trace.log` — recreated at every launch; copy it before relaunching if
  it contains a problem report.
- `library\` — atomic, versioned Local Library Scene entries. Valid legacy JSON
  entries migrate automatically; `library\trash\` retains removed Scenes and
  migration originals for manual recovery.
- `updates\` — downloaded console installers and verified firmware assets.

Do not hand-edit these files while the console is running. Normal sequence
portability should use Export/Import rather than copying `settings.json`.

## Before changing PCs or performing a full erase

1. Save every Scene, then Export external copies needed off this PC.
2. Copy the audio assets used by those sequences.
3. Record every droid's name and calibration values separately.
4. Record which board is master and which boards have previously used OTA.
5. Keep the release/version information with the backup set.

After restoration, verify names, calibration, a small gesture, audio output, and
mesh reachability before running a full sequence.
