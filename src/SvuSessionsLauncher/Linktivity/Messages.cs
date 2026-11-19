using System.Globalization;
using System.Text;

namespace SvuSessionsLauncher.Linktivity;

public static class Messages
{
    public const string User = "User";
    public const string Host = "Host";
    public const string Enable = "Enable";
    public const string Disable = "Disable";
    public const string PingUnknown = "?";

    public const int RequestJoinGroup = 4004;

    // Messages.HEART_BEAT = 4002; marshallHeartBeatRequest() -> Message(6, 4002, null).
    // The vendor client sends this on connection setup (capture session_20261020_163653
    // shows it as the first message after route negotiation). Empty body, control=6.
    public const int RequestHeartBeat = 4002;

    // Messages.PEER_UPDATED = 3001 used as marshallNodeUpdate's request id.
    public const int RequestNodeUpdate = 3001;

    // Messages.FILE_SYSTEM = 4022; the applet-register frame (control=6).
    public const int RequestFileSystem = 4022;

    public const int JoinSession = 0;
    public const int JoinSessionType = 1;
    public const int JoinUser = 2;
    public const int JoinUserType = 3;
    public const int JoinUserColor = 4;
    public const int JoinUserIcon = 5;
    public const int JoinEncrypt = 6;
    public const int JoinStartTime = 7;
    public const int JoinCount = 8;
    public const int JoinHandle = 9;
    public const int JoinScreenWidth = 10;
    public const int JoinScreenHeight = 11;
    public const int JoinJavaVendor = 12;
    public const int JoinJavaVersion = 13;
    public const int JoinJavaClass = 14;
    public const int JoinOsName = 15;
    public const int JoinOsVersion = 16;
    public const int JoinOsArch = 17;
    public const int JoinCertificate = 18;
    public const int JoinCapabilities = 19;
    public const int JoinSize = 20;

    public const string CapsCert = "11111111111111111";
    public const string CapsNoCert = "11011000000000100";
    public const string CapsHtml = "01010000000000000";
    public const string CapsGroup = "11110101011111111";

    // Control-channel requests. These mirror ConnectionApplet's command ids and
    // the request ids produced by Messages.marshallControlUpdate(n, val, control,
    // request, data) -- the wire payload is [u16 index][u16 value][data...].
    public const int RequestInitControl = 4011;
    public const int RequestEnableControl = 3009;
    public const int RequestAcceptControl = 3010;
    public const int RequestControlCommand = 3011;
    public const int RequestArchive = 5000;
    public const int RequestGroupJoin = 4004;

    // CONTROL_START (2001) / CONTROL_STOP (2002) carry [u16 index][u16 value]
    // (Messages.marshallControlUpdateRequest -> marshallControlUpdate(n, val, 6,
    // 2001|2002, data)).
    public const int RequestControlStart = 2001;
    public const int RequestControlStop = 2002;

    public const int ControlManager = 0;
    public const int ControlPeer = 1;
    public const int ControlViewport = 3;
    public const int ControlRemote = 4;
    public const int ControlNode = 6;
    public const int ControlGroup = 8;
    public const int ControlSigned = 15;

    // Statics.CONTROLS index of the vendor viewport/viewer applet pair.
    public const int ControlIndexViewer = 3;
    public const int ControlIndexViewport = 4;

    // Statics.CONTROLS index of the chat applet. ChatApplet is the ONLY applet
    // that issues INIT_CONTROL (108) at join (ChatApplet.java:951 -> index 1),
    // which is what makes the server start the chat control and push the
    // history/archive frame (control=1, request=1). The viewer/viewport applets
    // never send INIT_CONTROL -- they only register control 3/4 listeners.
    public const int ControlIndexChat = 1;

    // Statics.CONTROLS index of the voice applet. The vendor starts it with a
    // CONTROL_START (2001) for index 2 immediately after chat-init.
    public const int ControlIndexVoice = 2;

    public static byte[] MarshallStrings(string?[]? array, int offset)
    {
        byte[] result = BufferConversion.GetBytes(array!, offset + 2);
        BufferConversion.IntToBytes(array?.Length ?? 0, result, offset, 2);
        return result;
    }

    public static byte[] MarshallStrings(string?[]? array) => MarshallStrings(array, 0);

    public static string[] UnmarshallStrings(Message message, int offset)
    {
        int count = message.GetInt(offset, 2);
        return BufferConversion.GetStrings(message.Buffer ?? Array.Empty<byte>(), offset + 2, count);
    }

    public static string[] UnmarshallStrings(Message message) => UnmarshallStrings(message, 0);

