using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HamRig;

/// <summary>Ce folosim din OmniRig (abstract, pentru teste fara Windows/OmniRig).</summary>
internal interface IOmniRigApi : IDisposable
{
    /// <summary>0 neconfigurat, 1 dezactivat, 2 port ocupat, 3 nu raspunde, 4 online.</summary>
    int Status { get; }
    string StatusText { get; }
    long Frequency { get; }
    int Mode { get; }
    bool Tx { get; }
    void SetFrequency(long hz);
    void SetMode(int mode);
}

/// <summary>Constantele OmniRig (biblioteca de tipuri OmniRig).</summary>
internal static class OmniRigConst
{
    public const int StOnline = 4;
    public const int PmFreq = 0x00000002, PmFreqA = 0x00000004;
    public const int PmRx = 0x00200000, PmTx = 0x00400000;
    public const int PmCwU = 0x00800000, PmCwL = 0x01000000, PmSsbU = 0x02000000, PmSsbL = 0x04000000;
    public const int PmDigU = 0x08000000, PmDigL = 0x10000000, PmAm = 0x20000000, PmFm = 0x40000000;

    public static RigMode ToMode(int pm) => pm switch
    {
        PmCwU => RigMode.CwU,
        PmCwL => RigMode.CwL,
        PmSsbU => RigMode.Usb,
        PmSsbL => RigMode.Lsb,
        PmDigU => RigMode.DataU,
        PmDigL => RigMode.DataL,
        PmAm => RigMode.Am,
        PmFm => RigMode.Fm,
        _ => RigMode.Unknown,
    };

    /// <summary>Modul OmniRig cel mai apropiat (OmniRig nu are RTTY separat: DIG).</summary>
    public static int? FromMode(RigMode m) => m switch
    {
        RigMode.CwU => PmCwU,
        RigMode.CwL => PmCwL,
        RigMode.Usb => PmSsbU,
        RigMode.Lsb => PmSsbL,
        RigMode.DataU or RigMode.RttyU or RigMode.Psk => PmDigU,
        RigMode.DataL or RigMode.RttyL => PmDigL,
        RigMode.Am or RigMode.AmN => PmAm,
        RigMode.Fm or RigMode.FmN or RigMode.DataFm or RigMode.DataFmN => PmFm,
        _ => null,
    };

    public static string StatusName(int s) => s switch
    {
        0 => "OmniRig: statia nu e configurata",
        1 => "OmniRig: dezactivat",
        2 => "OmniRig: portul e ocupat",
        3 => "OmniRig: statia nu raspunde",
        4 => "conectat (OmniRig)",
        _ => "OmniRig: stare " + s,
    };
}

/// <summary>OmniRig prin COM (legare tarzie, fara biblioteca de tipuri in proiect).</summary>
[SupportedOSPlatform("windows")]
internal sealed class OmniRigComApi : IOmniRigApi
{
    private readonly object _engine;
    private readonly dynamic _rig;

    public OmniRigComApi(int rigNumber)
    {
        var type = Type.GetTypeFromProgID("OmniRig.OmniRigX")
                   ?? throw new RigException("OmniRig nu e instalat (https://www.dxatlas.com/omnirig/).");
        _engine = Activator.CreateInstance(type) ?? throw new RigException("OmniRig nu porneste.");
        dynamic e = _engine;
        _rig = rigNumber == 2 ? e.Rig2 : e.Rig1;
    }

    public int Status => (int)_rig.Status;
    public string StatusText => (string)_rig.StatusStr;

    public long Frequency
    {
        get
        {
            long f = (int)_rig.Freq;
            return f > 0 ? f : (int)_rig.FreqA;
        }
    }

    public int Mode => (int)_rig.Mode;
    public bool Tx => (int)_rig.Tx == OmniRigConst.PmTx;

    public void SetFrequency(long hz)
    {
        int writable = (int)_rig.WriteableParams;
        if ((writable & OmniRigConst.PmFreq) != 0) _rig.Freq = (int)hz;
        else if ((writable & OmniRigConst.PmFreqA) != 0) _rig.FreqA = (int)hz;
        else _rig.SetSimplexMode((int)hz);
    }

    public void SetMode(int mode) => _rig.Mode = mode;

    public void Dispose()
    {
        try { Marshal.FinalReleaseComObject(_rig); } catch { }
        try { Marshal.FinalReleaseComObject(_engine); } catch { }
    }
}

/// <summary>
/// Statia prin OmniRig (Windows; OmniRig trebuie instalat si configurat pentru statie). OmniRig
/// detine portul, deci statia poate fi folosita in acelasi timp si de alte programe (logger,
/// WSJT-X). Toate apelurile COM se fac pe un singur fir dedicat; starea se citeste la
/// <see cref="RigSettings.OmniRigPollMs"/>. Prin OmniRig: frecventa si modul (fara RTTY separat -
/// DIG); PTT-ul si manipularea doar pe pinii portului de manipulare, nu prin OmniRig.
/// </summary>
public sealed class OmniRigRig : IRig
{
    private readonly RigSettings _settings;
    private readonly Func<int, IOmniRigApi> _create;
    private readonly Func<string, int, ISerialLine> _openLine;
    private readonly object _lock = new();
    private SerialLines? _lines;
    private ISerialLine? _key;
    private ISerialLine? _ptt;   // PTT pe un pin al portului de manipulare (null = prin OmniRig)
    private volatile bool _linePtt;
    private BlockingCollection<Action<IOmniRigApi>>? _queue;
    private Thread? _thread;
    private RigState _state = RigState.Disconnected();

