# Attribuzioni

AirPlay non ha API pubbliche per un mittente (sender) su piattaforme non
Apple. Il protocollo implementato in questo progetto (`src/AirPlaySender.Core`)
è stato ricostruito seguendo la documentazione tecnica pubblicata da due
progetti open source indipendenti, entrambi validati contro hardware Apple
reale (Apple TV 4K, HomePod/macOS):

## akustikrausch/airplay2-sender-cpp

<https://github.com/akustikrausch/airplay2-sender-cpp> — Licenza **Apache-2.0**.

Fonte primaria per la sequenza esatta di pairing HAP (pair-setup SRP-6a
transient/PIN, pair-verify X25519), le stringhe HKDF-SHA512 salt/info, il
framing ChaCha20-Poly1305 del canale di controllo e del canale eventi, il
formato dei pacchetti RTP/sync/timing, e il layout bit-a-bit del frame ALAC
"uncompressed" usato dal percorso audio realtime di AirPlay 2. Questi dettagli
non sono pubblicati da Apple e sono stati ricostruiti da quel progetto tramite
reverse engineering pulito ("clean-room"), poi verificati contro un vero
Apple TV 4K e macOS.

## postlund/pyatv

<https://github.com/postlund/pyatv> — Copyright (c) Pierre Ståhl — Licenza **MIT**.

Fonte di riferimento incrociato per il parsing dei flag mDNS/TXT AirPlay
(`features`/`ft`, `sf`/`flags`, `et`), la logica di scelta del metodo di
pairing (transient vs PIN vs pair-verify con credenziali salvate), e come
controllo indipendente sui dettagli del protocollo HAP.

## FDH2/UxPlay (Fase 2 — ricevitore di mirroring)

<https://github.com/FDH2/UxPlay> — Licenza **GPLv3**.

A differenza dei due riferimenti sopra (usati come *ricetta*, non come
codice), la parte di questo progetto sotto `src/AirPlaySender.Core/Receiving/`
che implementa il cifrario FairPlay (`FairPlayCipher.cs`,
`FairPlayCipherTables.g.cs`) è un **porting diretto** del codice sorgente di
UxPlay (`lib/playfair/{omg_hax.c,omg_hax.h,sap_hash.c,hand_garble.c,
modified_md5.c,playfair.c}` e `lib/fairplay_playfair.c`), non una
reimplementazione indipendente seguendo una specifica — perché per questo
specifico algoritmo non esiste nessuna specifica pubblica da seguire: è
codice ricavato da disassemblaggio ("OmgHax"), di cui nemmeno UxPlay
rivendica di aver capito il funzionamento interno, solo di aver osservato
che riproduce l'output atteso.

Questo rende **questa parte specifica** del progetto un'opera derivata sotto
licenza GPLv3, non un'implementazione pulita come il resto — un vincolo di
licenza reale, non solo un'attribuzione di cortesia. Le tabelle sono state
estratte meccanicamente (non ritrascritte a mano) e verificate byte-per-byte
con hash SHA-256 incrociati contro la fonte C; la logica in `Garble()` è
copiata carattere-per-carattere dall'espressione C originale. Il resto della
Fase 2 (annuncio mDNS, server RTSP, pairing come accessorio, framing dei
pacchetti video) segue invece la *sequenza* documentata da UxPlay ma è
scritto da zero, come per i riferimenti Apache/MIT sopra.

**Implicazione pratica**: questo progetto resta un repository privato per
uso personale/R&D, non distribuito pubblicamente — coerente con quanto
discusso esplicitamente nella cronologia del progetto. Se in futuro si
volesse distribuire pubblicamente l'intero progetto, la parte derivata da
UxPlay dovrebbe essere trattata secondo i termini GPLv3 (es. isolata,
rilicenziata di conseguenza, o sostituita), a differenza del resto del
codice che non ha questo vincolo.

## Come sono stati usati

Il codice in questo repository è stato scritto da zero in C#, seguendo la
*ricetta* (sequenza di messaggi, stringhe costanti, formati dei pacchetti)
documentata da questi due progetti — non è una traduzione riga-per-riga del
loro codice sorgente. Dove il codice di questo repository descrive
esplicitamente questa provenienza (commenti nei file sotto
`src/AirPlaySender.Core/Pairing`, `Crypto`, `Audio`), è per tracciabilità e
per dare credito a chi ha fatto per primo il lavoro di reverse engineering,
non perché sia richiesto da un obbligo di licenza su codice copiato
letteralmente.

## Librerie di terze parti (NuGet)

| Pacchetto | Licenza | Uso |
|---|---|---|
| [NSec.Cryptography](https://github.com/ektrah/nsec) | MIT | X25519, Ed25519, ChaCha20-Poly1305 (libsodium) |
| [Zeroconf](https://github.com/novotnyllc/Zeroconf) | MIT | Discovery mDNS/Bonjour (`_raop._tcp`, `_airplay._tcp`) |
| [NAudio](https://github.com/naudio/NAudio) | MIT | Cattura audio WASAPI loopback + resampling |
| [Microsoft.WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) | MIT | WinUI 3 |
| [Makaretu.Dns.Multicast](https://github.com/richardschneider/net-mdns) | MIT | Annuncio mDNS (`_airplay._tcp`) per la Fase 2 |