    public static string?[] MarshallJoinGroup(string session, string type, string user, string handle,
        string? startTime, bool isHost, int width, int height, bool castEncrypt, bool digitalCertificate)
    {
        string?[] fields = new string?[JoinSize];
        fields[JoinJavaVendor] = "Oracle Corporation";
        fields[JoinJavaVersion] = Environment.Version.ToString();
        fields[JoinJavaClass] = "52.0";
        fields[JoinOsName] = "Windows";
        fields[JoinOsVersion] = Environment.OSVersion.Version.ToString();
        fields[JoinOsArch] = Environment.Is64BitProcess ? "x86_64" : "x86";
        fields[JoinSession] = session;
        fields[JoinSessionType] = type;
        fields[JoinEncrypt] = castEncrypt ? Enable : Disable;
        fields[JoinUser] = user;
        fields[JoinUserType] = isHost ? Host : User;
        fields[JoinUserColor] = "color";
        fields[JoinUserIcon] = "icon";
        fields[JoinScreenWidth] = width.ToString();
        fields[JoinScreenHeight] = height.ToString();
        // The vendor sends startTime as java.util.Date.toString()
        // (e.g. "Tue Oct 20 11:55:06 AST 2026"). Emit the same English,
        // culture-invariant shape so the server sees no localized/Arabic
        // text and no non-ASCII in this wire field.
        fields[JoinStartTime] = startTime ?? DateTime.Now.ToString("ddd MMM dd HH:mm:ss zzz yyyy", CultureInfo.InvariantCulture);
        fields[JoinHandle] = handle;
        fields[JoinCertificate] = digitalCertificate ? Enable : Disable;
        fields[JoinCapabilities] = digitalCertificate ? CapsCert : CapsNoCert;
        return fields;
    }

    public static Message MarshallJoinGroupRequest(string session, string type, string user,
        bool isHost, int width, int height, bool castEncrypt, bool digitalCertificate)
    {
        return MarshallJoinGroupRequest(session, type, user, PingUnknown, null, isHost, width, height, castEncrypt, digitalCertificate);
    }

    public static Message MarshallJoinGroupRequest(string session, string type, string user, string handle,
        string? startTime, bool isHost, int width, int height, bool castEncrypt, bool digitalCertificate)
    {
        string?[] fields = MarshallJoinGroup(session, type, user, handle, startTime, isHost, width, height, castEncrypt, digitalCertificate);
        return new Message(6, RequestJoinGroup, MarshallStrings(fields));
    }

    // Messages.marshallHeartBeatRequest() -> new Message(6, 4002, null).
    public static Message MarshallHeartBeatRequest() => new Message(6, RequestHeartBeat, null);

    // Messages.marshallNodeUpdate(string[], false) -> Message(6, 3001,
    // marshallStrings(stringArray)). The vendor sends it with a null array from
    // PeersStubApplet.runPreload() when applet preloading completes
    // (ClassRegistry.doCommand("ConnectionManager", 138)), i.e. a "preload done"
    // notification. The server withholds the control=8 CHAIN applet-activation
    // until it sees this, so the client must send it after join or no viewer or
    // voice applet is ever started (old engine capture session_20261020_163653).
    public static Message MarshallNodeUpdateRequest(string?[]? fields) =>
        new Message(6, RequestNodeUpdate, MarshallStrings(fields ?? Array.Empty<string?>()));

    // Messages.marshallFileMessage(6, 0, ["ConnectionPoint"], true, true) ->
    // Message(6, 4022, [u16 6][u32 0][u16 count=1]["ConnectionPoint\0"]). The
    // vendor client registers the loaded ConnectionPoint applet (Statics.CONTROLS[6])
    // with this frame after preload (ConnectionApplet command 136 -> 4022).
    public static Message MarshallFileSystemRegisterRequest(int controlIndex, string appletName) =>
        new Message(6, RequestFileSystem, MarshallFileRegister(controlIndex, appletName));

    private static byte[] MarshallFileRegister(int controlIndex, string appletName)
    {
        byte[] strings = MarshallStrings(new string?[] { appletName }, 6);
        BufferConversion.IntToBytes(controlIndex, strings, 0, 2);
        BufferConversion.IntToBytes(0, strings, 2, 4);
        return strings;
    }

    // Messages.marshallControlUpdate(int index, int value, int control, int request, byte[] data)
    // -> Message(control, request, buffer) where buffer = [u16 index][u16 value][data...].
    public static Message MarshallControlUpdate(int index, int value, int control, int request, byte[]? data)
    {
        byte[] buffer = new byte[4 + (data?.Length ?? 0)];
        BufferConversion.IntToBytes(index, buffer, 0, 2);
        BufferConversion.IntToBytes(value, buffer, 2, 2);
        if (data != null && data.Length > 0)
        {
            Array.Copy(data, 0, buffer, 4, data.Length);
        }
        return new Message(control, request, buffer);
    }

