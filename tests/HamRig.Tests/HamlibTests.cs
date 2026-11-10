using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HamRig.Tests;

/// <summary>Un rigctld simulat: protocolul extins Hamlib (+f, +m, +t, +F, +M, +T), pe un port TCP local.</summary>
internal sealed class FakeRigctld : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> _clients = new();
    private volatile bool _stopped;

    public FakeRigctld()
    {
        _listener.Start();
        new Thread(Accept) { IsBackground = true }.Start();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public long Frequency { get; set; } = 14_080_000;
    public string Mode { get; set; } = "RTTY";
    public bool Ptt { get; set; }
    /// <summary>Ca statia Dummy din Hamlib real: get_ptt / get_mode raspund RPRT -11 (indisponibil).</summary>
    public bool PttUnavailable { get; set; }
    public bool ModeUnavailable { get; set; }
    public HashSet<string> Modes { get; } = new() { "USB", "LSB", "RTTY", "RTTYR", "PKTUSB", "PKTLSB", "CW", "CWR", "AM", "FM" };
    public List<string> Commands { get; } = new();

    private void Accept()
    {
        while (!_stopped)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); } catch { return; }
            lock (_clients) _clients.Add(client);
            new Thread(() => Serve(client)) { IsBackground = true }.Start();
        }
    }

    private void Serve(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\n", AutoFlush = true };
            while (reader.ReadLine() is { } line)
            {
                if (line == "q") return;
                lock (Commands) Commands.Add(line);
                var parts = line.TrimStart('+').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string reply = parts[0] switch
                {
                    "f" => $"get_freq:\nFrequency: {Frequency}\nRPRT 0",
                    "m" when ModeUnavailable => "get_mode:\nRPRT -11",
                    "m" => $"get_mode:\nMode: {Mode}\nPassband: 2400\nRPRT 0",
                    "t" when PttUnavailable => "get_ptt:\nRPRT -11",
                    "t" => $"get_ptt:\nPTT: {(Ptt ? 1 : 0)}\nRPRT 0",
                    "F" => Set(() => Frequency = long.Parse(parts[1]), "set_freq: " + parts[1]),
                    "M" when Modes.Contains(parts[1]) => Set(() => Mode = parts[1], $"set_mode: {parts[1]} {parts[2]}"),
                    "M" => "set_mode: " + parts[1] + "\nRPRT -1",
                    "T" => Set(() => Ptt = parts[1] == "1", "set_ptt: " + parts[1]),
                    _ => "RPRT -4",
                };
                writer.WriteLine(reply);
            }
        }
        catch { /* clientul a plecat */ }
    }

    private static string Set(Action action, string echo)
    {
        action();
        return echo + "\nRPRT 0";
    }

    public bool Received(string command) { lock (Commands) return Commands.Contains(command); }

    public void Dispose()
    {
        _stopped = true;
        _listener.Stop();
        lock (_clients) foreach (var c in _clients) c.Dispose();
    }
}

public class HamlibTests
{
    private static RigSettings Settings(FakeRigctld server) => new()
    {
        Kind = RigKind.Hamlib, HamlibStartDaemon = false, HamlibHost = "127.0.0.1", HamlibPort = server.Port,
        HamlibPollMs = 50, ResponseTimeoutMs = 1000,
    };

    private static async Task Wait(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > limit) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task CitesteStareaSiUrmaresteSchimbarile()
    {
        using var server = new FakeRigctld();
        using var rig = Settings(server).CreateRig()!;
        Assert.IsType<HamlibRig>(rig);
        await rig.ConnectAsync();
        Assert.True(rig.State.Connected);
        Assert.Equal(14_080_000, rig.State.FrequencyHz);
        Assert.Equal(RigMode.RttyL, rig.State.Mode);
        // butonul statiei: citirea periodica vede frecventa si modul noi
        server.Frequency = 7_040_000; server.Mode = "RTTYR";
        await Wait(() => rig.State.FrequencyHz == 7_040_000 && rig.State.Mode == RigMode.RttyU);
    }

    [Fact]
    public async Task FrecventaModulSiPttPrinHamlib()
    {
        using var server = new FakeRigctld();
        using var rig = Settings(server).CreateRig()!;
        await rig.ConnectAsync();
        rig.SetFrequency(3_505_200);
        await Wait(() => rig.State.FrequencyHz == 3_505_200);
        Assert.True(server.Received("+F 3505200"));
        rig.SetMode(RigMode.DataU);
        await Wait(() => rig.State.Mode == RigMode.DataU);
        Assert.True(server.Received("+M PKTUSB 0"));
        // fara pin pe portul de manipulare, PTT-ul trece prin Hamlib
        rig.SetPtt(true);
        await Wait(() => server.Ptt);
        Assert.True(rig.State.Transmitting);
        rig.SetPtt(false);
        await Wait(() => !server.Ptt);
        Assert.False(rig.CanKey);
    }

    /// <summary>
    /// Gasit pe Hamlib 4.5.5 real (rigctld -m 1): get_ptt raspunde RPRT -11. Doar frecventa e obligatorie;
    /// modul si PTT-ul indisponibile nu opresc conectarea si nu se mai cer.
    /// </summary>
    [Fact]
    public async Task PttSiModulIndisponibileNuOprescConectarea()
    {
        using var server = new FakeRigctld { PttUnavailable = true, ModeUnavailable = true };
        using var rig = Settings(server).CreateRig()!;
        await rig.ConnectAsync();
        Assert.True(rig.State.Connected);
        Assert.Equal(RigMode.Unknown, rig.State.Mode);
        rig.SetPtt(true);                                  // set_ptt merge chiar daca get_ptt nu
        await Wait(() => server.Ptt);
        Assert.True(rig.State.Transmitting);
        await Task.Delay(200);
        int asked;
        lock (server.Commands) asked = server.Commands.Count(c => c == "+t");
        Assert.Equal(1, asked);                            // intrebat o singura data, apoi nu
        rig.SetPtt(false);
    }

