# Contrat firmware — extensions proposées au protocole B1

Ce document décrit les évolutions du protocole JSON-lines (`{cmd:...}` → `{evt:...}`,
115200 bauds, une ligne par message) que la console attend du firmware ESP32 maître.
La console (≥ v0.7.0) fonctionne **sans** ces extensions — chaque section décrit le
comportement de repli actuel et ce qui s'améliorera quand l'extension sera implémentée.

Rédigé le 2026-07-12, pendant que le code source du firmware n'était pas disponible.
À implémenter côté firmware (voir `src/sequence_store.h` pour le stockage des séquences).

---

## 1. Trame audio attachée à une séquence (priorité haute)

**Aujourd'hui** : la console associe une piste audio (1-10) à chaque slot de séquence,
mais uniquement dans son propre `localStorage` (`b1.audioBySlot`). Un `seqRun` lancé
par le maître ne joue **aucun** son ; seul le mode « Répéter » de la console
synchronise l'audio (elle envoie `playTrack` elle-même).

**Demandé** : `seqSave` accepte un champ `track` (entier 1-10, ou `null` = pas d'audio),
persisté en NVS avec la séquence.

```json
→ {"cmd":"seqSave","slot":2,"name":"Parade","loop":false,"track":3,"steps":[...]}
← {"evt":"seqSaved","ok":true,"slot":2,"name":"Parade"}

→ {"cmd":"seqLoad","slot":2}
← {"evt":"seqData","slot":2,"name":"Parade","loop":false,"track":3,"steps":[...]}
```

- `seqRun` sur un slot avec `track` non nul : le maître lance la piste (équivalent
  `playTrack`) **au moment de la première étape**, puis déroule les étapes normalement.
- `seqList` inclut `track` dans chaque entrée (affichage catalogue).
- Champ absent/inconnu = `null` (rétro-compatible).

**Migration console** : quand `seqData` contiendra `track`, la console l'utilisera comme
source de vérité et migrera son `localStorage` vers le firmware au premier `seqSave`.

## 2. Durée des pistes audio (priorité haute)

**Aujourd'hui** : la console chronomètre les pistes à la main (bouton « Mesurer la
durée ») pour dessiner la trame audio à l'échelle sur la timeline.

**Demandé** : si le module audio le permet (DFPlayer & co. savent parfois lire la
durée, sinon tabler sur une table maintenue avec les fichiers) :

```json
→ {"cmd":"getTrackDurations"}
← {"evt":"trackDurations","list":[{"track":1,"ms":12400},{"track":2,"ms":8100}]}
```

Pistes de durée inconnue : omises de la liste. La console gardera la mesure manuelle
en repli pour celles-là.

## 3. Lecture de la configuration générale (priorité moyenne)

**Aujourd'hui** : la console envoie `getConfig` au handshake mais **ne connaît pas la
forme de la réponse** (elle la journalise sans l'interpréter). Conséquences : les
curseurs volume/fréquence/amplitude/vitesse affichent des valeurs par défaut au
démarrage, et la restauration de sauvegarde ne peut pas comparer ces paramètres
(ils sont proposés « à l'aveugle », décochés par défaut).

**Demandé** : documenter/normaliser la réponse ainsi :

```json
→ {"cmd":"getConfig"}
← {"evt":"config","volume":20,"freq":50,"amp":60,"speed":50}
```

La console peuplera alors ses curseurs à la connexion et fera une vraie
réconciliation champ par champ de ces valeurs à la restauration.

## 4. Écriture atomique par lot — `setMulti` (priorité moyenne)

Inspiré du `SETM` du firmware Kyber : la restauration de sauvegarde envoie
aujourd'hui une rafale de commandes espacées de 200 ms (noms, calibrations,
séquences) — lent et interruptible à mi-chemin.

**Demandé** :

```json
→ {"cmd":"setMulti","ops":[
     {"cmd":"name","id":513,"name":"Rex"},
     {"cmd":"calib","target":513,"panMin":20,...},
     {"cmd":"seqSave","slot":0,"name":"Parade","loop":false,"steps":[...]}
   ]}
← {"evt":"setMultiDone","ok":true,"applied":3}
```

- Tout ou rien : si une op échoue, aucune n'est persistée, et la réponse indique
  l'index fautif : `{"evt":"setMultiDone","ok":false,"failedAt":1,"error":"..."}`.
- Taille bornée par le tampon série : accepter au minimum 4 Ko par ligne, et la
  console fragmentera ses lots au-delà.

## 5. Contrôle de lecture enrichi (confort)

- `{"cmd":"seqRun","slot":2,"from":5}` — démarrer à l'étape 6 (la console émule ça en
  mode répétition, mais pas pour l'exécution autonome).
- `{"cmd":"seqPause"}` / `{"cmd":"seqResume"}` — avec `{"evt":"seqState","paused":true,...}`.
- `seqState` pendant la lecture : pousser l'événement **à chaque étape** (déjà le cas
  semble-t-il) et inclure `track` s'il y a une trame audio.

## Rappels d'implémentation

- Une ligne = un JSON complet terminé par `\n` ; ignorer les lignes invalides.
- Champs inconnus dans une commande : les **ignorer** (la console peut être plus
  récente que le firmware). Ne jamais échouer sur un champ en trop.
- Toute nouvelle réponse doit garder un champ `evt` unique et stable — la console
  route exclusivement là-dessus.
