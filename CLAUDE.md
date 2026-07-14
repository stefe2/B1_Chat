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
2. **Console de supervision** (`console/`, WPF net8.0-windows, v0.8.x) :
   application de bureau **100 % WPF native** (XAML/MVVM,
   `CommunityToolkit.Mvvm`) qui possède le port série (`System.IO.Ports`) et
   reproduit le design de l'ancienne page web carte par carte.
   `console/wwwroot/index.html` (HTML+CSS+JS inline, tout en français) est
   **conservée intacte** comme référence de comportement/design, mais n'est
   plus rendue à l'exécution (l'ancienne coquille WebView2 a été retirée).
   Fusionnée dans ce dépôt (elle vivait avant dans `b1-chat-console`, dépôt
   séparé sans remote — jamais publié ; historique perdu au passage, aucune
   perte réelle). `FIRMWARE-CONTRACT.md` liste les extensions de protocole que
   la console attend du firmware.

Deux trains de release GitHub distincts au sein du **même** dépôt, distingués
par le préfixe du tag : `vX.Y.Z` pour l'app console, `fw-vX.Y.Z` pour le
firmware (voir `tools/release.ps1` et `console/installer/release.ps1`).

## Commandes

- `pio run -e b1` — compile le firmware (pio.exe : `%USERPROFILE%\.platformio\penv\Scripts\pio.exe`)
- `pio run -e b1 -t upload` — flash une carte (rôle choisi par `IS_MASTER` dans `src/config.h` **avant** de flasher : 1 = maître, un seul par réseau ; 0 = esclave)
- `tools\espflash.exe write-bin --port COMx -B 460800 0x10000 .pio\build\b1\firmware.bin` — flash sans PlatformIO (espflash 4.4.0, aussi utilisé par la carte Firmware de la console)
- `dotnet build` (depuis `console/`) — compile la console WPF
- `.\tools\release.ps1 [-Publish]` — release firmware manuelle (2 rôles + manifeste SHA-256, tag `fw-vX.Y.Z`) ; en usage normal, préférer bumper `FW_VERSION` (`src/config.h`) et pousser sur `main` — la CI publie toute seule (voir ci-dessous)
- `.\console\installer\release.ps1 [-Publish]` — release console (publish + installeur NSIS, tag `vX.Y.Z`)

**Release firmware automatique** (`.github/workflows/firmware-release.yml`) : se déclenche
sur push vers `main` touchant `src/config.h`, ou manuellement (`workflow_dispatch`). Lit
`FW_VERSION`, saute si le tag `fw-vX.Y.Z` existe déjà (idempotent), sinon compile
`b1_master`/`b1_slave` (PlatformIO sur runner GitHub), calcule le manifeste SHA-256, tague
et publie la release — aucun `gh auth login` local nécessaire (jeton `GITHUB_TOKEN`
fourni par Actions). Flux normal : bumper `FW_VERSION`, commit, push sur `main`, attendre
la CI. `tools/release.ps1 -Publish` reste un repli manuel (éviter de l'utiliser en plus de
la CI pour la même version — double tag/release).

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
| `ota_guard.{h,cpp}` | (tous rôles) anti-brick : flag NVS + rollback manuel vers l'autre partition si le nouveau firmware ne démarre pas correctement |
| `ota_master.{h,cpp}` | (maître) orchestre une session OTA vers un esclave (stop-and-wait, retry, confirmation post-reboot via heartbeat) |
| `ota_slave.{h,cpp}` | (esclave) reçoit une image OTA relayée par le mesh, écrit via `Update` |
| `droid.{h,cpp}` | machine à états haut niveau (étape 6, **pas encore fait**) |

