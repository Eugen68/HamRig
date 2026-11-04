using System.Globalization;

namespace HamRig;

/// <summary>
/// Protocolul CAT Yaesu pentru statiile noi (FTX-1, FTDX10, FT-710, FT-991A...): comenzi ASCII
/// terminate cu ';'. Codurile de mod sunt cele ale FTX-1, ca in aplicatia Android
/// Yaesu-FTX1-CAT-Control: 1 LSB, 2 USB, 3 CW-U, 4 FM, 5 AM, 6 RTTY-L, 7 CW-L, 8 DATA-L, 9 RTTY-U,
/// A DATA-FM, B FM-N, C DATA-U, D AM-N, E PSK, F DATA-FM-N.
/// </summary>
public static class YaesuCat
{
    private const string Codes = "123456789ABCDEF";

    private static readonly RigMode[] Modes =
    {
        RigMode.Lsb, RigMode.Usb, RigMode.CwU, RigMode.Fm, RigMode.Am, RigMode.RttyL, RigMode.CwL, RigMode.DataL,
        RigMode.RttyU, RigMode.DataFm, RigMode.FmN, RigMode.DataU, RigMode.AmN, RigMode.Psk, RigMode.DataFmN,
    };

    /// <summary>Codul de mod (MD0x) sau null daca modul nu exista pe statie.</summary>
    public static char? ModeCode(RigMode mode)
    {
        int i = Array.IndexOf(Modes, mode);
        return i < 0 ? null : Codes[i];
    }

    public static RigMode ModeFromCode(char code)
    {
        int i = Codes.IndexOf(char.ToUpperInvariant(code));
        return i < 0 ? RigMode.Unknown : Modes[i];
    }

    /// <summary>"FA014074000;" (VFO-A, 9 cifre).</summary>
    public static string SetFrequency(long hz)
    {
        if (hz <= 0 || hz > 999_999_999) throw new ArgumentOutOfRangeException(nameof(hz));
        return "FA" + hz.ToString("D9", CultureInfo.InvariantCulture) + ";";
    }

    /// <summary>"MD0C;" (DATA-U).</summary>
    public static string SetMode(RigMode mode) =>
        ModeCode(mode) is { } c ? "MD0" + c + ";" : throw new ArgumentException($"Modul {mode} nu exista pe statie.", nameof(mode));

    public static string Ptt(bool transmit) => transmit ? "TX1;" : "TX0;";

    /// <summary>Un raspuns (fara ';') al statiei.</summary>
    public readonly record struct Answer(long? FrequencyHz, RigMode? Mode, bool? Transmitting);

    /// <summary>
    /// Raspunsurile care ne intereseaza: FA (frecventa VFO-A), MD0x (modul), TXn (0 = receptie,
    /// 1/2 = emisie). Restul (si "?" - comanda nerecunoscuta) intorc null.
    /// </summary>
    public static Answer? Parse(string answer)
    {
        if (answer.Length < 3) return null;
        string p = answer.Substring(0, 2);
        string rest = answer.Substring(2);
        switch (p)
        {
            case "FA":
                return rest.Length >= 8 && rest.All(char.IsDigit) && long.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out long hz) && hz > 0
                    ? new Answer(hz, null, null)
                    : null;
            case "MD":
                if (rest.Length != 2 || rest[0] != '0') return null;
                var m = ModeFromCode(rest[1]);
                return m == RigMode.Unknown ? null : new Answer(null, m, null);
            case "TX":
                return rest.Length == 1 && rest[0] is >= '0' and <= '2' ? new Answer(null, null, rest[0] != '0') : null;
            default:
                return null;
        }
    }
}
