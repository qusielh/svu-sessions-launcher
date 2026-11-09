using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace SvuSessionsLauncher.Services;

/// <summary>
/// A single shared WebView2 environment, created once and reused by every
/// embedded surface (the standing greeting and the chat). Creating the
/// environment spins up the browser process, which is the slow, one-time cost;
/// sharing it — and starting it at launch — means the greeting and the
/// in-session loader paint almost immediately instead of each paying a cold
/// start.
/// </summary>
public static class WebViewRuntime
{
    private static readonly object Gate = new();
    private static Task<CoreWebView2Environment>? _environment;

    /// <summary>
    /// The shared environment. The first access starts it; later calls join the
    /// same startup.
    /// </summary>
    public static Task<CoreWebView2Environment> Environment
    {
        get
        {
            lock (Gate)
            {
                return _environment ??= CoreWebView2Environment.CreateAsync(null, null, new CoreWebView2EnvironmentOptions
                {
                    // Chromium throttles (or stops painting) windows it thinks
                    // are occluded or backgrounded. The greeting is both, so
                    // those behaviours are disabled or its page never paints.
                    AdditionalBrowserArguments =
                        "--disable-backgrounding-occluded-windows " +
                        "--disable-renderer-backgrounding " +
                        "--disable-features=CalculateNativeWinOcclusion",
                });
            }
        }
    }

    /// <summary>Begins warming the environment. Safe to call many times.</summary>
    public static void Prewarm() => _ = Environment;
}
