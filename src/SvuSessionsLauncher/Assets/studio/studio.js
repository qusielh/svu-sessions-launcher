(function () {
    "use strict";

    const $ = (selector, root) => (root || document).querySelector(selector);
    const $$ = (selector, root) => Array.from((root || document).querySelectorAll(selector));

    const bridge = window.chrome && window.chrome.webview;

    if ("scrollRestoration" in history) {
        history.scrollRestoration = "none";
    }

    const ITEM = 74;
    const GAP = 8;
    const TOP = 13;
    const OVERSCAN = 5;

    let state = null;
    let querySignature = "";
    let dataSignature = "";
    let renderedSignature = "";
    let scrollFrame = 0;
    let listBound = false;
    let hadList = false;
    let pendingSessions = null;
    let pendingDownloads = null;
    let lastDataSignature = "";
    let appearanceSignature = "";
    let presetsExpanded = false;
    let colorFrame = 0;
    let pendingColorHex = "";
    let downloadListSignature = "";
    let downloadsOpen = false;
    let activeSelect = null;
    let activeSelectIndex = -1;
    let activeSelectOptions = [];
    let selectQuery = "";
    let rendered = false;
    const selectLists = new WeakMap();

    const fallback = {
        "main.title": "Online Session Launcher",
        "main.sessions": "Sessions",
        "main.program": "Programme",
        "main.course": "Course",
        "main.options": "Show",
        "main.refresh": "Refresh",
        "main.filterTutor": "Tutor\u2026",
        "main.filterClass": "Class (C1)\u2026",
        "main.emptySessions": "No sessions to show right now.",
        "main.loadingSessions": "Loading sessions\u2026",
        "main.noMatches": "No sessions match your filters.",
        "main.launch": "Launch session",
        "col.start": "Starts",
        "col.course": "Course",
        "col.tutor": "Tutor",
        "col.session": "Session",
        "col.class": "Class",
        "col.server": "Server",
        "diag.button": "Diagnostics",
        "select.search": "Type to find\u2026",
        "select.empty": "No matches."
    };

    function t(key) {
        if (state && state.strings && state.strings[key]) {
            return state.strings[key];
        }
        return fallback[key] || key;
    }

    function esc(value) {
        return String(value === null || value === undefined ? "" : value).replace(/[&<>"']/g, function (c) {
            return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
        });
    }

    function icon(id, extra) {
        return '<svg class="icon ' + (extra || "") + '"><use href="#' + id + '"></use></svg>';
    }

    function send(action, payload) {
        if (!bridge) {
            return;
        }
        bridge.postMessage(Object.assign({ action: action }, payload || {}));
    }

    function signalRendered() {
        if (rendered) {
            return;
        }

        let sent = false;
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

    /* ---------- theming ---------- */

    const themeMap = {
        bg: "--bg",
        surface: "--surface",
        surfaceAlt: "--surface-alt",
        border: "--border",
        borderStrong: "--border-strong",
        text: "--text",
        muted: "--muted",
        primary: "--primary",
        primaryHover: "--primary-hover",
        primaryPressed: "--primary-pressed",
        onPrimary: "--on-primary",
        primarySoft: "--primary-soft",
        selection: "--selection",
        danger: "--danger",
        accentEdge: "--accent-edge",
        headerA: "--header-a",
        headerB: "--header-b",
        headerC: "--header-c",
        okBg: "--ok-bg",
        okFg: "--ok-fg",
        progressBg: "--progress-bg",
        progressFg: "--progress-fg",
        doneBg: "--done-bg",
        doneFg: "--done-fg",
        cancelBg: "--cancel-bg",
        cancelFg: "--cancel-fg"
    };

    function applyTheme(theme) {
        if (!theme) {
            return;
        }

        const root = document.documentElement;
        Object.keys(themeMap).forEach(function (key) {
            if (theme[key]) {
                root.style.setProperty(themeMap[key], theme[key]);
            }
        });
        root.dataset.theme = theme.dark ? "dark" : "light";
    }

    function applyLanguage(language) {
        if (!language) {
            return;
        }

        const root = document.documentElement;
        root.dir = language.dir || "ltr";
        root.lang = language.code || "en";
        root.dataset.lang = language.code || "en";
        // The preset expansion label is rendered imperatively, so force the
        // appearance panel through one more render when the language changes.
        appearanceSignature = "";
        root.style.setProperty(
            "--font-family",
            (language.font || "Rubik") + ', "Cairo", Tahoma, sans-serif');
    }

    function updateStrings() {
        $$("[data-t]").forEach(function (element) {
            element.textContent = t(element.dataset.t);
        });
        $$("[data-tip]").forEach(function (element) {
            element.title = t(element.dataset.tip);
        });

        const code = state && state.language ? state.language.code : "en";
        $$("#langSwitch button").forEach(function (button) {
            button.classList.toggle("is-active", button.dataset.lang === code);
        });

        $("#tutorFilter").placeholder = t("main.filterTutor");
        $("#classFilter").placeholder = t("main.filterClass");
        const menuSearch = $("#selectMenu .select-search-input");
        if (menuSearch) {
            menuSearch.placeholder = t("select.search");
        }
    }

    /* ---------- state application ---------- */

    function applyState(next) {
        if (!next || typeof next !== "object") {
            return;
        }

        if (next.type === "sessions") {
            if (!state) {
                pendingSessions = next;
                return;
            }

            state.sessions = next.sessions || [];
            state.sessionRevision = next.revision || 0;
            updateList();
            return;
        }

        if (next.type === "downloads") {
            if (!state) {
                pendingDownloads = next;
                return;
            }

            state.downloads = next.downloads || null;
            state.downloadsRevision = next.revision || 0;
            updateDownloads();
            return;
        }

        if (next.type === "theme") {
            if (!state) {
                return;
            }

            state.theme = next.theme;
            applyTheme(next.theme);
            return;
        }

        if (!state) {
            state = { sessions: [], sessionRevision: 0 };
        }

        Object.assign(state, next);

        if (pendingSessions) {
            state.sessions = pendingSessions.sessions || [];
            state.sessionRevision = pendingSessions.revision || 0;
            pendingSessions = null;
        }

        if (pendingDownloads) {
            state.downloads = pendingDownloads.downloads || null;
            state.downloadsRevision = pendingDownloads.revision || 0;
            pendingDownloads = null;
        }

        applyTheme(state.theme);
        applyLanguage(state.language);
        updateStrings();
        updateHeader();
        updateFilters();
        updateSort();
        updateList();
        updateFooter();
        updateAppearance();
        updateDownloads();
        document.body.dataset.ready = "1";
        signalRendered();
    }

    function sessionUnit(count) {
        const code = state && state.language ? state.language.code : "en";
        if (code === "ar") {
            return count >= 3 && count <= 10 ? "جلسات" : "جلسة";
        }
        return count === 1 ? "Session" : "Sessions";
    }

    function updateHeader() {
        const header = state.header || {};

        $("#appTitle").textContent = header.eyebrow || "";
        $("#mainTitle").textContent = header.title || t("main.title");
        $("#welcome").textContent = header.welcome || "";

        const source = $("#dataSource");
        source.textContent = header.dataSource || "";
        source.parentElement.hidden = !header.dataSource;

        const role = $("#roleChip");
        role.textContent = header.role || "";
        role.hidden = !header.role;

        const term = header.hasTerm && header.term ? header.term : "";
        $("#termChip").hidden = !term;
        $("#termLabel").textContent = header.termLabel || "";
        $("#termValue").textContent = term;

        const count = header.count || 0;
        const counter = $("#counter");
        counter.hidden = !header.showCount;
        $("#sessionCount").textContent = count;
        $("#counterLabel").textContent = sessionUnit(count);

        const loading = state.body && state.body.loading;
        const refresh = $("#refreshBtn");
        refresh.disabled = !!loading;
        refresh.classList.toggle("is-loading", !!loading);
        $("#loadbar").hidden = !loading;
    }

    function setSelectDisabled(control, disabled) {
        if (!control) {
            return;
        }

        control.classList.toggle("is-disabled", !!disabled);
        control.setAttribute("aria-disabled", disabled ? "true" : "false");
        const trigger = $(".select-trigger", control);
        if (trigger) {
            trigger.disabled = !!disabled;
        }
        if (disabled && activeSelect === control) {
            closeSelectMenu();
        }
    }

    function setSelectBusy(control, busy) {
        if (!control) {
            return;
        }

        control.classList.toggle("is-busy", !!busy);
        control.setAttribute("aria-busy", busy ? "true" : "false");
        if (busy && activeSelect === control) {
            closeSelectMenu(false);
        }
    }

    function selectOptionHtml(option, value) {
        const selected = option.value === value;
        return '<button type="button" class="select-option' + (selected ? " is-active" : "") +
            '" role="option" data-value="' + esc(option.value) + '" aria-selected="' +
            (selected ? "true" : "false") + '">' + esc(option.label) + "</button>";
    }

    function syncCustomSelect(control, list, selected, placeholder) {
        if (!control) {
            return;
        }

        const value = selected === null || selected === undefined ? "" : String(selected);
        control.dataset.value = value;
        selectLists.set(control, list);

        const option = list.find(function (item) {
            return item.value === value;
        });
        const label = $(".select-value", control);
        if (label) {
            label.textContent = option ? option.label : (placeholder || "");
            label.classList.toggle("is-placeholder", !option && !!placeholder);
        }

        const signature = list.map(function (item) {
            return item.value + ":" + item.label;
        }).join("|");
        if (control.dataset.signature !== signature) {
            control.dataset.signature = signature;
            if (activeSelect === control) {
                renderSelectOptions(control);
                const menu = $("#selectMenu");
                if (menu && !menu.hidden) {
                    positionSelectMenu(control, menu);
                }
            }
        }
    }

    function syncSelect(select, items, selected, valueKey) {
        const list = (items || []).map(function (item) {
            return {
                value: String(item[valueKey]),
                label: item.label == null ? "" : String(item.label)
            };
        });
        syncCustomSelect(select, list, selected);
    }

    function selectMatches(option, query) {
        return !query || option.label.toLocaleLowerCase().indexOf(query) >= 0;
    }

    function renderSelectOptions(control) {
        const box = $("#selectOptions");
        if (!box || !control) {
            return;
        }

        const list = selectLists.get(control) || [];
        const query = selectQuery.trim().toLocaleLowerCase();
        activeSelectOptions = list.filter(function (option) {
            return selectMatches(option, query);
        });

        const value = control.dataset.value || "";
        box.innerHTML = activeSelectOptions.length
            ? activeSelectOptions.map(function (option) {
                return selectOptionHtml(option, value);
            }).join("")
            : '<div class="select-empty">' + esc(t("select.empty")) + "</div>";

        if (activeSelectOptions.length) {
            const selectedIndex = activeSelectOptions.findIndex(function (option) {
                return option.value === value;
            });
            if (!query && selectedIndex >= 0) {
                activeSelectIndex = selectedIndex;
            } else {
                activeSelectIndex = 0;
            }
        } else {
            activeSelectIndex = -1;
        }

        updateSelectActive(true);
    }

    function renderSelectMenu(control) {
        const menu = $("#selectMenu");
        if (!menu) {
            return;
        }

        menu.innerHTML =
            '<label class="select-search">' +
            '<input class="select-search-input" type="text" autocomplete="off" spellcheck="false" ' +
            'placeholder="' + esc(t("select.search")) + '" value="' + esc(selectQuery) + '">' +
            "</label>" +
            '<div class="select-options" id="selectOptions"></div>';
        renderSelectOptions(control);
    }

    function focusSelectSearch() {
        window.setTimeout(function () {
            const input = $("#selectMenu .select-search-input");
            if (!input) {
                return;
            }

            input.focus({ preventScroll: true });
            input.setSelectionRange(input.value.length, input.value.length);
        }, 0);
    }

    function updateSelectActive(scrollIntoView) {
        const options = $$("#selectOptions .select-option");
        options.forEach(function (option, index) {
            option.classList.toggle("is-active", index === activeSelectIndex);
        });

        if (scrollIntoView && options[activeSelectIndex]) {
            options[activeSelectIndex].scrollIntoView({ block: "nearest" });
        }
    }

    function positionSelectMenu(control, menu) {
        const trigger = $(".select-trigger", control);
        if (!trigger) {
            return;
        }

        const rect = trigger.getBoundingClientRect();
        const margin = 8;
        const width = Math.max(rect.width, 160);
        const spaceBelow = window.innerHeight - rect.bottom - margin;
        const spaceAbove = rect.top - margin;
        const placeAbove = spaceBelow < 180 && spaceAbove > spaceBelow;

        menu.style.width = Math.round(width) + "px";
        menu.style.maxHeight = Math.min(320, Math.max(120, placeAbove ? spaceAbove : spaceBelow)) + "px";

        let left = rect.left;
        if (left + width > window.innerWidth - margin) {
            left = window.innerWidth - width - margin;
        }
        menu.style.left = Math.round(Math.max(margin, left)) + "px";

        const height = menu.offsetHeight;
        let top = placeAbove ? rect.top - height - 4 : rect.bottom + 4;
        top = Math.max(margin, Math.min(window.innerHeight - height - margin, top));
        menu.style.top = Math.round(top) + "px";
    }

    function openSelectMenu(control, seed) {
        if (!control) {
            return;
        }

        if (control.classList.contains("is-disabled") ||
            control.classList.contains("is-busy")) {
            closeSelectMenu(false);
            return;
        }

        if (activeSelect === control) {
            closeSelectMenu(false);
            return;
        }

        closeSelectMenu(false);
        activeSelect = control;
        selectQuery = seed || "";
        control.classList.add("is-open");
        const trigger = $(".select-trigger", control);
        if (trigger) {
            trigger.setAttribute("aria-expanded", "true");
        }

        const menu = $("#selectMenu");
        if (!menu) {
            return;
        }

        renderSelectMenu(control);
        menu.hidden = false;
        positionSelectMenu(control, menu);
        focusSelectSearch();
    }

    function closeSelectMenu(restoreFocus) {
        const control = activeSelect;
        if (!control) {
            return;
        }

        control.classList.remove("is-open");
        const trigger = $(".select-trigger", control);
        if (trigger) {
            trigger.setAttribute("aria-expanded", "false");
            if (restoreFocus) {
                trigger.focus({ preventScroll: true });
            }
        }

        activeSelect = null;
        activeSelectIndex = -1;
        activeSelectOptions = [];
        selectQuery = "";
        const menu = $("#selectMenu");
        if (menu) {
            menu.hidden = true;
            menu.innerHTML = "";
        }
    }

    function chooseSelectValue(control, value) {
        if (!control) {
            return;
        }

        const list = selectLists.get(control) || [];
        const option = list.find(function (item) {
            return item.value === value;
        });
        if (!option) {
            closeSelectMenu();
            return;
        }

        const changed = control.dataset.value !== value;
        control.dataset.value = value;
        const label = $(".select-value", control);
        if (label) {
            label.textContent = option.label;
        }
        closeSelectMenu(true);

        if (changed) {
            control.dispatchEvent(new Event("change", { bubbles: true }));
        }
    }

    function updateFilters() {
        const filters = state.filters || {};
        const tutor = $("#tutorFilter");
        const klass = $("#classFilter");

        if (document.activeElement !== tutor) {
            tutor.value = filters.tutor || "";
        }
        if (document.activeElement !== klass) {
            klass.value = filters.classFilter || "";
        }

        syncSelect($("#programSelect"), filters.programs, filters.program, "id");
        syncSelect($("#courseSelect"), filters.courses, filters.course, "id");
        syncSelect($("#modeSelect"), filters.modes, filters.mode, "option");
        setSelectDisabled($("#courseSelect"), !filters.courseEnabled);
        setSelectDisabled($("#programSelect"), !filters.usesProgramCourse);
    }

    function updateSort() {
        const sort = state.sort || {};
        $$("[data-sort]").forEach(function (button) {
            const active = sort.column === button.dataset.sort;
            button.classList.toggle("is-active", active);
            const arrow = $(".arrow", button);
            arrow.classList.toggle("is-active", active);
            arrow.classList.toggle("is-desc", active && !!sort.descending);
        });
    }

    /* ---------- session list (virtualized) ---------- */

    let launchDoorSeq = 0;

    function launchGlyph(hinge) {
        const id = "launch-door-cut-" + (++launchDoorSeq);
        const right = hinge === "right";
        const from = right ? 96 : 24;
        const to = right ? 24 : 96;
        const knob = right ? 34 : 86;

        return '<span class="launch-glyph" aria-hidden="true">' +
            '<svg viewBox="0 0 120 160" focusable="false">' +
            '<defs><mask id="' + id + '" maskUnits="userSpaceOnUse" x="0" y="0" width="120" height="160">' +
            '<rect x="0" y="0" width="120" height="160" fill="#fff"></rect>' +
            '<ellipse class="launch-door-knob" cx="' + knob + '" cy="88" rx="3.4" ry="3.4" fill="#000"></ellipse>' +
            "</mask></defs>" +
            '<path class="launch-door-frame" d="M15 150V12H105V150"></path>' +
            '<polygon class="launch-door-leaf" points="' +
            from + ',22 ' + to + ',22 ' + to + ',150 ' + from + ',150" ' +
            'fill="currentColor" mask="url(#' + id + ')"></polygon>' +
            "</svg></span>";
    }

    function initLaunchDoors(root) {
        const reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

        root.querySelectorAll(".ticket-launch[data-door-hinge]").forEach(function (button) {
            if (button.dataset.doorReady === "1") {
                return;
            }
            button.dataset.doorReady = "1";

            const leaf = button.querySelector(".launch-door-leaf");
            const knob = button.querySelector(".launch-door-knob");
            if (!leaf || !knob) {
                return;
            }

            const right = button.dataset.doorHinge === "right";
            const hinge = right ? 96 : 24;
            const dir = right ? -1 : 1;
            const max = 70 * Math.PI / 180;
            const depth = 230;
            const duration = 280;
            const cx = 60;
            const cy = 85;
            const y0 = 22;
            const y1 = 150;
            const width = 72;
            const ease = function (value) {
                return value < 0.5
                    ? 4 * value * value * value
                    : 1 - Math.pow(-2 * value + 2, 3) / 2;
            };
            let progress = 0;
            let target = 0;
            let last = 0;
            let frame = 0;

            function draw(value) {
                const theta = value * max;
                const cos = Math.cos(theta);
                const sin = Math.sin(theta);

                function project(u, y) {
                    const x = hinge + dir * u * cos;
                    const k = depth / (depth + u * sin);
                    return [cx + (x - cx) * k, cy + (y - cy) * k, k];
                }

                const points = [project(0, y0), project(width, y0), project(width, y1), project(0, y1)]
                    .map(function (point) {
                        return point[0].toFixed(2) + "," + point[1].toFixed(2);
                    })
                    .join(" ");
                leaf.setAttribute("points", points);

                const end = project(width - 10, 88);
                const squash = Math.max(cos, 0.25);
                knob.setAttribute("cx", end[0].toFixed(2));
                knob.setAttribute("cy", end[1].toFixed(2));
                knob.setAttribute("rx", (3.4 * end[2] * squash).toFixed(2));
                knob.setAttribute("ry", (3.4 * end[2]).toFixed(2));
            }

            function tick(now) {
                const step = (now - last) / duration;
                last = now;
                progress = target > progress
                    ? Math.min(target, progress + step)
                    : Math.max(target, progress - step);
                draw(ease(progress));

                if (progress !== target) {
                    frame = requestAnimationFrame(tick);
                }
            }

            function go(value) {
                if (progress === value && target === value) {
                    return;
                }
                target = value;
                if (reduce) {
                    progress = value;
                    draw(ease(value));
                    return;
                }
                cancelAnimationFrame(frame);
                last = performance.now();
                frame = requestAnimationFrame(tick);
            }

            function pointerLeave() {
                if (!button.matches(":focus-visible")) {
                    go(0);
                }
            }

            button.addEventListener("pointerenter", function () { go(1); });
            button.addEventListener("pointerleave", pointerLeave);
            button.addEventListener("pointercancel", function () { go(0); });
            button.addEventListener("focus", function () {
                if (button.matches(":focus-visible")) {
                    go(1);
                }
            });
            button.addEventListener("blur", function () { go(0); });
            draw(0);
        });
    }

    function ticketCard(session, index) {
        const launchAllowed = !state.body || (!state.body.busy && !state.body.active);
        const doorHinge = state.language && state.language.dir === "rtl" ? "right" : "left";
        const launch = session.canLaunch
            ? '<button class="ticket-launch" data-launch="' + esc(session.key) +
              '" data-door-hinge="' + doorHinge + '"' +
              ' title="' + esc(t("main.launch")) + '" aria-label="' + esc(t("main.launch")) + '"' +
              (launchAllowed ? "" : " disabled") + ">" +
              launchGlyph(doorHinge) + "</button>"
            : "";

        return '<article class="ticket" data-key="' + esc(session.key) +
            '" style="top:' + (index * ITEM) + 'px">' +
            '<div class="ticket-token">' +
            '<span class="token">' + esc(session.token || "\u2014") + "</span>" +
            '<span class="class-token">' + esc(session.classToken) + "</span>" +
            "</div>" +
            '<div class="ticket-perf"></div>' +
            '<div class="ticket-main">' +
            '<span class="ticket-course">' + esc(session.course) + "</span>" +
            '<span class="eyebrow">' + esc(session.program) + "</span>" +
            "</div>" +
            '<span class="ticket-tutor" title="' + esc(session.tutor) + '">' +
            esc(session.tutor) + "</span>" +
            '<span class="ticket-date" title="' + esc(session.start) + '">' +
            esc(session.start) + "</span>" +
            '<span class="ticket-status"><span class="stamp s-' + esc(session.status) + '">' +
            esc(session.statusText) + "</span></span>" +
            '<span class="ticket-action">' + launch + "</span>" +
            "</article>";
    }

    function renderWindow() {
        const sessions = state && state.sessions ? state.sessions : [];
        if (sessions.length === 0) {
            return;
        }

        const list = $("#sessionList");
        const start = Math.max(0, Math.floor((list.scrollTop - TOP) / ITEM) - OVERSCAN);
        const end = Math.min(
            sessions.length,
            start + Math.ceil(list.clientHeight / ITEM) + OVERSCAN * 2);
        const signature = start + "|" + end + "|" + dataSignature;

        if (signature === renderedSignature) {
            return;
        }

        renderedSignature = signature;
        let html = "";
        for (let index = start; index < end; index += 1) {
            html += ticketCard(sessions[index], index);
        }
        const virtual = $("#listVirtual");
        virtual.innerHTML = html;
        initLaunchDoors(virtual);
    }

    function onScroll() {
        if (scrollFrame) {
            return;
        }

        scrollFrame = requestAnimationFrame(function () {
            scrollFrame = 0;
            renderWindow();
        });
    }

    function bindList() {
        if (listBound) {
            return;
        }

        $("#sessionList").addEventListener("scroll", onScroll, { passive: true });
        window.addEventListener("resize", renderWindow);
        listBound = true;
    }

    function listState(kind, message) {
        if (kind === "loading") {
            return '<span class="spinner"></span><p>' + esc(message) + "</p>";
        }

        return '<div class="state-icon">' + icon("i-ticket") + "</div><p>" + esc(message) + "</p>";
    }

    function updateList() {
        const sessions = state.sessions || [];
        const body = state.body || {};
        const list = $("#sessionList");
        const spinner = $("#listState");
        const virtual = $("#listVirtual");

        const query = JSON.stringify([
            state.filters ? state.filters.tutor : "",
            state.filters ? state.filters.classFilter : "",
            state.filters ? state.filters.program : null,
            state.filters ? state.filters.course : null,
            state.filters ? state.filters.mode : null,
            state.sort ? state.sort.column : null,
            state.sort ? state.sort.descending : null
        ]);

        if (!hadList || query !== querySignature) {
            list.scrollTop = 0;
        }
        querySignature = query;

        dataSignature = (state.sessionRevision || 0) +
            "|" + (state.language ? state.language.code : "") +
            "|" + (body.busy ? 1 : 0) +
            "|" + (body.active ? 1 : 0);

        if (sessions.length === 0) {
            virtual.hidden = true;
            spinner.hidden = false;
            spinner.innerHTML = body.loading
                ? listState("loading", t("main.loadingSessions"))
                : listState("empty", body.noMatches ? t("main.noMatches") : t("main.emptySessions"));
            renderedSignature = "";
            return;
        }

        hadList = true;
        spinner.hidden = true;
        virtual.hidden = false;
        virtual.style.height = Math.max(0, sessions.length * ITEM - GAP) + "px";

        const max = Math.max(0, sessions.length * ITEM - list.clientHeight);
        if (list.scrollTop > max) {
            list.scrollTop = max;
        }

        bindList();
        if (dataSignature !== lastDataSignature) {
            lastDataSignature = dataSignature;
            renderedSignature = "";
        }
        renderWindow();
    }

    function updateFooter() {
        const body = state.body || {};
        $("#statusMsg").textContent = body.statusMessage || "";
        $("#mic").textContent = body.mic || "";
        $("#mic").hidden = !body.mic;
        $("#timeBtn").textContent = body.timeFormat || "";
        $("#timeBtn").title = t("main.clockFormat");
    }

    /* ---------- appearance drawer ---------- */

    function renderPresets(presets, activeKey) {
        const list = presets || [];
        const shown = presetsExpanded ? list : list.slice(0, 9);
        $("#presetGrid").innerHTML = shown.map(function (preset) {
            return '<button class="preset' + (preset.key === activeKey ? " is-active" : "") +
                '" data-preset="' + esc(preset.key) + '">' +
                '<span class="preset-swatch" style="background:' + esc(preset.swatch) + '"></span>' +
                '<span class="preset-name">' + esc(preset.label) + "</span>" +
                '<span class="preset-theme">' + esc(preset.themeLabel) + "</span>" +
                "</button>";
        }).join("");

        const more = $("#presetMore");
        more.hidden = list.length <= 9;
        more.textContent = t(presetsExpanded ? "main.showLess" : "main.showMore");
    }

    function normalizeHex(value) {
        const match = /^#?([0-9a-f]{6})$/i.exec(String(value || "").trim());
        return match ? "#" + match[1].toLowerCase() : null;
    }

    function updateAppearance() {
        const drawer = $("#appearanceDrawer");
        if (!drawer || drawer.hidden || !state) {
            return;
        }

        const ap = state.appearance || {};
        const signature = JSON.stringify(ap);
        if (signature === appearanceSignature) {
            return;
        }
        appearanceSignature = signature;

        renderPresets(ap.presets, ap.preset);

        $$("#themeSwitch button").forEach(function (button) {
            const dark = button.dataset.apTheme === "dark";
            button.classList.toggle("is-active", dark === !!ap.dark);
        });

        const accentHex = normalizeHex(ap.accentHex) || "#0f766e";
        const color = $("#accentColor");
        if (color && document.activeElement !== color) {
            color.value = accentHex;
        }
        $("#accentPreview").style.background = accentHex;

        const hex = $("#accentHex");
        if (document.activeElement !== hex) {
            hex.value = accentHex.toUpperCase();
        }

        $$("#inkSwitch [data-ap-ink]").forEach(function (button) {
            button.classList.toggle("is-active", button.dataset.apInk === (ap.ink || "auto"));
        });
    }

    /* ---------- downloads drawer ---------- */

    function syncDownloadSelect(select, items, selected, placeholder) {
        const list = (items || []).map(function (item) {
            return {
                value: String(item.value),
                label: item.label == null ? "" : String(item.label)
            };
        });
        syncCustomSelect(select, list, selected, placeholder);
    }

    function downloadRowHtml(row) {
        return '<div class="dl-row" data-index="' + row.index + '">' +
            '<label class="dl-check"><input type="checkbox" data-dl-check="' + row.index + '">' +
            '<span class="check-box" aria-hidden="true"><svg viewBox="0 0 12 12"><path d="M2.6 6.1l2.2 2.3 4.7-5"></path></svg></span></label>' +
            '<span class="dl-order">' + esc(row.order) + "</span>" +
            '<span class="dl-course" title="' + esc(row.course) + '">' + esc(row.course) + "</span>" +
            '<span class="dl-class">' + esc(row["class"]) + "</span>" +
            '<span class="dl-term">' + esc(row.term) + "</span>" +
            '<span class="dl-date">' + esc(row.date) + "</span>" +
            '<span class="dl-status"><span class="dl-spin"></span><span class="dl-mark"></span></span>' +
            '<div class="dl-progress"><span></span></div>' +
            "</div>";
    }

    function updateDownloadRows(list) {
        const signature = list.map(function (row) {
            return row.index + ":" + row.order + ":" + row.course + ":" + row.date;
        }).join("|");

        if (signature !== downloadListSignature) {
            downloadListSignature = signature;
            $("#downloadList").innerHTML = list.map(downloadRowHtml).join("");
        }

        const marks = { done: "\u2713", failed: "\u2715", cancelled: "\u2013" };

        list.forEach(function (row) {
            const element = $('#downloadList [data-index="' + row.index + '"]');
            if (!element) {
                return;
            }

            element.dataset.status = row.status;
            element.classList.toggle("is-selected", !!row.selected);
            element.title = row.note || row.statusText || "";

            const checkbox = $("input", element);
            if (checkbox) {
                checkbox.checked = !!row.selected;
            }

            const mark = $(".dl-mark", element);
            if (mark) {
                mark.textContent = marks[row.status] || "";
            }

            const progress = $(".dl-progress span", element);
            if (progress) {
                const value = Math.max(0, Math.min(1, Number(row.progress) || 0));
                progress.style.width = (value * 100).toFixed(1) + "%";
            }
        });
    }

    function setDownloadState(kind, message) {
        const element = $("#downloadState");
        if (!kind) {
            element.hidden = true;
            element.classList.remove("is-error");
            element.innerHTML = "";
            return;
        }

        element.hidden = false;
        element.classList.toggle("is-error", kind === "error");
        element.innerHTML = kind === "loading"
            ? '<span class="spinner"></span><span>' + esc(message) + "</span>"
            : esc(message);
    }

    function updateDownloads() {
        const drawer = $("#downloadsDrawer");
        if (!drawer || drawer.hidden) {
            return;
        }

        const downloads = state && state.downloads;

        if (!downloads) {
            $("#downloadHead").hidden = true;
            $("#downloadList").innerHTML = "";
            downloadListSignature = "";
            setDownloadState("loading", t("download.loading"));
            $("#downloadStart").disabled = true;
            $("#downloadCancel").hidden = true;
            $("#downloadSelectAll").disabled = true;
            $("#downloadClear").disabled = true;
            return;
        }

        $("#downloadFolder").textContent = downloads.saveDirectory || "";
        $("#downloadFolderBtn").title = t("download.changeFolder");

        const placeholders = downloads.placeholders || {};
        const hint = t("download.select");
        syncDownloadSelect($("#dlTerm"), downloads.terms, downloads.selected.term,
            placeholders.term ? hint : "");
        syncDownloadSelect($("#dlProgram"), downloads.programs, downloads.selected.program,
            placeholders.program ? hint : "");
        syncDownloadSelect($("#dlCourse"), downloads.courses, downloads.selected.course,
            placeholders.course ? hint : "");
        syncDownloadSelect($("#dlTutor"), downloads.tutors, downloads.selected.tutor,
            placeholders.tutor ? hint : "");
        syncDownloadSelect($("#dlClass"), downloads.classes, downloads.selected["class"],
            placeholders["class"] ? hint : "");

        const loading = downloads.loading || {};
        const locked = !!downloads.isDownloading;
        setSelectDisabled($("#dlTerm"), locked);
        setSelectDisabled($("#dlProgram"), locked || !downloads.enabled.program);
        setSelectDisabled($("#dlCourse"), locked || !downloads.enabled.course);
        setSelectDisabled($("#dlTutor"), locked || !downloads.enabled.tutor);
        setSelectDisabled($("#dlClass"), locked || !downloads.enabled["class"]);
        $("#downloadFolderBtn").disabled = locked;

        setSelectBusy($("#dlTerm"), !!loading.terms);
        setSelectBusy($("#dlProgram"), !!loading.programs);
        setSelectBusy($("#dlCourse"), !!loading.courses);
        setSelectBusy($("#dlTutor"), !!loading.tutors);
        setSelectBusy($("#dlClass"), !!loading.classes);

        $("#dlTermWrap").classList.toggle("is-loading", !!loading.terms);
        $("#dlProgramWrap").classList.toggle("is-loading", !!loading.programs);
        $("#dlCourseWrap").classList.toggle("is-loading", !!loading.courses);
        $("#dlTutorWrap").classList.toggle("is-loading", !!loading.tutors);
        $("#dlClassWrap").classList.toggle("is-loading", !!loading.classes);

        $("#downloadHead").hidden = !downloads.show.header;
        updateDownloadRows(downloads.sessions || []);

        if (downloads.show.spinner) {
            setDownloadState("loading", t("download.loading"));
        } else if (downloads.hasError) {
            setDownloadState("error", downloads.errorMessage || t("download.empty"));
        } else if (downloads.show.hint) {
            setDownloadState("info", t("download.hint"));
        } else if (downloads.show.empty) {
            setDownloadState("info", t("download.empty"));
        } else {
            setDownloadState(null);
        }

        $("#downloadSelectAll").disabled = !downloads.canSelectAll;
        $("#downloadClear").disabled = !downloads.canClear;
        $("#downloadStart").disabled = !downloads.canDownload;
        $("#downloadCancel").hidden = !downloads.isDownloading;
        $("#downloadCancel").disabled = !downloads.canCancel;

        const parallel = !!downloads.downloadInParallel;
        $$("[data-download-mode]").forEach(function (button) {
            const active = button.dataset.downloadMode === (parallel ? "parallel" : "queue");
            button.classList.toggle("is-active", active);
            button.setAttribute("aria-pressed", active ? "true" : "false");
            button.disabled = !downloads.canChangeMode;
        });

        $("#downloadSelected").textContent = downloads.selectedCountText || "";
        $("#downloadStatus").textContent = downloads.statusMessage || "";
    }

    /* ---------- drawers ---------- */

    function syncRailState() {
        const panels = {
            appearance: $("#appearanceDrawer"),
            downloads: $("#downloadsDrawer"),
            about: $("#aboutDrawer")
        };

        Object.keys(panels).forEach(function (action) {
            const button = $('.rail-btn[data-action="' + action + '"]');
            if (button) {
                button.classList.toggle("is-on", !panels[action].hidden);
            }
        });
    }

    function openDrawer(panel) {
        if (downloadsOpen && panel.id !== "downloadsDrawer") {
            downloadsOpen = false;
            send("downloadsClose");
        }

        $$(".drawer").forEach(function (drawer) {
            if (drawer !== panel) {
                drawer.hidden = true;
            }
        });

        $("#drawerBackdrop").hidden = false;
        panel.hidden = false;
        document.body.classList.add("has-drawer");
        syncRailState();
    }

    function closeDrawer(panel) {
        if (!panel) {
            return;
        }

        panel.hidden = true;
        panel.querySelectorAll("select").forEach(function (select) {
            select.blur();
        });

        if (!$(".drawer:not([hidden])")) {
            $("#drawerBackdrop").hidden = true;
            document.body.classList.remove("has-drawer");
        }

        syncRailState();
    }

    function closeDownloadsDrawer() {
        if (!downloadsOpen) {
            closeDrawer($("#downloadsDrawer"));
            return;
        }

        downloadsOpen = false;
        closeDrawer($("#downloadsDrawer"));
        send("downloadsClose");
    }

    function openAppearance() {
        openDrawer($("#appearanceDrawer"));
        updateAppearance();
    }

    function openAbout() {
        openDrawer($("#aboutDrawer"));
    }

    function openDownloads() {
        downloadsOpen = true;
        openDrawer($("#downloadsDrawer"));
        updateDownloads();
        send("downloadsOpen");
    }

    function closeAllDrawers() {
        if (downloadsOpen) {
            downloadsOpen = false;
            send("downloadsClose");
        }

        $$(".drawer").forEach(closeDrawer);
    }

    /* ---------- actions ---------- */

    function handleAction(action) {
        switch (action) {
            case "refresh":
                send("refresh");
                break;
            case "signOut":
                send("signOut");
                break;
            case "exit":
                send("exit");
                break;
            case "about":
                if ($("#aboutDrawer").hidden) {
                    openAbout();
                } else {
                    closeDrawer($("#aboutDrawer"));
                }
                break;
            case "downloads":
                if ($("#downloadsDrawer").hidden) {
                    openDownloads();
                } else {
                    closeDownloadsDrawer();
                }
                break;
            case "appearance":
                if ($("#appearanceDrawer").hidden) {
                    openAppearance();
                } else {
                    closeDrawer($("#appearanceDrawer"));
                }
                break;
            case "download-folder":
                send("downloadFolder");
                break;
            case "download-select-all":
                send("downloadSelectAll");
                break;
            case "download-clear":
                send("downloadClear");
                break;
            case "download-start":
                send("downloadStart");
                break;
            case "download-cancel":
                send("downloadCancel");
                break;
            case "diagnostics":
                send("diagnostics");
                break;
            case "toggleTime":
                send("toggleTime");
                break;
            case "openRepo":
                send("openRepo");
                break;
        }
    }

    /* ---------- events ---------- */

    document.addEventListener("click", function (event) {
        const close = event.target.closest("[data-drawer-close]");
        if (close) {
            const panel = close.closest(".drawer");
            if (panel && panel.id === "downloadsDrawer") {
                closeDownloadsDrawer();
            } else {
                closeDrawer(panel);
            }
            return;
        }

        if (event.target.id === "drawerBackdrop") {
            closeAllDrawers();
            return;
        }

        if (event.target.closest("#presetMore")) {
            presetsExpanded = !presetsExpanded;
            appearanceSignature = "";
            updateAppearance();
            return;
        }

        const preset = event.target.closest("[data-preset]");
        if (preset) {
            send("appearancePreset", { key: preset.dataset.preset });
            return;
        }

        const theme = event.target.closest("[data-ap-theme]");
        if (theme) {
            send("appearanceTheme", { value: theme.dataset.apTheme === "dark" });
            return;
        }

        const ink = event.target.closest("[data-ap-ink]");
        if (ink) {
            send("appearanceInk", { value: ink.dataset.apInk });
            return;
        }

        const language = event.target.closest("#langSwitch [data-lang]");
        if (language) {
            send("language", { code: language.dataset.lang });
            return;
        }

        const downloadMode = event.target.closest("[data-download-mode]");
        if (downloadMode && !downloadMode.disabled) {
            send("downloadMode", { value: downloadMode.dataset.downloadMode });
            return;
        }

        const launch = event.target.closest("[data-launch]");
        if (launch) {
            event.stopPropagation();
            send("select", { key: launch.dataset.launch });
            send("launch");
            return;
        }

        const sort = event.target.closest("[data-sort]");
        if (sort) {
            send("sort", { column: sort.dataset.sort });
            return;
        }

        const action = event.target.closest("[data-action]");
        if (action) {
            handleAction(action.dataset.action);
            return;
        }
    });

    document.addEventListener("dblclick", function (event) {
        const card = event.target.closest(".ticket");
        if (card && !event.target.closest("[data-launch]")) {
            send("select", { key: card.dataset.key });
            send("launch");
        }
    });

    document.addEventListener("keydown", function (event) {
        if (event.key === "Escape" && $(".drawer:not([hidden])")) {
            closeAllDrawers();
        }
    });

    function sendFilter() {
        send("filter", {
            tutor: $("#tutorFilter").value,
            classFilter: $("#classFilter").value
        });
    }

    $("#tutorFilter").addEventListener("input", sendFilter);
    $("#classFilter").addEventListener("input", sendFilter);

    const accentColor = $("#accentColor");
    const accentHex = $("#accentHex");
    accentColor.addEventListener("input", function () {
        const hex = normalizeHex(accentColor.value);
        if (!hex) {
            return;
        }

        accentHex.value = hex.toUpperCase();
        pendingColorHex = hex;
        // Coalesce to one bridge message per painted frame. The colour still
        // follows the picker continuously, without flooding the WPF side.
        if (!colorFrame) {
            colorFrame = requestAnimationFrame(function () {
                colorFrame = 0;
                send("appearanceColor", { value: pendingColorHex, final: false });
            });
        }
    });
    accentColor.addEventListener("change", function () {
        const hex = normalizeHex(accentColor.value);
        if (hex) {
            if (colorFrame) {
                cancelAnimationFrame(colorFrame);
                colorFrame = 0;
            }
            send("appearanceColor", { value: hex, final: true });
        }
    });
    accentHex.addEventListener("change", function () {
        const hex = normalizeHex(accentHex.value);
        if (!hex) {
            appearanceSignature = "";
            updateAppearance();
            return;
        }

        accentHex.value = hex.toUpperCase();
        send("appearanceColor", { value: hex, final: true });
    });
    accentHex.addEventListener("keydown", function (event) {
        if (event.key === "Enter") {
            event.preventDefault();
            accentHex.blur();
        }
    });

    $("#programSelect").addEventListener("change", function (event) {
        const value = event.target.dataset.value;
        if (value) {
            send("program", { id: Number(value) });
        }
    });

    $("#courseSelect").addEventListener("change", function (event) {
        const value = event.target.dataset.value;
        if (value) {
            send("course", { id: Number(value) });
        }
    });

    $("#modeSelect").addEventListener("change", function (event) {
        const value = event.target.dataset.value;
        if (value) {
            send("mode", { option: Number(value) });
        }
    });

    document.addEventListener("click", function (event) {
        const trigger = event.target.closest("[data-select] .select-trigger");
        if (trigger) {
            event.preventDefault();
            trigger.focus({ preventScroll: true });
            openSelectMenu(trigger.closest("[data-select]"));
            return;
        }

        const option = event.target.closest("#selectMenu .select-option");
        if (option) {
            if (activeSelect) {
                chooseSelectValue(activeSelect, option.dataset.value);
            }
            return;
        }

        if (activeSelect && !event.target.closest("#selectMenu")) {
            closeSelectMenu(false);
        }
    });

    document.addEventListener("input", function (event) {
        const search = event.target.closest("#selectMenu .select-search-input");
        if (!search || !activeSelect) {
            return;
        }

        selectQuery = search.value;
        renderSelectOptions(activeSelect);
        const menu = $("#selectMenu");
        if (menu) {
            positionSelectMenu(activeSelect, menu);
        }
    });

    document.addEventListener("keydown", function (event) {
        const search = event.target.closest("#selectMenu .select-search-input");
        if (activeSelect && search) {
            if (event.key === "Escape") {
                event.preventDefault();
                closeSelectMenu(true);
                return;
            }

            if (event.key === "Tab") {
                const option = activeSelectOptions[activeSelectIndex] || activeSelectOptions[0];
                if (option) {
                    event.preventDefault();
                    chooseSelectValue(activeSelect, option.value);
                } else {
                    closeSelectMenu(false);
                }
                return;
            }

            const list = activeSelectOptions;
            if (event.key === "ArrowDown" || event.key === "ArrowUp") {
                event.preventDefault();
                if (!list.length) {
                    return;
                }

                const step = event.key === "ArrowDown" ? 1 : -1;
                activeSelectIndex = (activeSelectIndex + step + list.length) % list.length;
                updateSelectActive(true);
                return;
            }

            if (event.key === "Home" || event.key === "End") {
                event.preventDefault();
                if (!list.length) {
                    return;
                }

                activeSelectIndex = event.key === "Home" ? 0 : list.length - 1;
                updateSelectActive(true);
                return;
            }

            if (event.key === "Enter") {
                event.preventDefault();
                const option = list[activeSelectIndex];
                if (option) {
                    chooseSelectValue(activeSelect, option.value);
                }
            }

            return;
        }

        const trigger = event.target.closest("[data-select] .select-trigger");
        if (!activeSelect) {
            if (!trigger) {
                return;
            }

            if (event.key === "ArrowDown" || event.key === "ArrowUp" ||
                event.key === "Enter" || event.key === " ") {
                event.preventDefault();
                openSelectMenu(trigger.closest("[data-select]"));
                return;
            }

            if (event.key.length === 1 && !event.ctrlKey && !event.altKey &&
                !event.metaKey && !event.isComposing) {
                event.preventDefault();
                openSelectMenu(trigger.closest("[data-select]"), event.key);
            }

            return;
        }

        if (event.key === "Escape") {
            event.preventDefault();
            closeSelectMenu(true);
            return;
        }

        if (event.key === "Tab") {
            const option = activeSelectOptions[activeSelectIndex] || activeSelectOptions[0];
            if (option) {
                event.preventDefault();
                chooseSelectValue(activeSelect, option.value);
            } else {
                closeSelectMenu(false);
            }
            return;
        }

        const list = activeSelectOptions;
        if (!list.length) {
            return;
        }

        if (event.key === "ArrowDown" || event.key === "ArrowUp") {
            event.preventDefault();
            const step = event.key === "ArrowDown" ? 1 : -1;
            activeSelectIndex = (activeSelectIndex + step + list.length) % list.length;
            updateSelectActive(true);
            return;
        }

        if (event.key === "Home" || event.key === "End") {
            event.preventDefault();
            activeSelectIndex = event.key === "Home" ? 0 : list.length - 1;
            updateSelectActive(true);
            return;
        }

        if (event.key === "Enter" || event.key === " ") {
            event.preventDefault();
            const option = list[activeSelectIndex];
            if (option) {
                chooseSelectValue(activeSelect, option.value);
            }
        }
    });

    window.addEventListener("resize", function () {
        closeSelectMenu(false);
    });

    document.addEventListener("wheel", function (event) {
        if (event.ctrlKey) {
            return;
        }

        const list = event.target.closest(".list, .dl-list");
        if (!list || list.scrollHeight <= list.clientHeight) {
            return;
        }

        const unit = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? list.clientHeight : 1;
        const next = Math.max(0, Math.min(
            list.scrollHeight - list.clientHeight,
            list.scrollTop + (event.deltaY * unit)));
        if (next === list.scrollTop) {
            return;
        }

        event.preventDefault();
        list.scrollTop = next;
    }, { passive: false });

    document.addEventListener("change", function (event) {
        const check = event.target.closest("[data-dl-check]");
        if (check) {
            send("downloadSelect", {
                index: Number(check.dataset.dlCheck),
                value: check.checked
            });
        }
    });

    $("#dlTerm").addEventListener("change", function (event) {
        send("downloadTerm", { value: event.target.dataset.value });
    });

    $("#dlProgram").addEventListener("change", function (event) {
        send("downloadProgram", { value: event.target.dataset.value });
    });

    $("#dlCourse").addEventListener("change", function (event) {
        send("downloadCourse", { value: event.target.dataset.value });
    });

    $("#dlTutor").addEventListener("change", function (event) {
        send("downloadTutor", { value: event.target.dataset.value });
    });

    $("#dlClass").addEventListener("change", function (event) {
        send("downloadClass", { value: event.target.dataset.value });
    });

    window.setTimeout(function () {
        if (document.body.dataset.ready !== "1") {
            document.body.dataset.ready = "1";
            signalRendered();
        }
    }, 2000);

    if (bridge) {
        bridge.addEventListener("message", function (event) {
            applyState(event.data);
        });
        send("ready");
    }
})();
