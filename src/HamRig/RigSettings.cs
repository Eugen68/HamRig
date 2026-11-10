using System.IO.Ports;

namespace HamRig;

/// <summary>Cum e legata statia.</summary>
public enum RigKind
{
    /// <summary>Fara statie.</summary>
    None,

    /// <summary>Direct pe portul serial, protocol CAT Yaesu (FTX-1, FTDX10, FT-710, FT-991A...).</summary>
    YaesuCat,

    /// <summary>Prin OmniRig (Windows): statia poate fi folosita in acelasi timp si de alte programe.</summary>
    OmniRig,

    /// <summary>Prin Hamlib (rigctld, TCP): sute de modele de statii, si alte programe pot folosi acelasi rigctld.</summary>
    Hamlib,
}

/// <summary>Setarile conexiunii (se pot salva direct cu System.Text.Json).</summary>
public sealed class RigSettings
{
    public RigKind Kind { get; set; } = RigKind.None;

    /// <summary>Portul CAT (la FTX-1: "Enhanced COM Port", primul din cele doua).</summary>
    public string? CatPort { get; set; }

    /// <summary>Viteza CAT; FTX-1: 38400 (meniul statiei trebuie sa aiba aceeasi valoare).</summary>
    public int CatBaud { get; set; } = 38400;

    /// <summary>
    /// PTT pe un pin al portului CAT (doar la conexiunea directa). Cel mult un loc pentru PTT: fie
    /// aici, fie <see cref="KeyPortPttPin"/>; niciunul = comanda CAT.
    /// </summary>
    public SerialPin CatPttPin { get; set; } = SerialPin.None;

    /// <summary>
    /// Portul de manipulare si PTT (FSK pentru RTTY, keying CW). La FTX-1 de obicei al doilea port
    /// ("Standard"): PTT pe un pin, manipularea pe celalalt. Gol = fara.
    /// </summary>
    public string? KeyPort { get; set; }

    /// <summary>Viteza portului de manipulare (conteaza doar la deschidere; liniile RTS/DTR nu depind de ea).</summary>
    public int KeyBaud { get; set; } = 4800;

    /// <summary>PTT pe un pin al portului de manipulare (vezi <see cref="CatPttPin"/>).</summary>
    public SerialPin KeyPortPttPin { get; set; } = SerialPin.None;

    /// <summary>Pinul pe care se manipuleaza.</summary>
    public SerialPin KeyPin { get; set; } = SerialPin.None;

    /// <summary>Manipularea e configurata (port si pin).</summary>
    public bool HasKeying => KeyPin != SerialPin.None && !string.IsNullOrWhiteSpace(KeyPort);

    /// <summary>
    /// Unde se face PTT-ul: portul si pinul, sau (null, None) = fara pin: la conexiunea directa prin
    /// comanda CAT (TX1;), la OmniRig deloc (OmniRig nu face PTT, iar portul CAT e al lui).
    /// </summary>
    public (string? Port, SerialPin Pin) PttLine
    {
        get
        {
            if (Kind == RigKind.YaesuCat && CatPttPin != SerialPin.None && !string.IsNullOrWhiteSpace(CatPort)) return (CatPort, CatPttPin);
            if (KeyPortPttPin != SerialPin.None && !string.IsNullOrWhiteSpace(KeyPort)) return (KeyPort, KeyPortPttPin);
            return (null, SerialPin.None);
        }
    }

    /// <summary>Portul de manipulare si PTT trebuie deschis (manipulare sau PTT pe el).</summary>
    public bool NeedsKeyPort =>
        !string.IsNullOrWhiteSpace(KeyPort) && (KeyPin != SerialPin.None || KeyPortPttPin != SerialPin.None);

