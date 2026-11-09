using System.IO;
using System.IO.Compression;

namespace SvuSessionsLauncher.Linktivity;

public class Message
{
    public const int Headersize = 16;

    public const int Manager = 0;
    public const int Chat = 1;
    public const int Voice = 2;
    public const int Viewport = 3;
    public const int RemoteControl = 4;
    public const int Cobrowse = 5;
    public const int ConnectionPoint = 6;
    public const int FileTransfer = 7;
    public const int Supervisor = 8;
    public const int RecoverIt = 9;
    public const int Video = 10;
    public const int Audio = 11;
    public const int SysInfo = 12;
    public const int FileIo = 13;
    public const int Polling = 14;
    public const int Whiteboard = 15;
    public const int FileSystem = 16;
    public const int Controls = 17;
    public const int Reconnect = 67;
    public const int Chain = 68;
    public const int Internal = 69;

    public const int Data = 0;
    public const int Init = 1;
    public const int Info = 2;
    public const int Text = 3;

    public const int CancelStart = 2000;
    public const int ControlStart = 2001;
    public const int ControlStop = 2002;
    public const int ControlInternal = 2004;

    public const int Abort = 65532;
    public const int Disconnect = 65533;
    public const int Route = 65534;
    public const int Pending = 65535;

    public const int FlagContinuation = 1;
    public const int FlagCompressed = 2;
    public const int FlagHost = 4;
    public const int FlagFinal = 8;
    public const int FlagHeaderEncrypted = 16;
    public const int FlagBufferEncrypted = 32;
    public const int FlagArchive = 64;
    public const int FlagRecorded = 128;
    public const int FlagUpdate = 256;
    public const int FlagIncremental = 512;
    public const int FlagEmbedded = 1024;
    public const int FlagAvailable = 2048;
    public const int FlagEncrypted = 4096;
    public const int FlagPadded = 8192;
    public const int FlagClearEncrypted = 53247;

    private static readonly byte[] EncryptTable = new byte[]
    {
        116, 109, 103, 98, 120, 100, 106, 102, 113, 97, 118, 110, 114, 104, 101, 119, 99, 122, 107, 115, 121, 112, 108, 117, 105, 111, 0
    };

    public byte[] Header = new byte[16];
    public byte[]? Buffer;
    public int BufferLength;

    public Message(int control, int request, byte[]? buffer)
        : this(control, request, buffer, buffer != null ? buffer.Length : 0, 0, false)
    {
    }

    public Message(int control, int request, byte[]? buffer, bool copy)
        : this(control, request, buffer, buffer != null ? buffer.Length : 0, 0, copy)
    {
    }

    public Message(int control, int request, byte[]? buffer, int length, int flags)
        : this(control, request, buffer, length, flags, false)
    {
    }

    public Message(int control, int request, byte[]? buffer, int length, int flags, bool copy)
    {
        Header[0] = 16;
        Header[1] = 4;
        SetSize(length);
        SetControl(control);
        SetFlags(flags);
        SetRoute(0);
        SetRequest(request);
        SetInflatedSize(0);
        BufferLength = length;
        if (!copy)
        {
            Buffer = buffer;
        }
        else if (BufferLength > 0 && buffer != null)
        {
            Buffer = BufferConversion.Slice(buffer, 0, BufferLength);
        }
    }

    public Message Copy()
    {
        Message copy = new Message(GetControl(), GetRequest(), null)
        {
            Header = (byte[])Header.Clone()
        };
        if (BufferLength > 0)
        {
            copy.Buffer = BufferConversion.Slice(Buffer ?? Array.Empty<byte>(), 0, BufferLength);
            copy.BufferLength = BufferLength;
        }
        return copy;
    }

    public bool IsValid => Header[0] == 16 && Header[1] == 4;

    public int GetControl() => BufferConversion.BytesToInt(Header, 5, 2);

    public void SetControl(int value) => BufferConversion.IntToBytes(value, Header, 5, 2);

    public int GetRequest() => BufferConversion.BytesToInt(Header, 11, 2);

    public void SetRequest(int value) => BufferConversion.IntToBytes(value, Header, 11, 2);

    public int GetRoute() => BufferConversion.BytesToInt(Header, 9, 2);

    public void SetRoute(int value) =>
        BufferConversion.IntToBytes(value, Header, 9, 2);

    public int GetFlags() => BufferConversion.BytesToInt(Header, 7, 2);

