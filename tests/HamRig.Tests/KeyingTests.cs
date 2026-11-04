namespace HamRig.Tests;

public class KeyingTests
{
    private static (YaesuCatRig Rig, Dictionary<string, FakeFtx1> Ports, Dictionary<string, int> Opens) Create(RigSettings s)
    {
        var ports = new Dictionary<string, FakeFtx1>();
        var opens = new Dictionary<string, int>();
        var rig = new YaesuCatRig(s, (port, baud) =>
        {
            opens[port] = opens.GetValueOrDefault(port) + 1;
            return ports[port] = new FakeFtx1(port);
        });
        return (rig, ports, opens);
    }

    [Fact]
    public async Task PttSiManipularePeAcelasiPort()
    {
        // ca FTX-1 / Android: al doilea port, PTT pe RTS, FSK pe DTR
        var (rig, ports, opens) = Create(new RigSettings
        {
            Kind = RigKind.YaesuCat, CatPort = "COM5",
            KeyPort = "COM6", KeyPortPttPin = SerialPin.Rts, KeyPin = SerialPin.Dtr,
        });
        await rig.ConnectAsync();
        Assert.Equal(1, opens["COM6"]);                       // portul deschis o singura data
        Assert.True(rig.CanKey);

        rig.SetPtt(true);
        rig.SetKey(true);
        Assert.True(ports["COM6"].Rts);
        Assert.True(ports["COM6"].Dtr);
        rig.SetKey(false);
        Assert.False(ports["COM6"].Dtr);
        Assert.True(ports["COM6"].Rts);                       // PTT ramane

        rig.SetKey(true);
        rig.Disconnect();
        Assert.False(ports["COM6"].Rts);                      // totul pe 0 la deconectare
        Assert.False(ports["COM6"].Dtr);
        Assert.True(ports["COM6"].Disposed);
    }

    [Fact]
    public async Task ManipularePePortSeparat()
    {
        var (rig, ports, _) = Create(new RigSettings
        {
            Kind = RigKind.YaesuCat, CatPort = "COM5", KeyPort = "COM7", KeyBaud = 9600, KeyPin = SerialPin.Rts,
        });
        await rig.ConnectAsync();
        rig.SetKey(true);
        Assert.True(ports["COM7"].Rts);
        Assert.False(ports["COM5"].Rts);
        rig.Dispose();
        Assert.True(ports["COM7"].Disposed);
    }

    [Fact]
    public async Task FaraManipulare()
    {
        var (rig, ports, _) = Create(new RigSettings { Kind = RigKind.YaesuCat, CatPort = "COM5", KeyPort = "COM7" }); // fara pin
        await rig.ConnectAsync();
        Assert.False(rig.CanKey);
        rig.SetKey(true);                                     // nu face nimic
        Assert.False(ports.ContainsKey("COM7"));
        rig.Dispose();
    }

    [Fact]
    public void Validare()
    {
        // PTT intr-un singur loc
        Assert.NotNull(new RigSettings { Kind = RigKind.YaesuCat, CatPort = "COM5", CatPttPin = SerialPin.Rts, KeyPort = "COM6", KeyPortPttPin = SerialPin.Rts }.Validate());
        // PTT si manipularea pe acelasi pin
        Assert.NotNull(new RigSettings { Kind = RigKind.YaesuCat, CatPort = "COM5", KeyPort = "COM6", KeyPortPttPin = SerialPin.Dtr, KeyPin = SerialPin.Dtr }.Validate());
        Assert.NotNull(new RigSettings { Kind = RigKind.YaesuCat, CatPort = "COM5", CatPttPin = SerialPin.Dtr, KeyPort = "COM5", KeyPin = SerialPin.Dtr }.Validate());
        // pin fara port
        Assert.NotNull(new RigSettings { Kind = RigKind.YaesuCat, CatPort = "COM5", KeyPin = SerialPin.Dtr }.Validate());
        Assert.NotNull(new RigSettings { Kind = RigKind.YaesuCat }.Validate());
        // bune
        Assert.Null(new RigSettings { Kind = RigKind.YaesuCat, CatPort = "COM5", KeyPort = "COM6", KeyPortPttPin = SerialPin.Rts, KeyPin = SerialPin.Dtr }.Validate());
        Assert.Null(new RigSettings { Kind = RigKind.YaesuCat, CatPort = "COM5", CatPttPin = SerialPin.Rts, KeyPort = "COM6", KeyPin = SerialPin.Rts }.Validate());
        Assert.Null(new RigSettings { Kind = RigKind.OmniRig, CatPttPin = SerialPin.Rts, KeyPort = "COM6", KeyPortPttPin = SerialPin.Rts }.Validate()); // la OmniRig pinul CAT nu conteaza
        Assert.Null(new RigSettings().Validate());
    }

