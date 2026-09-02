# WinPlay Mahogany

AirPlay nativo per Windows, nei due versi:

- **Sender audio** (Fase 1, funzionante): cattura l'audio di sistema e lo
  trasmette a qualunque speaker/TV AirPlay 2 (HomePod, Apple TV, altoparlanti
  AirPlay 2 di terze parti), con pairing e crittografia reali.
- **Ricevitore di mirroring** (Fase 2, in corso — vedi sotto): fa comparire
  questo PC come bersaglio "Duplica schermo" nel Centro di Controllo di un
  iPhone, restando raggiungibile anche in background (icona nel tray, avvio
  automatico con Windows, chiusura sincronizzata in entrambe le direzioni
  con il telefono). Pairing, cifratura e decrittazione funzionano fino in
  fondo, verificati dal vivo ripetutamente. **Il video del mirroring
  funziona** 🎉 — immagine nitida, colori giusti, 60fps fluidi per oltre un
  minuto senza freeze né crash, verificato dal vivo contro un iPhone 13 Pro
  Max (iOS 26.6.1). Sia `MediaPlayerElement` sia il frame-server mode di
  `MediaPlayer` si bloccavano dopo ~1s (bug interno a quello stack di
  Windows, non della cattura/decifratura); lo stadio finale è stato rifatto
  pilotando **il decoder H.264 MFT di Windows direttamente** (`H264Mft.cs`,
  via `Vortice.MediaFoundation`) + blit su Win2D — vedi "Pipeline di
  rendering riscritta". L'**audio dal telefono** (stream `type: 96`,
  AAC-ELD) è stato costruito — ricezione RTP + decifratura AES-CBC, decoder
  `libfdk-aac`, riproduzione via `AudioGraph` — ma non ancora verificato dal
  vivo.

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

**Fase 2 — ricevitore di mirroring: pairing, cifratura, e ora il video vero
funzionano fino in fondo su hardware reale.** 🎉

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

## 🎉 Il video vero funziona (stessa notte)

Fatto un altro test dal vivo con `GET /info` arricchito. Novità enorme,
mai vista prima in tutto il progetto: **il telefono ha mandato una seconda
`SETUP` reale con un array `streams` valido** (`streamConnectionID`
diverso da zero) — non è mai successo, nemmeno con l'offerta proattiva.
Si è **connesso davvero alla porta dati video**, e la sessione **non è
andata in TEARDOWN**.

Il canale dati si chiudeva comunque subito: il primo pacchetto veniva
letto con un `payloadSize` assurdo (603979776) e il nostro codice si
fermava. Controllato di nuovo il vero `raop_rtp_mirror.c` di UxPlay:
`payload_size = byteutils_get_int(packet, 0)`, e `byteutils_get_int` è
**little-endian** (confermato ore prima leggendo `byteutils.c`, mai
applicato qui) — il nostro codice lo leggeva big-endian. Un solo byte-order
sbagliato, mai esercitato prima contro un pacchetto vero. Sistemato,
ridistribuito, riprovato nello stesso test.

**Risultato: quasi 2000 pacchetti video ricevuti consecutivamente, tutti
decifrati correttamente.** La verifica non è "sembra giusto" — è
matematica esatta: ogni pacchetto video decifrato inizia con 4 byte che,
letti come lunghezza big-endian, corrispondono **esattamente** alla
lunghezza del payload meno 4 (es. pacchetto da 644 byte → prefisso
`00 00 02 80` = 640 = 644-4), seguiti da un byte di header NAL H.264
valido. Non è Annex-B (`00 00 00 01`) come UxPlay/la maggior parte dei
riferimenti — è **AVCC**, lunghezza a 4 byte invece di start code. Un
formato di framing diverso, non un problema di crittografia: la catena
pairing → FairPlay → chiave video AES-CTR è verificata corretta su un
flusso reale, sostenuto, non un singolo pacchetto fortunato.

Resta l'ultimo pezzo, esplicitamente rimandato dall'inizio della Fase 2:
un vero decoder/render H.264 (Media Foundation) — oggi il traguardo è
"riceviamo e decifriamo video vero", non ancora "lo vediamo a schermo".
Con l'AVCC confermato, però, sappiamo esattamente che formato aspettarci.
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

## Il rendering, il funzionamento in background e l'avvio con Windows

Continuazione della stessa notte, partendo dai quasi 2000 pacchetti video
veri decifrati qui sopra: costruita la pipeline che li trasforma davvero in
un'immagine a schermo, e il comportamento "app in background" richiesto
esplicitamente — restare raggiungibile mentre è ridotta a icona, avviarsi
da sola con Windows, aprire una finestra a dimensioni native quando arriva
un mirroring vero.

- **`H264Sps.TryParseDimensions`** — parser vero della SPS H.264 (Exp-Golomb,
  rimozione degli emulation-prevention byte, il ramo esteso di High Profile
  con `chroma_format_idc`/bit depth/matrice di scaling, formula
  larghezza/altezza standard coi valori di cropping). Rifiuta esplicitamente
  (ritorna `false`, non un numero a caso) quando `seq_scaling_matrix_present_flag`
  è impostato — fuori scopo dichiarato, non un tentativo di indovinare.
  Verificato bit per bit contro una SPS 1280×720 baseline scritta a mano,
  non contro dati inventati.
