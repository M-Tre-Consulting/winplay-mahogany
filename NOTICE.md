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