    [Fact]
    public void UndeSeFacePtt()
    {
        Assert.Equal(("COM5", SerialPin.Dtr), new RigSettings { Kind = RigKind.YaesuCat, CatPort = "COM5", CatPttPin = SerialPin.Dtr }.PttLine);
        Assert.Equal(("COM6", SerialPin.Rts), new RigSettings { Kind = RigKind.YaesuCat, CatPort = "COM5", KeyPort = "COM6", KeyPortPttPin = SerialPin.Rts }.PttLine);
        Assert.Equal(((string?)null, SerialPin.None), new RigSettings { Kind = RigKind.YaesuCat, CatPort = "COM5" }.PttLine);
        Assert.Equal(((string?)null, SerialPin.None), new RigSettings { Kind = RigKind.OmniRig, CatPort = "COM5", CatPttPin = SerialPin.Rts }.PttLine);
    }

    [Fact]
    public async Task PttPePinulPortuluiCat()
    {
        var (rig, ports, _) = Create(new RigSettings { Kind = RigKind.YaesuCat, CatPort = "COM5", CatPttPin = SerialPin.Dtr });
        await rig.ConnectAsync();
        rig.SetPtt(true);
        Assert.True(ports["COM5"].Dtr);
        Assert.DoesNotContain("TX1", ports["COM5"].Commands);
        Assert.True(rig.State.Transmitting);
        rig.Dispose();
        Assert.False(ports["COM5"].Dtr);
    }

    private sealed class Omni : IOmniRigApi
    {
        public int Status => 4;
        public string StatusText => "On-line";
        public long Frequency => 7_040_000;
        public int Mode => 0x04000000;
        public bool Tx => false;
        public void SetFrequency(long hz) { }
        public void SetMode(int mode) { }
        public void SetTx(bool transmit) { }
        public void Dispose() { }
    }

    [Fact]
    public async Task ManipulareCuOmniRig()
    {
        FakeFtx1? key = null;
        var rig = new OmniRigRig(new RigSettings { Kind = RigKind.OmniRig, KeyPort = "COM8", KeyPin = SerialPin.Dtr, KeyPortPttPin = SerialPin.Rts, OmniRigPollMs = 20 },
            _ => new Omni(), (port, baud) => key = new FakeFtx1(port));
        await rig.ConnectAsync();
        Assert.True(rig.CanKey);
        rig.SetKey(true);
        Assert.True(key!.Dtr);
        rig.SetPtt(true);                                     // PTT pe RTS, nu prin OmniRig
        Assert.True(key.Rts);
        await Task.Delay(100);                                // citirile periodice nu anuleaza emisia
        Assert.True(rig.State.Transmitting);
        rig.Dispose();
        Assert.False(key.Dtr);
        Assert.False(key.Rts);
        Assert.True(key.Disposed);
    }

    [Fact]
    public async Task OmniRigLipsaInchidePortulDeManipulare()
    {
        FakeFtx1? key = null;
        var rig = new OmniRigRig(new RigSettings { Kind = RigKind.OmniRig, KeyPort = "COM8", KeyPin = SerialPin.Dtr },
            _ => throw new RigException("OmniRig nu e instalat"), (port, baud) => key = new FakeFtx1(port));
        await Assert.ThrowsAsync<RigException>(() => rig.ConnectAsync());
        Assert.True(key!.Disposed);
        Assert.False(rig.CanKey);
    }
}
