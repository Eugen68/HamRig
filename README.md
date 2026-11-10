# HamRig

Biblioteca .NET pentru controlul statiei (CAT) din aplicatiile noastre (RttyDecoder, CwDecoder,
aplicatia mare): o singura interfata - frecventa, mod, PTT, evenimente la schimbare - cu trei
moduri de conectare:

| | Direct (`YaesuCatRig`) | OmniRig (`OmniRigRig`) | Hamlib (`HamlibRig`) |
|---|---|---|---|
| Statii | Yaesu-urile noi (FTDX10 verificat; FTX-1, FTDX101, FT-710, FT-991A, FT-891) | orice statie stiuta de OmniRig | orice statie stiuta de Hamlib (~280 de modele: Icom, Kenwood, Elecraft, Yaesu, Ten-Tec...) |
| Folosire simultana cu alte programe | nu (portul COM e al unui singur program) | da (OmniRig detine portul) | da, prin acelasi `rigctld` (de ex. si WSJT-X) |
| Actualizare | instantanee (AI1: statia anunta singura) | citire periodica (250 ms) | citire periodica (250 ms) |
| Comenzi | frecventa, mod (toate modurile FTX-1), PTT (CAT sau RTS/DTR), manipulare RTS/DTR | frecventa, mod (fara RTTY separat), PTT si manipulare doar RTS/DTR pe portul de manipulare (OmniRig nu face PTT) | frecventa, mod (inclusiv RTTY / RTTYR), PTT (prin Hamlib sau RTS/DTR), manipulare RTS/DTR pe portul de manipulare |
| Cerinte | nimic in plus | OmniRig instalat si configurat (Windows) | Hamlib instalat (`rigctld`), Windows sau Linux |

- tinta: `net8.0`; OmniRig doar pe Windows

### Hamlib

`HamlibRig` vorbeste cu **`rigctld`** (programul de retea din Hamlib) pe TCP, implicit `127.0.0.1:4532`,
cu protocolul extins (`+f`, `+m`, `+t`, `+F`, `+M`, `+T`; fiecare raspuns se termina cu `RPRT n`). Nu
se leaga direct la `libhamlib`: structurile ei se schimba intre versiuni, iar un program separat nu pune
probleme de licenta (Hamlib e GPL/LGPL, HamRig e MIT).

