# WinPlay Mahogany

AirPlay nativo per Windows, nei due versi:

- **Sender audio** (Fase 1, funzionante): cattura l'audio di sistema e lo
  trasmette a qualunque speaker/TV AirPlay 2 (HomePod, Apple TV, altoparlanti
  AirPlay 2 di terze parti), con pairing e crittografia reali.
- **Ricevitore di mirroring** (Fase 2, **funziona** 🎉 — vedi sotto): fa
  comparire questo PC come bersaglio "Duplica schermo" nel Centro di
  Controllo di un iPhone. Video (decoder H.264 MFT di Windows pilotato a
  mano + blit Win2D, 60fps fluidi per minuti senza freeze), audio (AAC-ELD
  via `libfdk-aac` + `AudioGraph`) e volume (segue lo slider del telefono),
  sincronizzati e verificati dal vivo contro un iPhone 13 Pro Max
  (iOS 26.6.1). Resta raggiungibile in background (icona nel tray, avvio
  automatico con Windows), e chiudere il mirroring da un lato lo chiude
  anche dall'altro.

Apple non pubblica API per nessuno dei due versi su piattaforme non Apple.
Questo progetto ricostruisce i protocolli (pairing HAP, RTSP cifrato,
streaming RTP, e per il mirroring il cifrario FairPlay vero) seguendo la
documentazione tecnica di alcuni progetti open source indipendenti, validati
dove possibile su hardware reale — vedi [NOTICE.md](NOTICE.md) per
l'attribuzione completa.

## Stato attuale

**Fase 1 — audio: funziona, verificato contro un HomePod reale.** 🎉

L'app si connette davvero a un HomePod (gen 2) in salotto, fa il pairing
transient (SRP-6a, PIN fisso, nessuna interazione utente), negozia le
chiavi AirPlay 2, e mette in stato "In riproduzione" — verificato sia da
riga di comando (log passo-passo dell'handshake) sia dall'app WinUI 3 vera
(screenshot: "Sala" → "In riproduzione", pulsante Disconnetti, barra volume
attiva). Arrivarci ha richiesto una sessione di debug seria contro
hardware reale — vedi i commit e i commenti in `AirPlaySession.cs` per i
dettagli, in breve:

- **Discovery mDNS**: due bug (interfaccia di rete sbagliata scelta in
  presenza di una VPN con metrica più bassa della LAN; lookup dei servizi
  sulla chiave sbagliata della libreria Zeroconf).
- **URI RTSP malformato**: un indirizzo locale IPv4-mappato-su-IPv6
  (`::ffff:192.168.1.88`) finiva senza parentesi quadre in un URI,
  sintatticamente invalido.
- **Schema del dizionario SETUP sbagliato**: il dizionario "sessione" deve
  offrire `timingPort`/`timingProtocol: NTP` (non `isRemoteControlOnly`,
  che è per un canale di *solo controllo remoto*, non audio — verificato
  contro la documentazione di pyatv); lo stream deve chiedere PCM raw
  (`ct=1`, `audioFormat=0x800`), non ALAC.
- **Il bug vero**: il "risponditore" della porta di timing partiva solo
  *dopo* l'intero handshake, ma il SETUP sessione dichiara quella porta
  *prima* — se il ricevitore prova a sincronizzarsi e nessuno ascolta
  ancora, resta in attesa e il SETUP non risponde mai. Bastava invertire
  l'ordine.

Tutta la parte protocollare (`src/AirPlaySender.Core`) ha **73 test
automatici** (fra gli altri: il formato dei pacchetti di
`NtpTimingSession`, la correttezza di `AesCtrKeystreamCipher`, un test di
regressione per `FairPlayCipher.Decrypt` con i byte veri — `KeyMessage`,
`ekey` e la chiave risultante — catturati da una sessione reale con
l'iPhone, e un test che fa girare `HapPairVerifyAccessorySession` (il vero
pair-verify HAP lato accessorio, scoperto stanotte con `rvictl`) contro il
`PairVerifyClient` di Fase 1, non modificato, su un socket reale; Fase 2),
incluso un test end-to-end che fa girare `AirPlaySession`
contro un *finto ricevitore AirPlay 2* scritto da zero apposta per i test
(`FakeAirPlay2Receiver`): un server TCP/UDP indipendente che implementa il
lato server di SRP-6a e la sequenza RTSP/bplist, senza riusare il codice
del client — utile per non regredire, ma è stato il test contro l'HomePod
vero a scoprire i bug elencati sopra (nessun test locale può anticipare il
comportamento di un ricevitore reale).

