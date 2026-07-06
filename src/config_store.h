#pragma once

// ============================================================================
//  ConfigStore — persistance des réglages en NVS (Preferences)
//
//  Stocke le volume audio, les paramètres d'animation et les noms attribués
//  aux droïdes (association srcId -> nom). Survit aux redémarrages.
//  Voir project.md (§10, réglages persistés).
// ============================================================================

#include <Arduino.h>
#include <Preferences.h>

// Bornes mécaniques (degrés) d'un droïde, persistées individuellement.
struct ServoCalib {
    uint8_t panMin, panCenter, panMax;
    uint8_t tiltMin, tiltCenter, tiltMax;
};

class ConfigStore {
public:
    void begin();

    // Volume audio (0..30).
    uint8_t volume();
    void    setVolume(uint8_t v);

    // Paramètres d'animation (0..100 chacun).
    void animParams(uint8_t& freq, uint8_t& amp, uint8_t& speed);
    void setAnimParams(uint8_t freq, uint8_t amp, uint8_t speed);

    // Nom d'un droïde (vide si non défini).
    String getName(uint16_t id);
    void   setName(uint16_t id, const String& name);

    // Calibration servo d'un droïde (bornes de config.h si jamais réglée).
    ServoCalib getCalib(uint16_t id);
    void       setCalib(uint16_t id, const ServoCalib& c);

private:
    Preferences _p;
    static void nameKey(uint16_t id, char out[8]);
    static void calibKey(uint16_t id, char out[8]);
};

extern ConfigStore Config;