    /// <summary>O problema a setarilor, sau null.</summary>
    public string? Validate()
    {
        if (Kind == RigKind.None) return null;
        if (Kind == RigKind.YaesuCat && string.IsNullOrWhiteSpace(CatPort)) return "Alege portul CAT.";
        if (Kind == RigKind.Hamlib && HamlibStartDaemon)
        {
            if (HamlibModel <= 0) return "Alege modelul statiei (Hamlib).";
            if (string.IsNullOrWhiteSpace(CatPort) && HamlibModel > 10) return "Alege portul statiei (Hamlib).";
        }
        if (Kind == RigKind.Hamlib && (string.IsNullOrWhiteSpace(HamlibHost) || HamlibPort is <= 0 or > 65535))
            return "Adresa sau portul rigctld nu sunt valide.";
        bool catPtt = Kind == RigKind.YaesuCat && CatPttPin != SerialPin.None;
        if (catPtt && KeyPortPttPin != SerialPin.None) return "PTT se alege intr-un singur loc: la portul CAT sau la portul de manipulare.";
        if ((KeyPin != SerialPin.None || KeyPortPttPin != SerialPin.None) && string.IsNullOrWhiteSpace(KeyPort))
            return "Alege portul de manipulare si PTT.";
        if (KeyPin != SerialPin.None && KeyPin == KeyPortPttPin)
            return $"PTT si manipularea nu pot fi pe acelasi pin ({KeyPin}).";
        if (Kind == RigKind.YaesuCat && KeyPin != SerialPin.None && catPtt && KeyPin == CatPttPin
            && string.Equals(KeyPort, CatPort, StringComparison.OrdinalIgnoreCase))
            return $"PTT si manipularea nu pot fi pe acelasi pin ({KeyPin}) al portului {KeyPort}.";
        return null;
    }

    /// <summary>OmniRig: Rig 1 sau Rig 2.</summary>
    public int OmniRigNumber { get; set; } = 1;

    /// <summary>Hamlib: numarul modelului de statie (din <c>rigctl -l</c>, de ex. 1035 = Yaesu FT-991).</summary>
    public int HamlibModel { get; set; }

    /// <summary>
    /// Hamlib: programul porneste singur rigctld (cu modelul, <see cref="CatPort"/> si <see cref="CatBaud"/>)
    /// si il opreste la deconectare. Nebifat: se leaga la un rigctld deja pornit.
    /// </summary>
    public bool HamlibStartDaemon { get; set; } = true;

    /// <summary>Hamlib: calea catre rigctld.exe (gol = cautat in PATH si in Program Files\hamlib*).</summary>
    public string? HamlibPath { get; set; }

    /// <summary>Hamlib: adresa lui rigctld.</summary>
    public string HamlibHost { get; set; } = "127.0.0.1";

    /// <summary>Hamlib: portul TCP al lui rigctld (implicit 4532).</summary>
    public int HamlibPort { get; set; } = Hamlib.DefaultPort;

    /// <summary>Hamlib: argumente suplimentare pentru rigctld (de ex. <c>--set-conf=...</c>).</summary>
    public string? HamlibExtraArgs { get; set; }

    /// <summary>Hamlib: citirea starii la atatea ms.</summary>
    public int HamlibPollMs { get; set; } = 250;

    /// <summary>OmniRig: citirea starii la atatea ms.</summary>
    public int OmniRigPollMs { get; set; } = 250;

    /// <summary>Cat se asteapta raspunsul statiei la conectare (ms).</summary>
    public int ResponseTimeoutMs { get; set; } = 2000;

    public RigSettings Clone() => (RigSettings)MemberwiseClone();

    /// <summary>Statia descrisa de setari, sau null pentru <see cref="RigKind.None"/>.</summary>
    public IRig? CreateRig() => Kind switch
    {
        RigKind.YaesuCat => new YaesuCatRig(this),
        RigKind.OmniRig => new OmniRigRig(this),
        RigKind.Hamlib => new HamlibRig(this),
        _ => null,
    };

    /// <summary>Porturile seriale ale calculatorului, sortate (COM3 inainte de COM10).</summary>
    public static IReadOnlyList<string> GetPortNames()
    {
        try
        {
            return SerialPort.GetPortNames().Distinct()
                .OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