Dépendances : `DFRobotDFPlayerMini`, `ArduinoJson`. Build flags : `-D MESH_TTL=4`,
`-D GROUP_KEY="changeme"` (clé **compilée uniquement**, pas de re-clé à l'exécution).

Environnements PlatformIO (`platformio.ini`) : `[env:b1]` — rôle décidé par `#define
IS_MASTER` dans `config.h`, pour le flash/dev local (`pio run -e b1 -t upload`).
`[env:b1_master]`/`[env:b1_slave]` — dédiés à la release CI, forcent le rôle via
`-D IS_MASTER=1|0` sans toucher à `config.h` (celui-ci guarde `IS_MASTER` avec un
`#ifndef`, comme `MESH_TTL`/`GROUP_KEY`, pour que la surcharge en ligne de commande
fonctionne) ; n'affectent pas `[env:b1]`.

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
| `MSG_HEARTBEAT` = 4 | uptime, état (bit0 = servos, bit1 = anims auto), version firmware (3 octets major/minor/patch) |
| `MSG_SERVO` = 5 | targetId, enabled |
| `MSG_CALIB` = 6 | targetId, 6 bornes pan/tilt (persisté par le droïde ciblé) |
| `MSG_PREVIEW` = 7 | targetId, pan, tilt (transitoire, non persisté) |
| `MSG_AUTOANIM` = 8 | targetId, enabled (pause des anims spontanées au repos) |
| `MSG_NEIGHBORS` = 9 | count + [{id, rssi}] : rapport périodique du voisinage radio **direct** de l'émetteur (3 s + gigue anti-collision ; le RSSI est mesuré par l'émetteur du rapport même si le rapport est ensuite relayé) |
| `MSG_OTA_START` = 10 | (maître→esclave ciblé) targetId, sessionId, totalSize, totalChunks, chunkSize, md5Hex[32] — démarre une session OTA |
| `MSG_OTA_CHUNK` = 11 | (maître→esclave ciblé) targetId, sessionId, chunkIndex, dataLen, data[190] — un fragment de l'image, envoyé à taille pleine |
| `MSG_OTA_ACK` = 12 | (esclave→maître) sessionId, kind (0=start/1=chunk/2=end), chunkIndex, status |
| `MSG_OTA_END` = 13 | (maître→esclave ciblé) targetId, sessionId, totalChunks — finalise (`Update.end()`) si tous les chunks attendus sont reçus |
| `MSG_OTA_ABORT` = 14 | (maître→esclave ciblé) targetId, sessionId, reason — annule la session en cours |

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
  `adopt {target}` · `forget {target}` ·
  `anim {target,animId,seed}` · `preview {target,pan,tilt}` ·
  `calib {target,+6 bornes}` · `getCalib {target}` · `getAnimDurations` ·
  `getMeshTopology` · `seqList` · `seqLoad {slot}` ·
  `seqSave {slot,name,loop,track,steps}` · `seqDelete {slot}` ·
  `seqRun {slot,from?}` · `seqStop` · `seqPause` · `seqResume` · `seqState` ·
  `setMulti {ops:[...]}` · `commit` · `revert` ·
  `otaStart {target,size,md5}` · `otaChunk {seq,data}` (data = base64) · `otaAbort {}`
- **Maître → console** (`evt`) : `hello {ok,id,fw,proto,lineMax,anims,seqSlots,trackCount,caps[],dirty}` ·
  `droids {list:[{id,name,rssi,age,role,servos,autoAnim,adopted,fw}]}` ·
  `log {msg}` · `err {msg}` · `config {volume,freq,amp,speed}` · `calibData {target,+6}` ·
  `meshTopology {links:[{from,to,rssi}]}` · `animDurations {list:[{animId,ms}]}` ·
  `seqList {list:[{slot,name,stepCount,loop,track}]}` · `seqData {slot,name,loop,track,steps}` ·
  `seqSaved {ok,slot,name}` · `seqDeleted {ok,slot}` ·
  `seqState {playing,slot,index,total,track,paused}` ·
  `setMultiDone {ok,applied,failedAt?,error?}` · `dirty {dirty}` · `allDone` ·
  `otaReady {target,sessionId,chunkSize,totalChunks}` · `otaChunkAck {seq,sent,total}` ·
  `otaDone {target,sessionId}` · `otaResult {target,ok,fw?,reason?}` ·
  `otaError {target?,sessionId?,reason}`

Champs inconnus dans une commande : ignorés (la console peut être plus récente que
le firmware). Réponses routées exclusivement sur `evt`. Tampon de ligne : 4 Ko
(`lineMax` annoncé au handshake ; toute ligne plus longue → `err`).

