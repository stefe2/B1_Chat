#pragma once

// ============================================================================
//  ConfigStore — persistance des réglages en NVS (Preferences)
//
//  Stocke le volume audio, les paramètres d'animation et les noms attribués
//  aux droïdes (association srcId -> nom). Survit aux redémarrages.
//
//  Modèle commit/revert (inspiré de KyberEditor) pour volume / params d'anim /
//  noms : les setters écrivent une surcouche RAM (« pending », l'effet est
//  immédiat via les getters) et la NVS n'est touchée qu'au commitPending() ;
//  revertPending() jette la surcouche et revient aux valeurs persistées.
//  La calibration servo (setCalib) reste à persistance IMMÉDIATE : c'est un
//  réglage physique fait en direct sur le droïde ciblé.
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
    // Persistance immédiate — hors du modèle commit/revert.
    ServoCalib getCalib(uint16_t id);
    void       setCalib(uint16_t id, const ServoCalib& c);

    // Statut d'adoption d'un droïde (false = jamais adopté). Persistance
    // immédiate, hors du modèle commit/revert : setAdopted(id, false) efface
    // la clé plutôt que d'écrire false, pour repartir de zéro proprement.
    bool isAdopted(uint16_t id);
    void setAdopted(uint16_t id, bool adopted);

    // Modèle commit/revert (volume, params d'anim, noms).
    bool dirty() const { return _dirty; }
    void commitPending();   // écrit la surcouche RAM en NVS puis la vide
    void revertPending();   // jette la surcouche RAM (la NVS fait foi)

private:
    Preferences _p;
    static void nameKey(uint16_t id, char out[8]);
    static void calibKey(uint16_t id, char out[8]);
    static void adoptKey(uint16_t id, char out[8]);

    // Surcouche RAM des modifications non engagées.
    bool    _dirty = false;
    bool    _pendVolSet = false;
    uint8_t _pendVol = 0;
    bool    _pendAnimSet = false;
    uint8_t _pendFreq = 0, _pendAmp = 0, _pendSpeed = 0;
    static const uint8_t PENDING_NAMES_MAX = 32;   // = Registry::MAX
    struct PendingName { bool used; uint16_t id; String name; };
    PendingName _pendNames[PENDING_NAMES_MAX];

    void refreshDirty();
};

extern ConfigStore Config;
