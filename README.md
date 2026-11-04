# HamRig

Biblioteca .NET pentru controlul statiei (CAT) din aplicatiile noastre (RttyDecoder, CwDecoder,
aplicatia mare): o singura interfata - frecventa, mod, PTT, evenimente la schimbare - cu doua
moduri de conectare:

| | Direct (`YaesuCatRig`) | OmniRig (`OmniRigRig`) |
|---|---|---|
| Statii | FTX-1 si Yaesu-urile noi (FTDX10, FT-710, FT-991A...) | orice statie stiuta de OmniRig |
| Folosire simultana cu alte programe | nu (portul COM e al unui singur program) | da (OmniRig detine portul) |
| Actualizare | instantanee (AI1: statia anunta singura) | citire periodica (250 ms) |
| Comenzi | frecventa, mod (toate modurile FTX-1), PTT CAT/RTS/DTR | frecventa, mod (fara RTTY separat), PTT |
| Cerinte | nimic in plus | OmniRig instalat si configurat (Windows) |

- tinta: `net8.0`; OmniRig doar pe Windows
- conectarea directa e portata din aplicatia Android Yaesu-FTX1-CAT-Control (`RadioManager`):
  38400 baud, `AI1;`, `FA` pe 9 cifre, codurile de mod `MD0x` ale FTX-1, PTT pe RTS/DTR pe al
  doilea port USB al statiei

## Utilizare

```csharp
using HamRig;

var settings = new RigSettings
{
    Kind = RigKind.YaesuCat,       // sau RigKind.OmniRig (+ OmniRigNumber = 1 / 2)
    CatPort = "COM5",              // FTX-1: primul port, "Enhanced COM Port"
    CatBaud = 38400,               // ca in meniul statiei
    Ptt = PttMethod.Rts,           // None / Cat / Rts / Dtr
    PttPort = "COM6",              // FTX-1: al doilea port, "Standard COM Port" (gol = portul CAT)
};

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

Fara statie: un FTX-1 simulat pe un port serial fals (`tests/HamRig.Tests/FakeFtx1.cs`) si un
OmniRig simulat. Verificarea pe statia reala: din aplicatie (Setari > Conexiune statie > Testeaza).

## Licenta

MIT, vezi [LICENSE](LICENSE).
