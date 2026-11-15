using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace HamRig;

/// <summary>
/// Protocolul text al lui rigctld (Hamlib), in forma "extinsa": comanda incepe cu '+' si raspunsul se
/// termina mereu cu "RPRT n" (0 = bine, negativ = eroare Hamlib), deci fiecare raspuns are un sfarsit clar.
/// </summary>
internal static class HamlibProtocol
{
    /// <summary>Modul Hamlib -&gt; modul nostru. RTTY e "normal" (ca LSB, RTTY-L la Yaesu), RTTYR inversat (RTTY-U).</summary>
    public static RigMode ToMode(string mode) => mode.Trim().ToUpperInvariant() switch
    {
        "USB" => RigMode.Usb,
        "LSB" => RigMode.Lsb,
        "CW" => RigMode.CwU,
        "CWR" => RigMode.CwL,
        "RTTY" => RigMode.RttyL,
        "RTTYR" => RigMode.RttyU,
        "PKTUSB" => RigMode.DataU,
        "PKTLSB" => RigMode.DataL,
        "PKTFM" => RigMode.DataFm,
        "PKTFMN" => RigMode.DataFmN,
        "AM" => RigMode.Am,
        "AMN" => RigMode.AmN,
        "FM" or "WFM" => RigMode.Fm,
        "FMN" => RigMode.FmN,
        "PSK" or "PSKR" => RigMode.Psk,
        _ => RigMode.Unknown,
    };

    /// <summary>Modul nostru -&gt; Hamlib, sau null daca nu are corespondent.</summary>
    public static string? FromMode(RigMode mode) => mode switch
    {
        RigMode.Usb => "USB",
        RigMode.Lsb => "LSB",
        RigMode.CwU => "CW",
        RigMode.CwL => "CWR",
        RigMode.RttyL => "RTTY",
        RigMode.RttyU => "RTTYR",
        RigMode.DataU => "PKTUSB",
        RigMode.DataL => "PKTLSB",
        RigMode.DataFm => "PKTFM",
        RigMode.DataFmN => "PKTFMN",
        RigMode.Am => "AM",
        RigMode.AmN => "AMN",
        RigMode.Fm => "FM",
        RigMode.FmN => "FMN",
        RigMode.Psk => "PSK",
        _ => null,
    };

    /// <summary>Valoarea "Cheie: valoare" dintr-un raspuns extins, sau null.</summary>
    public static string? Value(IReadOnlyList<string> lines, string key)
    {
        foreach (string line in lines)
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }
        return null;
    }

    /// <summary>Codul din "RPRT n", sau null daca linia nu e de sfarsit.</summary>
    public static int? ReportCode(string line)
    {
        var m = Regex.Match(line, @"^RPRT\s+(-?\d+)\s*$");
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }
}

/// <summary>Legatura TCP cu rigctld: o comanda, un raspuns. Folosita de pe un singur fir.</summary>
internal sealed class RigctlConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    private RigctlConnection(TcpClient client)
    {
        _client = client;
        var stream = client.GetStream();
        _reader = new StreamReader(stream, Encoding.ASCII);
        _writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\n", AutoFlush = true };
    }

    /// <param name="timeoutMs">Limita legaturii TCP.</param>
    /// <param name="replyTimeoutMs">Limita unui raspuns: rigctld reincearca singur statia (la Icom pana la cateva secunde).</param>
    public static async Task<RigctlConnection> OpenAsync(string host, int port, int timeoutMs, CancellationToken cancellationToken, int replyTimeoutMs = 0)
    {
        int reply = Math.Max(timeoutMs, replyTimeoutMs);
        var client = new TcpClient { NoDelay = true, ReceiveTimeout = reply, SendTimeout = reply };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return new RigctlConnection(client);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new RigException($"rigctld nu raspunde pe {host}:{port}" + (e is SocketException s ? $" ({s.SocketErrorCode})" : "") + ".", e);
        }
    }

    /// <summary>
    /// Trimite o comanda (fara '+') si intoarce liniile raspunsului extins, fara "RPRT". Arunca
    /// <see cref="RigException"/> la eroare Hamlib (RPRT negativ), timeout sau legatura pierduta.
    /// </summary>
    public IReadOnlyList<string> Command(string command)
    {
        try
        {
            _writer.WriteLine("+" + command);
            var lines = new List<string>();
            while (true)
            {
                string? line = _reader.ReadLine() ?? throw new RigException("rigctld a inchis legatura.");
                if (HamlibProtocol.ReportCode(line) is { } code)
                {
                    if (code < 0) throw new RigException($"Hamlib a refuzat \"{command}\" (RPRT {code}).");
                    return lines;
                }
                lines.Add(line);
            }
        }
        catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
        {
            throw new RigException($"Statia nu raspunde: rigctld nu a primit raspuns de la statie in {_client.ReceiveTimeout / 1000.0:0.#} s.", e);
        }
        catch (IOException e)
        {
            throw new RigException("Legatura cu rigctld s-a pierdut: " + e.Message, e);
        }
    }

    public void Dispose()
    {
        try { _writer.WriteLine("q"); } catch { /* deja inchisa */ }
        _client.Dispose();
    }
}