**Fase 2 — ricevitore di mirroring: funziona.** 🎉 Video + audio + volume,
verificati dal vivo contro un iPhone 13 Pro Max (iOS 26.6.1): l'iPhone vede
"PC-NICO" in "Duplica schermo", lo schermo appare a dimensioni native, 60fps
fluidi per minuti senza freeze né crash, audio AAC-ELD sincronizzato, il
volume segue lo slider del telefono, la X della finestra ferma il mirroring
sul telefono e viceversa. Il riferimento principale è
[UxPlay](https://github.com/FDH2/UxPlay) (GPLv3), scritto per iOS più vecchi:
molto qui è stato scoperto leggendone il *sorgente* (non i doc) e verificato
sul filo con `pktmon` e con `rvictl` su un Mac. La cronologia dei commit
racconta la caccia per esteso; qui sotto lo stato attuale.

### Il percorso protocollare

`GET /info` → **pairing come accessorio** (`PairingAccessorySession`: schema
legacy a offset di byte, non l'HAP TLV8 della Fase 1 — byte grezzi,
AES-128-CTR sulle sole firme Ed25519/X25519) → **`/fp-setup`** (byte
precatturati da UxPlay, riprodotti identici: nessuno ha mai capito
l'algoritmo di questo passo) → **cifrario FairPlay vero**
(`FairPlayCipher.cs` + `FairPlayCipherTables.g.cs`, ~1200 righe e ~480KB di
S-box portate da UxPlay/OmgHax con estrazione meccanica + hash incrociati)
che decifra la chiave di sessione AES → **`SETUP`**: una richiesta
"sessione" (con `ekey`/`eiv`, da cui la chiave), poi una con un array
`streams` — `type: 110` per il video, `type: 96` per l'audio. Lo scambio di
clock-sync (`NtpTimingSession`) parte prima di rispondere: è il *ricevitore*
a dover interrogare periodicamente la `timingPort` del client, non il
contrario. `HapPairVerifyAccessorySession`/`HapPairSetupAccessorySession`
esistono (immagine speculare del pair-verify/pair-setup HAP di Fase 1) per
lo schema moderno `X-Apple-HKP: 6` visto in una cattura reale, ma il
percorso legacy funziona fino in fondo e resta quello attivo.

### Il video (`MirroringDataReceiver` → `H264Mft` → Win2D)

Il telefono apre una connessione TCP dati dopo la `SETUP` stream. Ogni
pacchetto: header da 128 byte + payload. Fatti trovati sul filo, non nei
doc:

- **payloadSize** all'offset 0 è **little-endian**; il prefisso di lunghezza
  dentro il payload decifrato è **big-endian**.
- Il payload video è **AVCC** (prefisso di lunghezza a 4 byte per NAL), non
  Annex-B — e i due sono larghi 4 byte uguali, quindi si riscrive il
  prefisso in `00 00 00 01` **sul posto**, zero copie (`RewriteAvccToAnnexBInPlace`).
- Chiave AES-CTR video: `SHA-512("AirPlayStreamKey" + streamConnectionID + sessionKey)[0..16]`,
  con `streamConnectionID` formattato **unsigned** anche quando in C# esce
  negativo (`PRIu64` in UxPlay). Il keystream è **continuo** attraverso i
  pacchetti, non riparte dall'IV ad ogni pacchetto (`AesCtrKeystreamCipher`).
- L'**offset 8** dell'header è il timestamp del frame come **NTP a virgola
  fissa 32.32** (non nanosecondi grezzi): va convertito
  (`ns = sec·1e9 + (frac·1e9 >> 32)`, `Ntp.ToNanoseconds`). Letto storto →
  playback a ~14fps mentre ne arrivano 60.
- Lo stream manda **un solo IDR**, all'inizio, poi solo P-frame per decine
  di secondi: **mai scartare un frame** (un buco rende tutto il resto
  indecodificabile). SPS/PPS viene anteposto a ogni key frame comunque.

Il rendering: `MediaPlayerElement` e poi il *frame-server mode* di
`MediaPlayer` si bloccavano entrambi ~1 secondo dopo l'inizio (wedge interno
a quello stack di Media Foundation — `VideoFrameAvailable` scattava ~4 volte
poi mai più, con input e clock che continuavano). Risolto pilotando **il
decoder H.264 MFT di Windows direttamente** (`H264Mft.cs`, via
`Vortice.MediaFoundation`): loop esplicito `ProcessInput`/`ProcessOutput`,
`MF_LOW_LATENCY`, thread MTA dedicato, sample di output riusato tra i frame,
NV12→BGRA a mano, blit su una `CanvasSwapChainPanel` di Win2D con
`Present(0)`. Un `MirroringDataReceiver.AttachRenderer` fa il replay atomico
di config + frame-dall'ultimo-IDR a una finestra che si aggancia in ritardo.

### L'audio (`MirrorAudioReceiver` → `AacEldDecoder` → `AudioGraph`)

Stream `type: 96`, campi dalla `SETUP`: `ct=8` (**AAC-ELD**), `spf=480`,
44100/2. Windows non ha un decoder AAC-ELD (`CMSAACDecMFT` fa solo LC/HE),
esattamente perché UxPlay per il mirror-audio richiede `libfdk-aac`.

- `MirrorAudioReceiver` (UDP RTP): header RTP 12 byte, seq ai byte 2-3
  big-endian. Payload cifrato **AES-128-CBC** con la *stessa* chiave di
  sessione del video + l'`eiv`, IV reset per pacchetto, solo i blocchi
  interi da 16 byte (coda in chiaro). Riordino per sequence number con
  scarto dei re-invii ridondanti (`redundantAudio=2`).
- `AacEldDecoder`: P/Invoke a `libAACdec.dll` (NuGet `fdk-aac`,
  Fraunhofer prebuilt x64/arm64). AudioSpecificConfig `F8 E8 50 00`
  (AAC-ELD 44100/2 spf 480), verbatim da UxPlay. → PCM int16.
- `MirrorAudioPlayer`: `AudioGraph` WinRT + `AudioFrameInputNode`, ring
  buffer con prime ~50ms e drop-oldest ~500ms.
- **Volume**: RTSP `SET_PARAMETER` `text/parameters` "volume: &lt;dB&gt;"
  (0 = max, ~-30 = min, -144 = muto) → guadagno lineare `10^(dB/20)` su
  `AudioFrameInputNode.OutgoingGain`; `GET_PARAMETER volume` rimanda
  l'ultimo valore così lo slider del telefono resta in sync.

`et=32` (un valore di "encryption type" mai spiegato da nessun riferimento)
è ignorato — né il video né UxPlay lo leggono, e la decifratura produce
frame validi (primo byte AAC-ELD `0x8c`/`0x8d`/`0x8e`).

### Background, tray, avvio con Windows, chiusura sincronizzata

`MainWindow` intercetta `Closed` e nasconde la finestra invece di
terminare; l'uscita vera è solo dal menu del tray (icona via
`H.NotifyIcon.WinUI` 2.3.2 — l'ultima stabile per `net9.0-windows`).
`StartupRegistration` scrive la chiave `Run` di `HKEY_CURRENT_USER` con
`--minimized` (nessun privilegio admin), riscritta a ogni avvio così si
autoripara. Chiudere il mirroring dal telefono chiude la finestra
(`SessionEnded`); la X della finestra fa il contrario — chiude subito i
socket dati/audio/RTSP e il telefono lascia cadere il mirror.

### Verificato

Suite `AirPlaySender.Core` a **73 test**, tutti verdi. Diagnostica su file
(`AppLog.cs`, `mirroring.log` accanto all'exe) — l'app in background non ha
console; un watchdog logga `in`/`queued`/`decoded`/`shown` a 1 Hz. Aperto,
di rifinitura: lip-sync misurato (l'audio parte con ~50ms di buffer, senza
allineamento esplicito ai timestamp video); il pair-setup HAP moderno;
riconnessioni/più stream nella stessa sessione.

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
    Audio/                 trasporto RTP (PCM raw L16), cattura WASAPI, encoder ALAC "uncompressed" (non più usato di default — vedi sotto)
    Net/                   helper condivisi: filtro interfacce di rete per mDNS, MAC address locale
    Receiving/             Fase 2 — Windows come RICEVITORE di mirroring (vedi sopra)
      AirPlayMirroringAdvertiser.cs   annuncio mDNS _airplay._tcp
      AirPlayReceiverServer.cs        server RTSP: OPTIONS/GET info/SETUP/RECORD/...
      PairingAccessorySession.cs      pairing come accessorio (schema legacy, non HAP TLV8)
      HapPairVerifyAccessorySession.cs  pair-verify HAP TLV8 vero, lato accessorio
      HapPairSetupAccessorySession.cs   pair-setup HAP TLV8, variante transient (ipotesi, non confermata dal vivo)
      FairPlaySetup.cs                handshake /fp-setup (replay di byte catturati da UxPlay)
      FairPlayCipher.cs               il cifrario FairPlay vero, portato da UxPlay/OmgHax
      FairPlayCipherTables.g.cs       le sue tabelle S-box, estratte meccanicamente (non a mano)
      MirroringDataReceiver.cs        canale dati video (TCP), framing pacchetti + decrypt, assembla access unit interi (AVCC->Annex-B, SPS/PPS su ogni IDR, timestamp da header offset 8), espone ConfigReceived/FrameReceived
      MirrorAudioReceiver.cs          canale audio (UDP RTP), decrypt AES-128-CBC per pacchetto + riordino/dedup per seq, espone AudioFrameReceived (frame AAC-ELD grezzi)
      AvcDecoderConfig.cs             AVCDecoderConfigurationRecord + split dei NAL AVCC
      H264Sps.cs                      parser SPS H.264 → larghezza/altezza vere
    AirPlaySession.cs      orchestratore Fase 1: connect → pair → handshake → stream

  AirPlaySender.App/      app WinUI 3 (finestra, lista dispositivi, dialog PIN, volume,
                           icona nel tray, MirrorWindow per il rendering del mirroring)
    MirrorWindow.xaml(.cs)   finestra di rendering: coda -> H264Mft -> blit BGRA su CanvasSwapChainPanel (Win2D), dimensioni native
    H264Mft.cs               decoder H.264 MFT di Windows pilotato a mano (ProcessInput/ProcessOutput, low-latency, NV12->BGRA)
    AacEldDecoder.cs         decoder AAC-ELD via P/Invoke a libAACdec.dll (NuGet fdk-aac) -> PCM int16
    MirrorAudioPlayer.cs     riproduzione via AudioGraph WinRT (AudioFrameInputNode + ring buffer)
    StartupRegistration.cs   voce nella chiave Run di HKCU per l'avvio con Windows
    AppLog.cs                logger su file (mirroring.log accanto all'exe) — l'unico
                              modo di vedere i log in un'app senza console/in background

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

Dipendenze NuGet non ovvie del progetto App, tutte per il ricevitore di
mirroring: `Microsoft.Graphics.Win2D` (blit dei frame decodificati),
`Vortice.MediaFoundation` (P/Invoke al decoder H.264 MFT di Windows, nessun
binario nativo aggiunto) e `fdk-aac` (decoder AAC-ELD prebuilt Fraunhofer,
`libAACdec.dll` per x64/arm64 — porta con sé una licenza Fraunhofer + una
nota brevettuale AAC, vedi il `NOTICE.txt` del pacchetto; stessa base di
UxPlay).

Al primo avvio Windows chiederà il permesso firewall per il traffico di rete
locale (discovery mDNS + streaming RTP/RTSP) — va consentito almeno per la
rete privata.

## Come si distribuisce

Per dare l'app a qualcuno senza consegnargli l'intera cartella di build,
`installer/` produce un singolo `Setup.exe` (Inno Setup) che installa
per-utente (nessun prompt UAC), crea la voce nel menu Start e un
disinstallatore vero:

```powershell
powershell -File installer\build-installer.ps1
```

Output: `installer\output\WinPlayMahogany-Setup-<versione>.exe`. Lo script fa
due cose, entrambe scriptate apposta per restare un comando solo:

1. `dotnet publish` in configurazione Release, self-contained (nessun .NET
   da installare sulla macchina di chi lo riceve).
2. Compila `installer/WinPlayMahogany.iss` con `ISCC.exe`.

**Nota per chi tocca lo script**: `dotnet publish` di un'app WinUI 3 non
pacchettizzata *non* copia l'output XAML compilato dell'app
(`AirPlaySender.App.pri` e ogni `*.xbf`) nella cartella di publish, anche se
`dotnet build` lo produce correttamente nella cartella accanto — un gap noto
dello strumento, non un errore di questo script. Senza quei file l'app parte
e va in crash nativo all'istante (`Microsoft.UI.Xaml.dll`,
`STATUS_STOWED_EXCEPTION`) perché il runtime XAML non trova la finestra
compilata; `build-installer.ps1` li ricopia a mano da
`bin\Release\...\win-x64\` subito dopo il publish — se mai si sposta o si
rinomina quella cartella, aggiusta lì.

Lo script toglie anche ~64 MB di DLL WPF/WinForms che il publish
self-contained include sempre per questo target, anche se l'app è WinUI 3
pura e non le tocca mai (verificato: l'app parte e funziona identica senza,
comprese Presentation*.dll, System.Windows.Forms*.dll), più `libAACenc.dll`
(la parte *encoder* di `fdk-aac`, mai usata — serve solo il decoder). Non
sono le lingue a pesare — le risorse per-cultura di tutte le lingue insieme
sono ~3.6 MB; in Release il csproj imposta anche `InvariantGlobalization`,
che leva l'ICU (~27 MB) dato che l'app non fa formattazione culture-aware
oltre a qualche `CultureInfo.InvariantCulture` esplicito.

Deliberatamente **non** è un pacchetto MSIX: passare
`WindowsPackageType=MSIX` nel csproj riaprirebbe gli stessi problemi di
XamlCompiler.exe/`AppxMSBuildToolsPath` descritti sopra, e richiederebbe un
certificato di firma perché chi lo riceve possa installarlo senza sbattersi
a fidarsi manualmente di un certificato self-signed. Un `Setup.exe` non
firmato fa comunque scattare SmartScreen al primo avvio ("Informazioni
aggiuntive → Esegui comunque") — inevitabile senza un certificato di
code-signing, indipendentemente dal formato scelto.

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

**Sender audio (Fase 1)** — Windows → speaker AirPlay:

1. **Discovery**: scansione mDNS di `_raop._tcp` e `_airplay._tcp`.
2. **Autenticazione**: pairing *transient* (PIN fisso, HomePod/macOS) o con
   **PIN a schermo** (Apple TV, solo la prima volta — le credenziali si
   salvano).
3. **Handshake AirPlay 2**: `GET /info` → `SETUP` sessione (dichiara la
   porta di timing NTP — va aperta *prima*) → canale eventi → `RECORD` →
   `SETUP` stream.
4. **Streaming**: audio di sistema (WASAPI loopback) → PCM raw
   44.1kHz/16-bit big-endian, cifrato ChaCha20-Poly1305, via RTP, sync a
   1 Hz + risposta alle ritrasmissioni.

**Ricevitore di mirroring (Fase 2)** — iPhone → Windows:

1. **Annuncio** mDNS `_airplay._tcp`: il PC compare in "Duplica schermo".
2. **RTSP** (porta 7000): pairing come accessorio → `/fp-setup` → `SETUP`
   sessione (chiave FairPlay) → `SETUP` stream `type:110` (video) e
   `type:96` (audio), più lo scambio di clock-sync.
3. **Video**: TCP separato, pacchetti 128B header + payload AES-CTR; si
   decifra, si riscrive AVCC→Annex-B sul posto, si dà al decoder H.264 MFT
   di Windows, si blitta il frame decodificato su Win2D.
4. **Audio**: UDP RTP, payload AES-CBC; si decifra, si riordina, si
   decodifica l'AAC-ELD con `libfdk-aac`, si riproduce via `AudioGraph`; il
   volume segue `SET_PARAMETER volume`.

## Limitazioni note

**Fase 1 (sender audio)**

- Provato contro un HomePod (gen 2, transient); Apple TV con PIN, speaker
  di terze parti o HomePod più vecchi potrebbero avere sorprese nello
  schema dei dizionari SETUP.
- Un dispositivo per volta (niente multi-room/group).
- Nessuna rilevazione automatica di connessione persa a metà streaming.
- AirPlay 1 legacy con password RTSP o auth MFi-SAP (vecchi AirPort Express)
  non supportati — l'app mostra un errore chiaro.

**Fase 2 (ricevitore di mirroring)**

- Provato contro un iPhone 13 Pro Max, iOS 26.6.1. Altri modelli/versioni
  potrebbero negoziare uno schema diverso (soprattutto il pairing HAP
  moderno, di cui c'è solo il pair-verify confermato dal vivo).
- Nessun lip-sync misurato: l'audio parte con ~50 ms di buffer, senza
  allineamento esplicito ai timestamp del video.
- Una sola sessione di mirroring per volta; riconnessioni non provate.
- La build di sviluppo (`-c Debug`) tiene ICU e non ha
  `InvariantGlobalization` — quello vale solo per l'installer (Release).

## Roadmap

- **Fase 1.1**: provare contro più dispositivi reali (Apple TV con PIN,
  altri speaker AirPlay 2), rilevazione disconnessione, multi-room.
- **Fase 2 (ricevitore di mirroring)**: **funziona** 🎉 — video (decoder
  H.264 MFT di Windows pilotato a mano + Win2D, 60fps fluidi) **e audio**
  (AAC-ELD via `libfdk-aac` + `AudioGraph`, con controllo volume dallo
  slider dell'iPhone), sincronizzati, verificati dal vivo contro un iPhone
  reale. Più icona nel tray, chiusura sincronizzata in entrambe le
  direzioni, avvio automatico con Windows. Rifiniture aperte:
  1. **Sync A/V fine**: l'audio parte con ~50ms di buffer, senza
     allineamento esplicito ai timestamp del video — nella pratica va bene,
     ma non c'è un lip-sync misurato.
  2. Il pair-setup HAP "vero" (transient collegato ma probabilmente non la
     forma corretta — vedi sopra) resta un'incognita a bassa priorità: il
     percorso legacy già collegato funziona fino in fondo.
  3. Riconnessioni / più stream nella stessa sessione — non ancora provate.
- **Fase 2b (sender di mirroring, Windows → TV)**: non affrontata, R&D
  ancora più aperta di quanto sopra — nessun progetto open source esiste per
  questo verso. Vedi la discussione nella cronologia del progetto per la
  valutazione completa.

## Licenza e attribuzioni

Vedi [NOTICE.md](NOTICE.md).
