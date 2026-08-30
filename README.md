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

Tutta la parte protocollare (`src/AirPlaySender.Core`) ha **43 test
automatici** (gli ultimi coprono il formato dei pacchetti di
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
     chieda — nessun cambiamento osservato nella prima prova (fatta prima
     che esistesse un vero scambio di timing); poi **confermato necessario**
     da una cattura di rete reale (punto 7 sotto) e reintrodotto — ma anche
     con le condizioni corrette alle spalle, non basta da solo a sbloccare
     RECORD→TEARDOWN.
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
  7. **Cattura di rete reale** (`pktmon`, con privilegi di amministratore —
     la shell di Claude Code non li ha, serve farlo dall'utente) della
     sessione che fallisce contro il nostro stesso PC. Risultato netto:
     **nessun canale nascosto** — il telefono parla solo con la porta 7000
     (RTSP) e scambia i pacchetti di timing UDP già noti; niente tentativi
     di connessione ad altre porte, nessun ICMP, chiusura TCP pulita
     (FIN/FIN-ACK reciproco, non un RST). Decodificando i byte grezzi dei
     pacchetti (in chiaro salvo le firme di pairing) si è scoperto un fatto
     concreto prima non visibile dai soli log applicativi: **il client manda
     una sola `SETUP`** — quella con `ekey`/`eiv` — e mai una seconda con un
     array `streams`. Il nostro codice, prima di questo, rispondeva solo con
     `timingPort`/`eventPort`: non avevamo mai offerto una porta dati.
     Reintrodotta l'offerta proattiva (punto 2 sopra) con le condizioni
     ora corrette (timing vero, eventPort=0 di riferimento) — la porta dati
     offerta non viene comunque mai contattata, ma è comunque il
     comportamento corretto da tenere. Anche il corpo del `TEARDOWN` è stato
     decodificato: un dizionario vuoto, nessun codice di errore o motivo.
  8. **Tentata la cattura di una sessione riuscita** (iPhone → una vera Hisense
     TV `50A5FE-ELL10404`, AirPlay nativo, mirroring che *funziona* per
     l'utente): questo PC è collegato via Ethernet, quindi trasformato in
     hotspot Wi-Fi (Impostazioni → Hotspot mobile, condividendo l'Ethernet
     sulla scheda Wi-Fi) così sia l'iPhone sia la TV ci passano attraverso e
     tutto il loro traffico è visibile con `pktmon` senza nessun trucco da
     MITM — il PC è letteralmente il router. Fatta la cattura sulla
     sottorete dell'hotspot (`192.168.137.0/24`) durante un mirroring
     riuscito verso la TV. Risultato: **si vede solo la scoperta mDNS**
     (l'iPhone trova `TV._airplay._tcp.local.` su `192.168.137.238`) — **zero
     traffico applicativo** tra i due dispositivi dopo quello, nessuna
     connessione TCP, nessun RTSP. Spiegazione più probabile: iPhone e TV,
     una volta trovati via mDNS sulla rete "ufficiale", sono passati ad
     **AWDL** (Apple Wireless Direct Link) — il collegamento Wi-Fi
     peer-to-peer diretto tra chip Apple che AirPlay preferisce spesso anche
     stando sulla stessa rete — che bypassa completamente il punto di
     accesso a livello radio. Anche essendo noi il router, quel traffico non
     ci passa mai attraverso: non è un errore di impostazione, è un limite
     tecnico reale del protocollo. L'unico modo noto per catturare AWDL è
     l'interfaccia `awdl0` esposta da macOS (usata da chi ha reverse-engineered
     AirPlay/AirDrop in passato) — non disponibile con solo hardware Windows.

  Con `osVersion: 26.6.1`, `sourceVersion: 960.13.1`, tutto indica che questo
  iOS usa per il mirroring uno schema che nessun progetto open source
  disponibile pubblicamente documenta o implementa. **Aggiornamento — vedi il
  punto 9 sotto: questo era vero fino a stanotte. Con un Mac e `rvictl` la
  sessione riuscita si è vista per davvero, e cambia l'impostazione di tutta
  la Fase 2.**
  9. **La svolta**: `awdl0` di un Mac non coinvolto vede solo il proprio
     traffico (punto 8), ma su iOS esiste lo strumento fatto apposta per
     questo, usato dagli sviluppatori Apple stessi — **`rvictl`** (Xcode),
     che crea un'interfaccia virtuale (`rvi0`) che rispecchia *alla fonte*
     tutto il traffico dell'iPhone, prima che scelga Wi-Fi normale o AWDL.
     Collegato l'iPhone al Mac via USB, catturato con
     `sudo tcpdump -i rvi0` durante un mirroring vero verso la TV, e
     **stavolta la sessione RTSP reale si vede per intero**, in chiaro
     (salvo le firme di pairing) — sulla Wi-Fi normale (`en0`), non su
     AWDL. Un'ora scarsa di lavoro per far funzionare `rvictl` (non era nel
     `PATH` — trovato a mano in `/Library/Apple/usr/bin/rvictl`), poi un
     parser scritto da zero in Python per il formato `pcapng`/`PKTAP` che
     `tcpdump` su macOS produce di default (niente `tshark`/Wireshark
     disponibili). Quello che si è visto, punto per punto:
     - **`GET /info` vero è enorme** (2538 byte, contro le poche decine di
       byte del nostro): contiene `displays` (risoluzione, HDR, `maxFPS`),
       `PTPInfo` (Precision Time Protocol — non NTP), `playbackCapabilities`,
       `deviceID`, e feature flag molto più ricchi
       (`0x7F8AD0,0x38BCF46` contro il nostro `0x5A7FFEE6,0x0`).
     - **Il pairing è HAP TLV8 vero**, non lo schema legacy a offset di byte
       di UxPlay — **confermato byte per byte**, non un'ipotesi: il primo
       messaggio di `/pair-verify` decodifica esattamente come
       `Method(0x00)=7`, `State(0x06)=1`, `PublicKey(0x03)=<32 byte>`, 40
       byte totali = `Content-Length: 40` dell'header, nessun avanzo. È lo
       **stesso identico fondamento crittografico già costruito e
       collaudato in Fase 1** per l'audio (X25519 + Ed25519 + TLV8), solo
       mai applicato al ruolo di accessorio per il mirroring. Header
       `X-Apple-HKP: 6` — un valore mai visto nel codice esistente (Fase 1
       usa 3 per il PIN e 4 per il transient): quasi certamente un terzo
       contesto di pairing dedicato al mirroring.
     - **Dopo il pair-verify, tutta la connessione RTSP diventa cifrata**
       (SETUP, RECORD, GET_PARAMETER compresi — pacchetti binari illeggibili
       da lì in poi). UxPlay lascia tutto in chiaro dopo il pairing: questo
       è probabilmente il pezzo strutturale più importante che mancava.
     - Il video vero passa su **una connessione TCP separata** (porta 6030
       in questa sessione, 6550 pacchetti) — la struttura che avevamo già
       assunto giusta, confermata.
     - Durante lo streaming, **piccoli messaggi cifrati periodici** sul
       canale di controllo (keepalive/polling ogni pochi secondi); la
       sessione si chiude con un **FIN pulito iniziato dal telefono** quando
       si ferma il mirroring — non un TEARDOWN plist come nel nostro flusso.

     **Cosa è stato costruito stanotte stessa, partendo da questo:**
     `HapPairVerifyAccessorySession` (`src/AirPlaySender.Core/Receiving/`) —
     il pair-verify HAP TLV8 lato accessorio, immagine speculare esatta di
     `Pairing.PairVerifyClient` di Fase 1 (stessi tag TLV8, stesse stringhe
     HKDF, stessi nonce ChaCha20-Poly1305 "PV-Msg02"/"PV-Msg03", solo i passi
     scambiati). Verificato **non a occhio ma con un test vero**
     (`HapPairVerifyAccessorySessionTests`): fa girare il codice nuovo contro
     il vero `PairVerifyClient` di Fase 1, non modificato, su un socket TCP
     di loopback reale — stesso trucco già usato da `FakeAirPlay2Receiver`,
     stavolta nella direzione opposta. Entrambi i lati arrivano
     indipendentemente alla stessa chiave condivisa e alle stesse chiavi di
     sessione. 43 test totali, tutti verdi.

     **Cosa resta apertamente non risolto, per onestà**: il *pair-setup* di
     questo schema — la sessione catturata lo saltava (telefono già
     associato alla TV da prima), quindi non c'è nessuna prova reale di che
     forma abbia (transient come l'audio, PIN come Apple TV, o qualcos'altro
     sotto lo stesso HKP=6 mai visto) — costruirlo a intuito avrebbe ripetuto
     esattamente l'errore che questa indagine ha cercato di evitare tutta la
     notte. `HapPairVerifyAccessorySession` accetta la chiave pubblica
     lunga del client da fuori apposta per questo: è responsabilità di chi
     lo richiama, una volta che il pair-setup esiste, fornire quello che ha
     imparato lì.

     **Aggiornamento, stessa notte**: costruito anche
     `HapPairSetupAccessorySession` — pair-setup lato accessorio, variante
     *transient* (senza PIN, password fissa "3939", niente scambio di
     identità — la stessa forma che l'audio di Fase 1 usa già per un
     HomePod). Riusa la matematica SRP-6a server già collaudata (lo stesso
     codice, prima solo dentro `FakeAirPlay2Receiver` per i test, ora anche
     in produzione), verificata di nuovo contro il vero `PairSetupClient` di
     Fase 1. **Correzione di rotta onesta**: aver visto il pair-verify
     succedere davvero nella cattura implica quasi certamente che il
     pair-setup del mirroring NON sia transient — il transient, per
     definizione, salta il pair-verify e deriva le chiavi direttamente
     dalla SRP session key. La variante PIN/identità (quella che Fase 1 usa
     per un Apple TV) è il sospetto più plausibile ora, non ancora
     costruita (serve anche mostrare un PIN sul nostro harness, che oggi
     non esiste). Il transient resta comunque collegato al server — a
     basso rischio, riusa codice già provato — apposta per scoprire dal
     vivo, senza altro danno che un rifiuto pulito e loggato, se la vera
     richiesta del telefono porta davvero il flag transient o no.
     `GET /info` arricchito con `displays` (risoluzione vera dello schermo
     di questo PC), `deviceID`, `model`, `name`, `pi` — non toccato invece
     `features`: cambiare quei bit a intuito rischiava di rompere il
     percorso legacy già funzionante, per un cambiamento non verificabile
     stanotte. `/pair-setup` ora smista su `X-Apple-HKP: 6` (lo stesso
     valore visto sul pair-verify reale) verso il nuovo percorso HAP,
     loggando ogni campo TLV8 anche in caso di rifiuto — così se il
     telefono lo tenta stasera, si vede la forma vera anche se viene
     respinto. Ancora da fare: avvolgere l'intero ciclo RTSP in cifratura
     HAP dopo un pair-verify riuscito (`HapFrameCodec`, già scritto e
     collaudato per l'esperimento sul canale eventi) — non collegato finché
     non c'è un pair-setup capace di arrivarci davvero.
- 🐛 **Bug trovato (non ancora osservabile su hardware)**: una code review
  ha scovato che `MirroringDataReceiver` decifrava ogni pacchetto video
  ripartendo dal blocco 0 del keystream AES-CTR invece di continuarlo —
  avrebbe rotto la decodifica dal secondo pacchetto in poi. Mai emerso nei
  test perché il canale dati mirroring non ha ancora mai ricevuto un
  pacchetto vero (il blocco sopra impedisce di arrivarci). Corretto con
  `AesCtrKeystreamCipher`, un cifrario con stato che replica esattamente
  `mirror_buffer_decrypt` di UxPlay (portare avanti il keystream oltre i
  confini a 16 byte dei pacchetti) — verificato con test di round-trip e di
  "spezza in punti arbitrari e confronta col colpo unico", non ancora contro
  dati reali.
- 🐛 **Altri 7 fix da una code-review completa** su tutto il diff di Fase 2
  (`0be9b59..HEAD`): `EcdhSecret`/`EventChannelKeys` ora tornano `null`
  finché `pair-verify` non è davvero completato (prima la chiave di sessione
  poteva essere derivata da un client che aveva superato solo il passo 1,
  senza mai provare di possedere la chiave privata del passo 2); lunghezza
  di `ekey` (72 byte) e del byte "mode" del messaggio chiave di `/fp-setup`
  ora validati prima di finire nel cifrario FairPlay, invece di far esplodere
  un'eccezione non gestita su input malformato; una seconda `SETUP` sulla
  stessa connessione ora chiude la sessione di timing/il ricevitore dati
  precedenti invece di perderne il riferimento; `TEARDOWN`/`PAUSE` (che
  `OPTIONS` dichiara ma il dispatch non gestiva, finendo in 501) ora
  rispondono 200; `GET /info` con più qualificatori nello stesso array ora li
  onora tutti invece di leggere solo il primo. Un'ultima segnalazione (una
  maschera `& 0xff` mancante in `Rol8x` dentro il cifrario FairPlay)
  controllata contro il vero `sap_hash.c` di UxPlay ed **esclusa**: l'originale
  ha davvero quell'asimmetria, non è un errore di trascrizione — lasciata
  intatta di proposito. 41 test totali, tutti verdi.

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
- **Fase 2 (ricevitore di mirroring)**: la cattura con `rvictl` (vedi "Stato
  attuale" punto 9) ha cambiato l'impostazione — non più un mistero di
  protocollo, un pezzo concreto da costruire. In ordine:
  1. **Pair-setup HAP per il mirroring** — l'unico pezzo del nuovo schema
     senza nessuna prova reale ancora (la sessione catturata lo saltava).
     Serve un'altra cattura `rvictl` di un **primo** accoppiamento (non un
     iPhone già associato a quella TV — o un `pi`/identità diversa lato
     nostro per forzare un pairing nuovo) per vedere se è transient, PIN, o
     altro sotto `X-Apple-HKP: 6`.
  2. Collegare `HapPairVerifyAccessorySession` (già scritto e testato) ad
     `AirPlayReceiverServer`, avvolgendo l'intero ciclo RTSP successivo in
     cifratura HAP (`HapFrameCodec`, già scritto per l'esperimento sul
     canale eventi — va solo riusato sul ciclo principale, non su un canale
     separato).
  3. Arricchire `GET /info` con `displays`/`PTPInfo`/`playbackCapabilities`
     e feature flag più moderni, sul modello di quanto visto dalla TV vera.
  4. Ritestare dal vivo con la TV/harness nostro una volta che 1-3 esistono.
  5. Il decoder/render H.264 vero (Media Foundation) è ancora da scrivere
     del tutto — utile solo dopo aver risolto i punti sopra.
- **Fase 2b (sender di mirroring, Windows → TV)**: non affrontata, R&D
  ancora più aperta di quanto sopra — nessun progetto open source esiste per
  questo verso. Vedi la discussione nella cronologia del progetto per la
  valutazione completa.

## Licenza e attribuzioni

Vedi [NOTICE.md](NOTICE.md).
