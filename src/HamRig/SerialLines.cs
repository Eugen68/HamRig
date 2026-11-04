namespace HamRig;

/// <summary>
/// Porturile seriale deschise de o statie, fiecare o singura data: CAT, PTT si manipularea pot fi pe
/// acelasi port (la FTX-1: PTT pe RTS si FSK pe DTR ale celui de-al doilea port). Toate liniile
/// RTS/DTR pornesc si se opresc pe 0.
/// </summary>
internal sealed class SerialLines : IDisposable
{
    private readonly Func<string, int, ISerialLine> _open;
    private readonly Dictionary<string, ISerialLine> _lines = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public SerialLines(Func<string, int, ISerialLine> open) => _open = open;

    /// <summary>Portul (deschis acum, sau cel deja deschis - atunci viteza ramane cea de la prima deschidere).</summary>
    public ISerialLine Get(string port, int baud)
    {
        lock (_lock)
        {
            if (_lines.TryGetValue(port, out var l)) return l;
            l = _open(port, baud);
            try
            {
                l.Rts = false;
                l.Dtr = false;
            }
            catch
            {
                // unele drivere nu suporta setarea inainte de primul acces; nu e grav
            }
            _lines[port] = l;
            return l;
        }
    }

    public ISerialLine? Find(string? port)
    {
        if (string.IsNullOrWhiteSpace(port)) return null;
        lock (_lock) return _lines.TryGetValue(port!, out var l) ? l : null;
    }

    public IReadOnlyList<ISerialLine> All
    {
        get { lock (_lock) return _lines.Values.ToList(); }
    }

    public static void SetPin(ISerialLine line, SerialPin pin, bool on)
    {
        if (pin == SerialPin.Rts) line.Rts = on;
        else if (pin == SerialPin.Dtr) line.Dtr = on;
    }

    /// <summary>Toate liniile pe 0 (emisie si manipulare oprite).</summary>
    public void AllLow()
    {
        foreach (var l in All)
        {
            try
            {
                l.Rts = false;
                l.Dtr = false;
            }
            catch
            {
                // port pierdut
            }
        }
    }

    public void Dispose()
    {
        AllLow();
        List<ISerialLine> all;
        lock (_lock)
        {
            all = _lines.Values.ToList();
            _lines.Clear();
        }
        foreach (var l in all) l.Dispose();
    }
}
