using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SvuSessionsLauncher.Linktivity;

public sealed class LinkConnection : IDisposable
{
    public delegate void MessageHandler(LinkConnection connection, Message message);

    public event MessageHandler? MessageReceived;
    public event Action<LinkConnection, string>? Failed;

    private static readonly HttpClient Http = new HttpClient();

    private CastCipher? _cipher;
    private bool _useCipher;

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _offline;

    private Uri? _url;
    private Socket? _sock;
    private Stream? _input;
    private Stream? _output;

    private string _serverAddress = "";
    private string _serverPort = "";
    private string? _session;
    private string _host = "";
    private int _port;
    private int _bufferSize;
    private int _route;
    private int _type;
    private bool _tryConnect;
    private bool _tunnel;
    private long _lastReconnect;
    private int _retries;

    private string? _proxyRequest;
    private byte[]? _proxyConnect;

    private readonly ManualResetEventSlim _routeEvent = new(false);
    private readonly object _sendLock = new();

    public LinkConnection(string host, int port, int type, int bufferSize,
        string? session = null, string? user = null, string? pswd = null, bool tryConnect = true)
    {
        _session = session;
        _bufferSize = bufferSize;
        _serverAddress = host;
        _serverPort = port.ToString();
        _type = type;
        _host = host;
        _port = port;
        _tryConnect = tryConnect;
        _offline = false;
        if (_type == Connection.Proxy)
        {
            _proxyConnect = Encoding.ASCII.GetBytes(Connection.GetProxyHeader(host, _serverPort, null!, user!, pswd!));
            _proxyRequest = Connection.GetProxyHeader(host, _serverPort, "Proxy", user!, pswd!);
        }
        EstablishConnection(_host, _port);
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "LinktivityConnection" };
        _thread.Start();
    }

    public int Route => _route;
    public int ConnectionType => _type;
    public string ConnectionTypeName => _tunnel ? Connection.Type[Connection.Proxy] : Connection.Type[_type];
    public string Host => _host;
    public int Port => _port;
    public string ServerAddress => _serverAddress;
    public string ServerPort => _serverPort;
    public bool IsTunnel => _tunnel;
    public Socket? TcpSocket => _sock;

    public int WaitForRoute(int timeoutMs)
    {
        _routeEvent.Wait(timeoutMs);
        return _route;
    }

    public bool EnableCipher(bool enable)
    {
        _useCipher = _cipher != null && enable;
        return _useCipher;
    }

    public CastCipher? SetCipherKey(string? key)
    {
        _cipher = string.IsNullOrEmpty(key) ? null : new CastCipher(Encoding.UTF8.GetBytes(key));
        if (_cipher == null)
        {
            _useCipher = false;
        }
        return _cipher;
    }

    public bool CipherEnabled(bool requireKey) => requireKey ? _cipher != null : _useCipher;

    private void EstablishConnection(string host, int port)
    {
        if (_type == Connection.Http)
        {
            _url = new Uri("http://" + host + ":" + port + "/");
            return;
        }
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
                if (!InitiateHandShake(_tryConnect))
                {
                    break;
                }
                Message? message = RecvMessage();
                if (message == null || message.GetRequest() != Message.Route)
                {
                    break;
                }
                _route = message.GetRoute();
                if (_route == Connection.RouteNeedProxyAuthorization)
                {
                    break;
                }
                if (_route == Connection.RouteChange)
                {
                    if (ChangeRoute(message))
                    {
                        continue;
                    }
                    break;
                }
                MessageReceived?.Invoke(this, message);
                if (!EstablishProxyLink())
                {
                    break;
                }
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

    private bool InitiateHandShake(bool tryConnect)
    {
        if (_type == Connection.Http)
        {
            return true;
        }
        Message message = new Message(Message.ConnectionPoint, Message.Route, null);
        if (_session != null)
        {
            message.SetInflatedSize(int.Parse(_session));
        }
        if (_type != Connection.Proxy)
        {
            return Connection.SslHello(_output!, _input!, message);
        }
        if (tryConnect)
        {
            int result = Connection.ProxyConnect(_input!, _output!, _proxyConnect!);
            if (result == 0 && Connection.SslHello(_output!, _input!, message))
            {
                _type = Connection.Tcp;
                _tunnel = true;
                return true;
            }
            if (result == Connection.RouteNeedConnectAuthorization)
            {
                _route = Connection.RouteNeedConnectAuthorization;
                AbortConnection(true);
                return false;
            }
            if (!Reconnect())
            {
                return false;
            }
            SendMessage(message, false);
            return DataAvailable(_input!);
        }
        SendMessage(message, false);
        return DataAvailable(_input!);
    }

    private bool ChangeRoute(Message message)
    {
        string[] parts = BufferConversion.GetStrings(message.Buffer!, 2, message.GetInt(0, 2));
        _serverAddress = parts[0];
        _serverPort = parts[1];
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

    private bool Reconnect()
    {
        bool ok = false;
        if (_lastReconnect > 0)
        {
            _retries = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond - _lastReconnect < 9999 ? _retries + 1 : 0;
        }
        if (_retries <= 2)
        {
            try
            {
                AbortConnection(true);
                Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.NoDelay = true;
                socket.Connect(_host, _port);
                Stream stream = new NetworkStream(socket, false);
                _sock = socket;
                _output = stream;
                _input = new BufferedStream(stream, _bufferSize);
                ok = true;
            }
            catch (Exception exception)
            {
                DispatchError(exception);
            }
        }
        else
        {
            DispatchError(new Exception("retry limit exceeded"));
        }
        _lastReconnect = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        return ok;
    }

    private bool EstablishProxyLink() => true;

    public void Send(Message message) => SendMessage(message, message.GetRequest() != 0);

    public void Send(int control, byte[]? buffer) => Send(new Message(control, 0, buffer));

    public void Send(int control, byte[]? buffer, int length, int flags)
    {
        Message message = new Message(control, 0, buffer, length, flags);
        Send(message);
    }

    private void SendMessage(Message message, bool encrypt)
    {
        lock (_sendLock)
        {
            int attempts = 0;
            while (!_offline && message.GetRequest() != Message.ControlInternal)
            {
                if (_url != null)
                {
                    Connect(message, encrypt);
                    break;
                }
                if (message.GetRoute() == 0)
                {
                    message.SetRoute(_route);
                }
                try
                {
                    CastCipher? cipher = _useCipher ? _cipher : null;
                    if (_type != Connection.Proxy)
                    {
                        message.Write(_output!, encrypt, cipher, _type == Connection.Http);
                        break;
                    }
                    message.Write(_output!, encrypt, cipher, _proxyRequest != null);
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
            Stream input = _url != null ? Connect(null, false) : _input!;
            Message message = new Message(0, 0, null);
            message.ReadFrom(input);
            message.Decrypt(_cipher);
            return message;
        }
        catch (Exception exception)
        {
            DispatchError(exception);
            return null;
        }
    }

    private Stream Connect(Message? message, bool encrypt)
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        if (message != null)
        {
            byte[] wire = message.FillWriteBuffer(encrypt, _useCipher ? _cipher : null, false);
            request.Content = new ByteArrayContent(wire);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using HttpResponseMessage sent = Http.Send(request, HttpCompletionOption.ResponseContentRead);
            return new MemoryStream(sent.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
        }
        Message pending = new Message(Message.Pending, Message.Pending, null);
        byte[] pendingWire = pending.FillWriteBuffer(false, null, false);
        request.Content = new ByteArrayContent(pendingWire);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        HttpResponseMessage response = Http.Send(request, HttpCompletionOption.ResponseContentRead);
        return new MemoryStream(response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
    }

    private static bool DataAvailable(Stream input, int timeoutMs = 10000)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            if (HasData(input))
            {
                return true;
            }
            Thread.Sleep(100);
            waited += 100;
        }
        return false;
    }

    private static bool HasData(Stream input)
    {
        if (input is NetworkStream network)
        {
            return network.DataAvailable;
        }
        if (input is BufferedStream)
        {
            return true;
        }
        if (input.CanSeek)
        {
            return input.Position < input.Length;
        }
        return true;
    }

    public bool WaitForThreads(int timeoutMs)
    {
        _running = false;
        _thread?.Join(timeoutMs);
        return _thread == null || !_thread.IsAlive;
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
