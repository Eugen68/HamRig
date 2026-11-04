using System.IO.Ports;
using System.Text;

namespace HamRig;

/// <summary>Un port serial deschis (abstract, ca driverele sa poata fi testate fara statie).</summary>
public interface ISerialLine : IDisposable
{
    /// <summary>Text primit (ASCII), pe un fir al portului.</summary>
    event Action<string>? TextReceived;

    /// <summary>Portul s-a inchis sau a dat eroare (cablu scos, statie oprita).</summary>
    event Action<Exception>? Failed;

    string PortName { get; }

    void Write(string text);

    bool Rts { get; set; }

    bool Dtr { get; set; }
}

/// <summary><see cref="ISerialLine"/> pe <see cref="SerialPort"/> (8N1, fara control de flux).</summary>
internal sealed class SerialPortLine : ISerialLine
{
    private readonly SerialPort _port;
    private readonly object _writeLock = new();

    private SerialPortLine(SerialPort port)
    {
        _port = port;
        _port.DataReceived += (_, _) =>
        {
            try
            {
                string s = _port.ReadExisting();
                if (s.Length > 0) TextReceived?.Invoke(s);
            }
            catch (Exception e)
            {
                Failed?.Invoke(e);
            }
        };
        _port.ErrorReceived += (_, e) => { if (e.EventType == SerialError.Frame) return; };
    }

    public event Action<string>? TextReceived;
    public event Action<Exception>? Failed;

    public string PortName => _port.PortName;

    /// <summary>Deschide portul; RTS si DTR pornesc oprite (altfel unele statii intra pe emisie).</summary>
    public static SerialPortLine Open(string portName, int baud)
    {
        var p = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            Encoding = Encoding.ASCII,
            ReadTimeout = 500,
            WriteTimeout = 500,
            NewLine = ";",
        };
        try
        {
            p.Open();
            p.RtsEnable = false;
            p.DtrEnable = false;
        }
        catch (UnauthorizedAccessException e)
        {
            p.Dispose();
            throw new RigException($"{portName} e folosit de alt program.", e);
        }
        catch (Exception e)
        {
            p.Dispose();
            throw new RigException($"Nu pot deschide {portName}: {e.Message}", e);
        }
        return new SerialPortLine(p);
    }

    public void Write(string text)
    {
        lock (_writeLock)
        {
            try
            {
                _port.Write(text);
            }
            catch (Exception e)
            {
                Failed?.Invoke(e);
            }
        }
    }

    public bool Rts
    {
        get => _port.RtsEnable;
        set { try { _port.RtsEnable = value; } catch (Exception e) { Failed?.Invoke(e); } }
    }

    public bool Dtr
    {
        get => _port.DtrEnable;
        set { try { _port.DtrEnable = value; } catch (Exception e) { Failed?.Invoke(e); } }
    }

    public void Dispose()
    {
        try
        {
            if (_port.IsOpen)
            {
                _port.RtsEnable = false;
                _port.DtrEnable = false;
                _port.Close();
            }
        }
        catch
        {
            // portul a disparut deja (cablu scos)
        }
        _port.Dispose();
    }
}
