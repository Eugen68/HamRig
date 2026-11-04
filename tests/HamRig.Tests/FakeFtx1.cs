using System.Text;

namespace HamRig.Tests;

/// <summary>Port serial simulat cu un FTX-1 in spate: raspunde la FA, MD0, TX, AI ca statia.</summary>
internal sealed class FakeFtx1 : ISerialLine
{
    private readonly StringBuilder _in = new();

    public FakeFtx1(string port) => PortName = port;

    public event Action<string>? TextReceived;
    public event Action<Exception>? Failed;

    public string PortName { get; }
    public long Frequency { get; set; } = 14_083_500;
    public char ModeCode { get; set; } = 'C';   // DATA-U
    public bool Tx { get; set; }
    public bool AutoInfo { get; private set; }
    public bool Silent { get; set; }
    public bool Disposed { get; private set; }
    public List<string> Commands { get; } = new();
    public bool Rts { get; set; }
    public bool Dtr { get; set; }

    public void Write(string text)
    {
        foreach (char c in text)
        {
            if (c != ';') { _in.Append(c); continue; }
            string cmd = _in.ToString();
            _in.Clear();
            lock (Commands) Commands.Add(cmd);
            if (!Silent) Answer(cmd);
        }
    }

    private void Answer(string cmd)
    {
        string? reply = null;
        if (cmd == "AI1") AutoInfo = true;
        else if (cmd == "FA") reply = $"FA{Frequency:D9};";
        else if (cmd.StartsWith("FA")) { Frequency = long.Parse(cmd[2..]); if (AutoInfo) reply = $"FA{Frequency:D9};"; }
        else if (cmd == "MD0") reply = $"MD0{ModeCode};";
        else if (cmd.StartsWith("MD0")) { ModeCode = cmd[3]; if (AutoInfo) reply = $"MD0{ModeCode};"; }
        else if (cmd == "TX") reply = Tx ? "TX1;" : "TX0;";
        else if (cmd is "TX1" or "TX0") { Tx = cmd == "TX1"; if (AutoInfo) reply = Tx ? "TX1;" : "TX0;"; }
        else reply = "?;";
        if (reply != null) Task.Run(() => TextReceived?.Invoke(reply));
    }

    /// <summary>Statia anunta singura o schimbare (rotirea butonului VFO, cu AI1).</summary>
    public void TurnDial(long hz)
    {
        Frequency = hz;
        // impartit in doua bucati, ca pe un port real
        string s = $"FA{hz:D9};";
        TextReceived?.Invoke(s[..5]);
        TextReceived?.Invoke(s[5..]);
    }

    public void Fail() => Failed?.Invoke(new IOException("cablu scos"));

    public void Dispose() => Disposed = true;
}
