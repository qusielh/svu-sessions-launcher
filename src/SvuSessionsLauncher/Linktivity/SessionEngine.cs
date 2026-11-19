using System.Globalization;
using System.IO;
using System.Text;

namespace SvuSessionsLauncher.Linktivity;

/// <summary>
/// A member's microphone/voice state, matching the vendor peer icons: the mic
/// off, on but idle, or the member actively speaking.
/// </summary>
public enum MicStatus
{
    Off = 0,
    On = 1,
    Talking = 2
}

public sealed class SessionPeer
{
    public string Connection { get; set; } = "";
    public string Session { get; set; } = "";
    public string User { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string RoomKey { get; set; } = "";
    public string Company { get; set; } = "";
    public string Status { get; set; } = "";
    public string Role { get; set; } = "";
    public string Color { get; set; } = "";
    public string Icon { get; set; } = "";
    public string JoinTime { get; set; } = "";
    public string ScreenWidth { get; set; } = "";
    public string ScreenHeight { get; set; } = "";
    public string OsName { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string OsArch { get; set; } = "";
    public string[] Fields { get; set; } = Array.Empty<string>();

    // Per-member microphone / voice status. The wire exposes no live mic flag:
    // roster field 49 is a *static* capability bitmap ('11111111111111111' for
    // every peer, including students who never speak), so it cannot mean "mic
    // is on". What we can observe is actual speech: VoiceTalking is derived
    // locally from inbound voice frames (SessionEngine.MarkTalking). Once a
    // member has been heard once we know they can speak, which is the only
    // truthful source for the idle "on" icon. The local user stays Off by
    // policy (listen-only).
    public bool VoiceEnabled { get; set; }

    public bool VoiceTalking { get; set; }

    public MicStatus Mic => VoiceTalking ? MicStatus.Talking : (VoiceEnabled ? MicStatus.On : MicStatus.Off);

    public static SessionPeer FromFields(string[] fields)
    {
        string At(int index) => index < fields.Length ? fields[index] : "";

        // The roster's human-readable display name is field 25 (the vendor's
        // PeersApplet does "listItem.name = stringArray[25]"); field 2 is not
        // the name. Fall back to field 2 only if the name slot is empty.
        string name = At(25);
        if (string.IsNullOrEmpty(name))
        {
            name = At(2);
        }

        // Field 49 is a capability bitmap ('0'/'1'), *not* live mic state: every
        // peer advertises the same '11111111111111111'. Mic status starts Off
        // and is upgraded only when that member is actually heard.
        return new SessionPeer
        {
            Connection = At(0),
            Session = At(1),
            User = name,
            ClientId = ClientIdOf(At(0)),
            RoomKey = At(5),
            Company = At(21),
            Status = At(24),
            Role = At(33),
            Color = At(34),
            Icon = At(35),
            JoinTime = At(37),
            ScreenWidth = At(40),
            ScreenHeight = At(41),
            OsName = At(45),
            OsVersion = At(46),
            OsArch = At(47),
            Fields = fields
        };
    }

    public static string ClientIdOf(string connection)
    {
        int first = connection.IndexOf(':');
        int second = first >= 0 ? connection.IndexOf(':', first + 1) : -1;
        return first >= 0 && second > first ? connection.Substring(first + 1, second - first - 1) : "";
    }
}

public sealed class SessionChat
{
    public string Timestamp { get; set; } = "";
    public string User { get; set; } = "";
    public string Connection { get; set; } = "";
    public string Text { get; set; } = "";

    // True when this line came from the join-time history push rather than the
    // live relay. Used to decide whether our own line is a fresh server echo of
    // something we already showed locally (drop) or genuine history (show).
    public bool FromArchive { get; set; }

}

public sealed class SessionEngine : IDisposable
{
    public const string ClientType = "WebDemo";

    public event Action<SessionPeer>? SelfJoined;
    public event Action<SessionPeer>? PeerAdded;
    public event Action<SessionPeer>? PeerUpdated;
    public event Action<SessionPeer>? PeerRemoved;
    public event Action<SessionChat>? ChatReceived;
    public event Action<byte[]>? ViewportReceived;
    public event Action<byte[]>? VoiceFrameReceived;
    public event Action<string>? VoiceEndpointReceived;
    public event Action<string>? Failed;

    private readonly object _gate = new();
    private readonly Dictionary<string, SessionPeer> _byConnection = new();
    private readonly Dictionary<string, SessionPeer> _byUser = new(StringComparer.OrdinalIgnoreCase);
    private LinkConnection? _connection;

    // Silent voice channel. The server treats the media session as live once the
    // voice applet has an open channel. We send only the announce frame (tag
    // 0x03), which opens the channel without microphone audio. Never send audio
    // frames (tag 0x01): ControlApplet treats buffer[0] < 2 as speech and makes
    // the vendor peers flash our mic icon for 2222 ms.
    private bool _voiceChannelOpened;
    private string _voiceNode = "";
    private bool _voicePending;
    private bool _viewerStarted;

    // Who is speaking. The vendor derives this locally from the inbound voice
    // channel rather than from a server push: each voice frame carries the
    // sender's node id, and PeersApplet lights the "mic active" icon for that
    // node for 2222 ms (PeersApplet.java, VOICE_ICON + 2222). We keep the same
    // window: the timestamp map drives a 500 ms sweeper that clears the flag.
    private const long TalkWindowMs = 2222;
    private readonly Dictionary<string, long> _voiceLastAt = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _talkSweeper;

    public SessionPeer? Self { get; private set; }
    public string SessionId { get; private set; } = "";
    public string User { get; private set; } = "";
    public bool IsConnected => _connection != null;
    public int Route => _connection?.Route ?? 0;

    public IReadOnlyList<SessionPeer> Peers
    {
        get { lock (_gate) { return _byConnection.Values.ToList(); } }
    }

    public bool Join(string host, int port, int type, string sessionId, string user,
        int width, int height, bool asHost, int timeoutMs = 20000)
    {
        SessionId = sessionId;
        User = user;

        LinkConnection connection;
        try
        {
            connection = new LinkConnection(host, port, type, 8192, sessionId, user);
        }
        catch (Exception exception)
        {
            Failed?.Invoke("connect failed: " + exception.Message);
            return false;
        }

        connection.MessageReceived += OnMessage;
        connection.Failed += OnConnectionFailed;
        _connection = connection;
        _viewerStarted = false;
        _voiceChannelOpened = false;
        _voiceNode = "";
        _voicePending = false;

        if (connection.WaitForRoute(timeoutMs) == 0)
        {
            Failed?.Invoke("route negotiation timed out");
            return false;
        }

        // The vendor client sends a heart beat (control=6, request=4002, empty
        // body) immediately after route negotiation and before GROUP_JOIN. The
        // old engine capture (session_20261020_163653) shows it as the first
        // client message; sending it keeps the connection "live" so the server
        // routes applet activation (the control=8 CHAIN) to us.
        try
        {
            connection.Send(Messages.MarshallHeartBeatRequest());
        }
        catch
        {
            // Best-effort: the join below fails cleanly if the link is down.
        }

        // digitalCertificate=true: the vendor's PrivilegeManager.digitalCertificate()
        // reports true for any normal client (it only checks that the JVM can read
        // "user.name"), so real clients advertise certificate=Enable and the full
        // capability set (CAPS_CERT). Advertising the reduced CAPS_NO_CERT told the
        // server we could not receive viewport/screen-share or control frames, so it
        // withheld them (no viewer) -- match the vendor and send the full set.
        Message join = Messages.MarshallJoinGroupRequest(sessionId, ClientType, user, asHost, width, height, false, true);
        try
        {
            connection.Send(join);
        }
        catch (Exception exception)
        {
            Failed?.Invoke("join send failed: " + exception.Message);
            return false;
        }

        // Port of what the vendor client does immediately after a successful
        // join: ChatApplet issues INIT_CONTROL (ConnectionApplet 108 ->
        // marshallInitControlRequest -> control=6, request=4011, [index][0])
        // for index 1 (Statics.CONTROLS[1] == "ChatApplet",
        // ChatApplet.java:951). This is the ONLY applet that sends
        // INIT_CONTROL at join; it is what makes the server start the chat
        // control and push the history/archive frame (control=1, request=1).
        // The Viewer/Viewport applets never send INIT_CONTROL -- they only
        // register control 3/4 listeners, so we must NOT init 3/4 here.
        SendControlInit(connection, Messages.ControlIndexChat);

        // The vendor client tells the server its applets finished preloading by
        // sending marshallNodeUpdate(null, false) -- Message(6, 3001, [u16 0]) --
        // from PeersStubApplet.runPreload() ("preload done!" ->
        // ClassRegistry.doCommand("ConnectionManager", 138) ->
        // ConnectionApplet case 138). The server withholds the control=8 CHAIN
        // (applet activation for Voice/Viewer) until it sees this frame, so
        // without it no viewer is ever started (compare our session 2179961,
        // which got no CHAIN, with the old-engine capture of the same session).
        SendNodeUpdate(connection);

        // Register the loaded ConnectionPoint applet (Statics.CONTROLS[6]) the
        // way the vendor does: Message(6, 4022, [u16 6][u32 0][u16 1]"ConnectionPoint").
        SendFileSystemRegister(connection, 6, "ConnectionPoint");

        // The viewer and voice applets are NOT started by the client at join.
        // The server decides which applets to run and tells the client with a
        // CHAIN of control=8 (Supervisor) request=2001 (CONTROL_START) frames,
        // each carrying [u16 index][u16 0]. Capture session_20261018_223722.cap
        // shows the server sending "start 2 (VoiceApplet)" and "start 3
        // (ViewerApplet)" immediately after the chat archive; the client then
        // replies with its own control=6 request=2001 [index]. Dispatch() handles
        // that StartControl command below. Starting anything here (as the code
        // previously did) activates applets the server never asked for.
        return true;
    }

    // Applet activation requested by the server (control=8 request=2001). The
    // index selects Statics.CONTROLS: 2 = VoiceApplet, 3 = ViewerApplet. The
    // client answers with its own CONTROL_START (control=6 request=2001) and
    // enables the matching local behaviour. Voice is opened WITHOUT touching the
    // microphone; the mic is never muted or unmuted by the engine.
    private void StartServerControl(LinkConnection connection, int index)
    {
        if (index == Messages.ControlIndexViewer)
        {
            // ViewerApplet reply is ONLY the bare control=3 request=0 [03]
            // (UPDATE_REQUEST). Verified against a working vendor trace: after the
            // server's 8/2001 index=3 the vendor's ViewerApplet.doCommand sends
            // just "ctrl=3 req=0 body=03" and the server then streams the viewport
            // CHAIN + pixel frames. The vendor never answers index 3 with its own
            // control=6 request=2001 -- sending that (as we used to) suppresses the
            // stream. De-duplicate: only the first ask pulls the channel.
            if (!_viewerStarted)
            {
                _viewerStarted = true;
                RequestViewportFrame();
            }
        }
        else if (index == Messages.ControlIndexVoice)
        {
            // VoiceApplet: the vendor DOES answer index 2 with its own
            // control=6 request=2001 (body [02 00 00 00]) and then opens the media
            // channel. We never produce microphone audio; the announce alone
            // opens the channel (the vendor trace session_20261018_223722 starts
            // with one announce, followed by audio frames that we omit).
            SendControlStart(connection, index);
            OpenVoiceChannel(connection);
        }
        else
        {
            // Any other index: mirror the vendor's generic CONTROL_START ack.
            SendControlStart(connection, index);
        }
    }

    // Opens the listen-only voice channel with the vendor's announce frame only.
    // Tag 0x03 is not treated as speech by ControlApplet, so peers keep showing
    // the mic as closed. Audio frames (tag 0x01) would make them flash "mic
    // active" for 2222 ms, so we never send them.
    private void OpenVoiceChannel(LinkConnection connection)
    {
        if (_voiceChannelOpened) return;
        _voiceNode = Self?.Connection ?? "";
        if (string.IsNullOrEmpty(_voiceNode))
        {
            // Self may not be known yet; open once the roster arrives.
            _voicePending = true;
            return;
        }

        try
        {
            connection.Send(Messages.MarshallVoiceAnnounce(_voiceNode));
            _voiceChannelOpened = true;
        }
        catch
        {
            // Voice is optional; a failed announce must not fail the session.
        }
    }

    private void OpenVoiceChannelIfPending()
    {
        if (!_voicePending || _connection == null) return;
        _voicePending = false;
        OpenVoiceChannel(_connection);
    }

    private void SendControlStart(LinkConnection connection, int index)
    {
        try
        {
            connection.Send(Messages.MarshallControlStartRequest(index));
        }
        catch
        {
            // A failed control frame must not tear down the session.
        }
    }

    private void SendControlInit(LinkConnection connection, int index)
    {
        try
        {
            connection.Send(Messages.MarshallInitControlRequest(index));
        }
        catch
        {
            // A failed control frame must not tear down the session.
        }
    }

    // "preload done" notification (ConnectionApplet command 138). See Join.
    private void SendNodeUpdate(LinkConnection connection)
    {
        try
        {
            connection.Send(Messages.MarshallNodeUpdateRequest(null));
        }
        catch
        {
            // A failed control frame must not tear down the session.
        }
    }

    // Applet-register frame (control=6, request=4022). See Join.
    private void SendFileSystemRegister(LinkConnection connection, int controlIndex, string appletName)
    {
        try
        {
            connection.Send(Messages.MarshallFileSystemRegisterRequest(controlIndex, appletName));
        }
        catch
        {
            // A failed control frame must not tear down the session.
        }
    }

    // Sends the receiver's one-byte UPDATE_REQUEST (viewport protocol type 3)
    // that pulls the next screen-share frame from the host
    // (ViewportProtocol.bufferUpdateComplete).
    public void RequestViewportFrame()
    {
        LinkConnection? connection = _connection;
        if (connection == null) return;
        try
        {
            connection.Send(new Message(Message.Viewport, Message.Data, new byte[] { ViewportProtocol.UpdateRequest }));
        }
        catch (Exception exception)
        {
            Failed?.Invoke("viewport request failed: " + exception.Message);
        }
    }

    public void SendChat(string text)
    {
        LinkConnection? connection = _connection;
        if (connection == null || string.IsNullOrEmpty(text)) return;

        string stamp = DateTime.Now.ToString("ddd MMM dd HH:mm:ss zzz yyyy", CultureInfo.InvariantCulture);
        string own = Self?.Connection ?? "";
        byte[] body = Messages.MarshallStrings(new[] { stamp, User, own, text });
        Message message = new Message(Message.Chat, Message.Text, body, body.Length, Message.FlagArchive);
        try
        {
            connection.Send(message);
        }
        catch (Exception exception)
        {
            Failed?.Invoke("chat send failed: " + exception.Message);
        }
    }

    private void OnConnectionFailed(LinkConnection connection, string reason) => Failed?.Invoke(reason);
    private void OnMessage(LinkConnection connection, Message message)
    {
        try
        {
            Dispatch(message);
        }
        catch (Exception exception)
        {
            Failed?.Invoke("dispatch failed: " + exception.Message);
        }
    }

    private void Dispatch(Message message)
    {
        int control = message.GetControl();
        int request = message.GetRequest();

        if (control == Message.Chain)
        {
            byte[] nested = message.ReadAll();
            if (nested.Length == 0) return;
            using MemoryStream stream = new MemoryStream(nested, 0, nested.Length, false);
            while (stream.Position + Message.Headersize <= stream.Length)
            {
                Message child = new Message(0, 0, null);
                try { child.ReadFrom(stream); }
                catch { break; }
                Dispatch(child);
            }
            return;
        }

        if (control == Message.Manager)
        {
            if (request == 3000 || request == 3001 || request == 3004 || request == 4004)
            {
                string[] fields = SafeStrings(message);
                if (fields.Length < 3) return;

                SessionPeer peer = SessionPeer.FromFields(fields);
                if (request == 4004 || string.Equals(peer.User, User, StringComparison.OrdinalIgnoreCase))
                {
                    Self = peer;
                    SelfJoined?.Invoke(peer);
                    // If the voice applet was acked before we knew our node string,
                    // open the silent channel now.
                    if (!_voiceChannelOpened && _connection != null)
                    {
                        OpenVoiceChannelIfPending();
                    }
                    return;
                }

                if (request == 3004)
                {
                    RemovePeer(peer);
                    return;
                }

                AddOrUpdate(peer, request == 3000);
            }
            return;
        }

        if (control == Message.Chat)
        {
            // request == Init (1) is the chat archive: the server pushes the
            // session's prior chat history on join as a prologue followed by a
            // stream of nested Chat messages (vendor MessageArchive.unmarshallChatArchive).
            if (request == Message.Init)
            {
                DispatchArchive(message);
                return;
            }

            string[] fields = SafeStrings(message);
            if (request == Message.Text || request == Message.Data)
            {
                EmitChat(fields, false);
            }
            return;
        }

        if (control == Message.Viewport)
        {
            // control=3 carries the screen-share sub-protocol. The decompressed
            // body is a typed command stream consumed by ViewportProtocol; the
            // adapter (LinktivitySessionConnection) owns the decoder and state,
            // so forward the raw decompressed frame.
            byte[] body = message.ReadAll();
            if (body.Length > 0) ViewportReceived?.Invoke(body);
            return;
        }

        if (control == Message.Voice)
        {
            string endpoint = ExtractEndpoint(message);
            if (endpoint.Length > 0) VoiceEndpointReceived?.Invoke(endpoint);
            // Incoming tutor voice: hand the raw frame to the audio layer, which extracts
            // the 256-byte TrueSpeech payload and plays it through the 32-bit helper.
            try
            {
                byte[] frame = message.ReadAll();
                if (frame.Length > 0)
                {
                    VoiceFrameReceived?.Invoke(frame);
                    // The frame also names its sender, which is how the vendor lights
                    // the speaker's mic icon (no server push for "talking").
                    MarkTalking(ExtractVoiceSender(frame));
                }
            }
            catch { }
            return;
        }

        // Applet start requested by the server: control=8 (Supervisor)
        // request=2001 (CONTROL_START), payload [u16 index][u16 0]. This is how
        // the server tells the client to activate a control (PeersStubApplet
        // applyControlMessage case 2001 -> load the control at that index).
        // Capture 223722 shows "start 2 (voice)" and "start 3 (viewer)".
        if (control == Messages.ControlGroup && request == Messages.RequestControlStart)
        {
            int index = message.BufferLength >= 2 ? BufferConversion.BytesToInt(message.Buffer!, 0, 2) : -1;
            StartServerControl(_connection!, index);
            return;
        }

        // Control-channel handshake. The vendor routes these through AppletManager
        // (PeersStubApplet.process): ENABLE_CONTROL (control=8/req=3009) and
        // ACCEPT_CONTROL (control=0/req=3010) carry [u16 index][u16 on]; the
        // CONTROL_COMMAND (control=0/req=3011) payload[0] names the applet the
        // server wants started (e.g. "ViewportApplet").
        if ((control == Messages.ControlGroup && request == Messages.RequestEnableControl)
            || (control == Messages.ControlManager && request == Messages.RequestAcceptControl))
        {
            int index = message.BufferLength >= 2 ? BufferConversion.BytesToInt(message.Buffer!, 0, 2) : -1;
            int value = message.BufferLength >= 4 ? BufferConversion.BytesToInt(message.Buffer!, 2, 2) : 0;
            // Voice index 2 is the tutor's enable/disable-mic request. This client
            // is listen-only and sends no periodic voice frames, so there is no
            // outbound mic to re-enable; deliberately do not restart voice traffic.
            return;
        }
    }

    private void EmitChat(string[] fields, bool fromArchive)
    {
        ChatReceived?.Invoke(new SessionChat
        {
            Timestamp = At(fields, 0),
            User = At(fields, 1),
            Connection = At(fields, 2),
            Text = At(fields, 3),
            FromArchive = fromArchive
        });
    }

    // MessageArchive.unmarshallChatArchive (ChatApplet.java:501). The archive
    // body is a sequence of "[8-byte gap][complete nested Message]" records: the
    // leading 8 bytes are a prologue, and every nested frame (the archive summary
    // control=8/req=5000, then one control=1 record per past chat line) is
    // preceded by another 8-byte gap. The vendor loop reads those 8 bytes, then
    // constructs a Message from the stream; only control==Chat(1) records carry a
    // chat entry, unmarshalled as [timestamp, user, node, text]. Walking the body
    // as directly-concatenated 16-byte frames (no gaps) stops at the first record.
    private void DispatchArchive(Message message)
    {
        byte[] body = message.Buffer ?? Array.Empty<byte>();
        int length = message.BufferLength;
        if (length <= 8) return;

        using MemoryStream stream = new MemoryStream(body, 0, length, false);
        while (stream.Position + 8 + Message.Headersize <= stream.Length)
        {
            stream.Seek(8, SeekOrigin.Current);

            Message child = new Message(0, 0, null);
            try { child.ReadFrom(stream); }
            catch { break; }

            if (child.GetControl() != Message.Chat) continue;
            EmitChat(SafeStrings(child), true);
        }
    }

    private void AddOrUpdate(SessionPeer peer, bool added)
    {
        if (string.IsNullOrEmpty(peer.Connection)) return;

        SessionPeer? existing;
        lock (_gate)
        {
            _byConnection.TryGetValue(peer.Connection, out existing);

            // A roster update replaces the whole peer object, so carry the live
            // "talking" flag over -- otherwise an unrelated 3001 update would
            // blank the speaker icon mid-sentence.
            if (existing is { VoiceTalking: true })
            {
                peer.VoiceTalking = true;
            }

            _byConnection[peer.Connection] = peer;
            if (!string.IsNullOrEmpty(peer.User)) _byUser[peer.User] = peer;
        }

        if (added && existing == null) PeerAdded?.Invoke(peer);
        else PeerUpdated?.Invoke(peer);
    }

    /// <summary>
    /// Reads the sender node id out of a voice frame. Layout (verified against
    /// live frames): <c>[u16 count=1][u16 strLen][node (strLen-5 ASCII chars)][NUL][em]</c>.
    /// The node string is exactly the peer's roster <c>Connection</c> value.
    /// </summary>
    private static string ExtractVoiceSender(byte[] frame)
    {
        if (frame.Length < 8) return "";

        ushort count = (ushort)(frame[0] | (frame[1] << 8));
        int strLen = frame[2] | (frame[3] << 8);
        if (count != 1 || strLen < 6) return "";

        int length = strLen - 5;
        if (4 + length > frame.Length) return "";

        int end = 4;
        while (end < 4 + length && frame[end] != 0) end++;
        if (end == 4) return "";
        return Encoding.ASCII.GetString(frame, 4, end - 4);
    }

    /// <summary>
    /// Marks the peer that owns <paramref name="node"/> as speaking and starts the
    /// sweeper that clears the flag once the peer has been silent for
    /// <see cref="TalkWindowMs"/> (the vendor's mic-active icon window).
    /// </summary>
    private void MarkTalking(string node)
    {
        if (string.IsNullOrEmpty(node)) return;

        long now = Environment.TickCount64;
        SessionPeer? peer = null;
        bool flipped = false;
        lock (_gate)
        {
            _voiceLastAt[node] = now;
            _talkSweeper ??= new System.Threading.Timer(ClearStaleTalk, null, 500, 500);

            if (!_byConnection.TryGetValue(node, out peer)) return;

            // Hearing a member is the only truthful confirmation that their mic
            // is live, so this is what turns the idle "on" icon on.
            bool upgraded = !peer.VoiceEnabled;
            peer.VoiceEnabled = true;
            bool wasTalking = peer.VoiceTalking;
            peer.VoiceTalking = true;
            flipped = upgraded || !wasTalking;
            if (!flipped) return;
        }

        if (flipped) PeerUpdated?.Invoke(peer!);
    }

    private void ClearStaleTalk(object? state)
    {
        long now = Environment.TickCount64;
        List<SessionPeer> done = new List<SessionPeer>();
        lock (_gate)
        {
            foreach (SessionPeer peer in _byConnection.Values)
            {
                if (!peer.VoiceTalking) continue;
                if (!_voiceLastAt.TryGetValue(peer.Connection, out long last) || now - last > TalkWindowMs)
                {
                    peer.VoiceTalking = false;
                    done.Add(peer);
                }
            }

            // Keep the timestamp map from growing across a long session.
            if (_voiceLastAt.Count > 64)
            {
                foreach (string key in _voiceLastAt.Where(pair => now - pair.Value > 60000).Select(pair => pair.Key).ToList())
                {
                    _voiceLastAt.Remove(key);
                }
            }
        }

        foreach (SessionPeer peer in done) PeerUpdated?.Invoke(peer);
    }

    private void RemovePeer(SessionPeer peer)
    {
        SessionPeer? removed = null;
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(peer.Connection) && _byConnection.Remove(peer.Connection, out SessionPeer? byConnection))
            {
                removed = byConnection;
            }
            else if (!string.IsNullOrEmpty(peer.User) && _byUser.Remove(peer.User, out SessionPeer? byUser))
            {
                removed = byUser;
                if (!string.IsNullOrEmpty(removed.Connection)) _byConnection.Remove(removed.Connection);
            }
        }

        if (removed != null) PeerRemoved?.Invoke(removed);
    }

    private static string[] SafeStrings(Message message)
    {
        try { return Messages.UnmarshallStrings(message); }
        catch { return Array.Empty<string>(); }
    }

    private static string At(string[] fields, int index) => index < fields.Length ? fields[index] : "";

    private static string ExtractEndpoint(Message message)
    {
        byte[] body = message.Buffer ?? Array.Empty<byte>();
        int limit = Math.Min(body.Length, 256);
        for (int i = 0; i < limit; i++)
        {
            if (body[i] != (byte)'T') continue;
            if (i + 3 < body.Length && body[i + 1] == (byte)'C' && body[i + 2] == (byte)'P' && body[i + 3] == (byte)':')
            {
                int end = i;
                while (end < body.Length && body[end] != 0) end++;
                return Encoding.ASCII.GetString(body, i, end - i);
            }
        }
        return "";
    }

    public void Dispose()
    {
        System.Threading.Timer? talk = _talkSweeper;
        _talkSweeper = null;
        talk?.Dispose();

        LinkConnection? connection = _connection;
        _connection = null;
        if (connection == null) return;

        connection.MessageReceived -= OnMessage;
        connection.Failed -= OnConnectionFailed;
        try { connection.Dispose(); }
        catch { }
    }
}
