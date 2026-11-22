/*
 * Invisible resize grips for the page's left, right, and bottom edges.
 *
 * WebView2 renders in a native child window, so the page cannot rely on the
 * WPF WindowChrome border receiving edge clicks. Each grip reports its edge to
 * the host, which starts the normal Windows resize loop. The grips are
 * transparent and only change the cursor, so the page stays full-bleed.
 */
(function () {
    "use strict";

    var host = window.chrome && window.chrome.webview;
    if (!host) {
        return;
    }

    var grips = [
        { edge: "left", className: "window-resize-left" },
        { edge: "right", className: "window-resize-right" },
        { edge: "bottom", className: "window-resize-bottom" },
        { edge: "bottom-left", className: "window-resize-bottom-left" },
        { edge: "bottom-right", className: "window-resize-bottom-right" }
    ];

    function install() {
        if (document.querySelector(".window-resize-grip")) {
            return;
        }

        var style = document.createElement("style");
        style.textContent =
            ".window-resize-grip{position:fixed;z-index:2147483647;background:transparent;}" +
            ".window-resize-left{top:0;bottom:0;left:0;width:6px;cursor:w-resize;}" +
            ".window-resize-right{top:0;bottom:0;right:0;width:6px;cursor:e-resize;}" +
            ".window-resize-bottom{left:0;right:0;bottom:0;height:6px;cursor:s-resize;}" +
            ".window-resize-bottom-left{left:0;bottom:0;width:12px;height:12px;cursor:sw-resize;}" +
            ".window-resize-bottom-right{right:0;bottom:0;width:12px;height:12px;cursor:se-resize;}";
        document.head.appendChild(style);

        var installed = [];
        grips.forEach(function (grip) {
            var element = document.createElement("div");
            element.className = "window-resize-grip " + grip.className;
            element.addEventListener("mousedown", function (event) {
                if (event.button !== 0) {
                    return;
                }

                event.preventDefault();
                event.stopPropagation();
                host.postMessage({ action: "resize", type: "resize", edge: grip.edge });
            });
            document.body.appendChild(element);
            installed.push(element);
        });

        // The host drives this directly instead of toggling a class on <html>,
        // so only the grip elements restyle when the window is maximized.
        window.svuWindowState = function (maximized) {
            var display = maximized ? "none" : "";
            installed.forEach(function (element) {
                element.style.display = display;
            });
        };
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", install);
    } else {
        install();
    }
})();
