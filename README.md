# WinPlay Mahogany

AirPlay nativo per Windows, nei due versi:

- **Sender audio** (Fase 1, funzionante): cattura l'audio di sistema e lo
  trasmette a qualunque speaker/TV AirPlay 2 (HomePod, Apple TV, altoparlanti
  AirPlay 2 di terze parti), con pairing e crittografia reali.
- **Ricevitore di mirroring** (Fase 2, in corso — vedi sotto): fa comparire
  questo PC come bersaglio "Duplica schermo" nel Centro di Controllo di un
  iPhone. Pairing e cifratura funzionano fino in fondo; il video vero e
  proprio no, non ancora, per un motivo specifico e documentato più sotto.

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

Tutta la parte protocollare (`src/AirPlaySender.Core`) ha **31 test
automatici**, incluso un test end-to-end che fa girare `AirPlaySession`
contro un *finto ricevitore AirPlay 2* scritto da zero apposta per i test
(`FakeAirPlay2Receiver`): un server TCP/UDP indipendente che implementa il
lato server di SRP-6a e la sequenza RTSP/bplist, senza riusare il codice
del client — utile per non regredire, ma è stato il test contro l'HomePod
vero a scoprire i bug elencati sopra (nessun test locale può anticipare il
comportamento di un ricevitore reale).

**Fase 2 — ricevitore di mirroring: pairing e cifratura funzionano fino in
fondo su hardware reale; il video no, non ancora.** 🧩

A differenza della Fase 1 (dove esistevano due riferimenti open source
validati), qui il riferimento principale è [UxPlay](https://github.com/FDH2/UxPlay)
(GPLv3) — un ricevitore di mirroring reale e funzionante, ma scritto per
hardware/iOS più vecchi di quello disponibile qui. Ogni pezzo qui sotto è
stato verificato *davvero* contro un iPhone reale (iOS 26.6.1, iPhone 13 Pro
Max), non solo compilato:

- ✅ **Annuncio mDNS** (`_airplay._tcp`) — l'iPhone vede questo PC nel Centro
  di Controllo.
- ✅ **Server RTSP** che accetta la connessione reale dell'iPhone e dialoga
  `OPTIONS`/`GET /info`/`SETUP`/`RECORD`/`GET_PARAMETER`/`TEARDOWN`.
- ✅ **Pairing come accessorio** (`PairingAccessorySession`) — *non* lo
  schema HAP TLV8/SRP della Fase 1: uno schema legacy più semplice, byte
  grezzi invece di TLV8, AES-128-CTR invece di ChaCha20-Poly1305 per
  cifrare solo le firme Ed25519/X25519 dell'handshake. L'iPhone lo accetta
  e lo ricorda tra un tentativo e l'altro (salta dritto a pair-verify dal
  secondo tentativo in poi — esattamente il comportamento di un dispositivo
  già associato).
- ✅ **Handshake FairPlay** (`/fp-setup`, entrambi i round) — byte
  precatturati da UxPlay, riprodotti identici (non calcolati: nessuno,
  UxPlay incluso, ha mai capito l'algoritmo reale di questo passaggio, solo
  osservato che Apple accetta queste risposte fisse).