- **`AvcDecoderConfig`** — il pacchetto "SPS+PPS" non cifrato non è una
  semplice coppia di NAL: è un vero
  `AVCDecoderConfigurationRecord` (ISO/IEC 14496-15 §5.2.4.1) — versione,
  profilo/livello, byte di lunghezza, SPS/PPS con prefisso di lunghezza a 2
  byte. `SplitAvccNalUnits` spezza poi ogni payload video decifrato nei suoi
  NAL (prefisso di lunghezza a 4 byte big-endian, confermato contro i
  pacchetti reali di stanotte).
- **`MirroringDataReceiver`** riscritto da "logga quello che vede" a
  espositore di eventi (`ConfigReceived`, `NalReceived`) — un
  renderer si aggancia e riceve SPS/PPS e ogni NAL già decifrato e
  spacchettato, senza dover conoscere i dettagli del framing AVCC.
- **`MirrorWindow`** — la finestra che mostra davvero lo schermo del
  telefono: si aggancia agli eventi di cui sopra, ridimensiona la finestra
  alle dimensioni vere lette dalla SPS (fallback 1920×1080 se il parser
  rifiuta), e alimenta una `MediaStreamSource` di WinRT
  (`VideoEncodingProperties.CreateH264()`) dentro un `MediaPlayerElement` —
  decodifica H.264 e resa a schermo affidate al sistema operativo
  (accelerata via hardware), non reinventate a mano. Un dettaglio non ovvio,
  confermato contro un'integrazione reale (`webrtc-uwp`) prima di scriverlo:
  `CreateH264()` vuole **Annex-B** (`00 00 00 01`), non l'AVCC che il resto
  della pipeline usa — ogni campione viene ri-incapsulato al volo.
