(function () {
  "use strict";

  var host = (window.chrome && window.chrome.webview) ? window.chrome.webview : null;

  var el = {
    transcript: document.getElementById("transcript"),
    emptyState: document.getElementById("emptyState"),
    input: document.getElementById("input"),
    sendBtn: document.getElementById("sendBtn"),
    leaveBtn: document.getElementById("leaveBtn"),
    memberList: document.getElementById("memberList"),
    memberCount: document.getElementById("memberCount"),
    chatSub: document.getElementById("chatSub"),
    roleLabel: document.getElementById("roleLabel"),
    splash: document.getElementById("splash"),
    splashTitle: document.getElementById("splashTitle"),
    splashFail: document.getElementById("splashFail"),
    splashRetry: document.getElementById("splashRetry")
  };

  var self = { name: null, host: false };
  var roster = [];
  var roomLastAuthor = null;
  var announcedJoin = false;

  // ---------------------------------------------------------------- connection status

  // Live link state for the header subtitle. A heartbeat (PING) is sent to the
  // engine every few seconds; the engine always answers with a "pong" while it
  // is alive. Network loss is read straight from the browser, so the header
  // stops claiming "Connected" the moment the Wi-Fi drops instead of freezing
  // on the last status it ever received.
  //
  // "ready" is the key to not crying wolf on join: the engine takes a while to
  // boot the JVM and build its applets, during which nothing is heard back. We
  // only start treating silence as a failure once the engine has actually
  // spoken to us at least once.
  var conn = {
    up: true,
    ready: false,
    reason: "",
    upText: "Connecting\u2026",
    fixed: "",
    nextProbe: 0,
    lastLive: Date.now()
  };

  var PROBE_MS = 3000;
  var STALL_MS = 7000;

  function isOfflineNet() {
    return typeof navigator !== "undefined" && navigator.onLine === false;
  }

  function renderComposer() {
    var down = !conn.up;

    el.input.placeholder = down
      ? (isOfflineNet() ? "No internet connection" : "Reconnecting\u2026")
      : "Type a message\u2026";
    el.sendBtn.disabled = down || el.input.value.trim().length === 0;
  }

  function renderSub() {
    renderComposer();

    if (conn.fixed) {
      el.chatSub.classList.add("off");
      el.chatSub.textContent = conn.fixed;
      return;
    }

    if (conn.up) {
      el.chatSub.classList.remove("off");
      el.chatSub.textContent = conn.upText;
      return;
    }

    el.chatSub.classList.add("off");

    // While the machine is offline there is nothing to retry yet, so do not
    // leave a countdown frozen on screen; wait for the browser's online event.
    if (isOfflineNet()) {
      el.chatSub.textContent = conn.reason || "No internet connection";
      return;
    }

    var left = Math.max(0, Math.ceil((conn.nextProbe - Date.now()) / 1000));
    el.chatSub.textContent = conn.reason + " \u00b7 retrying in " + left + "s";
  }

  function setUp(text) {
    conn.fixed = "";
    if (typeof text === "string" && text.length > 0) {
      conn.upText = text;
    }

    if (!conn.up) {
      conn.up = true;
      conn.nextProbe = 0;
    }

    renderSub();
  }

  function setDown(reason) {
    if (conn.up) {
      conn.up = false;
    }

    conn.reason = reason;
    conn.nextProbe = Date.now() + PROBE_MS;
    renderSub();
  }

  function noteLive() {
    conn.lastLive = Date.now();
    if (!conn.up && !isOfflineNet()) {
      setUp();
    }
  }

  // The engine only speaks once it is actually up, so the first event (or pong)
  // ends the "connecting" grace period. After that, silence is a real signal.
  function markEstablished() {
    conn.ready = true;
  }

  // A fast join can deliver "hello" before the loader has even painted; keep it
  // visible for at least one intro so the animation is never skipped.
  var SPLASH_MIN_MS = 2500;
  var splashShownAt = performance.now();
  var splashHideTimer = null;

  function hideSplash() {
    if (!el.splash) {
      return;
    }

    var remaining = SPLASH_MIN_MS - (performance.now() - splashShownAt);
    if (remaining > 0) {
      if (splashHideTimer === null) {
        splashHideTimer = setTimeout(function () {
          splashHideTimer = null;
          hideSplash();
        }, remaining);
      }
      return;
    }

    if (splashHideTimer !== null) {
      clearTimeout(splashHideTimer);
      splashHideTimer = null;
    }

    el.splash.classList.add("hidden");
    // Let the fade/scale finish before we remove it from the tab order/flow
    setTimeout(function () {
      if (el.splash) {
        el.splash.style.display = "none";
      }
    }, 460);
  }

  // Join failure: every silent attempt was spent, so show the loader area as a
  // "check your internet" card with Retry instead (the engine never opened a
  // browser/file picker for this). The loader stage stays dimmed behind it.
  function showJoinFailed() {
    conn.fixed = "No internet connection";
    conn.up = false;
    renderSub();

    if (splashHideTimer !== null) {
      clearTimeout(splashHideTimer);
      splashHideTimer = null;
    }

    if (el.splash) {
      el.splash.classList.remove("hidden");
      el.splash.style.display = "";
      el.splash.classList.add("failed");
    }

    if (el.splashFail) {
      el.splashFail.style.display = "";
    }
  }

  // Back to the loading state, used at the start of each (re)attempt.
  function resetToConnecting() {
    conn.fixed = "";
    conn.ready = false;
    conn.up = true;
    conn.upText = "Connecting\u2026";

    if (el.splashFail) {
      el.splashFail.style.display = "none";
    }

    if (splashHideTimer !== null) {
      clearTimeout(splashHideTimer);
      splashHideTimer = null;
    }
    splashShownAt = performance.now();

    if (el.splash) {
      el.splash.classList.remove("failed");
      el.splash.classList.remove("hidden");
      el.splash.style.display = "";
      if (el.splashTitle) {
        el.splashTitle.textContent = "Loading session\u2026";
      }
    }

    renderSub();
  }

  function probe() {
    if (host) {
      host.postMessage({ type: "ping" });
    }
  }

  function tickConn() {
    var now = Date.now();

    if (conn.fixed) {
      // A terminal "check your internet" state waits for the user's Retry.
      return;
    }

    if (isOfflineNet()) {
      if (conn.up || conn.reason !== "No internet connection") {
        setDown("No internet connection");
      }

      return;
    }

    if (!conn.ready) {
      // Joining: the engine is still booting. Never mistake that delay for a
      // network failure -- keep the "Connecting…" copy and just keep poking so
      // we notice the instant the engine answers.
      if (conn.nextProbe === 0 || now >= conn.nextProbe) {
        conn.nextProbe = now + PROBE_MS;
        probe();
      }

      return;
    }

    if (conn.up) {
      if (now - conn.lastLive > STALL_MS) {
        setDown("Connection failed");
        return;
      }

      if (conn.nextProbe === 0 || now >= conn.nextProbe) {
        conn.nextProbe = now + PROBE_MS;
        probe();
      }

      return;
    }

    renderSub();
    if (now >= conn.nextProbe) {
      conn.nextProbe = now + PROBE_MS;
      probe();
      // Do not flip back to "connected" here: only a real event (or the
      // wrapper's pong for a joined link) proves the room is back. A timer
      // alone would re-enable the composer during a rejoin before hello.
    }
  }

  window.addEventListener("online", function () {
    // A terminal join failure waits for the user's Retry; do not clear it
    // just because the adapter came back.
    if (conn.fixed) {
      return;
    }

    conn.nextProbe = 0;

    if (!announcedJoin) {
      conn.lastLive = Date.now();
      setUp();
      return;
    }

    // Mid-session rejoin: stay down until the engine actually answers. Put the
    // reconnect copy back after the offline banner so the header does not keep
    // claiming "No internet connection" once the adapter is back.
    if (!conn.up) {
      setDown("Connection lost");
    }

    probe();
  });

  window.addEventListener("offline", function () {
    setDown("No internet connection");
  });

  // ---------------------------------------------------------------- helpers

  function initials(name) {
    if (!name) {
      return "?";
    }

    var trimmed = name.trim();
    if (/^t_/i.test(trimmed)) {
      var code = trimmed.slice(2, 4);
      if (code) {
        return code.toUpperCase();
      }
    }

    var parts = trimmed.split(/\s+/);
    if (parts.length === 1) {
      return parts[0].slice(0, 2).toUpperCase();
    }

    return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
  }

  function colorFor(name) {
    var palette = ["#e05a5a", "#5e6fc9", "#2e9e8f", "#e08a2b", "#a85ac0", "#2fa6d8", "#9a7b6a", "#7b68c9"];
    var hash = 0;
    var s = name || "";
    for (var i = 0; i < s.length; i++) {
      hash = (hash * 31 + s.charCodeAt(i)) >>> 0;
    }

    return palette[hash % palette.length];
  }

  function fmtTime(ts) {
    var d = ts ? new Date(ts) : new Date();
    return d.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
  }

  function isAtBottom(node) {
    return node.scrollHeight - node.scrollTop - node.clientHeight < 120;
  }

  function scrollToBottom(node) {
    node.scrollTop = node.scrollHeight;
  }

  function makeAvatar(name) {
    var a = document.createElement("div");
    a.className = "avatar";
    a.style.background = colorFor(name);
    a.textContent = initials(name);
    return a;
  }

  // ---------------------------------------------------------------- links

  var URL_RE = /https?:\/\/[^\s<>"']+/g;

  // Trailing punctuation that is almost never part of the intended link, plus
  // an unmatched closing bracket, is split back off, so "see https://x.com."
  // links to https://x.com rather than "https://x.com.".
  function splitUrl(raw) {
    var url = raw;
    var trail = "";
    var m = url.match(/[.,;:!?]+$/);
    if (m) {
      trail = m[0] + trail;
      url = url.slice(0, -m[0].length);
    }

    while (/\)$/.test(url) && url.split("(").length < url.split(")").length) {
      url = url.slice(0, -1);
      trail = ")" + trail;
    }

    return { url: url, trail: trail };
  }

  // Renders text into `node`, turning http/https links into real anchors. All
  // content is added as text nodes/attributes (never innerHTML), so a message
  // can never inject markup.
  function appendLinkedText(node, text) {
    if (!text) {
      return;
    }

    URL_RE.lastIndex = 0;
    var last = 0;
    var match;
    while ((match = URL_RE.exec(text)) !== null) {
      var parts = splitUrl(match[0]);

      if (match.index > last) {
        node.appendChild(document.createTextNode(text.slice(last, match.index)));
      }

      if (parts.url) {
        var a = document.createElement("a");
        a.href = parts.url;
        a.textContent = parts.url;
        a.rel = "noopener noreferrer";
        node.appendChild(a);
      }

      if (parts.trail) {
        node.appendChild(document.createTextNode(parts.trail));
      }

      last = match.index + match[0].length;
    }

    if (last < text.length) {
      node.appendChild(document.createTextNode(text.slice(last)));
    }
  }

  // ---------------------------------------------------------------- message rendering

  function isSelf(m) {
    // The username is unique, so a message is ours exactly when its author
    // matches our own name. This drives the "out" (green) styling and never
    // depends on what the server labelled the message.
    return self.name != null && (m.name || "Unknown") === self.name;
  }

  function makeMessageEl(m, grouped) {
    var name = m.name || "Unknown";
    var row = document.createElement("div");
    row.className = "msg" + (isSelf(m) ? " out" : "") + (grouped ? " grouped" : "");

    row.appendChild(makeAvatar(name));

    var wrap = document.createElement("div");
    wrap.className = "bubble-wrap";

    var meta = document.createElement("div");
    meta.className = "meta";
    var nameEl = document.createElement("span");
    nameEl.className = "name";
    nameEl.textContent = name;
    meta.appendChild(nameEl);
    if (m.host) {
      var badge = document.createElement("span");
      badge.className = "badge";
      badge.textContent = "Tutor";
      meta.appendChild(badge);
    }

    var time = document.createElement("span");
    time.className = "time";
    time.textContent = fmtTime(m.ts);
    meta.appendChild(time);
    wrap.appendChild(meta);

    var bubble = document.createElement("div");
    bubble.className = "bubble";
    bubble.setAttribute("dir", "auto");
    appendLinkedText(bubble, m.text || "");
    wrap.appendChild(bubble);

    row.appendChild(wrap);
    return row;
  }

  function hideEmpty() {
    if (el.emptyState && el.emptyState.parentNode) {
      el.emptyState.parentNode.removeChild(el.emptyState);
      el.emptyState = null;
    }
  }

  function addMessage(m) {
    hideEmpty();
    var atBottom = isAtBottom(el.transcript);
    var name = m.name || "Unknown";
    var grouped = roomLastAuthor === name;

    var row = makeMessageEl(m, grouped);
    row.classList.add("is-new");
    el.transcript.appendChild(row);
    roomLastAuthor = name;

    if (atBottom || isSelf(m)) {
      scrollToBottom(el.transcript);
    }
  }

  // ---------------------------------------------------------------- roster

  // The roster includes you so the room count is right, but you are never
  // drawn into the member panels.
  function notSelf(m) {
    return self.name == null || m.name !== self.name;
  }

  function renderMembers(hosts, users) {
    hosts = hosts || [];
    users = users || [];
    roster = [];

    hosts.forEach(function (h) {
      roster.push({ name: h.name, node: h.node, host: true, mic: h.mic });
    });
    users.forEach(function (u) {
      roster.push({ name: u.name, node: u.node, host: false, mic: u.mic });
    });

    // You are counted in the room (the total includes you) but you are not
    // listed: you don't need to see your own name among the students.
    var shownHosts = hosts.filter(notSelf);
    var shownUsers = users.filter(notSelf);

    el.memberList.textContent = "";
    el.memberCount.textContent = String(roster.length);

    if (shownHosts.length === 0 && shownUsers.length === 0) {
      var empty = document.createElement("div");
      empty.className = "empty small";
      empty.textContent = "Waiting for others to join";
      el.memberList.appendChild(empty);
    } else {
      group("Tutor", shownHosts, true);
      group("Students", shownUsers, false);
    }
  }

  function group(label, list, isHost) {
    if (list.length === 0) {
      return;
    }

    var gl = document.createElement("div");
    gl.className = "group-label";
    gl.textContent = label;
    el.memberList.appendChild(gl);

    list.forEach(function (member) {
      el.memberList.appendChild(makeMemberRow(member.name, member.node, isHost, member.mic));
    });
  }

  function makeMemberRow(name, node, isHost, mic) {
    var row = document.createElement("div");
    row.className = "member";
    row.appendChild(makeAvatar(name));

    var n = document.createElement("span");
    n.className = "m-name";
    n.textContent = name;
    row.appendChild(n);

    if (isHost) {
      var b = document.createElement("span");
      b.className = "m-badge";
      b.textContent = "Tutor";
      row.appendChild(b);
    }

    row.appendChild(makeMicIcon(mic));
    return row;
  }

  // ---------------------------------------------------------------- member mic status

  // Draws a member's microphone state beside their name, mirroring the vendor's
  // peer voice icon: 0 = mic off, 1 = mic on (idle), 2 = speaking. The state
  // arrives on each peer entry as `mic` (see the engine's SessionPeer.Mic).
  function makeMicIcon(mic) {
    var state = typeof mic === "number" ? mic : 0;
    var s = document.createElement("span");
    s.className = "m-mic mic-" + state;
    s.title = state === 2 ? "Speaking" : (state === 1 ? "Microphone on" : "Microphone off");
    s.setAttribute("aria-label", s.title);

    var svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("viewBox", "0 0 24 24");
    svg.setAttribute("width", "14");
    svg.setAttribute("height", "14");
    svg.setAttribute("fill", "none");
    svg.setAttribute("stroke", "currentColor");
    svg.setAttribute("stroke-width", "2");
    svg.setAttribute("stroke-linecap", "round");
    svg.setAttribute("stroke-linejoin", "round");

    var body = document.createElementNS("http://www.w3.org/2000/svg", "path");
    body.setAttribute("d", "M12 2a3 3 0 0 0-3 3v6a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3z");
    svg.appendChild(body);

    var stand = document.createElementNS("http://www.w3.org/2000/svg", "path");
    stand.setAttribute("d", "M5 11a7 7 0 0 0 14 0");
    svg.appendChild(stand);

    var stem = document.createElementNS("http://www.w3.org/2000/svg", "path");
    stem.setAttribute("d", "M12 18v3");
    svg.appendChild(stem);

    // A slash through the glyph reads as "muted" at a glance.
    if (state === 0) {
      var slash = document.createElementNS("http://www.w3.org/2000/svg", "path");
      slash.setAttribute("d", "M4 4l16 16");
      svg.appendChild(slash);
    }

    s.appendChild(svg);
    return s;
  }

  // ---------------------------------------------------------------- engine

  function onEngine(m) {
    if (!m || !m.t) {
      return;
    }

    // Synthetic events from the launcher's retry wrapper describe the join
    // attempt, not a live engine, so they must not count as a heartbeat
    // (which would wrongly mark the link established/live).
    if (m.t === "retrying") {
      // The first join keeps its full loader. A rejoin after a live drop must
      // leave the room on screen: show the reconnect status and keep the
      // composer disabled until hello arrives.
      if (announcedJoin) {
        setDown("Connection lost");
      } else {
        resetToConnecting();
      }

      return;
    }

    if (m.t === "joinFailed") {
      if (announcedJoin) {
        setDown("Connection failed");
      } else {
        showJoinFailed();
      }

      return;
    }

    noteLive();
    markEstablished();

    switch (m.t) {
      case "ready":
        if (m.role) {
          el.roleLabel.textContent = m.role === "Host" ? "Tutor" : "Student";
        }

        break;
      case "hello":
        self.name = m.name;
        self.host = !!m.host;
        el.roleLabel.textContent = self.host ? "Tutor" : "Student";
        setUp(m.host ? "You are the tutor" : "Connected");

        // Tell the host we are genuinely in the session, so it can reveal the chat
        // and drop the loading screen. This is the page's own "connected" moment
        // (the subtitle above just flipped off "Connecting…"), so the two always
        // agree -- unlike the engine's earlier lifecycle events.
        if (!announcedJoin) {
          announcedJoin = true;
          if (host) {
            host.postMessage({ type: "joined" });
          }
          hideSplash();
        }

        break;
      case "chat":
        addMessage(m);
        break;
      case "peers":
        renderMembers(m.hosts, m.users);
        break;
      case "status":
        if (m.text) {
          setUp(m.text);
        }

        break;
    }
  }

  if (host) {
    host.addEventListener("message", function (ev) {
      onEngine(ev.data);
    });
  }

  // ---------------------------------------------------------------- room composer

  function send() {
    var text = el.input.value.replace(/\s+$/, "");
    if (!text || !conn.up) {
      return;
    }

    if (host) {
      host.postMessage({ type: "send", text: text });
    }

    el.input.value = "";
    autoGrow(el.input);
    el.sendBtn.disabled = true;
    el.input.focus();
  }

  function autoGrow(node) {
    node.style.height = "auto";
    node.style.height = Math.min(node.scrollHeight, 160) + "px";
  }

  el.input.addEventListener("input", function () {
    autoGrow(el.input);
    el.sendBtn.disabled = !conn.up || el.input.value.trim().length === 0;
  });

  el.input.addEventListener("keydown", function (e) {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      send();
    }
  });

  el.sendBtn.addEventListener("click", send);
  el.sendBtn.disabled = true;

  el.leaveBtn.addEventListener("click", function () {
    if (host) {
      host.postMessage({ type: "leave" });
    }
  });

  // ---------------------------------------------------------------- boot

  autoGrow(el.input);
  renderSub();
  setInterval(tickConn, 500);
  el.input.focus();

  if (el.splashRetry) {
    el.splashRetry.addEventListener("click", function () {
      resetToConnecting();
      if (host) {
        host.postMessage({ type: "retry" });
      }
    });
  }

  if (host) {
    host.postMessage({ type: "ready" });
  }
})();