- ✅ **Il cifrario FairPlay vero** (`FairPlayCipher.cs` +
  `FairPlayCipherTables.g.cs`) — quello disassemblato ("OmgHax" nel codice
  di UxPlay), ~1200 righe e ~480KB di S-box opache. Porta a decifrare una
  chiave di sessione reale, inviata da un iPhone vero, senza errori.
  Portato con due discipline per non introdurre errori di trascrizione
  invece che a mano: le tabelle sono state estratte *meccanicamente* da
  script Python e verificate byte-per-byte con hash SHA-256 incrociati
  (C originale ↔ Python ↔ C#); la logica è stata copiata
  carattere-per-carattere dal C, con solo i cast che C# richiede e che C
  faceva implicitamente.
- ⏳ **Il blocco attuale**: dopo `SETUP`/`RECORD`, l'iPhone fa `TEARDOWN`
  senza mai aprire la connessione dati video vera. Il campo `et` (tipo di
  cifratura) nella richiesta `SETUP` vale **32**, un valore che non compare
  in nessun riferimento disponibile — ma leggendo il vero codice sorgente di
  UxPlay (non solo la sua doc) si scopre che `et` non viene **mai** letto da
  `raop_handler_setup`: non è lui a decidere niente, quindi rincorrerlo come
  causa diretta era una pista sbagliata.
  Ipotesi verificate una per una contro hardware reale, tutte esplicitamente
  escluse (nessuna ha cambiato il comportamento RECORD→TEARDOWN):
  1. `timingPort` reale invece di 0 — nessun cambiamento.
  2. Offrire proattivamente la porta dati mirroring senza che il client la
     chieda — nessun cambiamento; rimosso (UxPlay reale non lo fa mai).
  3. Un canale eventi reale e cifrato (stessa convenzione HKDF "Events-Salt"
     della Fase 1) — il client si connetteva, ma comunque TEARDOWN; rimosso,
     `eventPort` è tornato al valore letterale `0` che UxPlay stesso invia
     ("the event port is not used in mirror mode or audio mode").
  4. **La modalità "AirPlay2 Remote Control"** che UxPlay riconosce
     esplicitamente ma dichiara di non supportare (`isRemoteControlOnly`) —
     controllata sulla richiesta reale del dispositivo: **assente**. La
     forma della richiesta è del tutto standard (`timingProtocol: NTP`,
     `timingPort` valorizzata, nessun `isRemoteControlOnly`) — esclusa.
  5. **Lo scambio di clock-sync vero** (`NtpTimingSession.cs`, protocollo
     letto riga per riga da `raop_ntp.c`/`byteutils.c` di UxPlay — scoperta:
     è il *ricevitore* a dover interrogare periodicamente la `timingPort`
     del client, non il contrario, un dettaglio che prima mancava del tutto
     — quella porta veniva aperta e lasciata muta). Verificato che lo
     scambio funziona per davvero (risposta bidirezionale ricevuta
     dall'iPhone), ma anche questo non cambia l'esito: TEARDOWN comunque.
  6. Controllati altri due progetti Windows indipendenti che tentano la
     stessa cosa: `moieric11/AirPlay-Windows` (la sua stessa doc ammette che
     la decrittazione dello stream mirroring "remains unimplemented" — è
     fermo allo stesso punto nostro) e `xenos1337/AirPlayServer` (non è
     codice nuovo: è lo stesso codice C di UxPlay/RPiPlay ricompilato per
     Windows, nessuna soluzione aggiuntiva).

  Con `osVersion: 26.6.1`, `sourceVersion: 960.13.1`, tutto indica che questo
  iOS usa per il mirroring uno schema che nessun progetto open source
  disponibile pubblicamente documenta o implementa — non è un dettaglio che
  manca a noi soli. Le piste verificabili leggendo codice sono esaurite; il
  prossimo passo utile è una cattura di traffico di una sessione riuscita
  (stesso iPhone verso un vero Apple TV) per confronto diretto.

Tutto il codice di questa fase vive in `src/AirPlaySender.Core/Receiving/` —
architettura completa e riutilizzabile, non un tentativo buttato via.

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
      FairPlaySetup.cs                handshake /fp-setup (replay di byte catturati da UxPlay)
      FairPlayCipher.cs               il cifrario FairPlay vero, portato da UxPlay/OmgHax
      FairPlayCipherTables.g.cs       le sue tabelle S-box, estratte meccanicamente (non a mano)
      MirroringDataReceiver.cs        canale dati video (TCP), framing pacchetti + decrypt
    AirPlaySession.cs      orchestratore Fase 1: connect → pair → handshake → stream

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

## Come si distribuisce

Per dare l'app a qualcuno senza consegnargli l'intera cartella di build,
`installer/` produce un singolo `Setup.exe` (Inno Setup) che installa
per-utente (nessun prompt UAC), crea la voce nel menu Start e un
disinstallatore vero:

```powershell
powershell -File installer\build-installer.ps1
```

Output: `installer\output\AirPlayWindows-Setup-<versione>.exe`. Lo script fa
due cose, entrambe scriptate apposta per restare un comando solo:

1. `dotnet publish` in configurazione Release, self-contained (nessun .NET
   da installare sulla macchina di chi lo riceve).
2. Compila `installer/AirPlayWindows.iss` con `ISCC.exe`.

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
comprese Presentation*.dll, System.Windows.Forms*.dll). Non sono le lingue
a pesare — le risorse per-cultura di tutte le lingue insieme sono ~3.6 MB,
ininfluenti; l'app comunque non ha un sistema di localizzazione, il testo
è tutto italiano fisso.

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

1. **Discovery**: scansione mDNS di `_raop._tcp` (l'endpoint audio) e
   `_airplay._tcp` (flag di funzionalità/modello).
2. **Autenticazione**: in base ai flag annunciati dal dispositivo, si sceglie
   fra pairing *transient* (PIN fisso, HomePod/macOS — nessuna interazione
   utente) o pairing con **PIN a schermo** (Apple TV, la prima volta soltanto:
   le credenziali vengono salvate per le connessioni successive).
3. **Handshake AirPlay 2**: `GET /info` → `SETUP` sessione (dichiara la
   porta di timing NTP — va aperta *prima* di mandare questa richiesta,
   non dopo) → apertura canale eventi → `RECORD` → `SETUP` stream (negozia
   PCM raw + la chiave audio).
4. **Streaming**: l'audio di sistema (WASAPI loopback) viene ricampionato a
   44.1kHz/16-bit, impacchettato come PCM raw big-endian, cifrato
   ChaCha20-Poly1305 e spedito via RTP, con sync a 1Hz e risposta alle
   richieste di ritrasmissione del ricevitore.

## Limitazioni note (Fase 1)

- Provato contro un HomePod (gen 2, pairing transient); un Apple TV con PIN
  a schermo, un altoparlante AirPlay 2 di terze parti, o un HomePod meno
  recente potrebbero avere le loro sorprese — lo schema dei dizionari SETUP
  che questo progetto usa è quello confermato funzionante su *quel*
  dispositivo specifico, non garantito identico su tutti.
- Un solo dispositivo collegato per volta (nessun multi-room/AirPlay group).
- Nessuna rilevazione automatica di connessione persa a metà streaming (va
  disconnesso e riconnesso manualmente).
- Dispositivi AirPlay 1 "legacy" con password RTSP o auth MFi-SAP (vecchi
  AirPort Express) non sono ancora supportati — l'app mostra un errore
  chiaro invece di tentare una connessione destinata a fallire.

## Roadmap

- **Fase 1.1**: provare contro più dispositivi reali (Apple TV con PIN,
  altri speaker AirPlay 2), system tray icon, rilevazione disconnessione,
  multi-room.
- **Fase 2 (ricevitore di mirroring)**: bloccata dopo `RECORD`/`TEARDOWN` —
  vedi "Stato attuale" sopra per l'elenco completo delle 6 ipotesi già
  verificate ed escluse contro hardware reale. Prossimi passi realistici, in
  ordine di quanto sarebbero risolutivi:
  1. Una cattura di rete di una sessione di mirroring **riuscita** dello
     stesso iPhone verso un ricevitore vero (Apple TV, o un Mac/AppleTV con
     Wireshark) per confronto diretto — le piste verificabili leggendo solo
     codice sono esaurite, serve un dato di verità a terra.
  2. Se si trova un altro riferimento open source più recente di UxPlay (i
     due controllati finora, `moieric11/AirPlay-Windows` e
     `xenos1337/AirPlayServer`, non aggiungono nulla — vedi sopra), riprendere
     da lì.
  3. Il decoder/render H.264 vero (Media Foundation) è ancora da scrivere
     del tutto — utile solo dopo aver risolto il punto sopra, dato che senza
     una chiave video corretta non c'è niente di valido da decodificare.
- **Fase 2b (sender di mirroring, Windows → TV)**: non affrontata, R&D
  ancora più aperta di quanto sopra — nessun progetto open source esiste per
  questo verso. Vedi la discussione nella cronologia del progetto per la
  valutazione completa.

## Licenza e attribuzioni

Vedi [NOTICE.md](NOTICE.md).