**Commit/revert** (volume, params d'anim, noms — pas la calibration ni les
séquences) : les setters sont « live » (surcouche RAM), la NVS n'est écrite qu'au
`commit` ; `revert` recharge l'état persisté. La console doit envoyer `commit`
après un `setMulti` de restauration. [FIRMWARE-CONTRACT.md](FIRMWARE-CONTRACT.md)
tient l'état d'implémentation (§1/§3/§4/§5 faits ; §2 durées de pistes reporté —
approche broche BUSY).

**Adoption des droïdes** (`registry`/`config_store`) : un droïde jamais vu
(`adopted:false` dans `evt:droids`) reste dans le mesh (anims broadcast reçues
normalement) mais absent des contrôles individuels tant que la console n'a pas
envoyé `adopt`. `adopt` persiste le statut en NVS (survit aux redémarrages du
maître) ; `forget` retire l'entrée du registre **et** efface son statut NVS — un
droïde ainsi « oublié » ou dont l'adoption est refusée redemande donc dès qu'il
reparle. Le badge « perdu » (4 s de silence, `DROID_TIMEOUT_MS`) ne redéclenche
jamais cette question à lui seul.

## OTA du firmware (esclaves, relayé par le mesh)

Un esclave adopté peut être reflashé **sans USB**, déclenché par un bouton
« Flasher (OTA) » sur sa ligne dans la carte Droïdes. Le `.bin` transite par le
lien série existant (console → maître, en base64) puis par le mesh ESP-NOW
(maître → esclave ciblé), en `stop-and-wait` : un fragment de 190 o en vol à la
fois, accusé requis avant le suivant (`Update.write()` côté esclave est
séquentiel/append-only, pas de reprise de désordre). **Une seule session à la
fois** sur tout le parc.

Déroulé : `otaStart` (console, avec taille + MD5) → le maître valide la cible
(connue du registre) et envoie `MSG_OTA_START` → une fois acquitté, `evt:otaReady`
→ la console pousse les chunks un par un via `otaChunk`, chacun relayé en
`MSG_OTA_CHUNK` → `evt:otaChunkAck` après chaque accusé (déclenche l'envoi du
suivant côté console) → dernier chunk acquitté → `MSG_OTA_END` → `evt:otaDone`
(le maître a fini, l'esclave redémarre) → le maître surveille ensuite les
heartbeats de la cible jusqu'à `OTA_REBOOT_WAIT_MS` (~90 s) et pousse
`evt:otaResult`. Comme la console ne peut pas connaître de façon fiable la
version intégrée dans un `.bin` arbitraire, le succès est déterminé en comparant
la version rapportée **après** redémarrage à celle d'**avant** l'OTA (capturée
au moment de `otaStart`) plutôt qu'à une version annoncée — `ok:true` si elle a
changé, `reason:"rolledBack"` si elle est identique, `reason:"injoignable"` si
aucun heartbeat n'arrive dans le délai. Fenêtre de grâce (`OTA_REBOOT_GRACE_MS`,
5 s) : un signe de vie à version inchangée dans les premières secondes est
ignoré — l'esclave ne reboote que ~250 ms après son ack de END, un dernier
heartbeat de l'ancienne image peut encore arriver (faux « rolledBack » rendu
940 ms après `otaDone`, observé au banc) ; un vrai rollback prend ≥ 10-30 s.

Sécurité anti-brick (`ota_guard.{h,cpp}`) : avant de finaliser (`Update.end(true)`,
qui vérifie déjà taille/MD5 et refuse de rebooter sur une image invalide),
l'esclave arme un flag NVS puis redémarre. Au boot suivant, si ce flag est
présent, un compteur de tentatives s'incrémente ; au-delà de
`OTA_MAX_BOOT_ATTEMPTS` (3), le firmware bascule **lui-même**
(`esp_ota_set_boot_partition`) sur l'autre partition (`esp_ota_get_next_update_partition`
alterne forcément app0/app1) et redémarre — un rollback manuel via l'API
`esp_ota_ops` standard, sans dépendre du rollback bootloader d'ESP-IDF
(non exposé simplement en `framework=arduino`). Si le firmware tourne
`OTA_VERIFY_UPTIME_MS` (~20 s) sans reset, le flag est effacé : l'image est
confirmée bonne.

