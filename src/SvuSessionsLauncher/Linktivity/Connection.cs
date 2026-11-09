using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SvuSessionsLauncher.Linktivity;

public static class Connection
{
    public static readonly string[] Type = { "TCP", "Proxy", "HTTP", "SSL", "HTML" };

    public const int Tcp = 0;
    public const int Proxy = 1;
    public const int Http = 2;
    public const int Ssl = 3;
    public const int Html = 4;

    public const int RouteInvalid = 0;
    public const int RouteHttp = 32768;
    public const int RouteChange = 65532;
    public const int RouteNeedProxyAuthorization = 65533;
    public const int RouteNeedConnectAuthorization = 65534;
    public const int RouteAny = 65535;

    public const int RspCnpAddr = 0;
    public const int RspCnpPort = 1;
    public const int RspProxyAddr = 2;
    public const int RspProxyPort = 3;
    public const int RspProxyUser = 4;
    public const int RspProxyPswd = 5;
    public const int RspConnect = 6;

    public const int MessagePart = 0;
    public const int BufferPart = 1;

    public const string NoBalance = "0";
    public const string NoSession = "16777215";

    public const int ClientMessage = 47;

    public static readonly byte[] ClientHello =
    {
        0x80, 0x5D, 0x01, 0x03, 0x01, 0x00, 0x24, 0x00, 0x10, 0x00,
        0x20, 0x00, 0x00, 0x05, 0x00, 0x00, 0x04, 0x01, 0x00, 0x80,
        0x00, 0x00, 0x09, 0x06, 0x00, 0x40, 0x00, 0x00, 0x0A, 0x07,
        0x00, 0xC0, 0x00, 0x00, 0x12, 0x00, 0x00, 0x13, 0x00, 0x00,
        0x03, 0x02, 0x00, 0x80, 0x00, 0x00, 0x11, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x3D, 0xFE, 0x12, 0x9C, 0x8B, 0x27, 0xDE,
        0xEB, 0x5F, 0x98, 0x0F, 0xE7, 0x05, 0xD9, 0x17, 0x8B, 0x36,
        0xD7, 0xBC, 0x98, 0xB5, 0xC8, 0xBF, 0x0B, 0x25, 0x82, 0xA8,
        0xE2, 0x91, 0x3A, 0x94, 0xD4
    };

    public static readonly byte[] ServerHello =
    {
        0x16, 0x03, 0x01, 0x00, 0x4A, 0x02, 0x00, 0x00, 0x46, 0x03,
        0x01, 0x3D, 0xFE, 0x13, 0xD2, 0x16, 0x17, 0x0E, 0xF4, 0xA8,
        0xE8, 0x9E, 0x87, 0x92, 0x69, 0x9F, 0x6E, 0x36, 0xD6, 0xB5,
        0x46, 0xF6, 0xAA, 0x2D, 0xB2, 0xAE, 0x82, 0x91, 0x6A, 0x52,
        0xA6, 0x23, 0x81, 0x20, 0x3D, 0xFE, 0x13, 0xD2, 0x97, 0xE5,
        0xCC, 0xD9, 0xB7, 0x80, 0xBB, 0xBC, 0xDD, 0x91, 0x61, 0xD3,
        0xAC, 0xD2, 0x7F, 0x09, 0x34, 0xBC, 0x36, 0xBC, 0x7D, 0xBA,
        0x0F, 0xEC, 0x51, 0xAD, 0x4D, 0x96, 0x00, 0x05, 0x00
    };

    private const int ProxyNoData = 1;
    private const int ProxyReadLineNull = 2;
    private const int ProxyNotOk = 3;
    private const int ProxyException = 4;

    public static string GetProxyHeader(string host, string port, string path, string user, string password)
    {
        bool connect = path == null;
        string header = connect
            ? "CONNECT " + host + ":" + port + " HTTP/1.1\r\nHost: " + host + ":" + port + "\r\n"
            : "POST http://" + host + ":" + port + "/" + path + " HTTP/1.1\r\nHost: " + host + ":" + port
              + "\r\nContent-Type: application/octet-stream\r\n";
        if (user != null && password != null)
        {
            header += "Proxy-Authorization: Basic " + BufferConversion.StringToBase64(user + ":" + password) + "\r\n";
        }
        header += connect ? "\r\n" : "Content-Length: ";
        return header;
    }

    public static bool SslHello(Stream output, Stream input, Message message)
    {
        byte[] server = new byte[ServerHello.Length];
        byte[] client = (byte[])ClientHello.Clone();
        Buffer.BlockCopy(message.Header, 0, client, ClientMessage, message.Header.Length);
        try
        {
            output.Write(client, 0, client.Length);
            output.Flush();
            if (DataAvailable(input))
            {
                Message.ReadFully(input, server, 0, server.Length);
            }
        }
        catch
        {
        }
        return BufferConversion.Compare(server, ServerHello);
    }

    public static int ProxyConnect(Stream input, Stream output, byte[] connectHeader)
    {
        int result = 0;
        try
        {
            output.Write(connectHeader, 0, connectHeader.Length);
            output.Flush();
            if (!DataAvailable(input))
            {
                return ProxyNoData;
            }
            string? line = ReadLine(input);
            if (line == null)
            {
                result = ProxyReadLineNull;
            }
            else if (!IsHttpOk(line))
            {
                result = IsHttpAuthorization(line) ? RouteNeedConnectAuthorization : ProxyNotOk;
            }
            while (line != null && line.Length > 0)
            {
                line = ReadLine(input);
            }
        }
        catch
        {
            if (result == RouteNeedConnectAuthorization)
            {
                return result;
            }
            result = ProxyException;
        }
        return result;
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
        if (input.CanSeek)
        {
            return input.Position < input.Length;
        }
        return true;
    }

    private static string? ReadLine(Stream input)
    {
        int first = input.ReadByte();
        if (first < 0)
        {
            return null;
        }
        StringBuilder builder = new StringBuilder();
        int b = first;
        while (b >= 0)
        {
            if (b == '\n')
            {
                break;
            }
            if (b == '\r')
            {
                int next = input.ReadByte();
                if (next >= 0 && next != '\n' && input.CanSeek)
                {
                    input.Seek(-1, SeekOrigin.Current);
                }
                break;
            }
            builder.Append((char)b);
            b = input.ReadByte();
        }
        return builder.ToString();
    }

    private static bool IsHttpOk(string line) =>
        line.StartsWith("HTTP/1.1 200") || line.StartsWith("HTTP/1.0 200");

    private static bool IsHttpAuthorization(string line) =>
        line.StartsWith("HTTP/1.1 407") || line.StartsWith("HTTP/1.1 401")
        || line.StartsWith("HTTP/1.0 407") || line.StartsWith("HTTP/1.0 401");
}
