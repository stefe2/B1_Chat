# Projet — Contrôle multi-droïdes B1 Battle Droid

Firmware ESP32 (PlatformIO / Arduino) pour piloter plusieurs têtes de droïdes B1
en réseau **ESP-NOW mesh multi-sauts**, avec mouvements **fluides et organiques**
et **son joué par le droïde maître** via un DFPlayer Mini + ampli externe.

---

## 1. Objectifs

- 1 ESP32 par droïde (DOIT ESP32 DevKit V1).
- Réseau **ESP-NOW en vrai mesh multi-sauts** (relais avec TTL + déduplication).
- Contrôle de **2 servos** par droïde : tête en **pan** et **tilt**.
- **Animations aléatoires** variées, coordonnées par le maître.
- **Playback audio** depuis le **maître** (DFPlayer Mini, sortie DAC → ampli).
- Rendu **fluide et organique** (interpolation + easing + bruit d'idle).
- Démarrage avec **10 pistes son**.

---

## 2. Décisions validées

| Sujet | Décision |
|-------|----------|
| Audio | DFPlayer Mini (MP3 + carte SD) sur le **maître** |
| Sortie audio | **DAC_L / DAC_R** (niveau ligne) → **ampli externe** (ex. PAM8403) |
| Haut-parleur | 1 seul, sur la sortie de l'ampli (maître) |
| Rôle maître | Droïde **complet** (2 servos) qui **coordonne** les autres |
| Nombre de droïdes | 4–6 (extensible) |
| Servos | Standard PWM (SG90 / MG996R) |
| Réseau | ESP-NOW **mesh multi-sauts** (TTL + dédup) |
| Pistes son | **10** au démarrage |
| Supervision | **Page web autonome (Web Serial API)** branchée en **USB sur le maître** |
| Réglages | Persistés dans l'ESP32 (**NVS / Preferences**) |
| Clé de réseau | **HMAC-SHA256** par groupe (sépare 2 séries B1, anti-falsification) |
| Attribution clé | **Défaut compilé** (`-D GROUP_KEY`) + **re-clé via page web** (NVS) |

---

## 3. Câblage (DOIT ESP32 DevKit V1)

### Servos (tous les droïdes)
| Signal | GPIO ESP32 |
|--------|-----------|
| Servo **PAN** (signal) | GPIO25 |
| Servo **TILT** (signal) | GPIO26 |

- Servos alimentés en **5 V externe** (BEC/UBEC), **jamais** sur le 3V3 de l'ESP32.
- **Masse commune** obligatoire entre alimentation servos et ESP32.
- Condensateur de découplage (≥ 470 µF) conseillé sur le rail 5 V des servos.

### DFPlayer Mini + audio (maître uniquement)
| Signal | Connexion |
|--------|-----------|
| ESP GPIO17 (TX2) | → DFPlayer **RX** (via résistance **1 kΩ** en série) |
| ESP GPIO16 (RX2) | ← DFPlayer **TX** |
| DFPlayer **VCC** | 5 V |
| DFPlayer **GND** | GND commun |
| DFPlayer **BUSY** | → GPIO4 (optionnel : détection fin de lecture) |
| DFPlayer **DAC_L / DAC_R** | → entrée **ampli externe** (niveau ligne) |
| Ampli sortie | → haut-parleur (3 W, 4–8 Ω) |

- **GND audio commun** entre DFPlayer, ampli et ESP32.
- Les sorties intégrées SPK_1 / SPK_2 **ne sont pas utilisées**.
- Volume : géré logiciellement (commande DFPlayer) ; potentiomètre en entrée
  d'ampli possible pour un réglage matériel.

### Broches à éviter
- Strapping : GPIO0, 2, 5, 12, 15.
- Input-only (pas de sortie PWM) : GPIO34–39.

---

## 4. Architecture logicielle

Firmware **unique** ; le rôle et l'identité sont fixés par **build flags**.

```
platformio.ini      → envs master / slave, lib_deps, build flags
src/
  main.cpp          → setup() / loop(), câblage des modules
  config.h          → pins, IDs, bornes d'angles, vitesses, params mesh/audio
  mesh_comm.{h,cpp} → ESP-NOW, en-tête de message, dédup, relais multi-sauts
  servo_engine.{h,cpp} → interpolation 50 Hz, easing, bruit d'idle
  animation.{h,cpp} → keyframes, lecteur d'animation, tirage aléatoire
  audio.{h,cpp}     → wrapper DFPlayer (maître), mapping animation → piste
  droid.{h,cpp}     → machine à états haut niveau
  registry.{h,cpp}  → (maître) inventaire des droïdes (srcId, MAC, lastSeen, RSSI, nom)
  config_store.{h,cpp} → persistance NVS (clé réseau, noms, volume, params d'anim)
  serial_console.{h,cpp} → (maître) pont JSON USB <-> mesh pour la page web
web/
  dashboard.html    → page autonome Web Serial (UI + JS, aucun serveur)
```

### Environnements PlatformIO
- `env:master` : `-D ROLE_MASTER` (un seul maître dans le réseau)
- `env:slave`  : **aucun ID à définir** — firmware identique flashé tel quel sur
  chaque esclave.
- Commun : `-D MESH_TTL=4`, `-D GROUP_KEY="changeme"`,
  `lib_deps = ESP32Servo`, `DFRobotDFPlayerMini`.

### Identité auto (ID dérivé de la MAC)
- Au boot, chaque ESP32 lit sa **MAC** (`WiFi.macAddress` / `esp_read_mac`) et en
  dérive un **`srcId` 16 bits** (les 2 derniers octets de la MAC, unique par carte).
- **Aucune config par droïde** : brancher → flasher `env:slave` → terminé.
- Ajouter un droïde ne nécessite **aucune** modification du maître ni des autres.
- Le maître garde un `srcId` dérivé de sa MAC lui aussi ; son rôle vient du flag
  `ROLE_MASTER`, pas de son ID.

---

## 5. Protocole ESP-NOW mesh multi-sauts

Transport : broadcast ESP-NOW vers `FF:FF:FF:FF:FF:FF`, **canal WiFi fixe**.

### En-tête commun
```
struct MsgHeader {
  uint16_t srcId;   // dérivé de la MAC (2 derniers octets) → unique par carte
  uint16_t seq;     // compteur incrémental par nœud
  uint8_t  ttl;     // sauts restants
  uint8_t  type;    // MSG_ANIM, MSG_HEARTBEAT, ...
};
// Trame émise = header + payload + HMAC (voir « Sécurité réseau »)
// Identifiant logique de déduplication = (srcId, seq)
```

### Règles de relais
1. À la réception, si le couple **(srcId, seq)** est **déjà dans le cache**
   (ring buffer) → **ignorer**.
2. Sinon : enregistrer **(srcId, seq)**, **traiter** le message.
3. Si `ttl > 0` : `ttl--` puis **re-broadcast** (propagation multi-sauts).

### Types de messages
| Type | Charge utile | Relayé |
|------|--------------|--------|
| `MSG_ANIM` | `targetId (0xFFFF=tous), animId, syncDelayMs, seed` | Oui |
| `MSG_CONFIG` | `targetId, freq, amplitude, vitesse` (params d'anim) | Oui |
| `MSG_REKEY` | `newKeyHash` (authentifié avec l'ancienne clé) | Oui |
| `MSG_HEARTBEAT` | `uptime, état` (présence/voisinage) | Oui |
| `MSG_SOUND` | interne maître → DFPlayer | Non (local) |

- `syncDelayMs` permet aux droïdes de **synchroniser** ou **décaler** une anim.
- `seed` fait **varier** le rendu aléatoire tout en restant reproductible.

---

## 6. Mouvement fluide & organique

- **Moteur servo non bloquant** à **50 Hz** (timestep fixe).
- **Easing** *ease-in-out* entre keyframes (pas de mouvement linéaire brut).
- **Bruit d'idle** : micro-oscillations superposées (respiration / regard vivant)
  même à l'arrêt.
- Vitesses/accélérations bornées pour éviter les à-coups.

### Table d'animations (keyframes pan/tilt)
| animId | Nom | Description |
|--------|-----|-------------|
| 0 | IDLE | micro-bruit, tête quasi immobile mais vivante |
| 1 | LOOK_AROUND | balayage lent gauche/droite |
| 2 | NOD_YES | hochement vertical |
| 3 | SHAKE_NO | négation horizontale |
| 4 | CURIOUS_TILT | inclinaison interrogative |
| 5 | SCAN_SLOW | scan panoramique lent |
| 6 | ALERT_SNAP | redressement brusque (alerte) |
| 7 | TRACK | suivi d'une cible imaginaire |

---

## 7. Audio (maître)

- 10 fichiers sur la carte SD : `/mp3/0001.mp3` … `/mp3/0010.mp3`.
- **Mapping animation → son** (aléatoire dans une plage par type d'anim).
- Déclenchement **synchronisé** avec l'animation côté maître.
- Volume réglable par commande DFPlayer.

---

## 8. Machine à états (droïde)

```
BOOT → INIT (servos centrés, ESP-NOW up)
     → IDLE (bruit organique)
       ├─ timer aléatoire → ANIM_LOCAL (anim au hasard)
       └─ réception MSG_ANIM → ANIM_COMMANDED (+ son si maître)
     → retour IDLE
```

- Le **maître** émet périodiquement des `MSG_ANIM` pour orchestrer le groupe
  (synchronisé ou décalé) et joue le son correspondant.
- Chaque **esclave** peut aussi lancer ses propres animations d'idle localement.

---

## 9. Sécurité réseau — clé de groupe (HMAC)

Objectif : garantir que **seuls les droïdes voulus** communiquent ensemble
(ex. deux séries de B1 indépendantes) et rejeter les messages falsifiés.
Le broadcast ESP-NOW n'étant pas chiffrable nativement, la protection est
appliquée **au niveau du message**.

- **Clé de groupe** = mot de passe partagé. Clé HMAC = `SHA256(mot_de_passe)`
  (32 octets), calcul via **mbedTLS** (intégré à l'ESP32).
- Chaque trame émise = `header + payload + HMAC` (HMAC-SHA256 **tronqué ~8 o**).
- À la réception : recalcul du HMAC avec la clé locale ; **mismatch → ignoré**
  (avant la déduplication).
- **Anti-rejeu** : dédup `(srcId, seq)` + `seq` monotone (protection suffisante
  pour un prop, pas une garantie cryptographique absolue).

### Attribution et re-clé
- **Défaut compilé** : `-D GROUP_KEY="..."` → un droïde neuf démarre dans le bon
  groupe sans configuration.
- **Re-clé via page web** : `{cmd:"setKey", password}` → le maître stocke en
  **NVS**, puis diffuse un **`MSG_REKEY`** (relayé, **authentifié avec l'ancienne
  clé**) → les droïdes du groupe adoptent la nouvelle clé et la persistent.
- **Droïde neuf après re-clé** : encore sur la clé par défaut → admission par
  re-flash du même défaut **ou** fenêtre de provisioning (retour temporaire au
  défaut). Compromis inhérent à la distribution de clé sans canal préalable.

---

## 10. Console web USB (Web Serial API)

Page **HTML autonome** (Chrome/Edge, aucun serveur) branchée en **USB sur le
maître**. Le maître sert de **pont** USB ↔ mesh : il tient l'inventaire des
droïdes (via `MSG_HEARTBEAT`) et relaie les commandes.

### Protocole série JSON (une ligne = un message)
- **PC → maître** : `{cmd:"list"}`, `{cmd:"anim",target,animId}`,
  `{cmd:"config",target,freq,amp,speed}`, `{cmd:"volume",value}`,
  `{cmd:"name",id,name}`, `{cmd:"playTrack",track}`, `{cmd:"setKey",password}`,
  `{cmd:"getConfig"}`
- **Maître → PC** : `{evt:"droids",list:[...]}`, `{evt:"log",msg}`,
  `{evt:"state",...}`

### Fonctions
- Lister les droïdes détectés (srcId/MAC, vu il y a X s, RSSI)
- Déclencher une animation (tous ou un droïde précis)
- Régler le volume audio
- Nommer les droïdes (association ID/MAC → nom, persistée)
- Régler les paramètres d'anim (fréquence, amplitude, vitesse)
- Tester une piste son précise
- Changer le mot de passe du groupe
- Voir logs / état en temps réel

**Note** : Web Serial requiert **Chrome/Edge** (contexte sécurisé). Non supporté
par Firefox/Safari.

---

## 11. Étapes d'implémentation

> Suivi mis à jour après chaque étape.

- [x] 1. `platformio.ini` : envs master/slave, `lib_deps`, build flags (`GROUP_KEY`).
      → `src/config.h` créé. Build `slave` OK.
- [x] 2. `servo_engine` : interpolation + easing + bruit d'idle.
      → `src/servo_engine.{h,cpp}` + banc de test dans `main.cpp`. Build OK.
- [x] 3. `mesh_comm` : ESP-NOW, en-tête, dédup, relais multi-sauts, **HMAC** + `MSG_REKEY`.
      → `src/mesh_comm.{h,cpp}` (ID auto MAC, canal fixe, HMAC-SHA256 tronqué,
      relais TTL). LED de vie onboard (GPIO2) ajoutée. Build master+slave OK.
- [ ] 4. `animation` : keyframes + lecteur + RNG.
- [ ] 5. `audio` : wrapper DFPlayer + mapping (maître).
- [ ] 6. `droid` + `main.cpp` : machine à états et câblage des modules.
- [ ] 7. `config_store` (NVS) + `registry` : persistance et inventaire des droïdes.
- [ ] 8. `serial_console` : pont JSON USB (maître).
- [ ] 9. `web/dashboard.html` : page de supervision Web Serial.

---

## 12. Vérification

1. `pio run -e master` **et** `pio run -e slave` compilent sans erreur.
2. Sweep servo **fluide** (aucune saccade).
3. `MSG_ANIM` émis par le maître **relayé sur ≥ 2 sauts** et exécuté par un
   esclave distant (dédup : pas de tempête de broadcast).
4. Animation sur le maître **joue la piste son** correspondante (1 des 10).
5. Deux groupes avec **mots de passe différents** s'ignorent mutuellement ;
   message falsifié rejeté.
6. `dashboard.html` (Chrome) connecté au maître : la **liste des droïdes** se
   peuple ; anim/volume/nom/clé déclenchés depuis la page ; réglages **persistés**
   après reboot.