- **Icona nel tray, chiusura che nasconde invece di terminare, avvio con
  Windows** — la richiesta esplicita era che il programma resti
  raggiungibile ("PC-NICO" scopribile nel mirroring) anche a finestra
  chiusa, chiudibile per davvero solo dal tray. `MainWindow` intercetta
  `Closed` e chiama `AppWindow.Hide()` invece di lasciar terminare il
  processo, a meno che non sia stato il menu del tray stesso ("Esci") a
  chiedere l'uscita vera (`Application.Current.Exit()`). Icona via
  **H.NotifyIcon.WinUI 2.3.2** (l'ultima versione stabile che supporta
  ancora `net9.0-windows` — la 2.4.1, più recente, richiede `net10.0-windows`,
  incompatibile col resto del progetto). Un dettaglio non ovvio della
  libreria, verificato leggendone il sorgente prima di scriverci codice
  contro: la modalità `ContextMenuMode` di default (`PopupMenu`) costruisce
  un vero menu nativo win32 a partire dal **`Command`** di ogni
  `MenuFlyoutItem` — non solleva mai il suo evento `Click`, quindi il menu
  "Apri"/"Esci" è cablato su due `ICommand` minimi scritti a mano, non su
  gestori di evento. `StartupRegistration` scrive la voce
  nella chiave `Run` di `HKEY_CURRENT_USER` (nessun privilegio admin,
  nessun pacchetto MSIX/attività di Task Scheduler — coerente con
  l'app non pacchettizzata) con l'argomento `--minimized`, riscritta a ogni
  avvio così si autoripara se l'eseguibile cambia percorso.

**Verificato quella notte**: l'intero progetto App (incluso `MirrorWindow`)
compila senza errori né warning, la suite di test di `AirPlaySender.Core`
passa tutta (52/52). **Non ancora verificato**: che `MirrorWindow` mostri
davvero un fotogramma vero a schermo durante un mirroring reale — quel
test dal vivo è la sessione raccontata nella sezione seguente.

## 🐛 La caccia al bug del rendering (notte successiva)

Primo test dal vivo di `MirrorWindow` contro un mirroring reale. Trovati e
corretti **due bug veri** lungo la strada, poi una terza cosa — quella per
cui il video si vede ma si blocca dopo pochi secondi — che a fine sessione
resta **non risolta**, con tutto quello che è stato escluso documentato qui
sotto perché non vada rifatto da capo.

### Bug #1 — la chiave video, per un ID negativo

Il primo test: la finestra si apre ma resta tutta nera. Il log (mai
collegato a nessun output prima di quella notte — vedi `AppLog.cs`, un
logger su file perché l'app in background non ha più una console)
mostra l'SPS/PPS decodificato correttamente, ma **zero** pacchetti video
mai trasformati in un NAL valido, anche se la decifratura non solleva mai
un errore — sintomo classico di una chiave sbagliata: AES-CTR "riesce"
sempre, produce solo spazzatura.

`MirroringDataReceiver.DeriveVideoKeyIv` deriva la chiave video formattando
`streamConnectionID` dentro una stringa (`"AirPlayStreamKey{id}"`) prima di
farne lo SHA-512. Controllato il vero sorgente di UxPlay
(`mirror_buffer_init_aes`, `lib/mirror_buffer.c`): usa `PRIu64` — **senza
segno**. Il campo del plist però decodifica come `long` con segno in C#
(l'encoding a 8 byte di bplist), e per un ID il cui bit più alto è
impostato usciva **negativo** in C# — stringa tipo
`"AirPlayStreamKey-292324589914665516"` invece di quella vera
(`"...18154419483794886100"`, la stessa sequenza di bit letta come
`ulong`). La sessione precedente ("il video vero funziona") aveva avuto la
fortuna di un ID positivo — stessa stringa in entrambi i casi, bug mai
esercitato. Corretto reinterpretando come `ulong` prima di formattare, con
test di regressione sull'ID reale negativo visto quella notte.

### Bug #2 — la finestra e il telefono non si sincronizzavano alla chiusura

Fermare il mirroring da iPhone non chiudeva la finestra su Windows (e
viceversa). Aggiunto `MirroringDataReceiver.SessionEnded` (scatta nel
`finally` di `AcceptLoopAsync`, qualunque sia la causa della fine) perché
`MirrorWindow` si chiuda da sola quando il telefono ferma il mirroring, e
`CloseSessionRequested` (con `RequestSessionClose()`) perché chiudere la
finestra chiuda anche la connessione RTSP — così il telefono se ne accorge
e si ferma anche lui, invece di continuare a credere di stare ancora
facendo mirroring nel vuoto. Il collegamento vive nel setter di
`MirrorSetupState.DataReceiver` dentro `AirPlayReceiverServer`, l'unico
punto con accesso sia al receiver sia al `TcpClient` della connessione RTSP
che lo possiede — `BuildSetupResponse`, dove i receiver nascono, non ha un
proprio riferimento a quel `client`.

**Un bug vero, introdotto e corretto nello stesso giro**: ogni sessione di
mirroring crea *due* `MirroringDataReceiver` (uno dall'offerta proattiva
`ekey`/`eiv`, uno dalla vera `SETUP` con `streams[]` che arriva quasi
subito e sostituisce il primo, mai davvero connesso). La chiusura
automatica di quello "fantasma" chiamava comunque `CloseSessionRequested`
— chiudendo la connessione RTSP **condivisa** e facendo cadere anche la
sessione vera appena nata: connessione, poi disconnessione immediata,
senza preavviso. Distinto con `MirrorWindow.ShouldRequestSessionClose`
(`true` solo per una chiusura iniziata davvero dall'utente, non per
`SessionEnded`).

### Il bug che resta: video che si blocca dopo pochi secondi

Con SPS/PPS e video che arrivano decifrati bene, il rendering **a volte
esce pulito** (l'home screen dell'iPhone, nitida, verificata con uno
screenshot vero di Windows — non una foto col telefono, che introduce i
suoi artefatti fuorvianti) e **a volte esce corrotto** (sbavature
cromatiche sui bordi, come se i canali colore fossero disallineati) — ma
**si blocca sempre**, dopo circa 1-3 secondi, e non si riprende più, anche
se il telefono continua a mandare dati.

Ogni pista seguita, verificata e **esclusa** (nessuna ha cambiato il
comportamento):
- **Ordine dei campioni**: `OnSampleRequested` lanciava un `Task` per
  richiesta, che con letture concorrenti potevano completarsi fuori
  ordine — serializzato con un `SemaphoreSlim(1,1)`.
- **Timestamp duplicati**: `DateTime.UtcNow` ha risoluzione reale di
  circa 15ms, non il millisecondo che stampa — più campioni consecutivi
  (SPS/PPS + i primi NAL, arrivati quasi insieme) finivano sullo stesso
  timestamp. Resi strettamente crescenti.
- **`Duration` mai impostata**, poi impostata a un valore fisso (~30fps),
  poi corretta al tempo vero trascorso dal campione precedente — un
  valore fisso con arrivi a raffica dalla rete fa sembrare "bufferizzato"
  molto più di quanto sia realmente passato in tempo reale.
- **`_streamStart`** risincronizzato all'avvio vero della pipeline
  invece che alla costruzione della finestra (poteva essere già
  qualche centinaio di ms nel passato per via dell'attesa di SPS/PPS).
- **Dimensione finestra vs. area video**: `AppWindow.Resize` imposta la
  finestra *esterna* (bordi/barra del titolo inclusi), non l'area client
  dove il video renderizza — ~16×39px di differenza misurati dal vivo.
  Corretto con un resize in due passaggi (misura, poi compensa).
- **`MediaPlayer`/`MediaStreamSource` mai rilasciati** (`Dispose`) alla
  chiusura di una finestra — attraverso una dozzina di tentativi nello
  stesso processo, plausibile causa di risorse GPU accumulate. Corretto,
  nessun cambiamento osservato.
- **`CanSeek`/`RealTimePlayback`**: entrambi documentati esplicitamente
  da Microsoft per lo streaming live via `MediaStreamSource` (un vero
  Q&A ufficiale), aggiunti, nessun cambiamento.
- **RTX Video Enhancement** (Super Resolution/HDR di NVIDIA): già
  disattivati dall'utente prima ancora di controllare.
- **Focus della finestra**: la teoria più promettente — Windows può
  sospendere il rendering di finestre non a fuoco per risparmio
  energetico — testata **tenendo la finestra a fuoco per tutto il
  tempo**: si blocca comunque.
- **Formato AVCC + `SetFormatUserData`** invece di Annex-B (il formato
  usato da praticamente ogni MP4 su Windows, quindi il percorso più
  battuto del decoder H.264 di Media Foundation): peggio, non meglio —
  `MediaOpened` smetteva del tutto di scattare. Confermato che
  `VideoEncodingProperties.CreateH264()` è legato all'Annex-B a
  prescindere da dove arrivano SPS/PPS. Ripristinato.
- **Cifratura AES-CTR**: sospettata per ultima, perché tutti i test
  esistenti verificavano solo coerenza interna (round-trip, spezzare vs.
  blocco unico) — proprietà che un keystream sistematicamente sbagliato
  soddisferebbe comunque, dato che applicare la stessa sequenza sbagliata
  due volte si annulla da sé. Scritto un test indipendente da zero
  (contatore `BigInteger`, non il loop a byte con riporto sotto test) su
  90.000 byte — la taglia di un vero IDR — **verificata corretta anche su
  larga scala**.

**Conclusione onesta**: il flusso dati è verificato sano al 100% —
campioni consegnati in ordine, temporizzati bene, dimensioni corrette,
`MediaPlayer` non solleva mai un errore (`MediaFailed` non scatta
nemmeno una volta in tutta la sessione). Il blocco succede dentro
l'engine di rendering di Windows/Media Foundation, in un punto
completamente opaco da questa API — nessun log possibile da lì. `MediaPlayerElement`
inoltre non compone il video nel normale albero XAML: usa uno swap
chain separato che Windows disegna "sotto" l'interfaccia attraverso un
buco ritagliato — un dettaglio architetturale scoperto tardi, non ancora
sfruttato per una diagnosi più profonda.

**Prossimo passo onesto, non tentato quella notte**: il *frame-server
mode* di WinRT (`MediaPlayer.IsVideoFrameServerEnabled` +
`VideoFrameAvailable` + `CopyFrameToVideoSurface`) — invece di lasciare
che WinRT componga automaticamente il video, prendere i fotogrammi già
decodificati **dall'hardware** (nessuna perdita di accelerazione) e
disegnarli a mano tramite Win2D. Richiede una libreria in più
(`Microsoft.Graphics.Win2D`) e una riscrittura della parte finale della
pipeline — non un tweak leggero, e Microsoft stessa segnala su GitHub
che questa modalità "ha dei bug propri e non può sostituire
`MediaPlayerElement` in pieno" (issue mai risolta su
`MixedReality-WebRTC`, un altro progetto Microsoft che ha usato lo
stesso approccio `MediaStreamSource` e ha lasciato un identico bug di
freeze irrisolto quando il repository è stato archiviato). Da provare con
occhi freschi, non a fine di una sessione già lunga.

Tutto il resto costruito quella notte — le due correzioni sopra, il
logger su file, i test AES-CTR indipendenti — è verificato e solido, non
buttato via: il blocco è isolato all'ultimissimo miglio (il pixel a
schermo), non alla cattura/decifratura/instradamento del video, che
restano la parte difficile e già risolta di questo progetto.

## 🔧 Pipeline di rendering riscritta: PTS dall'encoder, access unit interi, watchdog

Sessione successiva, ripartendo dalla "conclusione onesta" qui sopra —
riletta con occhi freschi, quel "flusso dati sano al 100%" non lo era del
tutto. Tre difetti strutturali, tutti a monte del compositor opaco, tutti
corretti:

- **Il PTS dei campioni veniva assegnato al momento della richiesta, non
  all'arrivo del frame.** `OnSampleRequested` faceva
  `timestamp = DateTime.UtcNow - _streamStart` *quando `MediaStreamSource`
  chiedeva il campione*. Ogni volta che il producer correva avanti (una
  raffica di pacchetti dalla rete, o il buffering iniziale del renderer che
  teneva ferma la coda), decine di frame venivano tirati fuori in pochi
  millisecondi e **timbrati tutti con lo stesso "adesso"** — la timeline di
  presentazione collassava, il renderer si ritrovava senza margine e
  andava in stallo. È il candidato numero uno per un blocco "dopo 1-3
  secondi": è esattamente quanto dura il primo buffer prima che la coda
  smetta di essere in vantaggio.
- **Ogni NAL era un campione a sé, con timestamp proprio.** Un access unit
  H.264 (SEI + slice, o più slice) finiva spezzato in campioni separati a
  timestamp crescenti — il decoder non ha più un confine di frame
  affidabile, e una volta in errore non ha modo di recuperare.
- **SPS/PPS non veniva mai ripresentato.** Consegnati una volta sola in
  testa allo stream. Al primo fault del decoder (un pacchetto perso, un
  reference frame corrotto) il successivo IDR da solo non basta a
  ripartire: servono i parameter set *in-band con lui*.

**Formato header confermato leggendo il sorgente vero di UxPlay** (non i
doc): `raop_rtp_mirror.c` legge l'**offset 8** dei 128 byte di header come
`byteutils_get_long` — cioè uint64 **little-endian**, e `raop_ntp` lavora
in **nanosecondi** (clock del telefono, epoca arbitraria dal boot). È il
timestamp reale di presentazione del frame, prodotto dall'encoder,
**jitter-free** — e non veniva mai letto.

Cosa è stato costruito:

- **`MirroringDataReceiver`** assembla ora **un access unit intero per
  pacchetto video**: converte AVCC→Annex-B (sostituisce il prefisso di
  lunghezza a 4 byte big-endian con lo start code `00 00 00 01`, la stessa
  identica operazione del mirror thread di UxPlay), **antepone SPS/PPS a
  ogni key frame** (stream auto-recuperante), e passa avanti il timestamp
  del frame. I frame **prima del primo IDR vengono scartati** (un decoder
  non può partire da un P-frame). I NAL non-VCL isolati (SEI/AUD in un
  pacchetto senza slice) vengono agganciati al frame successivo, non
  spediti come "frame senza immagine". Nuovo evento
  `FrameReceived(MirroringVideoFrame)` al posto del vecchio `NalReceived`
  per-NAL; `ConfigReceived` resta ma serve solo a dimensionare la finestra.
- **`MirrorWindow`**: un `MediaStreamSample` per frame; **PTS preso
  dall'encoder** (delta del timestamp di header convertito in tick, con
  guardia: mai all'indietro, un salto di >2s è trattato come glitch del
  clock e non come tempo reale); `Duration` = intervallo reale al frame
  successivo (un frame di ritardo per calcolarlo esatto, invece di un
  ~33ms fisso che con arrivi a raffica mente sul tempo trascorso);
  `KeyFrame` impostato sul campione. **Coda limitata** (~2.5s a 60fps):
  quando il renderer resta indietro, scarta i frame stantii **fino
  all'ultimo key frame** — il mirroring in tempo reale deve tenere bassa
  la latenza, e un resync pulito a un IDR batte un backlog che cresce.
- **Watchdog** (1 Hz): se la posizione di playback smette di avanzare per
  >3s mentre in coda ci sono frame decodificabili e siamo ben oltre
  l'avvio, **ricostruisce la pipeline** (`MediaPlayer`/`MediaStreamSource`
  nuovi) e riparte da un key frame, mantenendo **un'unica timeline PTS
  continua** attraverso la ricostruzione. Fino a 6 volte, poi si ferma e
  logga: se anche una pipeline nuova di zecca resta bloccata, il colpevole
  è il compositor di `MediaPlayerElement` e il fix strutturale è il
  frame-server mode (vedi sotto). Ogni tick logga
  `served`/`queued`/`pos`/stato — così il prossimo test dal vivo dice
  **con certezza** se `MediaStreamSource` smette di chiedere campioni
  (scenario A) o se continua a chiederli ma il fotogramma non cambia
  (scenario B): una distinzione che finora nessun log permetteva di fare.

