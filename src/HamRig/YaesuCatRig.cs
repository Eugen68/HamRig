using System.Text;

namespace HamRig;

/// <summary>
/// Conectare directa pe portul serial, CAT Yaesu (FTX-1 si statiile Yaesu noi). Ca in aplicatia
/// Android: 38400 baud, auto-informare pornita (AI1 - statia anunta singura orice schimbare), PTT
/// prin CAT sau pe linia RTS/DTR a celui de-al doilea port (la FTX-1, "Standard COM Port").
/// In plus, o interogare de rezerva a frecventei la fiecare secunda, care arata si daca statia
/// mai raspunde. Portul COM poate fi deschis de un singur program: pentru folosirea simultana cu
/// alte programe, <see cref="OmniRigRig"/>.
/// </summary>
public sealed class YaesuCatRig : IRig
{
    private readonly RigSettings _settings;
    private readonly Func<string, int, ISerialLine> _open;
    private readonly object _lock = new();
    private readonly StringBuilder _buffer = new();
    private ISerialLine? _cat;
    private SerialLines? _lines;
    private ISerialLine? _ptt;   // linia pentru PTT pe RTS/DTR (poate fi chiar portul CAT)
    private ISerialLine? _key;   // linia de manipulare (poate fi acelasi port ca PTT)
    private Timer? _poll;
    private RigState _state = RigState.Disconnected();
    private DateTime _lastAnswer;
    private TaskCompletionSource<bool>? _firstAnswer;
    private int _pollCount;
    private volatile bool _linePtt;   // PTT tinut pe RTS/DTR

    public YaesuCatRig(RigSettings settings) : this(settings, (port, baud) => SerialPortLine.Open(port, baud))
    {
    }

    /// <param name="open">Deschiderea unui port (pentru teste).</param>
    internal YaesuCatRig(RigSettings settings, Func<string, int, ISerialLine> open)
    {
        _settings = settings.Clone();
        _open = open;
    }

    /// <summary>Interogarea de rezerva / verificarea legaturii (ms).</summary>
    internal int PollIntervalMs { get; set; } = 1000;

    /// <summary>Fara niciun raspuns atata timp, statia e considerata deconectata (ms).</summary>
    internal int SilenceTimeoutMs { get; set; } = 3500;

    public string Description => $"Yaesu CAT pe {_settings.CatPort}";

    public RigState State
    {
        get { lock (_lock) return _state; }
    }

    public event Action<RigState>? StateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Disconnect();
        if (string.IsNullOrWhiteSpace(_settings.CatPort)) throw new RigException("Nu e ales portul CAT.");