**Risque résiduel assumé** : un crash survenant *avant* `OtaGuard::earlyCheck()`
(1re ligne de `setup()`) ne serait jamais compté ni rattrapé. Réduit dans la
pratique par la vérification MD5/format déjà faite avant tout reboot, qui
filtre l'essentiel des cas de transfert corrompu — il ne reste que « l'image
est valide mais plante quasi instantanément ». Un `esp_task_wdt` (10 s) est
aussi armé pour rattraper un nouveau firmware qui boucle/bloque sans céder la
main.

**Durée réaliste** : ~5 240 fragments pour une image ~1 Mo → **8 à 15 minutes**
par esclave en conditions normales, jusqu'à 20-30 min en liaison faible ou
multi-sauts. Affiché dans la console comme une progression (fragments envoyés
sur total), pas une durée fixe promise.

## Ancienne page web de référence (`console/wwwroot/index.html`)

Page HTML+CSS+JS inline conservée **intacte** (non modifiée depuis la réécriture
WPF) — sert uniquement de spec comportementale/visuelle (texte français exact,
palette, comportement carte par carte) pour l'implémentation native ci-dessous.
N'est **plus rendue** par l'application (l'ancienne coquille WebView2, qui la
chargeait via `window.chrome.webview.postMessage`, a été retirée).