**Verificato**: build App pulita (0 warning), test verdi —
`MirroringDataReceiverVideoTests` guida un `MirroringDataReceiver` vero su
una connessione TCP di loopback con pacchetti mirroring costruiti a mano
(header da 128 byte, payload video cifrato AES-CTR con keystream continuo,
record di config in chiaro) e verifica: assemblaggio Annex-B byte-per-byte,
SPS/PPS anteposto agli IDR, timestamp letto e convertito da offset 8,
scarto dei frame prima del primo IDR. Segue il primo test dal vivo — vedi
sotto.

**Onestà**: questa prima versione teneva ancora `MediaPlayerElement` per la
composizione. I due test dal vivo qui sotto hanno mostrato che è proprio lì
il blocco — e la sezione "Frame-server mode + Win2D" lo toglie di mezzo.

### Primo test dal vivo della pipeline nuova: due difetti trovati nei log

Il log del watchdog ha reso la sessione **conclusiva** — cosa che nessun
test precedente permetteva. Due bug, entrambi corretti:

- **La scala del timestamp era sbagliata di 4.29x.** L'offset 8 dell'header
  mirroring non è un contatore di nanosecondi grezzo: è un timestamp **NTP
  a virgola fissa 32.32** (secondi nei 32 bit alti, frazione scalata a 2^32
  nei bassi). Letto come nanosecondi diretti, ogni frame risultava distante
  ~72ms dal precedente invece di ~16.6ms → il renderer riproduceva lo
  stream a **~14fps mentre ne arrivavano ~60**, la coda si riempiva a
  35 frame/s. Corretto con `Ntp.ToNanoseconds` (identico a
  `raop_ntp_timestamp_to_nano_seconds` di UxPlay:
  `ns = sec*1e9 + (frac*1e9 >> 32)`), letto dal sorgente vero e verificato
  con vettori calcolati a mano.
