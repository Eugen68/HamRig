namespace HamRig;

/// <summary>Modul de lucru al statiei.</summary>
public enum RigMode
{
    Unknown,
    Lsb,
    Usb,
    CwU,
    CwL,
    RttyL,
    RttyU,
    DataL,
    DataU,
    Am,
    AmN,
    Fm,
    FmN,
    DataFm,
    DataFmN,
    Psk,
}

/// <summary>Un pin al portului serial: pentru PTT sau manipulare (FSK / CW).</summary>
public enum SerialPin
{
    None,
    Rts,
    Dtr,
}

/// <summary>Starea statiei, asa cum a raportat-o ultima data.</summary>
/// <param name="Connected">Statia raspunde.</param>
/// <param name="FrequencyHz">Frecventa VFO-ului activ (0 = necunoscuta).</param>
/// <param name="StatusText">Explicatia pentru utilizator ("conectat", "statia nu raspunde", ...).</param>
public sealed record RigState(bool Connected, long FrequencyHz, RigMode Mode, bool Transmitting, string StatusText)
{
    public static RigState Disconnected(string text = "deconectat") => new(false, 0, RigMode.Unknown, false, text);
}

/// <summary>O eroare la conectare (port inexistent sau ocupat, statia nu raspunde, OmniRig lipsa).</summary>
public sealed class RigException : Exception
{
    public RigException(string message) : base(message) { }
    public RigException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// O statie radio controlata prin CAT. Aceeasi interfata pentru conectarea directa (Yaesu) si pentru
/// OmniRig. Comenzile nu blocheaza; rezultatul vine prin <see cref="StateChanged"/> (pe un fir din
/// fundal - interfata trebuie sa treaca pe firul UI). Intr-o aplicatie, un singur obiect detine
/// statia si toate modulele il folosesc pe acela.
/// </summary>
public interface IRig : IDisposable
{
    /// <summary>"Yaesu CAT pe COM5", "OmniRig Rig 1".</summary>
    string Description { get; }

    RigState State { get; }

    /// <summary>Starea s-a schimbat (frecventa, mod, emisie, conexiune).</summary>
    event Action<RigState>? StateChanged;

    /// <summary>Deschide legatura si citeste starea; arunca <see cref="RigException"/> daca nu se poate.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Inchide legatura (emisia se opreste intai).</summary>
    void Disconnect();

    /// <summary>Frecventa VFO-ului activ (Hz).</summary>
    void SetFrequency(long hz);

    void SetMode(RigMode mode);

    /// <summary>
    /// Emisie / receptie: pe pinul ales in setari (RTS/DTR la portul CAT sau la portul de
    /// manipulare), iar daca nu e ales niciun pin, prin comanda CAT (Yaesu TX1; / OmniRig Tx).
    /// </summary>
    void SetPtt(bool transmit);

    /// <summary>Exista port de manipulare configurat (si deschis).</summary>
    bool CanKey { get; }

    /// <summary>
    /// Manipularea: pinul ales (RTS/DTR) al portului de manipulare - la RTTY FSK activ = Space (ca la
    /// FTX-1 / Android), la CW activ = cheie apasata. Temporizarea o face apelantul.
    /// </summary>
    void SetKey(bool down);
}
