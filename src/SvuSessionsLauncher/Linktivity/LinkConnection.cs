using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace SvuSessionsLauncher.Linktivity;

/// <summary>
/// The vendor's message transport: a TCP socket that negotiates a route with
/// the vendor's fixed preamble, then reads and writes 16-byte-header
/// <see cref="Message"/> frames. The class owns its receive thread and raises
/// <see cref="MessageReceived"/> and <see cref="Failed"/>.
/// </summary>
public sealed class LinkConnection : IDisposable
{
    /// <summary>The server asks the client to reconnect to another address.</summary>
    private const int RouteChange = 65532;

    public delegate void MessageHandler(LinkConnection connection, Message message);

    public event MessageHandler? MessageReceived;
    public event Action<LinkConnection, string>? Failed;

    private Thread? _thread;
    private volatile bool _running;

    private Socket? _sock;
    private Stream? _input;
    private Stream? _output;

    private string _host = "";
    private int _port;
    private int _bufferSize;
    private int _route;
    private string? _session;

    private readonly ManualResetEventSlim _routeEvent = new(false);
    private readonly object _sendLock = new();

    public LinkConnection(string host, int port, int bufferSize, string? session = null)
    {
        _session = session;
        _bufferSize = bufferSize;
        _host = host;
        _port = port;
        EstablishConnection(_host, _port);
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "LinktivityConnection" };
        _thread.Start();
    }

    public int Route => _route;

    public int WaitForRoute(int timeoutMs)
    {
        _routeEvent.Wait(timeoutMs);
        return _route;
    }

    private void EstablishConnection(string host, int port)
    {
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        socket.Connect(host, port);
        Stream stream = new NetworkStream(socket, false);
        _sock = socket;
        _output = stream;
        _input = new BufferedStream(stream, _bufferSize);
    }

    private void Run()
    {
        try
        {
            while (_route == 0 && _running)
            {
                _routeEvent.Reset();
                if (!InitiateHandShake())
                {
                    break;
                }
                Message? message = RecvMessage();
                if (message == null || message.GetRequest() != Message.Route)
                {
                    break;
                }
                _route = message.GetRoute();
                if (_route == RouteChange)
                {
                    if (ChangeRoute(message))
                    {
                        continue;
                    }
                    break;
                }
                MessageReceived?.Invoke(this, message);
            }
            RunReceive();
        }
        catch (Exception exception)
        {
            DispatchError(exception);
        }
        _running = false;
        AbortConnection(true);
        _routeEvent.Set();
    }

    private void RunReceive()
    {
        _routeEvent.Set();
        while (_running)
        {
            Message? message = RecvMessage();
            if (message == null || MessageReceived == null)
            {
                break;
            }
            DispatchMessage(message);
        }
    }

    private void DispatchMessage(Message message)
    {
        if (message.GetControl() == Message.Pending)
        {
            return;
        }
        MessageReceived?.Invoke(this, message);
    }

    private bool InitiateHandShake()
    {
        Message message = new Message(Message.ConnectionPoint, Message.Route, null);
        if (_session != null)
        {
            message.SetInflatedSize(int.Parse(_session));
        }
        return VendorHandshake.SslHello(_output!, _input!, message);
    }

    private bool ChangeRoute(Message message)
    {
        string[] parts = BufferConversion.GetStrings(message.Buffer!, 2, message.GetInt(0, 2));
        _host = parts[0];
        _port = int.Parse(parts[1]);
        try
        {
            EstablishConnection(_host, _port);
        }
        catch (Exception exception)
        {
            DispatchError(exception);
            return false;
        }
        _route = 0;
        _session = null;
        return true;
    }

    public void Send(Message message) => SendMessage(message, message.GetRequest() != 0);

    private void SendMessage(Message message, bool encrypt)
    {
        lock (_sendLock)
        {
            int attempts = 0;
            while (message.GetRequest() != Message.ControlInternal)
            {
                if (message.GetRoute() == 0)
                {
                    message.SetRoute(_route);
                }
                try
                {
                    message.Write(_output!, encrypt);
                    break;
                }
                catch (Exception exception)
                {
                    if (attempts > 1)
                    {
                        DispatchError(exception);
                        break;
                    }
                    attempts++;
                }
            }
        }
    }

    private Message? RecvMessage()
    {
        try
        {
            Message message = new Message(0, 0, null);
            message.ReadFrom(_input!);
            return message;
        }
        catch (Exception exception)
        {
            DispatchError(exception);
            return null;
        }
    }

    private void AbortConnection(bool linger)
    {
        try
        {
            if (_output != null)
            {
                _output.Flush();
            }
        }
        catch (Exception)
        {
        }
        if (_sock != null)
        {
            if (linger)
            {
                try
                {
                    _sock.LingerState = new LingerOption(true, 0);
                }
                catch (Exception)
                {
                }
            }
            try
            {
                _sock.Close();
            }
            catch (Exception)
            {
            }
        }
        _sock = null;
        _output = null;
        _input = null;
    }

    public void Kill()
    {
        _running = false;
        AbortConnection(false);
    }

    private void DispatchError(Exception exception)
    {
        Failed?.Invoke(this, "caught:" + exception);
    }

    public void Dispose()
    {
        Kill();
        _routeEvent.Dispose();
    }
}