- **Lo stream ha un solo IDR.** In tutta la sessione (~900 pacchetti, 25
  secondi) l'iPhone ha mandato **un solo key frame**, all'inizio, e poi
  solo P-frame. Su TCP affidabile va benissimo — ma significa che
  **scartare anche un solo frame rende tutti i successivi non
  decodificabili** finché il telefono non manda un altro IDR di sua
  iniziativa (un cambio di scena, un cambio app), che può non arrivare per
  decine di secondi. La logica "coda limitata, scarta fino all'ultimo key
  frame" della prima versione era quindi attivamente dannosa: appena la
  coda si riempiva (per via del bug del timestamp), scartava P-frame e
  mandava in crisi il decoder — ed è **esattamente** il punto in cui nel
  log `served` si è congelato a 72 mentre `pos` continuava ad avanzare
  (schermo fermo, contatore che scorre). Ora la coda **non viene mai
  potata** in funzionamento normale: consegna ogni frame in ordine. Il
  `SampleRequested` non fa più polling ma si blocca su un semaforo. Resta
  solo un tetto di sicurezza (~15s di frame): se lo si supera, la pipeline
  di rendering ha davvero ceduto e — non essendoci un IDR a cui
  riagganciarsi — la mossa onesta è chiudere la sessione perché l'utente ne
  faccia ripartire una pulita.

