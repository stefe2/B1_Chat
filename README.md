# B1 Chat — Contrôle multi-droïdes B1 Battle Droid

Firmware ESP32 (PlatformIO / Arduino) pour animer plusieurs têtes de **droïdes de
combat B1** en réseau **ESP-NOW mesh multi-sauts**, avec des mouvements **fluides
et organiques** et du **son joué par un droïde maître**.

> Plan détaillé et suivi d'avancement : [`project.md`](project.md).

## Fonctionnalités

- **1 ESP32 par droïde** — firmware unique, rôle maître/esclave par build flag.
- **Réseau ESP-NOW mesh multi-sauts** (relais avec TTL + déduplication) : les
  droïdes hors de portée directe sont atteints par relais.
- **Tête pan/tilt** — 2 servos par droïde, moteur d'interpolation à 50 Hz avec
  *easing* et bruit d'idle pour un rendu vivant.
- **Animations aléatoires** coordonnées par le maître (synchronisées ou décalées).
- **Audio** joué par le maître via un **DFPlayer Mini** (sortie DAC → ampli).
- **Identité auto** — l'ID de chaque droïde est dérivé de sa MAC : un nouveau
  droïde se flashe tel quel, **zéro configuration**.
- **Sécurité réseau** — clé de groupe **HMAC-SHA256** : deux séries de B1
  indépendantes s'ignorent et les messages falsifiés sont rejetés.
- **Console web USB** — page autonome (Web Serial API) branchée sur le maître
  pour lister les droïdes, déclencher des animations, régler le volume, nommer
  les droïdes et changer la clé de groupe.

## Matériel

- Carte : **DOIT ESP32 DevKit V1** (1 par droïde)
- 2 servos PWM standard (SG90 / MG996R) par droïde
- Maître : **DFPlayer Mini** + carte SD + **ampli externe** (ex. PAM8403) + HP
- Alimentation **5 V externe** pour les servos (masse commune avec l'ESP32)

### Câblage (résumé)

| Fonction | GPIO ESP32 |
|----------|-----------|
| Servo pan | GPIO25 |
| Servo tilt | GPIO26 |
| DFPlayer TX2 → RX (via 1 kΩ) | GPIO17 |
| DFPlayer RX2 ← TX | GPIO16 |
| DFPlayer BUSY (optionnel) | GPIO4 |

Détails complets (audio DAC → ampli, broches à éviter) dans [`project.md`](project.md).

## Build & flash (PlatformIO)

```bash
# Maître (coordination + son + console web)
pio run -e master -t upload

# Esclave (identique pour chaque droïde, aucune config)
pio run -e slave -t upload
```

Clé de groupe par défaut définie dans `platformio.ini` (`-D GROUP_KEY`) ;
modifiable ensuite via la console web.

## Structure du projet

```
platformio.ini    envs master / slave, dépendances, build flags
project.md        plan détaillé + suivi d'avancement
src/
  config.h        pins, bornes d'angles, paramètres mesh/audio
  main.cpp        point d'entrée
web/
  dashboard.html  console de supervision Web Serial (à venir)
```

## État du projet

En développement, par étapes. Voir la section *Étapes d'implémentation* de
[`project.md`](project.md) pour l'avancement à jour.
