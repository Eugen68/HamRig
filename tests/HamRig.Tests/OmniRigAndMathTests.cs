namespace HamRig.Tests;

public class OmniRigTests
{
    private sealed class FakeOmni : IOmniRigApi
    {
        public int Status { get; set; } = 4;
        public string StatusText => "On-line";
        public long Frequency { get; set; } = 14_080_000;
        public int Mode { get; set; } = 0x08000000; // DIG-U
        public bool Tx { get; set; }
        public int CreatedOnThread { get; } = Environment.CurrentManagedThreadId;
        public List<int> CallThreads { get; } = new();
        public void SetFrequency(long hz) { CallThreads.Add(Environment.CurrentManagedThreadId); Frequency = hz; }
        public void SetMode(int mode) { CallThreads.Add(Environment.CurrentManagedThreadId); Mode = mode; }
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
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
    public async Task CitireComenziPeFirulOmniRig()
    {
        FakeOmni? omni = null;
        var rig = new OmniRigRig(new RigSettings { Kind = RigKind.OmniRig, OmniRigNumber = 2, OmniRigPollMs = 50 },
            n => { Assert.Equal(2, n); return omni = new FakeOmni(); });
        await rig.ConnectAsync();
        Assert.True(rig.State.Connected);
        Assert.Equal(14_080_000, rig.State.FrequencyHz);
        Assert.Equal(RigMode.DataU, rig.State.Mode);

        omni!.Frequency = 14_085_000;                         // schimbat din alt program / de pe statie
        await Wait(() => rig.State.FrequencyHz == 14_085_000);

        rig.SetFrequency(7_040_000);
        rig.SetMode(RigMode.RttyL);                           // OmniRig: DIG-L
        await Wait(() => rig.State.Mode == RigMode.DataL);
        rig.SetPtt(true);                                     // OmniRig nu face PTT (fara pin ales: nimic)
        await Task.Delay(100);
        Assert.False(rig.State.Transmitting);
        Assert.False(omni.Tx);
        Assert.Equal(7_040_000, omni.Frequency);
        Assert.All(omni.CallThreads, t => Assert.Equal(omni.CreatedOnThread, t)); // toate apelurile COM pe acelasi fir

        omni.Status = 3;
        await Wait(() => !rig.State.Connected);
        Assert.Contains("nu raspunde", rig.State.StatusText);
        rig.Dispose();
        Assert.True(omni.Disposed);
    }

    [Fact]
    public async Task OmniRigLipsa()
    {
        var rig = new OmniRigRig(new RigSettings { Kind = RigKind.OmniRig }, _ => throw new RigException("OmniRig nu e instalat"));
        var e = await Assert.ThrowsAsync<RigException>(() => rig.ConnectAsync());
        Assert.Contains("nu e instalat", e.Message);
    }
}

public class RigMathTests
{
    [Fact]
    public void FrecventaRfATonului()
    {
        Assert.Equal(14_085_625, RigMath.ToneToRf(14_083_500, RigMode.DataU, 2125));
        Assert.Equal(14_085_625, RigMath.ToneToRf(14_083_500, RigMode.Usb, 2125));
        Assert.Equal(7_040_875, RigMath.ToneToRf(7_043_000, RigMode.Lsb, 2125));
        Assert.Equal(14_030_100, RigMath.ToneToRf(14_030_000, RigMode.CwU, 700, 600));
        Assert.Null(RigMath.ToneToRf(14_083_500, RigMode.RttyL, 2125));
        Assert.Null(RigMath.ToneToRf(0, RigMode.Usb, 2125));
    }

    [Fact]
    public void Qsy()
    {
        // semnalul auzit la 1500 Hz adus pe Mark 2125: VFO-ul coboara cu 625 Hz in USB...
        Assert.Equal(14_082_875, RigMath.QsyDial(14_083_500, RigMode.DataU, 1500, 2125));
        // ... si urca in LSB
        Assert.Equal(7_043_625, RigMath.QsyDial(7_043_000, RigMode.Lsb, 1500, 2125));
        Assert.Null(RigMath.QsyDial(14_083_500, RigMode.Fm, 1500, 2125));
        // dupa QSY, tonul nou are aceeasi frecventa RF ca semnalul de dinainte
        long dial = RigMath.QsyDial(14_083_500, RigMode.DataU, 1500, 2125)!.Value;
        Assert.Equal(RigMath.ToneToRf(14_083_500, RigMode.DataU, 1500), RigMath.ToneToRf(dial, RigMode.DataU, 2125));
    }

    [Fact]
    public void Afisare()
    {
        Assert.Equal("14.083.500", RigMath.FormatFrequency(14_083_500));
        Assert.Equal("7.043.000", RigMath.FormatFrequency(7_043_000));
        Assert.Equal("-", RigMath.FormatFrequency(0));
        Assert.Equal("DATA-U", RigMath.ModeName(RigMode.DataU));
    }

    [Fact]
    public void Setari()
    {
        Assert.Null(new RigSettings().CreateRig());
        Assert.IsType<YaesuCatRig>(new RigSettings { Kind = RigKind.YaesuCat, CatPort = "COM5" }.CreateRig());
        Assert.IsType<OmniRigRig>(new RigSettings { Kind = RigKind.OmniRig }.CreateRig());
        var json = System.Text.Json.JsonSerializer.Serialize(new RigSettings { Kind = RigKind.YaesuCat, CatPort = "COM5", KeyPort = "COM6", KeyPortPttPin = SerialPin.Rts, KeyPin = SerialPin.Dtr });
        var back = System.Text.Json.JsonSerializer.Deserialize<RigSettings>(json)!;
        Assert.Equal("COM6", back.KeyPort);
        Assert.Equal(SerialPin.Rts, back.KeyPortPttPin);
        Assert.Equal(SerialPin.Dtr, back.KeyPin);
        Assert.Equal(38400, back.CatBaud);
    }
}