    public OmniRigRig(RigSettings settings) : this(settings, CreateCom, (port, baud) => SerialPortLine.Open(port, baud))
    {
    }

    /// <param name="openLine">Deschiderea portului de manipulare (OmniRig nu face manipulare; pentru teste).</param>
    internal OmniRigRig(RigSettings settings, Func<int, IOmniRigApi> create, Func<string, int, ISerialLine>? openLine = null)
    {
        _settings = settings.Clone();
        _create = create;
        _openLine = openLine ?? ((port, baud) => SerialPortLine.Open(port, baud));
    }

    private static IOmniRigApi CreateCom(int rig) =>
        OperatingSystem.IsWindows() ? new OmniRigComApi(rig) : throw new RigException("OmniRig merge doar pe Windows.");

    public string Description => $"OmniRig Rig {_settings.OmniRigNumber}";

    public RigState State
    {
        get { lock (_lock) return _state; }
    }

    public event Action<RigState>? StateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Disconnect();
        // manipularea nu trece prin OmniRig: portul ei se deschide aici (nu poate fi portul CAT al lui OmniRig)
        SerialLines? lines = null;
        if (_settings.NeedsKeyPort)
        {
            lines = new SerialLines(_openLine);
            try
            {
                var port = lines.Get(_settings.KeyPort!, _settings.KeyBaud);
                lock (_lock)
                {
                    _lines = lines;
                    _key = _settings.HasKeying ? port : null;
                    _ptt = _settings.PttLine.Pin != SerialPin.None ? port : null;
                }
            }
            catch (RigException e)
            {
                lines.Dispose();
                throw new RigException("Portul de manipulare: " + e.Message, e);
            }
        }
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new BlockingCollection<Action<IOmniRigApi>>();
        var thread = new Thread(() => Run(queue, started)) { IsBackground = true, Name = "OmniRig" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        lock (_lock)
        {
            _queue = queue;
            _thread = thread;
        }
        thread.Start();
        try
        {
            await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            CloseLines();
            throw;
        }
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

    private void CloseLines()
    {
        SerialLines? lines;
        lock (_lock)
        {
            lines = _lines;
            _lines = null;
            _key = null;
            _ptt = null;
        }
        _linePtt = false;
        lines?.Dispose();
    }

    private void Run(BlockingCollection<Action<IOmniRigApi>> queue, TaskCompletionSource<bool> started)
    {
        IOmniRigApi api;
        try
        {
            api = _create(_settings.OmniRigNumber);
        }
        catch (Exception e)
        {
            lock (_lock) { if (_queue == queue) { _queue = null; _thread = null; } }
            started.TrySetException(e as RigException ?? new RigException("OmniRig nu porneste: " + e.Message, e));
            return;
        }
        try
        {
            Read(api);
            started.TrySetResult(true);
            var next = DateTime.UtcNow;
            while (!queue.IsCompleted)
            {
                int wait = Math.Max(0, (int)(next - DateTime.UtcNow).TotalMilliseconds);
                if (queue.TryTake(out var action, wait))
                {
                    try { action(api); } catch { /* comanda respinsa de OmniRig */ }
                    continue;
                }
                if (queue.IsCompleted) break;
                Read(api);
                next = DateTime.UtcNow.AddMilliseconds(Math.Max(50, _settings.OmniRigPollMs));
            }
        }
        finally
        {
            api.Dispose();
        }
    }

    private void Read(IOmniRigApi api)
    {
        RigState s;
        try
        {
            int status = api.Status;
            bool online = status == OmniRigConst.StOnline;
            s = online
                ? new RigState(true, api.Frequency, OmniRigConst.ToMode(api.Mode), _linePtt || api.Tx, OmniRigConst.StatusName(status))
                : new RigState(false, 0, RigMode.Unknown, false, OmniRigConst.StatusName(status));
        }
        catch (Exception e)
        {
            s = RigState.Disconnected("OmniRig: " + e.Message);
        }
        Publish(s);
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

    private void Post(Action<IOmniRigApi> action)
    {
        BlockingCollection<Action<IOmniRigApi>>? q;
        lock (_lock) q = _queue;
        try { q?.Add(action); } catch (InvalidOperationException) { }
    }

    public void Disconnect()
    {
        BlockingCollection<Action<IOmniRigApi>>? q;
        Thread? t;
        lock (_lock)
        {
            q = _queue;
            t = _thread;
            _queue = null;
            _thread = null;
        }
        CloseLines();
        if (q == null) return;
        q.CompleteAdding();
        t?.Join(2000);
        Publish(RigState.Disconnected());
    }

    public void SetFrequency(long hz)
    {
        if (hz > 0) Post(api => { api.SetFrequency(hz); Read(api); });
    }

    public void SetMode(RigMode mode)
    {
        if (OmniRigConst.FromMode(mode) is { } pm) Post(api => { api.SetMode(pm); Read(api); });
    }

    /// <summary>
    /// PTT doar pe pinul ales al portului de manipulare (RTS/DTR); OmniRig nu face PTT. Fara pin
    /// ales nu face nimic.
    /// </summary>
    public void SetPtt(bool transmit)
    {
        ISerialLine? line;
        lock (_lock) line = _ptt;
        if (line == null) return;
        SerialLines.SetPin(line, _settings.PttLine.Pin, transmit);
        _linePtt = transmit;
        var s = State with { Transmitting = transmit };
        if (s.Connected) Publish(s);
    }

    public void Dispose() => Disconnect();
}