/// <summary>Un model de statie din lista Hamlib (<c>rigctl -l</c>).</summary>
public sealed record HamlibModel(int Id, string Manufacturer, string Model, string Status)
{
    public override string ToString() => $"{Id} · {Manufacturer} {Model}";
}

/// <summary>Gasirea programelor Hamlib si lista modelelor.</summary>
public static class Hamlib
{
    /// <summary>Portul implicit al lui rigctld.</summary>
    public const int DefaultPort = 4532;

    /// <summary>
    /// Versiunea recomandata: 4.7.2 are doua corecturi de securitate in rigctld (CVE-2026-54634,
    /// GHSA-f72v-7gmh-m9mj) si corecturi pentru FTX-1 (model 1051, din Hamlib 4.7).
    /// </summary>
    public static readonly Version RecommendedVersion = new(4, 7, 2);

    /// <summary>Versiunea Hamlib din textul lui <c>rigctld --version</c> ("rigctld Hamlib 4.5.5 ..."), sau null.</summary>
    public static Version? ParseVersion(string text)
    {
        var m = Regex.Match(text, @"Hamlib\s+(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return null;
        int Part(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        return new Version(Part(1), Part(2), Part(3));
    }

    /// <summary>Versiunea lui rigctld (<c>rigctld --version</c>), sau null daca nu se poate afla.</summary>
    public static Version? GetVersion(string rigctldPath)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(rigctldPath, "--version")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            });
            if (p == null) return null;
            string text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return ParseVersion(text);
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// rigctld.exe: in PATH sau intr-un folder Hamlib din Program Files (instalarea oficiala pune
    /// <c>hamlib-w64-4.x\bin</c>). Null daca nu se gaseste.
    /// </summary>
    public static string? FindRigctld()
    {
        string exe = OperatingSystem.IsWindows() ? "rigctld.exe" : "rigctld";
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { string p = Path.Combine(dir.Trim(), exe); if (File.Exists(p)) return p; } catch { }
        }
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string folder = Environment.GetFolderPath(root);
            if (folder.Length == 0 || !Directory.Exists(folder)) continue;
            try
            {
                var found = Directory.GetDirectories(folder, "hamlib*")
                    .Select(d => Path.Combine(d, "bin", exe)).Where(File.Exists)
                    .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (found != null) return found;
            }
            catch { /* fara drept de citire */ }
        }
        return null;
    }

    /// <summary>
    /// Modelele stiute de Hamlib: <c>rigctl -l</c>, programul de langa <paramref name="rigctldPath"/>.
    /// Arunca <see cref="RigException"/> daca nu se poate rula.
    /// </summary>
    public static IReadOnlyList<HamlibModel> ListModels(string rigctldPath)
    {
        string dir = Path.GetDirectoryName(rigctldPath) ?? "";
        string rigctl = Path.Combine(dir, OperatingSystem.IsWindows() ? "rigctl.exe" : "rigctl");
        if (!File.Exists(rigctl)) throw new RigException("Nu gasesc rigctl langa rigctld (" + dir + ").");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(rigctl, "-l")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            }) ?? throw new RigException("rigctl nu porneste.");
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return ParseModels(text);
        }
        catch (Exception e) when (e is not RigException)
        {
            throw new RigException("rigctl -l: " + e.Message, e);
        }
    }

    /// <summary>
    /// Iesirea lui <c>rigctl -l</c>: un rand pe model, coloanele (numar, producator, model, versiune,
    /// stare, macro) despartite de cel putin doua spatii. Antetul si randurile nerecunoscute se sar.
    /// </summary>
    public static IReadOnlyList<HamlibModel> ParseModels(string text)
    {
        var models = new List<HamlibModel>();
        foreach (string raw in text.Replace("\r", "").Split('\n'))
        {
            var cols = Regex.Split(raw.Trim(), @"\s{2,}");
            if (cols.Length < 3 || !int.TryParse(cols[0], NumberStyles.None, CultureInfo.InvariantCulture, out int id)) continue;
            models.Add(new HamlibModel(id, cols[1], cols[2], cols.Length > 4 ? cols[4] : ""));
        }
        return models;
    }

    /// <summary>Argumentele pentru rigctld (model, port, viteza, adresa locala, plus argumentele suplimentare).</summary>
    public static string DaemonArguments(RigSettings s)
    {
        var args = new StringBuilder();
        args.Append(CultureInfo.InvariantCulture, $"-m {s.HamlibModel}");
        if (!string.IsNullOrWhiteSpace(s.CatPort)) args.Append(CultureInfo.InvariantCulture, $" -r {s.CatPort!.Trim()} -s {s.CatBaud}");
        args.Append(CultureInfo.InvariantCulture, $" -T {s.HamlibHost} -t {s.HamlibPort}");
        if (!string.IsNullOrWhiteSpace(s.HamlibExtraArgs)) args.Append(' ').Append(s.HamlibExtraArgs!.Trim());
        return args.ToString();
    }
}

