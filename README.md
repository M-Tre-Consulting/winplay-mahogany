# AirPlay per Windows

Un sender AirPlay 2 nativo per Windows: cattura l'audio di sistema e lo
trasmette a qualunque speaker/TV AirPlay 2 (HomePod, Apple TV, altoparlanti
AirPlay 2 di terze parti), con pairing e crittografia reali — non un
"receiver" che riceve da iPhone, ma il contrario: Windows che *manda* audio
alla rete Apple.

Apple non pubblica API per un sender AirPlay su piattaforme non Apple.
Questo progetto ricostruisce il protocollo (pairing HAP, RTSP cifrato,
streaming RTP) seguendo la documentazione tecnica di due progetti open
source indipendenti, validati su hardware reale — vedi [NOTICE.md](NOTICE.md)
per l'attribuzione completa.

## Stato attuale

**Fase 1 — audio: implementata, testata a fondo, non ancora provata contro hardware Apple reale.**

Tutta la parte protocollare (`src/AirPlaySender.Core`) è scritta, compila
pulita e ha **31 test automatici**, incluso un test end-to-end che fa girare
`AirPlaySession` contro un *finto ricevitore AirPlay 2* scritto da zero
apposta per i test (`FakeAirPlay2Receiver`): un server TCP/UDP indipendente
che implementa il lato server di SRP-6a e la sequenza RTSP/bplist, senza
riusare il codice del client. Quel test fa completare all'app un pairing
transient completo, cifrare un pacchetto audio RTP reale, e lo decifra sul
lato server verificando che i byte corrispondano esattamente a quelli
attesi — è la verifica più forte possibile senza un dispositivo Apple vero.
Uno smoke-test manuale ha inoltre confermato che la cattura audio WASAPI e
la discovery mDNS funzionano davvero su Windows.

Quello che **manca ancora** prima di poter dire "funziona" senza riserve:
una sessione di prova contro un HomePod o un Apple TV *reale* — un
ricevitore Apple vero potrebbe avere comportamenti non documentati che
nessun test locale può anticipare.

**Fase 2 — screen mirroring: non implementata, R&D vera e propria.**

A differenza dell'audio, il mirroring dello schermo AirPlay non ha *nessun*
riferimento open source funzionante per il verso "Windows come sender". Vedi
la sezione [Roadmap](#roadmap) più sotto.

## Architettura

```
src/
  AirPlaySender.Core/     libreria .NET 9 — tutto il protocollo, nessuna UI
    Crypto/               SRP-6a-3072, HKDF-SHA512, ChaCha20-Poly1305/X25519/Ed25519 (NSec/libsodium)
    Tlv/                  HomeKit TLV8
    Plist/                bplist00 binario (encoder/decoder minimale)
    Discovery/             mDNS (_raop._tcp, _airplay._tcp) + parsing feature flags
    Pairing/               pair-setup (transient + PIN) e pair-verify
    Rtsp/                  connessione RTSP con framing cifrato AirPlay 2 + canale eventi
    Audio/                 encoder ALAC "uncompressed", trasporto RTP, cattura WASAPI
    AirPlaySession.cs      orchestratore: connect → pair → handshake → stream

  AirPlaySender.App/      app WinUI 3 (finestra, lista dispositivi, dialog PIN, volume)

tests/
  AirPlaySender.Core.Tests/
    TestSupport/                 FakeAirPlay2Receiver — un ricevitore AirPlay 2 indipendente per i test end-to-end
    AirPlaySessionIntegrationTests.cs   handshake completo client↔finto-ricevitore su loopback
    *Tests.cs                    xUnit — crypto, TLV8, bplist, encoder ALAC, feature flags
```

## Come si compila

Serve **Visual Studio 2022** con il workload **"Sviluppo Windows universale"**
installato (necessario per i task MSBuild di packaging AppX/PRI usati anche
dalle app WinUI 3 *non* pacchettizzate — è una lacuna nota del solo SDK
`dotnet` da riga di comando, si veda
[microsoft/WindowsAppSDK#4889](https://github.com/microsoft/WindowsAppSDK/issues/4889)).

```powershell
dotnet test tests/AirPlaySender.Core.Tests/AirPlaySender.Core.Tests.csproj
dotnet build src/AirPlaySender.App/AirPlaySender.App.csproj -r win-x64
```

Al primo avvio Windows chiederà il permesso firewall per il traffico di rete
locale (mDNS discovery + streaming audio via UDP) — va consentito almeno per
la rete privata.

## Come funziona (in breve)

1. **Discovery**: scansione mDNS di `_raop._tcp` (l'endpoint audio) e
   `_airplay._tcp` (flag di funzionalità/modello).
2. **Autenticazione**: in base ai flag annunciati dal dispositivo, si sceglie
   fra pairing *transient* (PIN fisso, HomePod/macOS — nessuna interazione
   utente) o pairing con **PIN a schermo** (Apple TV, la prima volta soltanto:
   le credenziali vengono salvate per le connessioni successive).
3. **Handshake AirPlay 2**: `GET /info` → `SETUP` sessione → apertura canale
   eventi → `RECORD` → `SETUP` stream (negozia ALAC realtime + la chiave
   audio).
4. **Streaming**: l'audio di sistema (WASAPI loopback) viene ricampionato a
   44.1kHz/16-bit, impacchettato in frame ALAC "non compressi", cifrato
   ChaCha20-Poly1305 e spedito via RTP, con sync a 1Hz e risposta alle
   richieste di ritrasmissione del ricevitore.

## Limitazioni note (Fase 1)

- Un solo dispositivo collegato per volta (nessun multi-room/AirPlay group).
- Nessuna rilevazione automatica di connessione persa a metà streaming (va
  disconnesso e riconnesso manualmente).
- Dispositivi AirPlay 1 "legacy" con password RTSP o auth MFi-SAP (vecchi
  AirPort Express) non sono ancora supportati — l'app mostra un errore
  chiaro invece di tentare una connessione destinata a fallire.

## Roadmap

- **Fase 1.1**: prova end-to-end contro hardware reale, system tray icon,
  rilevazione disconnessione, multi-room.
- **Fase 2 (screen mirroring)**: R&D aperta. Nessun progetto open source
  implementa oggi un sender AirPlay Mirroring funzionante (esistono solo
  *receiver*, cioè il verso opposto). Servirebbe: cattura schermo
  (Desktop Duplication API), encoding H.264 hardware (Media Foundation),
  e — il vero ignoto — la crittografia del canale video, mai documentata
  pubblicamente con lo stesso livello di dettaglio dell'audio. Da trattare
  come progetto di ricerca a sé, con hardware Apple reale a disposizione per
  il reverse engineering iterativo.

## Licenza e attribuzioni

Vedi [NOTICE.md](NOTICE.md).