        var lines = new SerialLines(_open);
        ISerialLine cat, ptt, key;
        try
        {
            cat = lines.Get(_settings.CatPort!, _settings.CatBaud);
            ptt = _settings.Ptt is PttMethod.Rts or PttMethod.Dtr && !string.IsNullOrWhiteSpace(_settings.PttPort)
                ? lines.Get(_settings.PttPort!, 4800)
                : cat;
            key = _settings.HasKeying ? lines.Get(_settings.KeyPort!, _settings.KeyBaud) : null!;
        }
        catch
        {
            lines.Dispose();
            throw;
        }

        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _cat = cat;
            _lines = lines;
            _ptt = ptt;
            _key = _settings.HasKeying ? key : null;
            _buffer.Clear();
            _firstAnswer = first;
            _state = new RigState(false, 0, RigMode.Unknown, false, "se conecteaza...");
        }
        cat.TextReceived += OnText;
        foreach (var l in lines.All) l.Failed += OnFailed;

        // ca in Android: auto-informare, apoi starea curenta
        cat.Write("AI1;FA;MD0;TX;");

        var done = await Task.WhenAny(first.Task, Task.Delay(_settings.ResponseTimeoutMs, cancellationToken)).ConfigureAwait(false);
        if (done != first.Task)
        {
            Disconnect();
            cancellationToken.ThrowIfCancellationRequested();
            throw new RigException($"Statia nu raspunde pe {_settings.CatPort} la {_settings.CatBaud} baud " +
                                   "(verifica portul - la FTX-1 primul, \"Enhanced\" - si viteza CAT din meniul statiei).");
        }
        _poll = new Timer(_ => Poll(), null, PollIntervalMs, PollIntervalMs);
    }

    public void Disconnect()
    {
        ISerialLine? cat;
        SerialLines? lines;
        lock (_lock)
        {
            cat = _cat;
            lines = _lines;
            _cat = null;
            _lines = null;
            _ptt = null;
            _key = null;
            _firstAnswer?.TrySetResult(false);
            _firstAnswer = null;
        }
        _poll?.Dispose();
        _poll = null;
        _linePtt = false;
        if (lines == null) return;
        // emisia si manipularea se opresc intai
        try
        {
            if (_settings.Ptt == PttMethod.Cat) cat?.Write("TX0;");
        }
        catch
        {
            // portul poate fi deja pierdut
        }
        if (cat != null) cat.TextReceived -= OnText;
        foreach (var l in lines.All) l.Failed -= OnFailed;
        lines.Dispose();   // RTS/DTR pe 0, apoi inchide
        Publish(RigState.Disconnected());
    }

    public bool CanKey
    {
        get { lock (_lock) return _key != null; }
    }

    public void SetKey(bool down)
    {
        ISerialLine? key;
        lock (_lock) key = _key;
        if (key != null) SerialLines.SetPin(key, _settings.KeyPin, down);
    }

    public void SetFrequency(long hz) => Send(YaesuCat.SetFrequency(hz) + "FA;");

    public void SetMode(RigMode mode) => Send(YaesuCat.SetMode(mode) + "MD0;");

    public void SetPtt(bool transmit)
    {
        switch (_settings.Ptt)
        {
            case PttMethod.Cat:
                Send(YaesuCat.Ptt(transmit) + "TX;");
                break;
            case PttMethod.Rts:
            case PttMethod.Dtr:
                ISerialLine? line;
                lock (_lock) line = _ptt;
                if (line == null) return;
                if (_settings.Ptt == PttMethod.Rts) line.Rts = transmit;
                else line.Dtr = transmit;
                // emisia pe linie nu apare neaparat in raspunsul CAT "TX": o stim noi, cat timp linia e activa
                _linePtt = transmit;
                Update(s => s with { Transmitting = transmit });
                break;
        }
    }

    private void Send(string commands)
    {
        ISerialLine? cat;
        lock (_lock) cat = _cat;
        cat?.Write(commands);
    }

    private void Poll()
    {
        bool silent;
        lock (_lock)
        {
            if (_cat == null) return;
            silent = (DateTime.UtcNow - _lastAnswer).TotalMilliseconds > SilenceTimeoutMs;
        }
        if (silent) Update(s => s with { Connected = false, StatusText = "statia nu raspunde" });
        // la fiecare a 5-a interogare si modul (AI1 il anunta oricum, aceasta e doar rezerva)
        Send(++_pollCount % 5 == 0 ? "FA;MD0;" : "FA;");
    }

    private void OnFailed(Exception e)
    {
        Update(s => s with { Connected = false, Transmitting = false, StatusText = "legatura pierduta: " + e.Message });
    }

    private void OnText(string text)
    {
        var answers = new List<string>();
        lock (_lock)
        {
            foreach (char c in text)
            {
                if (c == ';')
                {
                    answers.Add(_buffer.ToString());
                    _buffer.Clear();
                }
                else if (!char.IsWhiteSpace(c) && _buffer.Length < 256)
                {
                    _buffer.Append(c);
                }
            }
        }
        foreach (string a in answers)
        {
            if (YaesuCat.Parse(a) is not { } ans) continue;
            TaskCompletionSource<bool>? first;
            lock (_lock)
            {
                _lastAnswer = DateTime.UtcNow;
                first = ans.FrequencyHz != null ? _firstAnswer : null;
                if (first != null) _firstAnswer = null;
            }
            Update(s => s with
            {
                Connected = true,
                StatusText = "conectat",
                FrequencyHz = ans.FrequencyHz ?? s.FrequencyHz,
                Mode = ans.Mode ?? s.Mode,
                Transmitting = _linePtt || (ans.Transmitting ?? s.Transmitting),
            });
            first?.TrySetResult(true);
        }
    }

    private void Update(Func<RigState, RigState> change)
    {
        RigState next;
        lock (_lock)
        {
            if (_cat == null) return;
            next = change(_state);
            if (next == _state) return;
            _state = next;
        }
        StateChanged?.Invoke(next);
    }

    private void Publish(RigState s)
    {
        lock (_lock)
        {
            if (_state == s) return;
            _state = s;
        }
        StateChanged?.Invoke(s);
    }

    public void Dispose() => Disconnect();
}
