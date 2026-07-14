#pragma once

// ============================================================================
//  OtaGuard — sécurité anti-brick pour l'OTA (tous rôles)
//
//  Avant de rebooter sur une image fraîchement écrite, on arme un flag NVS
//  ("pending" + compteur de tentatives). Au boot suivant, si ce flag est
//  présent, on incrémente le compteur ; au-delà de OTA_MAX_BOOT_ATTEMPTS on
//  bascule manuellement esp_ota_set_boot_partition() vers l'AUTRE partition
//  (esp_ota_get_next_update_partition alterne forcément app0/app1) et on
//  redémarre — un rollback fait "à la main" avec l'API esp_ota_ops standard,
//  sans dépendre du rollback bootloader d'ESP-IDF (non exposé simplement en
//  framework Arduino). Si le firmware tourne sans reset pendant
//  OTA_VERIFY_UPTIME_MS, le flag est effacé : l'image est confirmée bonne.
//
//  earlyCheck() DOIT être la toute première ligne de setup(), avant tout
//  autre code : un crash survenant avant cet appel ne serait jamais compté
//  (risque résiduel assumé, voir CLAUDE.md).
// ============================================================================

#include <Arduino.h>

class OtaGuard {
public:
    // Retourne true si un rollback a été déclenché (ne revient jamais en
    // pratique : esp_restart() est appelé avant le retour).
    bool earlyCheck();

    // À appeler à chaque tour de loop().
    void confirmIfPending(uint32_t nowMs);

    // À appeler juste avant Update.end(true) réussi, avant ESP.restart().
    void armPendingReboot();

private:
    bool _pendingActive = false;
    uint32_t _bootMs = 0;
};

extern OtaGuard Guard;
