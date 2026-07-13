# CLAUDE.md — Projet B1 Chat (contrôle multi-droïdes B1 Battle Droid)

Fichier de suivi unique du projet (fusion de l'ancien `project.md` et du CLAUDE.md
de la console — **à maintenir à jour à chaque étape terminée**, demande explicite
de l'utilisateur).

## Vue d'ensemble

Un seul dépôt git (`stefe2/B1_Chat`), deux moitiés :

1. **Firmware ESP32** (racine du dépôt, PlatformIO/Arduino) : pilote plusieurs
   têtes de droïdes B1 (2 servos pan/tilt chacune) en réseau **ESP-NOW mesh
   multi-sauts**, avec animations fluides/organiques coordonnées par un
   **maître** qui joue aussi le son (DFPlayer Mini + ampli). Réglages persistés
   en NVS.
2. **Console de supervision** (`console/`, WPF net8.0-windows + WebView2,
   v0.8.x) : application de bureau qui possède le port série
   (`System.IO.Ports`) et rend **`console/wwwroot/index.html`** — la page
   unique de l'UI (HTML+CSS+JS inline, tout en français), **copie canonique**
   (remplaçant direct de l'ancien `web/dashboard_V7.html`, supprimé). Fusionnée
   dans ce dépôt (elle vivait avant dans `b1-chat-console`, dépôt séparé sans
   remote — jamais publié ; historique perdu au passage, aucune perte réelle).
   `FIRMWARE-CONTRACT.md` liste les extensions de protocole que la console
   attend du firmware.

Deux trains de release GitHub distincts au sein du **même** dépôt, distingués
par le préfixe du tag : `vX.Y.Z` pour l'app console, `fw-vX.Y.Z` pour le
firmware (voir `tools/release.ps1` et `console/installer/release.ps1`).

## Commandes

- `pio run -e b1` — compile le firmware (pio.exe : `%USERPROFILE%\.platformio\penv\Scripts\pio.exe`)
- `pio run -e b1 -t upload` — flash une carte (rôle choisi par `IS_MASTER` dans `src/config.h` **avant** de flasher : 1 = maître, un seul par réseau ; 0 = esclave)
- `tools\espflash.exe write-bin --port COMx -B 460800 0x10000 .pio\build\b1\firmware.bin` — flash sans PlatformIO (espflash 4.4.0, aussi utilisé par la carte Firmware de la console)
- `dotnet build` (depuis `console/`) — compile la console WPF
- `.\tools\release.ps1 [-Publish]` — release firmware (2 rôles + manifeste SHA-256, tag `fw-vX.Y.Z`)
- `.\console\installer\release.ps1 [-Publish]` — release console (publish + installeur NSIS, tag `vX.Y.Z`)

## Matériel (DOIT ESP32 DevKit V1)

| Signal | GPIO |
| --- | --- |
| Servo PAN | GPIO25 |
| Servo TILT | GPIO26 |
| DFPlayer RX (maître) | GPIO17 (TX2), via 1 kΩ |
| DFPlayer TX (maître) | GPIO16 (RX2) |
| DFPlayer BUSY (maître) | GPIO4 (câblé, **pas encore exploité**) |
| LED de vie | GPIO2 (onboard) |

- Servos en 5 V externe (BEC), masse commune, condo ≥ 470 µF conseillé.
- Audio : DAC_L/DAC_R du DFPlayer → ampli externe (PAM8403) → 1 haut-parleur (maître).
- Broches à éviter : strapping GPIO0/2/5/12/15 ; input-only GPIO34-39.
- 4-6 droïdes prévus (extensible), servos SG90/MG996R.

## Architecture firmware (`src/`)

Firmware **unique** ; rôle par build (`IS_MASTER`), identité auto (srcId 16 bits =
2 derniers octets de la MAC — brancher → flasher → terminé, aucun ID à gérer).

| Fichier | Rôle |
| --- | --- |
| `main.cpp` | setup()/loop(), câblage des modules, timers non bloquants |
| `config.h` | rôle, pins, bornes servo par défaut, constantes mesh/audio/topologie |
| `mesh_comm.{h,cpp}` | ESP-NOW : en-tête {srcId,seq,ttl,type}, dédup (srcId,seq), relais TTL, HMAC-SHA256 tronqué 8 o, **voisinage radio direct** (MAC émetteur physique + RSSI) |
| `mesh_topology.{h,cpp}` | (maître) agrégateur d'arêtes dirigées {from,to,rssi} du graphe de voisinage |
| `servo_engine.{h,cpp}` | PWM LEDC natif 50 Hz, easing smootherstep, bruit d'idle, limites calibrables |
| `animation.{h,cpp}` | 18 keyframes-anims, lecteur non bloquant, seed de variation, `totalDurationMs()` |
| `audio.{h,cpp}` | (maître) wrapper DFPlayer, mapping anim → plage de pistes (10 pistes `/mp3/0001..0010.mp3`) |
| `registry.{h,cpp}` | (maître) inventaire vivant : srcId, RSSI, lastSeen, servos, autoAnim |
| `config_store.{h,cpp}` | NVS : noms, volume, params d'anim, calibration servo par droïde |
| `sequence_store.{h,cpp}` | (maître) NVS : 8 slots de séquences nommées, ≤ 32 étapes {targetId,animId,delayMs} |
| `serial_console.{h,cpp}` | (maître) pont JSON USB ↔ mesh pour la console |
| `droid.{h,cpp}` | machine à états haut niveau (étape 6, **pas encore fait**) |

Dépendances : `DFRobotDFPlayerMini`, `ArduinoJson`. Build flags : `-D MESH_TTL=4`,
`-D GROUP_KEY="changeme"` (clé **compilée uniquement**, pas de re-clé à l'exécution).

## Protocole mesh (ESP-NOW broadcast, canal fixe)

Trame = header + payload + HMAC(8 o, TTL exclu de la signature). Relais : dédup
(srcId,seq) en ring buffer, puis si ttl>0 → ttl-- et re-broadcast. Deux séries de
B1 avec des `GROUP_KEY` différents s'ignorent ; messages falsifiés rejetés.
Anti-rejeu : dédup + seq monotone (suffisant pour un prop, pas une garantie
cryptographique absolue).

| Type | Charge utile |
| --- | --- |
| `MSG_ANIM` = 1 | targetId (0xFFFF = tous), animId, syncDelayMs, seed |
| `MSG_CONFIG` = 2 | targetId, freq, amplitude, speed |
| `MSG_HEARTBEAT` = 4 | uptime, état (bit0 = servos, bit1 = anims auto) |
| `MSG_SERVO` = 5 | targetId, enabled |
| `MSG_CALIB` = 6 | targetId, 6 bornes pan/tilt (persisté par le droïde ciblé) |
| `MSG_PREVIEW` = 7 | targetId, pan, tilt (transitoire, non persisté) |
| `MSG_AUTOANIM` = 8 | targetId, enabled (pause des anims spontanées au repos) |
| `MSG_NEIGHBORS` = 9 | count + [{id, rssi}] : rapport périodique du voisinage radio **direct** de l'émetteur (3 s + gigue anti-collision ; le RSSI est mesuré par l'émetteur du rapport même si le rapport est ensuite relayé) |

## Animations (18, alignées firmware ↔ tableau `ANIMS` dans index.html)

0 IDLE · 1 LOOK_AROUND · 2 NOD_YES · 3 SHAKE_NO · 4 CURIOUS_TILT · 5 SCAN_SLOW ·
6 ALERT_SNAP · 7 TRACK · 8 GLITCH_STUTTER · 9 CONFUSED_TILT · 10 DOUBLE_TAKE ·
11 SLEEPY_DROOP · 12 TARGET_LOCK · 13 WHIRR_SEARCH · 14 SIGNAL_GLITCH ·
15 GREETING_NOD · 16 POWER_DOWN (**boucle**) · 17 TALK (**boucle**, tilt rapide
façon bouche qui parle, pensé pour accompagner une piste audio).

Les deux gestes en boucle sont exclus du tirage aléatoire d'idle et comptent pour
`LOOPING_ANIM_DEFAULT_MS` (2 s, indicatif) dans `totalDurationMs()`.
Comportement au repos : le maître tire un geste au hasard toutes les 2,5-5 s et le
diffuse à tous (esclave isolé : 3-7 s, local) — suspendable par droïde (« Anims
auto »), sans couper les servos ni bloquer Jouer/Séquenceur.

## Protocole série JSON (console ↔ maître, 115200 bauds, 1 ligne = 1 message)

Session gardée par handshake : `hello` → `{evt:"hello",ok,id}`, puis keepalive
`ping` (timeout 5 s côté firmware, `_clientReady`).

- **Console → maître** (`cmd`) : `hello` · `ping` · `list` · `getConfig` · `getAll` ·
  `config {target,freq,amp,speed}` · `volume {value}` · `name {id,name}` ·
  `playTrack {track}` · `servo {target,enabled}` · `autoAnim {target,enabled}` ·
  `anim {target,animId,seed}` · `preview {target,pan,tilt}` ·
  `calib {target,+6 bornes}` · `getCalib {target}` · `getAnimDurations` ·
  `getMeshTopology` · `seqList` · `seqLoad {slot}` ·
  `seqSave {slot,name,loop,track,steps}` · `seqDelete {slot}` ·
  `seqRun {slot,from?}` · `seqStop` · `seqPause` · `seqResume` · `seqState` ·
  `setMulti {ops:[...]}` · `commit` · `revert`
- **Maître → console** (`evt`) : `hello {ok,id,fw,proto,lineMax,anims,seqSlots,trackCount,caps[],dirty}` ·
  `droids {list:[{id,name,rssi,age,role,servos,autoAnim}]}` ·
  `log {msg}` · `err {msg}` · `config {volume,freq,amp,speed}` · `calibData {target,+6}` ·
  `meshTopology {links:[{from,to,rssi}]}` · `animDurations {list:[{animId,ms}]}` ·
  `seqList {list:[{slot,name,stepCount,loop,track}]}` · `seqData {slot,name,loop,track,steps}` ·
  `seqSaved {ok,slot,name}` · `seqDeleted {ok,slot}` ·
  `seqState {playing,slot,index,total,track,paused}` ·
  `setMultiDone {ok,applied,failedAt?,error?}` · `dirty {dirty}` · `allDone`

Champs inconnus dans une commande : ignorés (la console peut être plus récente que
le firmware). Réponses routées exclusivement sur `evt`. Tampon de ligne : 4 Ko
(`lineMax` annoncé au handshake ; toute ligne plus longue → `err`).

**Commit/revert** (volume, params d'anim, noms — pas la calibration ni les
séquences) : les setters sont « live » (surcouche RAM), la NVS n'est écrite qu'au
`commit` ; `revert` recharge l'état persisté. La console doit envoyer `commit`
après un `setMulti` de restauration. [FIRMWARE-CONTRACT.md](FIRMWARE-CONTRACT.md)
tient l'état d'implémentation (§1/§3/§4/§5 faits ; §2 durées de pistes reporté —
approche broche BUSY).

## index.html (page UI de la console — `console/wwwroot/index.html`)

Page WebView2 (pas Web Serial : c'est le C# qui tient le port). Elle parle à
l'hôte via `window.chrome.webview.postMessage` — **deux vocabulaires à ne pas
confondre** :

1. **Transport** (page ↔ hôte C#, champ `type`) : `listPorts`/`ports` ·
   `open`/`opened` · `close`/`closed {unexpected?}` · `write` · `line {data}` ·
   `getAppInfo`/`appInfo` · `saveFile`/`fileSaved` · `openFile`/`fileOpened` ·
   `pickBin`/`binPicked` · `flash`/`flashLog`/`flashDone` ·
   `libList`/`libSave`/`libDelete` (bibliothèque locale de séquences).
2. **Protocole firmware** (ci-dessus), transporté dans `write` (sortant) et
   `line` (entrant, parsé → `handleEvent()`).

Cartes : Droïdes (noms, servos, anims auto, sauvegarde/restauration) · Calibration
servos (aperçu direct + auto-save) · Animation · Audio · Firmware (flash espflash) ·
Topologie du mesh (graphe SVG des liens directs, fusion bidirectionnelle au RSSI
le plus faible) · Séquenceur (catalogue 8 slots + bibliothèque locale illimitée +
éditeur + timeline multi-pistes + trame audio + mode Répéter console-side +
undo/redo + export/import `.b1seq.json`) · Activité.

Particularités : page `file://` auto-suffisante (aucun CDN/fetch) ; `sendCmd()` =
point de passage unique (gate handshake) ; re-rendus du tableau différés pendant
interaction (`UI_INTERACTION_SELECTOR`) ; durées de pistes audio et mapping
slot→piste en `localStorage` (`b1.trackDurations`, `b1.audioBySlot`) tant que le
firmware ne les connaît pas ; l'éditeur de séquences a un `handleEvent` **wrapper**
(les events séquenceur sont traités dans le wrapper, le reste retombe sur
l'original — ajouter les nouveaux `evt` dans le wrapper) ; interceptors
(`seqCollector`/`calibCollector`/`pushingLib`) à garder au **début** de leurs
handlers, sinon la collecte de fond (sauvegarde/restauration) corrompt l'éditeur.

Détails d'implémentation côté C# (`console/MainWindow.xaml.cs`, versioning,
NSIS, vérification jsdom) : voir le CLAUDE.md ci-dessous (§ Architecture console).

### Architecture console (`console/`)

| Fichier | Rôle |
| --- | --- |
| `MainWindow.xaml.cs` | port série (`System.IO.Ports`), pont transport ↔ WebView2, flash espflash, vérification/téléchargement des mises à jour GitHub |
| `wwwroot/index.html` | page UI unique (voir ci-dessus) |
| `b1-chat-console.csproj` | build number auto-incrémenté, version depuis `VersionPrefix`, embarque `wwwroot/` et `tools/` (espflash) dans le publish |
| `installer/b1-chat-console.nsi` + `release.ps1` | installeur NSIS + script de release GitHub (tag `vX.Y.Z`) |

## Stockage

| Quoi | Où |
| --- | --- |
| Noms, volume, params anim, calibrations | NVS du maître (`config_store`) |
| Séquences (8 slots, ≤ 32 étapes) | NVS du maître (`sequence_store`) |
| Durées de pistes + slot→piste audio | `localStorage` de la page (temporaire, cf. contrat §1-2) |
| Bibliothèque de séquences, dernier port | `%LOCALAPPDATA%\B1ChatConsole\` (côté console) |

## État d'avancement

- [x] Étapes 1-5, 7-10 : servo_engine, mesh_comm (HMAC, relais), animation (18
      gestes), audio, config_store + registry, serial_console, dashboard,
      sequence_store + exécution autonome.
- [x] Refonte séquenceur : catalogue par nom (slots cachés), durées réelles
      (`getAnimDurations`), progression live (`seqState`), acks seqSaved/seqDeleted,
      garde `anim.isPlaying()` pour les étapes ciblant le maître.
- [x] Pause « Anims auto » par droïde (MSG_AUTOANIM, heartbeat bit1, colonne UI).
- [x] Topologie du mesh (MSG_NEIGHBORS, module mesh_topology, carte graphe SVG).
- [x] Console WPF v0.8.0 : port série natif, auto-reconnexion, flash intégré,
      bibliothèque locale, sauvegarde/restauration, timeline, Répéter,
      export/import. `index.html` remplace `web/dashboard_V7.html`.
- [x] Phase « écosystème » (2026-07-07), firmware 1.0.0 : tampon série 4 Ko +
      `err` explicites, handshake enrichi (fw/proto/lineMax/caps/dirty), `getAll`
      + `allDone`, contrat §3 (`evt:config`), §1 (trame audio par séquence, jouée
      en autonome, sons par geste supprimés quand une trame existe), §5 (`seqRun
      from`, pause/reprise avec pause DFPlayer), §4 (`setMulti` atomique),
      commit/revert KyberEditor (volume/params/noms). Page + C# (checkUpdates
      GitHub, download+install, scripts de release firmware et console) livrés.
- [x] Fusion des dépôts (2026-07-13) : la console (`b1-chat-console`, dépôt
      local jamais poussé) est rapatriée dans `console/` de ce dépôt (copie
      simple, un commit, historique des 6 commits console non conservé — rien
      n'était publié). Un seul dépôt GitHub désormais (`stefe2/B1_Chat`), deux
      trains de tags (`vX.Y.Z` app, `fw-vX.Y.Z` firmware) ; `MainWindow.xaml.cs`
      adapté (liste les releases du dépôt et filtre par préfixe de tag au lieu
      de `/releases/latest`, qui aurait mélangé les deux trains).
- [ ] Étape 6 : machine à états `droid.{h,cpp}`.
- [ ] Contrat §2 : durées de pistes mesurées via la broche BUSY (GPIO4) — reporté.
- [ ] Params d'anim freq/amp/speed : reçus + persistés mais **aucun effet**
      (hook `onConfig` jamais branché dans main.cpp ; curseurs marqués « bientôt
      actif » dans l'UI).
- [ ] GitHub : `gh auth login` (interactif, à faire par l'utilisateur) puis
      premier push de ce dépôt fusionné + première release de chaque train.

## Pièges connus

- `serial_console` : tampon de ligne 256 o (voir bug ci-dessus).
- `IS_MASTER` vit dans `config.h` : vérifier sa valeur avant chaque flash (il
  part dans les commits avec la dernière valeur utilisée).
- `handleRaw()` : l'enregistrement du voisinage doit rester **avant** le
  early-return `srcId==_myId` et la dédup (même un écho relayé de notre propre
  message prouve un lien radio direct avec le relais).
- La page est en français partout ; commentaires code en français (firmware et page).
- ESP32Servo abandonné (bug double-attach) → LEDC natif uniquement.
- DFPlayer : ne sait pas rapporter la durée d'une piste ; la broche BUSY (GPIO4)
  est le seul moyen d'observer la fin de lecture.
- KyberEditor (`C:\Program Files\KyberEditor`) : source d'inspiration UX de la
  console et origine de `tools\espflash.exe` ; ses firmwares/bootloaders ne nous
  servent pas (PlatformIO génère les nôtres).
- Un seul dépôt GitHub pour l'app et le firmware : ne jamais utiliser l'API
  `/releases/latest` (elle ignore le préfixe de tag et mélangerait les deux
  trains) — toujours lister `/releases` et filtrer par préfixe (`v` hors `fw-`
  pour l'app, `fw-` pour le firmware), voir `GetLatestReleaseAsync` dans
  `console/MainWindow.xaml.cs`.

## Vérification (rappels)

1. `pio run -e b1` compile (tester aussi `IS_MASTER 0`).
2. Sweep servo fluide ; `MSG_ANIM` relayé ≥ 2 sauts sans tempête de broadcast.
3. Anim maître → piste son associée ; 2 clés de groupe différentes s'ignorent.
4. Console connectée : liste des droïdes, anim/volume/nom, persistance après reboot.
5. Séquence sauvée → reboot maître → `Jouer` fonctionne sans PC.
6. Topologie : éloigner un esclave hors de portée directe du maître → son lien
   direct disparaît du graphe, les liens via relais restent.
