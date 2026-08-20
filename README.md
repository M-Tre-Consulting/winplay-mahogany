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

**Fase 1 — audio: implementata, testata a fondo, l'app compila e gira; non ancora provata contro hardware Apple reale.**

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

L'app WinUI 3 (`src/AirPlaySender.App`) **compila senza errori e si avvia
correttamente** — finestra nativa con titlebar personalizzata, tema
scuro/chiaro automatico (Mica), lista dispositivi ed empty-state verificati
visivamente con uno screenshot reale della finestra in esecuzione.

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
installato — non per usare l'IDE, ma perché quel workload è l'unico modo di
avere sul disco i task MSBuild di packaging AppX/PRI che *anche* le app
WinUI 3 non pacchettizzate richiedono in fase di build. Con solo l'SDK
`dotnet` da riga di comando quei task non esistono da nessuna parte
([microsoft/WindowsAppSDK#4889](https://github.com/microsoft/WindowsAppSDK/issues/4889));
`AirPlaySender.App.csproj` punta `AppxMSBuildToolsPath` alla cartella reale
del workload installato apposta per far funzionare `dotnet build` senza
dover invocare `msbuild.exe` a mano — se il percorso della tua installazione
VS è diverso, aggiusta quella proprietà nel csproj.

```powershell
dotnet test tests/AirPlaySender.Core.Tests/AirPlaySender.Core.Tests.csproj
dotnet build src/AirPlaySender.App/AirPlaySender.App.csproj -r win-x64
```

Al primo avvio Windows chiederà il permesso firewall per il traffico di rete
locale (mDNS discovery + streaming audio via UDP) — va consentito almeno per
la rete privata.

**Nota per chi tocca `AirPlaySender.App`**: XamlCompiler.exe (lo strumento
.NET Framework 4.7.2 di WindowsAppSDK 1.6 che compila XAML/x:Bind) va in
crash silenzioso, senza nessun messaggio d'errore, su due pattern specifici
verificati empiricamente in questo progetto:
1. proprietà C# `required` su un tipo raggiungibile dalla superficie
   pubblica bindabile (vedi il commento su `AirPlayDevice`);
2. istanziare un tipo `local:` (dello stesso progetto, non ancora compilato)
   come elemento risorsa XAML, es. in `Window.Resources` — anche una classe
   vuota, converter o no. Per questo `DeviceItem` espone proprietà
   `Visibility`/`bool` già calcolate invece di usare `IValueConverter`.

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
- **Fase 2 (screen mirroring)**: R&D aperta, **deliberatamente rimandata**
  (non affrontata insieme all'audio, per scelta). Nessun progetto open
  source implementa oggi un sender AirPlay Mirroring funzionante (esistono
  solo *receiver*, cioè il verso opposto). Servirebbe: cattura schermo
  (Desktop Duplication API), encoding H.264 hardware (Media Foundation),
  e — il vero ignoto — la crittografia del canale video, mai documentata
  pubblicamente con lo stesso livello di dettaglio dell'audio. Da trattare
  come progetto di ricerca a sé, con hardware Apple reale a disposizione per
  il reverse engineering iterativo, quando si deciderà di affrontarla.

## Licenza e attribuzioni

Vedi [NOTICE.md](NOTICE.md).