    [Fact]
    public async Task ModRefuzatNuRupeLegatura()
    {
        using var server = new FakeRigctld();
        server.Modes.Remove("PKTFM");
        using var rig = Settings(server).CreateRig()!;
        await rig.ConnectAsync();
        rig.SetMode(RigMode.DataFm);     // Hamlib raspunde RPRT -1
        rig.SetFrequency(14_090_000);
        await Wait(() => rig.State.FrequencyHz == 14_090_000);
        Assert.True(rig.State.Connected);
        Assert.Equal(RigMode.RttyL, rig.State.Mode);
    }

    [Fact]
    public async Task RigctldOpritDuceLaDeconectat()
    {
        var server = new FakeRigctld();
        using var rig = Settings(server).CreateRig()!;
        await rig.ConnectAsync();
        server.Dispose();
        await Wait(() => !rig.State.Connected);
        Assert.Contains("rigctld", rig.State.StatusText);
    }

    [Fact]
    public async Task FaraRigctldConectareaSpuneDeCe()
    {
        int port;
        using (var free = new FakeRigctld()) port = free.Port;   // un port sigur nefolosit
        var settings = new RigSettings { Kind = RigKind.Hamlib, HamlibStartDaemon = false, HamlibPort = port, ResponseTimeoutMs = 500 };
        using var rig = settings.CreateRig()!;
        var e = await Assert.ThrowsAsync<RigException>(() => rig.ConnectAsync());
        Assert.Contains("rigctld", e.Message);
        Assert.False(rig.State.Connected);
    }

    [Fact]
    public async Task PttPePinulPortuluiDeManipulareNuPrinHamlib()
    {
        using var server = new FakeRigctld();
        var line = new FakeFtx1("COM9");
        var settings = Settings(server);
        settings.KeyPort = "COM9"; settings.KeyPortPttPin = SerialPin.Rts; settings.KeyPin = SerialPin.Dtr;
        using var rig = new HamlibRig(settings, (port, _) => { Assert.Equal("COM9", port); return line; }, null);
        await rig.ConnectAsync();
        Assert.True(rig.CanKey);
        rig.SetPtt(true);
        Assert.True(line.Rts);
        Assert.True(rig.State.Transmitting);
        rig.SetKey(true);
        Assert.True(line.Dtr);
        rig.SetPtt(false); rig.SetKey(false);
        await Task.Delay(150);
        Assert.False(server.Commands.Any(c => c.StartsWith("+T", StringComparison.Ordinal)));
    }

    [Fact]
    public void ModurileSeTraducInAmbeleSensuri()
    {
        foreach (RigMode mode in Enum.GetValues<RigMode>())
            if (HamlibProtocol.FromMode(mode) is { } name) Assert.Equal(mode, HamlibProtocol.ToMode(name));
        Assert.Equal(RigMode.RttyL, HamlibProtocol.ToMode("RTTY"));
        Assert.Equal(RigMode.RttyU, HamlibProtocol.ToMode("RTTYR"));
        Assert.Equal(RigMode.Unknown, HamlibProtocol.ToMode("FAX"));
        Assert.Null(HamlibProtocol.FromMode(RigMode.Unknown));
    }

    [Fact]
    public void ListaModelelorDinRigctl()
    {
        const string text = """
             Rig #  Mfg                    Model                   Version         Status      Macro
                 1  Hamlib                 Dummy                   20221128.0      Stable      RIG_MODEL_DUMMY
                 2  Hamlib                 NET rigctl              20230328.0      Stable      RIG_MODEL_NETRIGCTL
              1035  Yaesu                  FT-991                  20230105.0      Stable      RIG_MODEL_FT991
              1042  Yaesu                  FTDX-10                 20230105.21     Stable      RIG_MODEL_FTDX10
              3073  Icom                   IC-7300                 20230109.0      Stable      RIG_MODEL_IC7300
            """;
        var models = Hamlib.ParseModels(text);
        Assert.Equal(5, models.Count);
        Assert.Equal(new HamlibModel(1042, "Yaesu", "FTDX-10", "Stable"), models[3]);
        Assert.Equal("NET rigctl", models[1].Model);
        Assert.Equal("3073 · Icom IC-7300", models[4].ToString());
    }

    [Fact]
    public void ArgumenteleLuiRigctld()
    {
        var s = new RigSettings { Kind = RigKind.Hamlib, HamlibModel = 1042, CatPort = "COM5", CatBaud = 38400, HamlibPort = 4533, HamlibExtraArgs = "--set-conf=serial_handshake=None" };
        Assert.Equal("-m 1042 -r COM5 -s 38400 -T 127.0.0.1 -t 4533 --set-conf=serial_handshake=None", Hamlib.DaemonArguments(s));
    }

    [Fact]
    public void SetarileHamlibSuntVerificate()
    {
        Assert.Equal("Alege modelul statiei (Hamlib).", new RigSettings { Kind = RigKind.Hamlib }.Validate());
        Assert.Equal("Alege portul statiei (Hamlib).", new RigSettings { Kind = RigKind.Hamlib, HamlibModel = 1042 }.Validate());
        Assert.Null(new RigSettings { Kind = RigKind.Hamlib, HamlibModel = 1042, CatPort = "COM5" }.Validate());
        Assert.Null(new RigSettings { Kind = RigKind.Hamlib, HamlibModel = 1 }.Validate());              // Dummy: fara port
        Assert.Null(new RigSettings { Kind = RigKind.Hamlib, HamlibStartDaemon = false }.Validate());   // rigctld deja pornit
    }
}
