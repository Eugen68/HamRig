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
}

/// <summary>Setarile conexiunii (se pot salva direct cu System.Text.Json).</summary>
public sealed class RigSettings
{
    public RigKind Kind { get; set; } = RigKind.None;

    /// <summary>Portul CAT (la FTX-1: "Enhanced COM Port", primul din cele doua).</summary>
    public string? CatPort { get; set; }

    /// <summary>Viteza CAT; FTX-1: 38400 (meniul statiei trebuie sa aiba aceeasi valoare).</summary>
    public int CatBaud { get; set; } = 38400;

    public PttMethod Ptt { get; set; } = PttMethod.None;

    /// <summary>
    /// Portul pentru PTT pe RTS/DTR (la FTX-1: "Standard COM Port", al doilea). Gol = acelasi cu
    /// portul CAT.
    /// </summary>
    public string? PttPort { get; set; }

    /// <summary>
    /// Portul de manipulare (FSK pentru RTTY, keying CW). La FTX-1 de obicei al doilea port
    /// ("Standard"), acelasi ca PTT-ul, pe celalalt pin. Gol = fara manipulare.
    /// </summary>
    public string? KeyPort { get; set; }

    /// <summary>Viteza portului de manipulare (conteaza doar la deschidere; liniile RTS/DTR nu depind de ea).</summary>
    public int KeyBaud { get; set; } = 4800;

    /// <summary>Pinul pe care se manipuleaza.</summary>
    public SerialPin KeyPin { get; set; } = SerialPin.None;

    /// <summary>Manipularea e configurata (port si pin).</summary>
    public bool HasKeying => KeyPin != SerialPin.None && !string.IsNullOrWhiteSpace(KeyPort);

    /// <summary>
    /// O problema a setarilor, sau null: ex. PTT si manipularea pe acelasi pin al aceluiasi port.
    /// </summary>
    public string? Validate()
    {
        if (Kind == RigKind.YaesuCat && string.IsNullOrWhiteSpace(CatPort)) return "Alege portul CAT.";
        if (HasKeying && Kind == RigKind.YaesuCat && Ptt is PttMethod.Rts or PttMethod.Dtr)
        {
            string pttPort = string.IsNullOrWhiteSpace(PttPort) ? CatPort ?? "" : PttPort!;
            bool samePin = (Ptt == PttMethod.Rts && KeyPin == SerialPin.Rts) || (Ptt == PttMethod.Dtr && KeyPin == SerialPin.Dtr);
            if (samePin && string.Equals(pttPort, KeyPort, StringComparison.OrdinalIgnoreCase))
                return $"PTT si manipularea nu pot fi pe acelasi pin ({KeyPin}) al portului {KeyPort}.";
        }
        return null;
    }

    /// <summary>OmniRig: Rig 1 sau Rig 2.</summary>
    public int OmniRigNumber { get; set; } = 1;

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
