namespace HamRig;

/// <summary>Legatura dintre frecventa VFO, modul statiei si tonul audio din waterfall.</summary>
public static class RigMath
{
    /// <summary>Modurile in care tonul audio sta deasupra frecventei VFO (banda laterala superioara).</summary>
    public static bool IsUpperSideband(RigMode mode) => mode is RigMode.Usb or RigMode.DataU or RigMode.CwU;

    /// <summary>Modurile in care tonul audio sta sub frecventa VFO (banda laterala inferioara).</summary>
    public static bool IsLowerSideband(RigMode mode) => mode is RigMode.Lsb or RigMode.DataL or RigMode.CwL;

    /// <summary>
    /// Frecventa RF a unui ton audio: USB / DATA-U = VFO + ton, LSB / DATA-L = VFO - ton; la CW tonul
    /// se raporteaza la pitch (<paramref name="cwPitchHz"/>). Null pentru modurile in care statia
    /// demoduleaza singura (RTTY, PSK, AM, FM) - acolo afisajul statiei e deja frecventa semnalului.
    /// </summary>
    public static double? ToneToRf(long dialHz, RigMode mode, double audioHz, double cwPitchHz = 600)
    {
        if (dialHz <= 0) return null;
        return mode switch
        {
            RigMode.Usb or RigMode.DataU => dialHz + audioHz,
            RigMode.Lsb or RigMode.DataL => dialHz - audioHz,
            RigMode.CwU => dialHz + (audioHz - cwPitchHz),
            RigMode.CwL => dialHz - (audioHz - cwPitchHz),
            _ => null,
        };
    }

    /// <summary>Modul RTTY al statiei (FSK): RTTY-L sau RTTY-U.</summary>
    public static bool IsRtty(RigMode mode) => mode is RigMode.RttyL or RigMode.RttyU;

    /// <summary>
    /// QSY: noua frecventa VFO astfel incat semnalul auzit acum la <paramref name="fromAudioHz"/> sa
    /// ajunga la <paramref name="toAudioHz"/> (ex. pe Mark-ul decodorului). In RTTY-L statia lucreaza
    /// ca in LSB (ton audio mai jos = frecventa RF mai mare), in RTTY-U ca in USB - pentru QSY conteaza
    /// doar diferenta, nu ce arata afisajul. Null daca modul nu e SSB/DATA/CW/RTTY.
    /// </summary>
    public static long? QsyDial(long dialHz, RigMode mode, double fromAudioHz, double toAudioHz)
    {
        if (dialHz <= 0) return null;
        double delta = fromAudioHz - toAudioHz;
        if (IsUpperSideband(mode) || mode == RigMode.RttyU) return (long)Math.Round(dialHz + delta);
        if (IsLowerSideband(mode) || mode == RigMode.RttyL) return (long)Math.Round(dialHz - delta);
        return null;
    }

    /// <summary>"14.083.500" (MHz.kHz.Hz, ca pe afisajul statiei).</summary>
    public static string FormatFrequency(double hz)
    {
        long h = (long)Math.Round(hz);
        if (h <= 0) return "-";
        long mhz = h / 1_000_000, khz = h / 1000 % 1000, rest = h % 1000;
        return $"{mhz}.{khz:000}.{rest:000}";
    }

    /// <summary>Numele modului ca pe statie ("USB", "DATA-U", "RTTY-L"...).</summary>
    public static string ModeName(RigMode mode) => mode switch
    {
        RigMode.Lsb => "LSB",
        RigMode.Usb => "USB",
        RigMode.CwU => "CW-U",
        RigMode.CwL => "CW-L",
        RigMode.RttyL => "RTTY-L",
        RigMode.RttyU => "RTTY-U",
        RigMode.DataL => "DATA-L",
        RigMode.DataU => "DATA-U",
        RigMode.Am => "AM",
        RigMode.AmN => "AM-N",
        RigMode.Fm => "FM",
        RigMode.FmN => "FM-N",
        RigMode.DataFm => "DATA-FM",
        RigMode.DataFmN => "DATA-FM-N",
        RigMode.Psk => "PSK",
        _ => "?",
    };
}