    public void SetFlags(int value) => BufferConversion.IntToBytes(value, Header, 7, 2);

    public int GetSize() => BufferConversion.BytesToInt(Header, 2, 3) - 11;

    public void SetSize(int length) => BufferConversion.IntToBytes(11 + length, Header, 2, 3);

    public int GetInflatedSize() => BufferConversion.BytesToInt(Header, 13, 3);

    public void SetInflatedSize(int value) => BufferConversion.IntToBytes(value, Header, 13, 3);

    public bool IsCompressed => (GetFlags() & FlagCompressed) == FlagCompressed;

    public bool IsContinuation => (GetFlags() & FlagContinuation) == FlagContinuation;

    public bool IsLastPacket => (GetFlags() & FlagFinal) == FlagFinal;

    public bool IsHost => (GetFlags() & FlagHost) == FlagHost;

    public bool IsArchive => (GetFlags() & FlagArchive) == FlagArchive;

    public bool IsRecorded => (GetFlags() & FlagRecorded) == FlagRecorded;

    public bool IsUpdate => (GetFlags() & FlagUpdate) == FlagUpdate;

    public bool IsIncremental => (GetFlags() & FlagIncremental) == FlagIncremental;

    public bool IsEmbedded => (GetFlags() & FlagEmbedded) == FlagEmbedded;

    public bool IsPadded => (GetFlags() & FlagPadded) == FlagPadded;

    public bool IsEncrypted => (GetFlags() & FlagEncrypted) == FlagEncrypted;

    public int GetInt(int offset, int length) =>
        BufferConversion.BytesToInt(GetBuffer(offset, length), offset, length);

    public void SetInt(int value, int offset, int length) =>
        BufferConversion.IntToBytes(value, Buffer!, offset, length);

    public byte[] GetBuffer(int offset, int length) => IsEncrypted ? (Buffer ?? Array.Empty<byte>()) : (Buffer ?? Array.Empty<byte>());

    public byte[] GetBytes(int offset) =>
        BufferConversion.Slice(GetBuffer(offset, BufferLength - offset), offset, BufferLength - offset);

    public byte[] GetBytes(int offset, int length) =>
        BufferConversion.Slice(GetBuffer(offset, length), offset, length);

    public byte[] ReadAll()
    {
        if (Buffer == null || BufferLength == 0)
        {
            return Array.Empty<byte>();
        }
        if (IsCompressed)
        {
            int inflatedSize = GetInflatedSize();
            byte[] inflated = new byte[inflatedSize];
            using (ZLibStream zip = new ZLibStream(new MemoryStream(Buffer, 0, BufferLength), CompressionMode.Decompress))
            {
                int total = 0;
                while (total < inflatedSize)
                {
                    int read = zip.Read(inflated, total, inflatedSize - total);
                    if (read <= 0)
                    {
                        break;
                    }
                    total += read;
                }
            }
            return inflated;
        }
        return BufferConversion.Slice(Buffer, 0, BufferLength);
    }

    private static int GetFlags(byte[] header, int offset) =>
        BufferConversion.BytesToInt(header, offset + 7, 2);

    private static void SetFlags(byte[] header, int offset, int value) =>
        BufferConversion.IntToBytes(value, header, offset + 7, 2);

    private static int GetSize(byte[] header, int offset) =>
        BufferConversion.BytesToInt(header, offset + 2, 3) - 11;

    private static void SetSize(byte[] header, int offset, int value) =>
        BufferConversion.IntToBytes(11 + value, header, offset + 2, 3);

    private static bool IsHeaderEncrypted(byte[] header, int offset) =>
        (BufferConversion.BytesToInt(header, offset + 7, 2) & FlagHeaderEncrypted) == FlagHeaderEncrypted;

    private static bool IsBufferEncrypted(byte[] header, int offset) =>
        (BufferConversion.BytesToInt(header, offset + 7, 2) & FlagBufferEncrypted) == FlagBufferEncrypted;

    private static void EncryptHeader(byte[] header, int offset, bool apply)
    {
        bool encrypted = IsHeaderEncrypted(header, offset);
        if (encrypted && !apply || !encrypted && apply)
        {
            int idx = 0;
            for (int i = offset + 11; i < offset + 16; i++)
            {
                header[i] ^= EncryptTable[idx % 26];
                idx++;
            }
            SetFlags(header, offset, encrypted ? GetFlags(header, offset) - FlagHeaderEncrypted : GetFlags(header, offset) + FlagHeaderEncrypted);
        }
    }