Con la scala del timestamp giusta, il renderer riproduce a 60fps ≈ la
velocità di arrivo, la coda resta vicina a zero, e non si scarta nulla.

### Secondo test dal vivo: il flusso dati è perfetto, ma il compositor si blocca lo stesso

Log della sessione con i due fix sopra: **impeccabile**. Passo tra i frame
+16.7 / +33.3ms (60 e 30fps veri), `queued=0` per tutti i 25 secondi,
`served` che insegue `in` frame per frame (1100/1101 a fine sessione),
`pos` che avanza in tempo reale, zero scarti, zero rebuild, chiusura
pulita. Risoluzione 666×1440, `NaturalVideoSizeChanged` che combacia con la
SPS.

E però: **l'utente vede l'immagine perfetta e nitida per ~1 secondo, poi si
blocca del tutto e non riparte più.** Niente `MediaFailed`, `served` che
continua a salire, `pos` che avanza: la pipeline dati è sana al 100%, sono
i *pixel* che si fermano. È il compositor di `MediaPlayerElement` (lo swap
chain separato che Windows disegna sotto la UI) che si incastra — lo stesso
vicolo cieco di MixedReality-WebRTC, e a questo punto **provato sul serio,
non solo temuto**: il rumore di rendering (bitrate basso, colori sballati)
è sparito del tutto, quindi decodifica e instradamento sono corretti; resta
solo l'ultimo stadio.

### Frame-server mode + Win2D (fatto, da verificare dal vivo)

Rifatto lo stadio finale come previsto: **`MediaPlayer` in frame-server
mode** (`IsVideoFrameServerEnabled`) — `MediaStreamSource` decodifica
esattamente come prima (parte provata sana), ma invece di lasciare che
`MediaPlayerElement` componga il video, ogni frame decodificato arriva
nell'evento `VideoFrameAvailable` e viene copiato **a mano** su un
`CanvasSwapChainPanel` di Win2D (`CopyFrameToVideoSurface` →
`CanvasRenderTarget` → `DrawImage` → `Present(0)`). Nessun
`MediaPlayerElement` nella finestra: la superficie è nostra, quindi un
compositore che si rifiuta di presentare non può più bloccarci.

- `MirrorWindow.xaml` non ha più `MediaPlayerElement`, solo un `Grid` in
  cui il code-behind inserisce il `CanvasSwapChainPanel`.
- `Present(0)` (sync interval 0): il callback `VideoFrameAvailable` non
  aspetta mai il vsync — un handler bloccato è proprio ciò da cui si
  scappa; si accetta un raro tearing.
- Swap chain e blit target sono legati al device, non al player: si tengono
  tra un rebuild e l'altro, si ricreano solo se cambia la risoluzione
  (`NaturalVideoSizeChanged`).
- Il **watchdog** ora guarda la cosa giusta — se `VideoFrameAvailable`
  smette di scattare (`shown` fermo) mentre il decoder è ancora alimentato
  (`served` che sale) — invece di dedurre la vita dal clock di playback, che
  avanzava anche a compositor congelato.
- Nuova dipendenza: `Microsoft.Graphics.Win2D` 1.3.2 (il ramo per
  WindowsAppSDK 1.6).

### La finestra non appariva più: corsa tra creazione finestra e dati

Primo test del frame-server: **la MirrorWindow non è comparsa affatto**. Il
log lo spiega: la finestra di rendering si crea sul thread UI, un salto di
dispatcher *dopo* che il `MirroringDataReceiver` sta già leggendo i
pacchetti — e la prima inizializzazione di `CanvasDevice` (Win2D/D3D) fa
durare quel salto qualche centinaio di ms. In quella finestra temporale il
pacchetto di config **e l'unico IDR della sessione** venivano emessi nel
vuoto (nessun iscritto), poi `MirrorWindow.Attach` si agganciava troppo
tardi: `OnConfigReceived` non scattava mai → niente pipeline, niente
`Activate()`, finestra invisibile.