    // ConnectionApplet 108 INIT_CONTROL -> marshallInitControlRequest(index):
    // control=6 (node), request=4011, payload [index][0].
    public static Message MarshallInitControlRequest(int index) =>
        MarshallControlUpdate(index, 0, ControlNode, RequestInitControl, null);

    // Messages.marshallControlUpdateRequest(index, data, true, false) ->
    // control=6 (node), request=2001 (CONTROL_START), payload [index][0][data].
    // The vendor client uses this to start an applet's control; capture
    // session_20261018_223722.cap shows it for the voice applet (index 2).
    public static Message MarshallControlStartRequest(int index, byte[]? data = null) =>
        MarshallControlUpdate(index, 0, ControlNode, RequestControlStart, data);

    // ConnectionApplet 105 ENABLE_CONTROL -> marshallEnableControlRequest(index, on):
    // control=8 (group), request=3009, payload [index][on?1:0]. Sent distributed.
    public static Message MarshallEnableControlRequest(int index, bool enabled) =>
        MarshallControlUpdate(index, enabled ? 1 : 0, ControlGroup, RequestEnableControl, null);

    // ConnectionApplet 126 ACCEPT_CONTROL -> marshallAcceptControlRequest(index, on):
    // control=0 (manager), request=3010, payload [index][on?1:0]. Sent distributed.
    public static Message MarshallAcceptControlRequest(int index, bool enabled) =>
        MarshallControlUpdate(index, enabled ? 1 : 0, ControlManager, RequestAcceptControl, null);

    // ---- Voice channel (control 2) ----
    // The voice applet's wire framing lives entirely inside the native DLL
    // (LkVoiceAppletDll.SendMsgProc); the Java layer never sees it. The format
    // below was reverse-engineered from a real vendor student capture
    // (session_20261020_163653.cap, C->S control=2 frames):
    //
    //   body = [u16 count=1][u16 blockLen][node\0][10 04 0C 00][nested Message]
    //   nested Message = 16-byte header (control=2, request=0) + payload
    //   - announce: payload = 1 byte, 0x03 (voice channel open)
    //   - audio:    payload = 0x01/0x04 + codec bytes
    //
    // blockLen counts (node + NUL) + 4 trailing bytes ("10 04 0C 00") that the
    // vendor folds into the string length field. We send only the announce:
    // ControlApplet flashes the sender's mic icon when buffer[0] < 2, which is
    // true for audio tag 0x01 but not for announce tag 0x03.
    public static Message MarshallVoiceAnnounce(string node)
    {
        byte[] nodeBytes = Encoding.ASCII.GetBytes(node);

        // Voice body: [00 02][10 zero bytes][tag]. The leading "00 02" mirrors
        // control=2; the trailing tag identifies the frame kind. This is not a
        // full Message header -- the vendor folds "10 04 <innerLen>" into the
        // string block before it.
        byte[] voiceBody = new byte[13];
        voiceBody[0] = 0x00;
        voiceBody[1] = 0x02;
        voiceBody[12] = VoiceTagAnnounce;

        // The vendor's inline "10 04 <innerLen>" trailer counts the 12-byte voice
        // header but NOT the trailing tag byte (announce body=13B -> innerLen=12).
        int innerLen = voiceBody.Length - 1;

        // String block = node + NUL + "10 04 <u16 innerLen>" (the vendor counts
        // these 4 trailer bytes in the block length). blockLen covers node+NUL+4.
        int blockLen = nodeBytes.Length + 1 + 4;
        byte[] body = new byte[4 + blockLen + voiceBody.Length];
        BufferConversion.IntToBytes(1, body, 0, 2);
        BufferConversion.IntToBytes(blockLen, body, 2, 2);
        Array.Copy(nodeBytes, 0, body, 4, nodeBytes.Length);
        int trailerAt = 4 + nodeBytes.Length + 1;
        body[trailerAt] = 0x10;
        body[trailerAt + 1] = 0x04;
        body[trailerAt + 2] = (byte)(innerLen & 0xFF);
        body[trailerAt + 3] = (byte)((innerLen >> 8) & 0xFF);
        Array.Copy(voiceBody, 0, body, 4 + blockLen, voiceBody.Length);

        Message message = new Message(Message.Voice, 0, body);
        // Voice frames ride with the vendor's 0x0400 flag (see capture).
        message.SetFlags(VoiceFlag);
        return message;
    }

    public const byte VoiceTagAnnounce = 0x03;
    public const int VoiceFlag = 0x0400;

}