Cartes qu'elle documente : Droïdes (noms, servos, anims auto, sauvegarde/
restauration) · Calibration servos (aperçu direct + auto-save) · Animation ·
Audio · Firmware (flash espflash) · Topologie du mesh (graphe SVG des liens
directs, fusion bidirectionnelle au RSSI le plus faible) · Séquenceur (catalogue
8 slots + bibliothèque locale illimitée + éditeur + timeline multi-pistes + trame
audio + mode Répéter console-side + undo/redo + export/import `.b1seq.json`) ·
Activité (carte retirée côté WPF, voir État d'avancement).

Le protocole firmware (`cmd`/`evt`, ci-dessus) y est transporté dans `write`
(sortant) et `line` (entrant, parsé → `handleEvent()`) ; le vocabulaire transport
WebView2 (`listPorts`/`open`/`write`/`flash`/`libList`/...) n'a plus cours côté
WPF, remplacé par un appel direct à `Services/SerialLinkService.cs` +
`Services/ProtocolClient.cs` (pas de pont postMessage).

### Architecture console (`console/`) — WPF natif (XAML/MVVM)

Réécriture complète (2026-07-13) : l'ancienne coquille WebView2 est remplacée par
une UI **100 % XAML**, carte par carte, pilotée par `CommunityToolkit.Mvvm`
(`[ObservableProperty]`/`[RelayCommand]`). `index.html` reste sur disque, intacte,
comme référence de design/comportement (section ci-dessus) mais n'est plus
chargée par l'application.

| Dossier/fichier | Rôle |
| --- | --- |
| `MainWindow.xaml(.cs)` | en-tête (logo, statut connexion, commit/revert, bouton « Firmware… ») + grille des cartes |
| `FirmwareWindow.xaml(.cs)` | fenêtre séparée hébergeant `Views/FirmwareCardView` (flash espflash + MAJ GitHub), ouverte depuis le bouton d'en-tête |
| `App.xaml(.cs)` | composition root : convertisseurs + dictionnaires de ressources fusionnés |
| `Themes/Theme.xaml` | palette (brushes), dégradés boutons/LED/nœuds du mesh — portée depuis les custom properties CSS de `index.html` |
| `Themes/Effects.xaml` | styles partagés : `CardBorderStyle`, `BeveledButtonStyle`, `HaloBadge*Style`, `MetalSliderStyle`, `DarkComboBoxStyle`, `CardIconBoxStyle`, `MeshNodeEllipseStyle`, etc. |
| `Models/` | `Droid`, `MeshNodeVisual`/`MeshEdgeVisual`, séquences, calibration — objets liés aux vues |
| `ViewModels/` | `MainViewModel` + un par carte (`DroidsViewModel`, `CalibrationViewModel`, `AnimationViewModel`, `AudioViewModel`, `FirmwareViewModel`, `MeshTopologyViewModel`, `SequencerViewModel`) |
| `Views/` | un `UserControl` XAML par carte (plus de carte Activité) |
| `Services/SerialLinkService.cs` | port série natif (`System.IO.Ports`), auto-reconnexion (3 s) |
| `Services/ProtocolClient.cs` | état central : parse les `evt` JSON entrants, construit les `cmd` sortants (équivalent C# du `sendCmd()`/`handleEvent()` JS) |
| `Services/UpdateService.cs` / `FlashService.cs` / `LibraryService.cs` / `SettingsService.cs` | MAJ GitHub, flash espflash, bibliothèque locale de séquences, `settings.json` |
| `Services/OtaService.cs` | pilote une session OTA (un esclave à la fois) : lit le `.bin`, calcule le MD5, envoie un fragment par `evt:otaChunkAck` reçu |
| `Converters/` | `BoolToStyleConverter`, `BoolToTextConverter`, `BoolToVisibilityConverter`, `BoolToBrushConverter`, `StrengthToBrushConverter` (couleur des liens du mesh selon le RSSI) |
| `b1-chat-console.csproj` | build number auto-incrémenté, version depuis `VersionPrefix`, `IncludeNativeLibrariesForSelfExtract`, `tools/` (espflash) exclu du single-file mais copié au publish |
| `installer/b1-chat-console.nsi` + `release.ps1` | installeur NSIS + script de release GitHub (tag `vX.Y.Z`) |

Disposition de la grille principale (`MainWindow.xaml`) : Droïdes (colonne gauche,
pleine hauteur) · colonne droite empilée Calibration → Topologie du mesh →
(rangée Animation + Audio côte à côte, largeur égale) · Séquenceur en pleine
largeur en bas. Carte Firmware sortie de la grille (fenêtre séparée).

## Stockage

| Quoi | Où |
| --- | --- |
| Noms, volume, params anim, calibrations, statut d'adoption | NVS du maître (`config_store`) |
| Séquences (8 slots, ≤ 32 étapes) | NVS du maître (`sequence_store`) |
| Durées de pistes + slot→piste audio | `localStorage` de la page (temporaire, cf. contrat §1-2) |
| Bibliothèque de séquences, dernier port | `%LOCALAPPDATA%\B1ChatConsole\` (côté console) |
| Flag anti-brick OTA (pending/attempts) | NVS de **chaque droïde** flashé en OTA, namespace `"ota"` séparé (`ota_guard`) |

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
- [x] Réécriture complète de la console en WPF natif (2026-07-13, `index.html`
      conservée intacte comme référence, plus rendue à l'exécution) : 8 cartes
      portées en XAML/MVVM (`CommunityToolkit.Mvvm`), `ProtocolClient` C# comme
      nouvel état central (équivalent natif du `sendCmd()`/`handleEvent()` JS),
      auto-reconnexion série, undo/redo séquenceur, topologie du mesh (Canvas +
      layout circulaire porté tel quel).
- [x] Polish visuel + réagencement (2026-07-13) : carte Calibration servos prise
      comme modèle de référence (slider métal, pastilles de valeur, ComboBox
      sombre) puis propagée aux autres cartes ; en-tête redessiné (logo, statut,
      CTA accent) ; carte Topologie du mesh redessinée (nœuds glossy, anneaux
      radar, liens colorés par force de signal) et déplacée entre Calibration et
      Animation ; carte Activité supprimée ; carte Firmware sortie de la grille
      vers une fenêtre séparée (`FirmwareWindow`, bouton dédié en en-tête) ;
      Animation et Audio placées côte à côte au-dessus du Séquenceur.
- [x] Refonte du flux firmware console (rôle Maître/Esclave choisi explicitement
      avant la source, source GitHub auto-suffisante avec vérification, adresse
      reléguée en options avancées) + release firmware automatique par CI
      (`.github/workflows/firmware-release.yml`, déclenchée par bump de
      `FW_VERSION` ; `IS_MASTER` rendu surchargeable via `#ifndef` pour les
      nouveaux environnements PlatformIO `b1_master`/`b1_slave`).
- [x] Adoption des droïdes (2026-07-13, fw 1.1.0) : un droïde jamais vu (ou
      « oublié »/refusé) n'est plus ajouté automatiquement à la liste — la
      console propose Adopter/Ignorer (`cmd adopt`/`forget`, statut persisté en
      NVS via `config_store`, jamais dans le `registry` RAM éphémère). Bouton
      « Oublier » pour retirer un droïde déjà adopté. Correctif au passage :
      `ProtocolClient.HandleDroids` ne retirait jamais une entrée disparue de
      `evt:droids` (bug latent, sans effet visible avant cette fonctionnalité).
- [x] Version firmware par droïde (2026-07-13, fw 1.2.0) : chaque esclave
      rapporte sa version dans son heartbeat (3 octets major/minor/patch),
      stockée dans le `registry` et exposée via `evt:droids.fw` ; nouvelle
      colonne FW dans la carte Droïdes. Casse la compatibilité binaire du
      heartbeat (voir pièges) — tout le parc doit être reflashé ensemble.
- [x] Polish carte Droïdes (2026-07-13) : colonne NOM à largeur fixe, pastilles
      ÉTAT/RÔLE à largeur fixe et texte centré, RSSI affiché `-` (au lieu de la
      dernière valeur figée) quand un droïde est « perdu », seuil « perdu »
      abaissé à 4 s (`DROID_TIMEOUT_MS` + seuil console), interrupteurs on/off
      coulissants (`OnOffSwitchStyle`) pour Servos/Anims auto.
- [ ] Étape 6 : machine à états `droid.{h,cpp}`.
- [ ] Contrat §2 : durées de pistes mesurées via la broche BUSY (GPIO4) — reporté.
- [ ] Params d'anim freq/amp/speed : reçus + persistés mais **aucun effet**
      (hook `onConfig` jamais branché dans main.cpp ; curseurs marqués « bientôt
      actif » dans l'UI).
- [x] Release firmware automatique opérationnelle (`fw-v1.0.0`, `fw-v1.1.0`
      publiées via la CI). Console : encore manuel (`gh auth login` une fois,
      puis `console\installer\release.ps1 -Publish`).
- [x] OTA du firmware relayé par le mesh ESP-NOW (2026-07-14, fw 1.3.0) : un
      esclave adopté peut être reflashé sans USB depuis la carte Droïdes
      (`MSG_OTA_START/CHUNK/ACK/END/ABORT`, stop-and-wait, une session à la
      fois) + `ota_guard` (rollback manuel anti-brick, voir section OTA
      ci-dessus). Testé uniquement sur carte de rechange dédiée pour l'instant
      (voir Vérification) — pas encore validé sur un vrai droïde du parc.

## Pièges connus

- `serial_console` : tampon de ligne 256 o (voir bug ci-dessus).
- `IS_MASTER` vit dans `config.h` : vérifier sa valeur avant chaque flash (il
  part dans les commits avec la dernière valeur utilisée).
- `handleRaw()` : l'enregistrement du voisinage doit rester **avant** le
  early-return `srcId==_myId` et la dédup (même un écho relayé de notre propre
  message prouve un lien radio direct avec le relais).
- `HeartbeatPayload` (`mesh_comm.h`) : la réception exige `len ==
  sizeof(HeartbeatPayload)` exact ([main.cpp](src/main.cpp)) — tout changement
  de taille de cette struct (ex. ajout de la version FW) casse silencieusement
  la compatibilité avec un droïde resté sur l'ancien firmware : ses heartbeats
  sont juste ignorés (pas d'erreur), donc servos/anims auto/FW gèlent pour lui
  côté registre/console. Reflasher tout le parc ensemble à chaque changement de
  cette struct.
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
- WPF `Setter.TargetName` ne peut pas cibler un `Freezable` nommé imbriqué dans
  une propriété (ex. un `TranslateTransform` dans `Border.RenderTransform`, un
  `DropShadowEffect` dans `Border.Effect`) : le `Trigger` doit remplacer toute la
  propriété du parent par un nouvel objet plutôt que nommer l'enfant.
- `DockPanel.LastChildFill` vaut `True` par défaut : le **dernier** enfant ignore
  son propre `Dock` et s'étire pour remplir l'espace restant — piège classique
  pour un groupe censé rester collé à un bord (ex. les contrôles de connexion de
  l'en-tête) ; mettre `LastChildFill="False"` si tous les enfants doivent
  respecter leur `Dock`.
- `IS_MASTER` a deux mécanismes de réglage distincts, ne pas les confondre :
  `[env:b1]` (flash/dev local) lit la valeur écrite en dur dans `config.h` ;
  `[env:b1_master]`/`[env:b1_slave]` (release CI) l'ignorent et forcent le rôle
  via `-D IS_MASTER=1|0`. Modifier `config.h` n'affecte jamais les deux derniers.
- `OtaGuard::earlyCheck()` (`ota_guard.cpp`) doit rester la toute première
  ligne de `setup()` — tout code qui plante avant cet appel n'est jamais compté
  par le mécanisme anti-brick (risque résiduel assumé, voir section OTA).
- `OtaSlave::processChunk()` : `Update.write()` est séquentiel/append-only. Un
  ack retransmis pour un chunk déjà écrit ne doit **jamais** réappeler
  `Update.write()` — seul le ré-ack doit se répéter, sinon l'image écrite est
  corrompue silencieusement.
- `Update.begin/write/end` (accès SPI flash réels : effacement de secteur tous
  les ~21 chunks de 190 o, MD5 sur toute l'image au `end`) ne doivent **jamais**
  s'exécuter depuis le callback ESP-NOW (tâche Wi-Fi) ni sous
  `portENTER_CRITICAL` — gel/panic systématique au chunk 21 (premier
  débordement du tampon de secteur 4 Ko d'`Update`). D'où la boîte aux lettres
  d'`OtaSlave` : les `on*()` (callback) ne font que déposer le message brut,
  `update()` (loop()) valide, écrit la flash et acke, hors verrou.
- `OTA_CHUNK_DATA_MAX` (`mesh_comm.h`) est autoritaire côté firmware et annoncé
  à la console via `evt:otaReady.chunkSize` — ne jamais le coder en dur côté
  C# (`OtaService.cs` le lit dynamiquement).
- **Horodatages écrits par le callback ESP-NOW (tâche Wi-Fi)** (`lastSeen` du
  registry, `_lastSendMs`/`_serialWaitSince` d'OtaMaster, etc.) : ils peuvent
  être POSTÉRIEURS au `now` capturé en début de `loop()`. Toute soustraction
  `now - horodatage` doit être comparée en **signé** (`(int32_t)(diff) >
  seuil`) ou clampée — en non signé, la différence négative déborde en ~4e9 :
  timeouts qui sautent instantanément (bug OTA fw ≤ 1.3.7) ou `age` à 4 Md
  dans `evt:droids` qui faisait crasher `HandleDroids` côté console.
- `ProtocolClient.OnLineReceived` isole chaque ligne dans un try/catch : une
  ligne malformée du firmware ne doit JAMAIS tuer la boucle de lecture (mort
  silencieuse du lien, historique) ni l'application. Ne pas « simplifier » en
  retirant cette garde.

## Vérification (rappels)

1. `pio run -e b1` compile (tester aussi `IS_MASTER 0`).
2. Sweep servo fluide ; `MSG_ANIM` relayé ≥ 2 sauts sans tempête de broadcast.
3. Anim maître → piste son associée ; 2 clés de groupe différentes s'ignorent.
4. Console connectée : liste des droïdes, anim/volume/nom, persistance après reboot.
5. Séquence sauvée → reboot maître → `Jouer` fonctionne sans PC.
6. Topologie : éloigner un esclave hors de portée directe du maître → son lien
   direct disparaît du graphe, les liens via relais restent.
7. OTA — **uniquement sur une carte de rechange, jamais un droïde en service** :
   transfert nominal (progression, `otaResult{ok:true}`, `evt:droids.fw` à
   jour) ; `.bin` corrompu → `ERR_MD5` à la fin, pas de reboot ; abandon série
   (fermer la console en cours de transfert) → auto-abort côté maître, session
   suivante propre ; rollback — build de test qui plante juste après
   `earlyCheck()`, poussé par OTA, doit revenir seul sur l'ancienne image après
   `OTA_MAX_BOOT_ATTEMPTS` boots ratés.