    private static void EncryptBuffer(byte[] header, int headerOffset, byte[] target, int offset, int length, bool apply)
    {
        bool encrypted = IsBufferEncrypted(header, headerOffset);
        if (encrypted && !apply || !encrypted && apply)
        {
            int idx = 5;
            for (int i = offset; i < offset + length; i++)
            {
                target[i] ^= EncryptTable[idx % 26];
                idx++;
            }
            SetFlags(header, headerOffset, encrypted ? GetFlags(header, headerOffset) - FlagBufferEncrypted : GetFlags(header, headerOffset) + FlagBufferEncrypted);
        }
    }

    private void EncryptHeader(bool apply) => EncryptHeader(Header, 0, apply);

    private void EncryptBuffer(bool apply) => EncryptBuffer(Header, 0, Buffer ?? Array.Empty<byte>(), 0, BufferLength, apply);

    public byte[]? Decrypt(CastCipher? cipher)
    {
        if (cipher != null && IsEncrypted)
        {
            Buffer = cipher.Decrypt(Buffer ?? Array.Empty<byte>(), BufferLength, IsPadded);
            BufferLength = Buffer.Length;
            SetSize(BufferLength);
            SetFlags(GetFlags() & FlagClearEncrypted);
        }
        return Buffer;
    }

    public void DecryptXor()
    {
        EncryptHeader(false);
        EncryptBuffer(false);
    }

    public byte[] FillWriteBuffer(bool encrypt, CastCipher? cipher, bool httpEnvelope = false)
    {
        byte[] outBuffer = Buffer ?? Array.Empty<byte>();
        int outLength = BufferLength;
        bool cipherApplied = false;
        if (cipher != null)
        {
            encrypt = false;
            if (outLength > 0 && !IsEncrypted && !IsBufferEncrypted(Header, 0))
            {
                outBuffer = cipher.Encrypt(outBuffer, outLength);
                outLength = outBuffer.Length;
                cipherApplied = true;
            }
        }

        int tempSize = Header.Length + outLength;
        byte[]? prefix = null;
        int prefixLen = 0;
        if (httpEnvelope)
        {
            prefix = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: " + tempSize + "\r\n\r\n");
            prefixLen = prefix.Length;
        }
        int headerPos = prefixLen;

        byte[] wire = new byte[tempSize + prefixLen];
        if (prefix != null)
        {
            System.Buffer.BlockCopy(prefix, 0, wire, 0, prefixLen);
        }
        System.Buffer.BlockCopy(Header, 0, wire, headerPos, Header.Length);
        if (encrypt && cipher == null)
        {
            EncryptHeader(wire, headerPos, true);
        }
        if (outLength > 0)
        {
            System.Buffer.BlockCopy(outBuffer, 0, wire, headerPos + Header.Length, outLength);
            if (encrypt && cipher == null)
            {
                EncryptBuffer(wire, headerPos, wire, headerPos + Header.Length, outLength, true);
            }
            else if (cipherApplied)
            {
                SetSize(wire, headerPos, outLength);
                SetFlags(wire, headerPos, GetFlags(wire, headerPos) | (outLength > BufferLength ? FlagEncrypted | FlagPadded : FlagEncrypted));
            }
        }
        return wire;
    }

    public void Write(Stream output, bool encrypt, CastCipher? cipher, bool httpEnvelope = false)
    {
        byte[] wire = FillWriteBuffer(encrypt, cipher, httpEnvelope);
        output.Write(wire, 0, wire.Length);
    }

    public void ReadFrom(Stream input)
    {
        ReadFully(input, Header, 0, Header.Length);
        if (!IsValid)
        {
            throw new IOException("invalid protocol header");
        }
        BufferLength = BufferConversion.BytesToInt(Header, 2, 3) - 11;
        if (BufferLength > 0)
        {
            Buffer = new byte[BufferLength];
            ReadFully(input, Buffer, 0, BufferLength);
        }
        DecryptXor();
    }

    public static void ReadFully(Stream input, byte[] target, int offset, int length)
    {
        int total = 0;
        while (total < length)
        {
            int read = input.Read(target, offset + total, length - total);
            if (read <= 0)
            {
                throw new EndOfStreamException("connection closed");
            }
            total += read;
        }
    }
}