/// <summary>
/// Statia prin Hamlib, legata la <c>rigctld</c> pe TCP (implicit 127.0.0.1:4532). Optional, porneste
/// singura rigctld cu modelul, portul si viteza din setari si il opreste la deconectare; altfel se
/// leaga la unul deja pornit (de ex. folosit in paralel de WSJT-X). Frecventa, modul si emisia se
/// citesc la <see cref="RigSettings.HamlibPollMs"/>. PTT: pe pinul ales al portului de manipulare,
/// altfel prin Hamlib (comanda T). Manipularea (FSK / CW): doar pe portul de manipulare.
/// </summary>
public sealed class HamlibRig : IRig
{
    private readonly RigSettings _settings;
    private readonly Func<string, int, ISerialLine> _openLine;
    private readonly Func<RigSettings, Process?>? _startDaemon;
    private readonly object _lock = new();
    private SerialLines? _lines;
    private ISerialLine? _key, _ptt;
    private volatile bool _linePtt;
    private Process? _daemon;
    private System.Collections.Concurrent.BlockingCollection<Action<RigctlConnection>>? _queue;
    private Thread? _thread;
    private RigState _state = RigState.Disconnected();

    public HamlibRig(RigSettings settings) : this(settings, (port, baud) => SerialPortLine.Open(port, baud), null)
    {
    }

    /// <param name="startDaemon">Pornirea lui rigctld (pentru teste: null = procesul real).</param>
    internal HamlibRig(RigSettings settings, Func<string, int, ISerialLine> openLine, Func<RigSettings, Process?>? startDaemon)
    {
        _settings = settings.Clone();
        _openLine = openLine;
        _startDaemon = startDaemon;
    }

    public string Description => _settings.HamlibStartDaemon
        ? $"Hamlib model {_settings.HamlibModel} pe {_settings.CatPort}"
        : $"Hamlib rigctld {_settings.HamlibHost}:{_settings.HamlibPort}";

    public RigState State
    {
        get { lock (_lock) return _state; }
    }