Corretto in tre modi:
- **`MirroringDataReceiver` ora ricorda** l'ultimo config e tutti i frame
  dall'ultimo key frame (`EmitConfig`/`EmitFrame`/`EmitSessionEnded` sotto
  un lock). `AttachRenderer(onConfig, onFrame, onEnded)` iscrive il renderer
  **e** gli ripassa il backlog in un colpo solo, atomico: un frame che
  arriva durante l'aggancio viene consegnato dal vivo subito dopo, senza
  buchi né doppioni. Se la sessione è già finita, chiama subito `onEnded`
  (così la finestra "fantasma" dell'offerta proattiva si chiude da sola
  invece di restare orfana).
- **`CanvasDevice` creato pigramente** (non nel costruttore della finestra)
  e **pre-scaldato** all'avvio dell'app su un thread di sfondo, così il
  costo non cade più sul thread UI a metà connessione.
- Il watchdog logga `shown` (i frame che arrivano davvero a schermo via
  `VideoFrameAvailable`), per distinguere "il compositor si blocca" da "il
  decoder si blocca".

Con questi fix la finestra è comparsa e l'immagine era **nitida** — ma
sempre bloccata dopo ~1s. Il log ha reso la cosa definitiva: in
frame-server mode `VideoFrameAvailable` scatta **esattamente 4 volte**
(22:34:05.685→06.564) e poi mai più, mentre `served` sale a 764, `pos`
avanza a 15s, `state=Playing`, zero errori. Il compositor era già
bypassato: **il blocco è dentro `MediaPlayer` / Media Foundation stesso** —
il decoder viene alimentato ma smette di produrre fotogrammi.

### MFT H.264 pilotato direttamente — la soluzione

Tolto del tutto `MediaPlayer` e `MediaStreamSource`. Nuovo `H264Mft.cs`:
parla direttamente al **decoder H.264 MFT di Windows**
(`CLSID_CMSH264DecoderMFT`, via `Vortice.MediaFoundation`) con un loop
esplicito `ProcessInput`/`ProcessOutput` — nessun clock nascosto, nessuna
euristica di buffering, nessuna assunzione sul riordino dei fotogrammi.

- `MF_LOW_LATENCY` impostato sul MFT: senza, il decoder bufferizza ~30
  frame prima del primo output (probabilmente parte di ciò che si vedeva
  come "4 frame poi niente" via `MediaPlayer`).
- Un frame Annex-B intero entra, esce NV12 → convertito a BGRA
  (BT.709 limited, loop unsafe) → blit sulla `CanvasSwapChainPanel` Win2D
  già presente, `Present(0)`.
- Thread dedicato **MTA** per tutte le chiamate MF, con attesa bloccante
  (niente continuazioni async che saltano tra thread del pool).
- `H264Sps` fornisce la dimensione di partenza; il decoder rinegozia
  (`MF_E_TRANSFORM_STREAM_CHANGE`) e si legge la geometria vera.
- Watchdog: logga `decoded` (fotogrammi usciti dal MFT) vs `shown`
  (fotogrammi blittati) — così si distingue un blocco nel decoder da uno
  nel blit.
- Dipendenza nuova: `Vortice.MediaFoundation` 3.6.2 (P/Invoke puro su
  `mfplat.dll`/`ole32.dll`, nessun binario nativo in più).

### Primo test del MFT diretto: FUNZIONA, poi un crash da double-free

Il log è netto: `decoded` e `shown` inseguono `in` a **60fps veri**,
`queued=0`, per ~10 secondi filati (`in=422 decoded=416 shown=416`,
`ultimaDecodifica=0.0s` sempre) — **niente più blocco**. Poi il log si
interrompe di colpo: nessun errore, nessun teardown, la finestra sparisce
ma il processo e la connessione RTSP restano vivi. Crash nativo secco.

Causa trovata: **double-free COM** in `H264Mft.DrainOutputs`. Dopo
`ProcessOutput` con un `MFT_OUTPUT_DATA_BUFFER` che gli abbiamo fornito
noi, Vortice/SharpGen ricostruisce `db.Sample` come wrapper managed nuovo
sullo **stesso** puntatore nativo di `ours`, **senza `AddRef`**
(`__MarshalFrom` di struct). Il codice faceva `produced.Dispose()` +
`ours.Dispose()` → doppio `Release` sullo stesso oggetto → refcount
sotto zero → memoria liberata e riusata → heap corrotto → crash "a caso"
qualche centinaio di frame dopo (416 ≈ 10s, combacia). Corretto: quando il
sample lo forniamo noi, si tocca e si rilascia **solo** `ours`, mai
`db.Sample`.

Nello stesso giro, altre due strette anti-crash:
- Niente più `CanvasBitmap.CreateFromBytes` per ogni frame (40 texture GPU
  al secondo, allocate e distrutte) — un `CanvasRenderTarget` persistente
  con `SetPixelBytes`. E il buffer BGRA in `H264Mft` è riusato, non
  riallocato (3.8MB × 40/s di garbage in meno).
- Swap chain / render target protetti da un lock contro il (raro) percorso
  di resize che li sostituisce dal thread UI mentre il thread di decodifica
  sta presentando.
- `App` ora aggancia `UnhandledException` (XAML + AppDomain) e
  `UnobservedTaskException` e li scrive nel log — un crash nativo non deve
  più sparire senza traccia.

### 🎉 Il video del mirroring funziona

