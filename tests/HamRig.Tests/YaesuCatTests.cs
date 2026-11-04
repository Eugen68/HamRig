namespace HamRig.Tests;

public class YaesuCatTests
{
    [Fact]
    public void Coduri()
    {
        Assert.Equal("FA014074000;", YaesuCat.SetFrequency(14_074_000));
        Assert.Equal("FA432100000;", YaesuCat.SetFrequency(432_100_000));
        Assert.Equal("MD0C;", YaesuCat.SetMode(RigMode.DataU));
        Assert.Equal("MD06;", YaesuCat.SetMode(RigMode.RttyL));
        Assert.Equal("TX1;", YaesuCat.Ptt(true));
        Assert.Equal(RigMode.CwL, YaesuCat.ModeFromCode('7'));
        Assert.Equal(RigMode.Unknown, YaesuCat.ModeFromCode('Z'));
        Assert.Throws<ArgumentException>(() => YaesuCat.SetMode(RigMode.Unknown));
    }

    [Theory]
    [InlineData("FA014083500", 14_083_500L, null, null)]
    [InlineData("MD0C", null, RigMode.DataU, null)]
    [InlineData("MD02", null, RigMode.Usb, null)]
    [InlineData("TX1", null, null, true)]
    [InlineData("TX0", null, null, false)]
    public void Raspunsuri(string answer, long? hz, RigMode? mode, bool? tx)
    {
        var a = YaesuCat.Parse(answer)!.Value;
        Assert.Equal(hz, a.FrequencyHz);
        Assert.Equal(mode, a.Mode);
        Assert.Equal(tx, a.Transmitting);
    }

    [Theory]
    [InlineData("?")]
    [InlineData("FA")]
    [InlineData("FA01408X500")]
    [InlineData("MD1C")]
    [InlineData("RM8123")]
    [InlineData("TX5")]
    public void RaspunsuriIgnorate(string answer) => Assert.Null(YaesuCat.Parse(answer));
}

public class YaesuCatRigTests
{
    private static (YaesuCatRig Rig, Dictionary<string, FakeFtx1> Ports) Create(RigSettings s, bool silent = false)
    {
        var ports = new Dictionary<string, FakeFtx1>();
        var rig = new YaesuCatRig(s, (port, baud) =>
        {
            var p = new FakeFtx1(port) { Silent = silent };
            ports[port] = p;
            return p;
        })
        { PollIntervalMs = 100, SilenceTimeoutMs = 300 };
        return (rig, ports);
    }

    private static async Task Wait(Func<bool> cond)
    {
        var limit = DateTime.UtcNow.AddSeconds(5);
        while (!cond())
        {
            if (DateTime.UtcNow > limit) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ConectareCitireSiComenzi()
    {
        var (rig, ports) = Create(new RigSettings { Kind = RigKind.YaesuCat, CatPort = "COM5", Ptt = PttMethod.Cat });
        var states = new List<RigState>();
        rig.StateChanged += s => { lock (states) states.Add(s); };
        await rig.ConnectAsync();
        var radio = ports["COM5"];
        Assert.True(radio.AutoInfo);                          // AI1 trimis
        await Wait(() => rig.State.Mode == RigMode.DataU);
        Assert.True(rig.State.Connected);
        Assert.Equal(14_083_500, rig.State.FrequencyHz);

        radio.TurnDial(14_085_000);                           // butonul VFO, raspuns in doua bucati
        await Wait(() => rig.State.FrequencyHz == 14_085_000);

        rig.SetFrequency(7_043_000);
        await Wait(() => rig.State.FrequencyHz == 7_043_000);
        Assert.Equal(7_043_000, radio.Frequency);

        rig.SetMode(RigMode.Lsb);
        await Wait(() => rig.State.Mode == RigMode.Lsb);

        rig.SetPtt(true);
        await Wait(() => rig.State.Transmitting);
        Assert.Contains("TX1", radio.Commands);

        rig.Disconnect();
        Assert.False(radio.Tx);                               // emisia oprita la deconectare
        Assert.True(radio.Disposed);
        Assert.False(rig.State.Connected);
    }

    [Fact]
    public async Task PttPeRtsAlDoileaPort()
    {
        var (rig, ports) = Create(new RigSettings { Kind = RigKind.YaesuCat, CatPort = "COM5", Ptt = PttMethod.Rts, PttPort = "COM6" });
        await rig.ConnectAsync();
        rig.SetPtt(true);
        Assert.True(ports["COM6"].Rts);
        Assert.False(ports["COM6"].Dtr);
        Assert.False(ports["COM5"].Rts);
        Assert.True(rig.State.Transmitting);
        ports["COM5"].TurnDial(14_090_000);                   // un raspuns CAT nu anuleaza emisia tinuta pe RTS
        await Task.Delay(50);
        Assert.True(rig.State.Transmitting);
        rig.SetPtt(false);
        Assert.False(ports["COM6"].Rts);
        Assert.False(rig.State.Transmitting);
        rig.Dispose();
        Assert.False(ports["COM6"].Rts);
        Assert.True(ports["COM6"].Disposed);
    }

    [Fact]
    public async Task StatiaNuRaspunde()
    {
        var (rig, ports) = Create(new RigSettings { Kind = RigKind.YaesuCat, CatPort = "COM5", ResponseTimeoutMs = 200 }, silent: true);
        var e = await Assert.ThrowsAsync<RigException>(() => rig.ConnectAsync());
        Assert.Contains("nu raspunde", e.Message);
        Assert.True(ports["COM5"].Disposed);
    }

    [Fact]
    public async Task LegaturaPierduta()
    {
        var (rig, ports) = Create(new RigSettings { Kind = RigKind.YaesuCat, CatPort = "COM5" });
        await rig.ConnectAsync();
        await Wait(() => rig.State.Connected);
        ports["COM5"].Silent = true;                          // statia oprita
        await Wait(() => !rig.State.Connected);
        Assert.Equal("statia nu raspunde", rig.State.StatusText);
        ports["COM5"].Silent = false;                         // pornita din nou: interogarea de rezerva o regaseste
        await Wait(() => rig.State.Connected);
        ports["COM5"].Fail();
        Assert.False(rig.State.Connected);
        rig.Dispose();
    }

    [Fact]
    public async Task FaraPort()
    {
        var (rig, _) = Create(new RigSettings { Kind = RigKind.YaesuCat });
        await Assert.ThrowsAsync<RigException>(() => rig.ConnectAsync());
    }
}