- `HamlibStartDaemon = true` (implicit): HamRig porneste singur `rigctld -m <model> -r <port> -s <viteza>`
  si il opreste la deconectare. Calea: `HamlibPath`, altfel cautat in PATH si in
  `Program Files\hamlib*\bin`. Instalarea pe Windows: arhiva sau installer-ul din
  [Hamlib releases](https://github.com/Hamlib/Hamlib/releases).
- `HamlibStartDaemon = false`: legatura la un `rigctld` deja pornit (`HamlibHost`, `HamlibPort`).
- `Hamlib.ListModels(rigctldPath)` intoarce lista modelelor (`rigctl -l`); de ex. 1042 = FTDX-10,
  1049 = FT-710, 1035 = FT-991, 3073 = IC-7300 (Hamlib 4.5.5; FTX-1 doar in versiunile mai noi).
- Modurile: `RTTY` = RTTY-L, `RTTYR` = RTTY-U, `PKTUSB`/`PKTLSB` = DATA-U/L.
- Doar frecventa e obligatorie: multe statii nu raporteaza PTT-ul sau modul (Hamlib raspunde `RPRT -11`);
  atunci nu se mai cer. O comanda refuzata (de ex. un mod pe care statia nu-l are) nu rupe legatura.
- Verificat cu Hamlib 4.5.5 real (`rigctld -m 1`, statia Dummy): pornirea lui rigctld, citirea,
  frecventa, RTTY / RTTYR / PKTUSB, PTT, oprirea la deconectare.
- conectarea directa e portata din aplicatia Android Yaesu-FTX1-CAT-Control (`RadioManager`):
  38400 baud, `AI1;`, `FA` pe 9 cifre, codurile de mod `MD0x` ale FTX-1, PTT si FSK pe RTS/DTR pe
  al doilea port USB al statiei (portul se deschide o singura data)

## Statii verificate

| Statie | Direct (CAT Yaesu) | OmniRig | PTT | Manipulare (FSK) |
|---|---|---|---|---|
| **FTDX10** | verificat | verificat | verificat (CAT si RTS/DTR) | verificat |

**Compatibile** (aceleasi comenzi CAT ca FTDX10: `FA` pe 9 cifre, `MD0x`, `TX`, `AI1`; cele cu
USB apar ca doua porturi COM, "Enhanced" pentru CAT si "Standard" pentru PTT/FSK): FTX-1,
FTDX101D/MP, FT-710, FT-991A, FT-891.

De stiut:
- **codurile de mod rare difera** intre modele: la FT-991A `MD0E` e C4FM (aici apare ca PSK),
  FT-891 nu are `E`/`F`. Frecventa, PTT-ul, manipularea si modurile uzuale (LSB, USB, CW, DATA,
  RTTY) au aceleasi coduri;
- ce face RTS si ce face DTR (PTT, FSK, CW) se alege **din meniul statiei**, nu din program;
- viteza CAT trebuie sa fie aceeasi ca in meniul statiei.

**Nu merg direct**: Yaesu mai vechi cu frecventa pe 8 cifre (FT-950, FT-2000, FT-450 - citirea
merge, schimbarea frecventei nu) si FT-817/857/897 (protocol binar). Pentru ele si pentru alte
marci: OmniRig.

## Utilizare

```csharp
using HamRig;

var settings = new RigSettings
{
    Kind = RigKind.YaesuCat,       // sau RigKind.OmniRig (+ OmniRigNumber = 1 / 2)
    CatPort = "COM5",              // FTX-1: primul port, "Enhanced COM Port"
    CatBaud = 38400,               // ca in meniul statiei
    CatPttPin = SerialPin.None,    // PTT pe un pin al portului CAT (None / Rts / Dtr)

    // portul de manipulare si PTT - FTX-1: al doilea port, "Standard COM Port"
    KeyPort = "COM6",
    KeyBaud = 4800,
    KeyPortPttPin = SerialPin.Rts, // PTT aici sau la CatPttPin, nu in ambele locuri
    KeyPin = SerialPin.Dtr,        // FSK / keying CW: rig.SetKey(true/false)
};
// niciun pin de PTT ales = conexiunea directa: comanda CAT TX1;  OmniRig: fara PTT
// settings.Validate() spune ce nu e bine (PTT in doua locuri, acelasi pin pentru PTT si manipulare...)

using IRig rig = settings.CreateRig()!;
rig.StateChanged += s => Console.WriteLine($"{RigMath.FormatFrequency(s.FrequencyHz)} {RigMath.ModeName(s.Mode)} {(s.Transmitting ? "TX" : "RX")} - {s.StatusText}");
await rig.ConnectAsync();          // RigException: port ocupat/inexistent, statia nu raspunde, OmniRig lipsa

rig.SetFrequency(14_083_000);
rig.SetMode(RigMode.DataU);
rig.SetPtt(true);
```

`StateChanged` vine pe un fir din fundal (in WPF: `Dispatcher.BeginInvoke`). `RigSettings` se
salveaza direct cu System.Text.Json; `RigSettings.GetPortNames()` da lista porturilor COM.

### Frecventa semnalului si QSY

```csharp
// frecventa RF a Mark-ului decodat la 2125 Hz in waterfall (USB/DATA-U: VFO + ton, LSB: VFO - ton)
double? rf = RigMath.ToneToRf(rig.State.FrequencyHz, rig.State.Mode, 2125);

// QSY: semnalul auzit la 1500 Hz adus pe 2125 Hz
if (RigMath.QsyDial(rig.State.FrequencyHz, rig.State.Mode, 1500, 2125) is { } dial) rig.SetFrequency(dial);
```

In modurile in care statia demoduleaza singura (RTTY, PSK, AM, FM) afisajul statiei e deja
frecventa semnalului, iar `ToneToRf` / `QsyDial` intorc null.

## Intr-o aplicatie

Un singur obiect `IRig` detine statia si toate modulele il folosesc pe acela (pentru conectarea
directa, portul COM nu poate fi deschis de doua ori). Ca submodul git:

```bash
git submodule add https://github.com/Eugen68/HamRig.git external/HamRig
```

```xml
<ProjectReference Include="..\..\external\HamRig\src\HamRig\HamRig.csproj" />
```

## Teste

```bash
dotnet test
```

Fara statie: un FTX-1 simulat pe un port serial fals (`tests/HamRig.Tests/FakeFtx1.cs`), un
OmniRig simulat si un `rigctld` simulat pe TCP (`HamlibTests.cs`). Verificarea pe statia reala: din aplicatie (Setari > Conexiune statie > Testeaza).

## Licenta

MIT, vezi [LICENSE](LICENSE).