    public event Action<RigState>? StateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Disconnect();
        _noMode = _noPtt = false;
        OpenKeyPort();
        try
        {
            if (_settings.HamlibStartDaemon) StartDaemon();
            var connection = await ConnectWithRetryAsync(cancellationToken).ConfigureAwait(false);
            RigState first;
            try { first = Read(connection); }
            catch (RigException e)
            {
                connection.Dispose();
                throw new RigException(e.Message + FirstReadHint(), e);
            }
            catch { connection.Dispose(); throw; }
            var queue = new System.Collections.Concurrent.BlockingCollection<Action<RigctlConnection>>();
            var thread = new Thread(() => Run(connection, queue)) { IsBackground = true, Name = "Hamlib" };
            lock (_lock) { _queue = queue; _thread = thread; }
            Publish(first);
            thread.Start();
        }
        catch
        {
            StopDaemon();
            CloseLines();
            throw;
        }
    }

    /// <summary>Asteptarea unui raspuns: rigctld reincearca singur o statie tacuta (Icom: ~3 x 1 s), apoi raspunde cu eroare.</summary>
    public const int ReplyTimeoutMs = 6000;

    /// <summary>rigctld pornit de noi asculta abia dupa ce deschide statia: se reincearca cateva secunde.</summary>
    private async Task<RigctlConnection> ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        int timeout = Math.Max(500, _settings.ResponseTimeoutMs);
        var limit = DateTime.UtcNow.AddMilliseconds(_settings.HamlibStartDaemon ? Math.Max(timeout, 8000) : timeout);
        while (true)
        {
            try
            {
                return await RigctlConnection.OpenAsync(_settings.HamlibHost, _settings.HamlibPort, timeout, cancellationToken, ReplyTimeoutMs).ConfigureAwait(false);
            }
            catch (RigException) when (DateTime.UtcNow < limit && DaemonAlive())
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (RigException e) when (_daemon is { HasExited: true } d)
            {
                throw new RigException($"rigctld s-a oprit (cod {d.ExitCode}): {DaemonError()}", e);
            }
        }
    }

    /// <summary>Prima citire a esuat: ce e de verificat (statia nu raspunde aproape mereu din port/viteza/adresa).</summary>
    private string FirstReadHint()
    {
        string hint = _settings.HamlibStartDaemon
            ? $" Verifica: portul {_settings.CatPort} (portul CAT al statiei; la statiile cu doua porturi USB de obicei primul), viteza {_settings.CatBaud} = viteza CAT/CI-V din meniul statiei, modelul {_settings.HamlibModel}, adresa CI-V (Icom) si ca statia e pornita."
            : " Verifica rigctld-ul pornit separat (modelul, portul si viteza lui).";
        string log;
        lock (_daemonLog) log = _daemonLog.Count > 0 ? " rigctld: " + string.Join(" ", _daemonLog) : "";
        return hint + log;
    }

    private bool DaemonAlive()
    {
        lock (_lock) return _daemon == null ? !_settings.HamlibStartDaemon : !_daemon.HasExited;
    }

    // ultimele mesaje ale lui rigctld (citite in fundal, ca iesirea lui sa nu se blocheze)
    private readonly Queue<string> _daemonLog = new();

    private string DaemonError()
    {
        lock (_daemonLog) return _daemonLog.Count > 0 ? string.Join(" ", _daemonLog) : "fara mesaj";
    }

    private void Capture(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_daemonLog)
        {
            _daemonLog.Enqueue(line.Trim());
            while (_daemonLog.Count > 5) _daemonLog.Dequeue();
        }
    }

    private void StartDaemon()
    {
        Process? process;
        if (_startDaemon != null) process = _startDaemon(_settings);
        else
        {
            string path = string.IsNullOrWhiteSpace(_settings.HamlibPath) ? Hamlib.FindRigctld()
                          ?? throw new RigException("Nu gasesc rigctld. Instaleaza Hamlib (https://github.com/Hamlib/Hamlib/releases) sau alege calea catre rigctld.exe.")
                          : _settings.HamlibPath!;
            if (!File.Exists(path)) throw new RigException("rigctld nu exista: " + path);
            try
            {
                process = Process.Start(new ProcessStartInfo(path, Hamlib.DaemonArguments(_settings))
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                });
            }
            catch (Exception e) { throw new RigException("rigctld nu porneste: " + e.Message, e); }
            if (process != null)
            {
                process.ErrorDataReceived += (_, e) => Capture(e.Data);
                process.OutputDataReceived += (_, e) => Capture(e.Data);
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
            }
        }
        lock (_lock) _daemon = process;
    }

    private void StopDaemon()
    {
        Process? p;
        lock (_lock) { p = _daemon; _daemon = null; }
        if (p == null) return;
        try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(2000); } } catch { }
        p.Dispose();
    }

    private void OpenKeyPort()
    {
        if (!_settings.NeedsKeyPort) return;
        var lines = new SerialLines(_openLine);
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

    private void CloseLines()
    {
        SerialLines? lines;
        lock (_lock) { lines = _lines; _lines = null; _key = null; _ptt = null; }
        _linePtt = false;
        lines?.Dispose();
    }

    // multe statii (si Dummy) nu raporteaza PTT-ul sau modul: Hamlib raspunde RPRT -11 (indisponibil).
    // Atunci nu le mai cerem - doar frecventa e obligatorie.
    private bool _noMode, _noPtt;

    private RigState Read(RigctlConnection c)
    {
        var freq = c.Command("f");
        long hz = (long)Math.Round(double.Parse(HamlibProtocol.Value(freq, "Frequency") ?? "0", CultureInfo.InvariantCulture));
        var rigMode = RigMode.Unknown;
        if (!_noMode)
        {
            try { rigMode = HamlibProtocol.ToMode(HamlibProtocol.Value(c.Command("m"), "Mode") ?? ""); }
            catch (RigException e) when (e.Message.StartsWith("Hamlib a refuzat", StringComparison.Ordinal)) { _noMode = true; }
        }
        bool tx = false;
        if (!_noPtt)
        {
            try { tx = HamlibProtocol.Value(c.Command("t"), "PTT") is { } p && p != "0"; }
            catch (RigException e) when (e.Message.StartsWith("Hamlib a refuzat", StringComparison.Ordinal)) { _noPtt = true; }
        }
        return new RigState(true, hz, rigMode, _linePtt || tx, "conectat (Hamlib)");
    }

    private void Run(RigctlConnection connection, System.Collections.Concurrent.BlockingCollection<Action<RigctlConnection>> queue)
    {
        try
        {
            var next = DateTime.UtcNow.AddMilliseconds(Math.Max(50, _settings.HamlibPollMs));
            while (!queue.IsCompleted)
            {
                int wait = Math.Max(0, (int)(next - DateTime.UtcNow).TotalMilliseconds);
                if (queue.TryTake(out var action, wait))
                {
                    try { action(connection); }
                    catch (RigException e) when (e.InnerException is null && e.Message.StartsWith("Hamlib a refuzat", StringComparison.Ordinal))
                    {
                        // comanda respinsa (de ex. mod nesuportat): legatura ramane
                    }
                    continue;
                }
                if (queue.IsCompleted) break;
                Publish(Read(connection));
                next = DateTime.UtcNow.AddMilliseconds(Math.Max(50, _settings.HamlibPollMs));
            }
        }
        catch (Exception e)
        {
            Publish(RigState.Disconnected(e is RigException ? e.Message : "Hamlib: " + e.Message));
        }
        finally
        {
            connection.Dispose();
        }
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

    private void Post(Action<RigctlConnection> action)
    {
        System.Collections.Concurrent.BlockingCollection<Action<RigctlConnection>>? q;
        lock (_lock) q = _queue;
        try { q?.Add(action); } catch (InvalidOperationException) { }
    }

    public void Disconnect()
    {
        System.Collections.Concurrent.BlockingCollection<Action<RigctlConnection>>? q;
        Thread? t;
        lock (_lock) { q = _queue; t = _thread; _queue = null; _thread = null; }
        if (q != null)
        {
            if (_ptt == null && _linePtt) Post(c => c.Command("T 0"));
            q.CompleteAdding();
            t?.Join(2000);
        }
        CloseLines();
        StopDaemon();
        Publish(RigState.Disconnected());
    }

    public void SetFrequency(long hz)
    {
        if (hz > 0) Post(c => { c.Command(string.Create(CultureInfo.InvariantCulture, $"F {hz}")); Publish(Read(c)); });
    }

    public void SetMode(RigMode mode)
    {
        // latimea 0 = cea implicita a statiei pentru modul ales
        if (HamlibProtocol.FromMode(mode) is { } m) Post(c => { c.Command($"M {m} 0"); Publish(Read(c)); });
    }

    public void SetPtt(bool transmit)
    {
        ISerialLine? line;
        lock (_lock) line = _ptt;
        _linePtt = transmit;
        if (line != null) SerialLines.SetPin(line, _settings.PttLine.Pin, transmit);
        else Post(c => c.Command(transmit ? "T 1" : "T 0"));
        var s = State with { Transmitting = transmit };
        if (s.Connected) Publish(s);
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

    public void Dispose() => Disconnect();
}
