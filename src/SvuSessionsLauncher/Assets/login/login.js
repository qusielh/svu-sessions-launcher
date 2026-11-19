(function () {
    "use strict";

    var host = window.chrome && window.chrome.webview ? window.chrome.webview : null;
    var root = document.documentElement;
    var params = new URLSearchParams(window.location.search);
    var strings = {};
    var focusedOnce = false;
    var rendered = false;

    var form = document.getElementById("form");
    var username = document.getElementById("username");
    var password = document.getElementById("password");
    var savePassword = document.getElementById("savePassword");
    var autoSignIn = document.getElementById("autoSignIn");
    var togglePassword = document.getElementById("togglePassword");
    var error = document.getElementById("error");
    var errorText = document.getElementById("errorText");
    var submit = document.getElementById("submit");
    var submitLabel = document.getElementById("submitLabel");

    function signalRendered() {
        if (rendered) {
            return;
        }

        var sent = false;
        function finish() {
            if (sent) {
                return;
            }

            sent = true;
            window.setTimeout(function () {
                rendered = true;
                send("rendered");
            }, 16);
        }

        if (document.fonts && document.fonts.ready) {
            document.fonts.ready.then(finish, finish);
            window.setTimeout(finish, 500);
        } else {
            finish();
        }
    }

    function send(type, data) {
        if (!host) {
            return;
        }

        var message = { type: type };
        if (data) {
            Object.keys(data).forEach(function (key) {
                message[key] = data[key];
            });
        }
        host.postMessage(message);
    }

    document.querySelectorAll("[data-window]").forEach(function (button) {
        button.addEventListener("click", function () {
            send(button.dataset.window);
        });
    });

    var dragBar = document.querySelector("[data-drag]");
    if (dragBar) {
        dragBar.addEventListener("pointerdown", function (event) {
            if (event.button !== 0 || event.target.closest("[data-window]")) {
                return;
            }

            event.preventDefault();
            send("drag");
        });
    }

    function hex(name, fallback) {
        var value = params.get(name);
        return value && /^[0-9a-f]{6}$/i.test(value) ? "#" + value : fallback;
    }

    function applyTheme() {
        root.dataset.theme = params.get("theme") === "dark" ? "dark" : "light";
        root.style.setProperty("--accent", hex("accent", "#0f766e"));
        root.style.setProperty("--accent-hover", hex("accentHover", "#2f9188"));
        root.style.setProperty("--accent-pressed", hex("accentStrong", "#0b5f59"));
        root.style.setProperty("--on-accent", hex("ink", "#ffffff"));
        root.style.setProperty("--hero-a", hex("hd0", "#2f9188"));
        root.style.setProperty("--hero-b", hex("hd1", "#0f766e"));
        root.style.setProperty("--hero-c", hex("hd2", "#0b5f59"));
    }

    function applyStrings() {
        document.querySelectorAll("[data-t]").forEach(function (element) {
            var value = strings[element.dataset.t];
            if (typeof value === "string") {
                element.textContent = value;
            }
        });

        username.placeholder = strings["login.username.placeholder"] || "";
        password.placeholder = strings["login.password.placeholder"] || "";
        togglePassword.setAttribute("aria-label", strings["login.showPassword"] || "");
    }

    function applyState(state) {
        if (state.language === "en" || state.language === "ar") {
            root.lang = state.language;
            root.dir = state.language === "ar" ? "rtl" : "ltr";
        }

        if (state.strings) {
            strings = state.strings;
            applyStrings();
        }

        if (document.activeElement !== username && username.value !== state.username) {
            username.value = state.username || "";
        }

        if (document.activeElement !== password && password.value !== state.password) {
            password.value = state.password || "";
        }

        savePassword.checked = !!state.savePassword;
        autoSignIn.checked = !!state.autoSignIn;
        togglePassword.classList.toggle("is-visible", !!state.showPassword);
        password.type = state.showPassword ? "text" : "password";

        error.hidden = !state.hasError;
        errorText.textContent = state.error || "";

        submit.disabled = !state.canSignIn;
        submit.classList.toggle("is-busy", !!state.busy);
        submitLabel.textContent = state.busy
            ? (strings["login.signingIn"] || "Signing in...")
            : (strings["login.signIn"] || "Sign in");

        document.querySelectorAll("[data-lang]").forEach(function (button) {
            button.classList.toggle("is-active", button.dataset.lang === state.language);
        });

        document.body.dataset.ready = "1";
        signalRendered();

        if (!focusedOnce) {
            focusedOnce = true;
            var target = state.username ? password : username;
            window.setTimeout(function () {
                if (!document.activeElement || document.activeElement === document.body) {
                    target.focus();
                }
            }, 60);
        }
    }

    username.addEventListener("input", function () {
        send("username", { value: username.value });
    });

    password.addEventListener("input", function () {
        send("password", { value: password.value });
    });

    savePassword.addEventListener("change", function () {
        savePassword.closest(".options").classList.add("is-interactive");
        send("savePassword", { value: savePassword.checked });
    });

    autoSignIn.addEventListener("change", function () {
        autoSignIn.closest(".options").classList.add("is-interactive");
        send("autoSignIn", { value: autoSignIn.checked });
    });

    togglePassword.addEventListener("click", function () {
        var visible = password.type === "password";
        password.type = visible ? "text" : "password";
        togglePassword.classList.toggle("is-visible", visible);
        send("showPassword", { value: visible });
    });

    document.querySelectorAll("[data-lang]").forEach(function (button) {
        button.addEventListener("click", function () {
            send("language", { code: button.dataset.lang });
        });
    });

    form.addEventListener("submit", function (event) {
        event.preventDefault();
        if (!submit.disabled) {
            submit.disabled = true;
            submit.classList.add("is-busy");
            send("signIn");
        }
    });

    if (host) {
        host.addEventListener("message", function (event) {
            var message = event.data;
            if (message && message.type === "login") {
                applyState(message);
            }
        });
    }

    applyTheme();
    send("ready");

    window.setTimeout(function () {
        if (document.body.dataset.ready !== "1") {
            document.body.dataset.ready = "1";
            signalRendered();
        }
    }, 2000);
})();