Test dal vivo con il double-free corretto: **oltre un minuto di mirroring
fluido**, `in=3098 decoded=3089 shown=3089`, `queued` quasi sempre 0,
`ultimaDecodifica=0.0s` per tutta la durata, nessun freeze, nessun crash,
chiusura pulita quando l'utente ferma dal telefono. Immagine nitida, colori
giusti, 666×1440 a 60fps. Il pezzo grosso della Fase 2 è fatto.

### L'audio dal telefono (implementato, da verificare dal vivo)

Lo stesso log mostra l'iPhone che, a mirroring avviato, manda una `SETUP`
separata con `streams:[{type: 96, ...}]` — lo **stream audio AirPlay** — e
la ritenta ogni ~2s finché non gli si risponde. Il dump dei campi dello
stream ha confermato: `ct=8` (**AAC-ELD**), `spf=480`, `sr=44100`, stereo
(`audioFormat=0x1000000`), `redundantAudio=2`.

Windows non ha un decoder AAC-ELD (`CMSAACDecMFT` fa solo LC/HE-AAC), come
UxPlay che per questo richiede `libfdk-aac`. Costruito:

- **`MirrorAudioReceiver`** (Core): socket UDP dati + controllo, loop RTP.
  Ogni pacchetto — header RTP 12 byte, seq ai byte 2-3 big-endian — ha il
  payload cifrato **AES-128-CBC** (chiave = la stessa `SessionAesKey` del
  video, IV = l'`eiv` di sessione, reset per pacchetto, solo i blocchi
  interi da 16 byte, coda in chiaro — da `raop_buffer.c` di UxPlay). Riordino
  per sequence number con scarto dei re-invii ridondanti; emette il frame
  AAC-ELD grezzo.
- **`AacEldDecoder`** (App): P/Invoke a `libAACdec.dll` (NuGet `fdk-aac`
  2.0.3, Fraunhofer FDK AAC prebuilt x64/arm64). AudioSpecificConfig
  `F8 E8 50 00` (AAC-ELD 44100/2, spf 480) preso verbatim da UxPlay
  (`audio_renderer.c`, `aac_eld_caps ... codec_data=(buffer)f8e85000`).
  → PCM int16.
- **`MirrorAudioPlayer`** (App): `AudioGraph` WinRT + `AudioFrameInputNode`,
  ring buffer con prime a ~50ms, drop-oldest a ~500ms, silenzio sull'underrun.
- `AirPlayReceiverServer` risponde alla `SETUP type 96` con `{dataPort,
  controlPort, type:96}` e alza `MirrorAudioSessionStarted`; `App` collega
  decoder + player.

`et=32` (mai spiegato) è ignorato — sia il video sia UxPlay non lo leggono.
Se l'audio esce come rumore, il primo sospetto è proprio la modalità/chiave
di cifratura audio (il log stampa `primo byte decifrato 0xXX`: deve essere
`0x8c`/`0x8d`/`0x8e`).

**Verificato in locale**: build App pulita (0 warning), **63/63 test**
(nuovo: `MirrorAudioReceiver` — decifratura CBC + riordino + scarto
ridondanti su UDP di loopback). **Da verificare dal vivo.**

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
  altri speaker AirPlay 2), rilevazione disconnessione, multi-room.
- **Fase 2 (ricevitore di mirroring)**: **il video funziona** 🎉 — ricevuto,
  decifrato, decodificato (decoder H.264 MFT di Windows pilotato a mano) e
  reso a schermo (Win2D), 60fps fluidi per oltre un minuto dal vivo contro
  un iPhone reale. Più icona nel tray, chiusura sincronizzata in entrambe le
  direzioni, avvio automatico con Windows. Resta:
  1. **L'audio dal telefono — costruito, da verificare dal vivo.** Lo
     stream è AAC-ELD (`ct=8`, 480 spf, 44100/2). Costruiti
     `MirrorAudioReceiver` (UDP RTP + decrypt AES-128-CBC + riordino),
     `AacEldDecoder` (P/Invoke a `libfdk-aac`, NuGet `fdk-aac`) e
     `MirrorAudioPlayer` (`AudioGraph`); `AirPlayReceiverServer` risponde
     alla `SETUP type 96`. Vedi "L'audio dal telefono". Da confermare:
     modalità/chiave di cifratura audio (`et=32` è ignorato), e la
     sincronizzazione A/V (per ora l'audio parte con ~50ms di buffer, senza
     allineamento esplicito ai timestamp del video).
  2. Il pair-setup HAP "vero" (transient collegato ma probabilmente non la
     forma corretta — vedi sopra) resta un'incognita a bassa priorità: il
     percorso legacy già collegato funziona fino in fondo per il video.
  3. La sessione dati potrebbe aver bisogno di gestire riconnessioni/più
     stream — il video regge bene una sessione lunga, non ancora provate
     riconnessioni.
- **Fase 2b (sender di mirroring, Windows → TV)**: non affrontata, R&D
  ancora più aperta di quanto sopra — nessun progetto open source esiste per
  questo verso. Vedi la discussione nella cronologia del progetto per la
  valutazione completa.

## Licenza e attribuzioni

Vedi [NOTICE.md](NOTICE.md).
