#pragma once

// ============================================================================
//  B1 Battle Droid — Configuration matérielle et constantes globales
//  Voir project.md (section 3 Câblage) pour le détail des branchements.
// ============================================================================

#include <Arduino.h>

// ---------------------------------------------------------------------------
//  VERSION DU FIRMWARE — source de vérité pour les releases GitHub.
//  Annoncée à la console dans la réponse hello (champ "fw"); la console la
//  compare à la derniere release de stefe2/B1_Chat pour proposer une mise a
//  jour. Bump a chaque release (tools/release.ps1 s'appuie dessus).
// ---------------------------------------------------------------------------
#define FW_VERSION "1.3.9"

// Version du protocole série console<->maître (incrementée quand un changement
// n'est pas retro-compatible; les ajouts de champs/commandes n'en ont pas besoin).
#define FW_PROTO 2

// ---------------------------------------------------------------------------
//  RÔLE DU DROÏDE  —  À RÉGLER ICI AVANT DE FLASHER (pio run -e b1 -t upload)
//  1 = MAÎTRE (un seul dans le réseau : coordination + son + console web)
//  0 = ESCLAVE (valeur par défaut pour tous les autres droïdes)
//  Surchargeable via build_flags (-D IS_MASTER=0|1) — utilisé par les
//  environnements b1_master/b1_slave (platformio.ini) pour la release CI,
//  qui compilent les deux rôles sans toucher à ce fichier.
// ---------------------------------------------------------------------------
#ifndef IS_MASTER
#define IS_MASTER 1
#endif

// Pause temporaire des servos/animations sur le MAÎTRE (protège les servos
// pendant la mise au point de la page web). Mettre à 0 pour réactiver.
#define MASTER_ANIM_PAUSED 1

// ---------------------------------------------------------------------------
//  Réseau & clé de groupe (définis par les build flags dans platformio.ini)
// ---------------------------------------------------------------------------
#ifndef MESH_TTL
#define MESH_TTL 4              // nombre de sauts max pour le relais mesh
#endif

#ifndef GROUP_KEY
#define GROUP_KEY "changeme"    // clé de réseau par défaut (compilée)
#endif

// ---------------------------------------------------------------------------
//  LED de vie (onboard) — témoin d'exécution du programme
// ---------------------------------------------------------------------------
static const uint8_t PIN_LED_ONBOARD = 2;   // LED bleue intégrée DOIT DevKit V1
static const uint16_t LED_BLINK_MS   = 500; // période de clignotement

// ---------------------------------------------------------------------------
//  Servos (tous les droïdes) — signal PWM
// ---------------------------------------------------------------------------
static const uint8_t PIN_SERVO_PAN  = 25;   // GPIO25 -> servo pan
static const uint8_t PIN_SERVO_TILT = 26;   // GPIO26 -> servo tilt

// Bornes mécaniques (degrés). À ajuster selon le montage de la tête.
static const uint8_t SERVO_PAN_MIN   = 20;
static const uint8_t SERVO_PAN_MAX   = 160;
static const uint8_t SERVO_PAN_CENTER = 90;

static const uint8_t SERVO_TILT_MIN   = 60;
static const uint8_t SERVO_TILT_MAX   = 120;
static const uint8_t SERVO_TILT_CENTER = 90;

// Largeurs d'impulsion (µs) pour la calibration ESP32Servo.
static const uint16_t SERVO_MIN_US = 500;
static const uint16_t SERVO_MAX_US = 2400;

// Fréquence de mise à jour du moteur de mouvement.
static const uint16_t SERVO_UPDATE_HZ = 50;

// ---------------------------------------------------------------------------
//  Audio DFPlayer Mini (maître uniquement) — UART2
// ---------------------------------------------------------------------------
static const uint8_t PIN_DFPLAYER_RX = 16;  // ESP RX2  <- DFPlayer TX
static const uint8_t PIN_DFPLAYER_TX = 17;  // ESP TX2  -> DFPlayer RX (via 1k)
static const uint8_t PIN_DFPLAYER_BUSY = 4; // BUSY (optionnel)

static const uint8_t  AUDIO_TRACK_COUNT   = 10;   // pistes 0001..0010 sur SD
static const uint8_t  AUDIO_VOLUME_DEFAULT = 20;  // 0..30

// ---------------------------------------------------------------------------
//  Réseau ESP-NOW
// ---------------------------------------------------------------------------
static const uint8_t MESH_WIFI_CHANNEL = 1;   // canal radio commun à tout le groupe
static const uint8_t MESH_DEDUP_CACHE  = 32;  // taille du cache anti-doublon

// Topologie du mesh (voisinage radio direct, indépendant des relais).
static const uint8_t  MAX_NEIGHBORS      = 12;   // voisins directs max par rapport
static const uint32_t NEIGHBOR_REPORT_MS = 3000; // période de diffusion du voisinage
static const uint32_t NEIGHBOR_STALE_MS  = 9000; // péremption d'un lien radio (~3x la période)

// ---------------------------------------------------------------------------
//  Timing des animations (ms)
// ---------------------------------------------------------------------------
static const uint32_t IDLE_ANIM_MIN_MS = 3000;   // délai mini avant une anim locale
static const uint32_t IDLE_ANIM_MAX_MS = 9000;   // délai maxi avant une anim locale
static const uint32_t HEARTBEAT_MS     = 2000;   // période d'émission heartbeat

// ---------------------------------------------------------------------------
//  OTA firmware (esclaves, relayé par le mesh) — voir CLAUDE.md
// ---------------------------------------------------------------------------
static const uint8_t  OTA_MESH_TTL          = 2;        // TTL réduit dédié aux paquets OTA
static const uint32_t OTA_MAX_IMAGE_SIZE    = 1200000UL; // marge sous les 1.25 Mo d'une partition app
static const uint32_t OTA_ACK_TIMEOUT_MS    = 400;      // délai avant retransmission d'un chunk/start/end
static const uint8_t  OTA_MAX_RETRIES       = 5;        // tentatives avant échec de session (maître)
static const uint32_t OTA_SESSION_TIMEOUT_MS = 60000;   // inactivité max côté esclave avant auto-abort
                                                         // (> OTA_SERIAL_IDLE_TIMEOUT_MS : le maître doit
                                                         // toujours abandonner AVANT l'esclave, pour pouvoir
                                                         // le prévenir par MSG_OTA_ABORT — un hoquet série
                                                         // résorbé ne doit pas trouver un esclave déjà parti)
static const uint32_t OTA_SERIAL_IDLE_TIMEOUT_MS = 45000; // inactivité max côté maître (console disparue)
static const uint8_t  OTA_MAX_BOOT_ATTEMPTS = 3;        // tentatives de boot avant rollback (OtaGuard)
static const uint32_t OTA_VERIFY_UPTIME_MS  = 20000;    // uptime requis pour confirmer un nouveau firmware
static const uint32_t OTA_REBOOT_WAIT_MS    = 90000;    // délai max pour confirmer un heartbeat post-OTA
