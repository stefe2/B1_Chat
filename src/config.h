#pragma once

// ============================================================================
//  B1 Battle Droid — Configuration matérielle et constantes globales
//  Voir project.md (section 3 Câblage) pour le détail des branchements.
// ============================================================================

#include <Arduino.h>

// ---------------------------------------------------------------------------
//  RÔLE DU DROÏDE  —  À RÉGLER ICI AVANT DE FLASHER
//  1 = MAÎTRE (un seul dans le réseau : coordination + son + console web)
//  0 = ESCLAVE (valeur par défaut pour tous les autres droïdes)
// ---------------------------------------------------------------------------
#define IS_MASTER 0

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

// ---------------------------------------------------------------------------
//  Timing des animations (ms)
// ---------------------------------------------------------------------------
static const uint32_t IDLE_ANIM_MIN_MS = 3000;   // délai mini avant une anim locale
static const uint32_t IDLE_ANIM_MAX_MS = 9000;   // délai maxi avant une anim locale
static const uint32_t HEARTBEAT_MS     = 2000;   // période d'émission heartbeat
