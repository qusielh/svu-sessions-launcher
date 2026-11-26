using System.Windows;
using SvuSessionsLauncher.Localization;
using SvuSessionsLauncher.Services;
using SvuSessionsLauncher.Theming;
using SvuSessionsLauncher.ViewModels;
using SvuSessionsLauncher.Views;

namespace SvuSessionsLauncher;

/// <summary>
/// Application entry point. Wires the services together and drives the
/// login &#8594; main &#8594; (sign out) &#8594; login loop.
/// </summary>
public partial class App : Application
{
    private AppSettings _settings = null!;
    private ICredentialStore _credentials = null!;
    private IWebDemoService _service = null!;
    private ISessionLauncher _launcher = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
            Log.Write($"Unhandled UI exception: {args.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write($"Unhandled process exception: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
            Log.Write($"Unobserved task exception: {args.Exception}");

        // Start the embedded browser now so the first greeting (and, later, the
        // in-session loader) paints without paying a cold start on screen.
        WebViewRuntime.Prewarm();

        _settings = AppSettings.Load();
        _credentials = new DpapiCredentialStore();
        DisplayOptions.Use24HourClock = _settings.Use24HourClock;

        _service = new CachedWebDemoService(new SoapWebDemoService(_settings.ServiceUrl));
        _launcher = new LinktivitySessionLauncher();

        Loc.Instance.Language = string.IsNullOrWhiteSpace(_settings.Language)
            ? Loc.Instance.DetectDefaultLanguage()
            : _settings.Language;

        // Apply the saved appearance + language font before any window loads.
        ThemeManager.Apply(
            ThemeManager.Parse(_settings.Theme),
            _settings.Accent,
            _settings.Header,
            _settings.Ink,
            _settings.AccentHex);
        ThemeManager.ApplyFont(Loc.Instance.Language);
        Loc.LanguageChanged += () => ThemeManager.ApplyFont(Loc.Instance.Language);

        await RunAppAsync();
    }

    private async Task RunAppAsync()
    {
        // Auto sign-in applies only to the very first prompt (the app start).
        // After the user signs out we show the sign-in window again but must not
        // immediately sign them back in, otherwise "sign out" would just loop.
        var firstRun = true;
        Rect? lastMainBounds = null;

        while (true)
        {
            var allowAutoSignIn = firstRun;
            firstRun = false;

            var loginVm = new LoginViewModel(_service, _settings, _credentials, allowAutoSignIn);

            // On startup, when auto sign-in is enabled with saved credentials,
            // sign in silently and go straight to the main window (no flash of
            // the sign-in window). If it does not apply or fails, fall through
            // to showing the window so the user can sign in manually.
            if (allowAutoSignIn && _settings.AutoSignIn)
            {
                void OnTutorBlocked(object? sender, EventArgs e) =>
                    MessageBox.Show(
                        Loc.Instance["login.error.tutorBlocked"],
                        Loc.Instance["app.title"],
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                loginVm.TutorBlocked += OnTutorBlocked;
                try
                {
                    await loginVm.TryAutoSignInAsync();
                }
                finally
                {
                    loginVm.TutorBlocked -= OnTutorBlocked;
                }
            }

            var silent = loginVm.AuthenticatedUser is { Length: > 0 };

            if (!silent)
            {
                var login = new LoginWindow { DataContext = loginVm };
                login.PlaceOver(lastMainBounds);

                // Keep the sign-in surface visible while credentials are checked:
                // the page already shows the busy state. Hiding it here made a
                // slow or failed attempt look like the click did nothing.
                void OnSignInStarted(object? sender, EventArgs e)
                {
                    Log.Write("[flow] sign-in started");
                }

                void OnSignInFailed(object? sender, EventArgs e)
                {
                    Log.Write("[flow] sign-in failed");
                    if (!login.IsLoaded)
                    {
                        return;
                    }

                    if (!login.IsVisible)
                    {
                        login.Show();
                    }

                    login.Activate();
                }

                void OnTutorBlocked(object? sender, EventArgs e)
                {
                    MessageBox.Show(
                        login,
                        Loc.Instance["login.error.tutorBlocked"],
                        Loc.Instance["app.title"],
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                void OnSignedIn(object? sender, EventArgs e)
                {
                    Log.Write("[flow] sign-in accepted");
                    login.Close();
                }

                loginVm.SignInStarted += OnSignInStarted;
                loginVm.SignInFailed += OnSignInFailed;
                loginVm.TutorBlocked += OnTutorBlocked;
                loginVm.SignedIn += OnSignedIn;

                try
                {
                    login.ShowDialog();
                    Log.Write($"[flow] login dialog closed; authenticated={loginVm.AuthenticatedUser is { Length: > 0 }}");
                    if (loginVm.AuthenticatedUser is not { Length: > 0 })
                    {
                        Shutdown();
                        return;
                    }
                }
                finally
                {
                    loginVm.SignInStarted -= OnSignInStarted;
                    loginVm.SignInFailed -= OnSignInFailed;
                    loginVm.TutorBlocked -= OnTutorBlocked;
                    loginVm.SignedIn -= OnSignedIn;
                }
            }

            var user = loginVm.AuthenticatedUser ?? loginVm.Username;
            var mainVm = new MainViewModel(_service, _launcher, _settings, user);

            var main = new MainStudioWindow { DataContext = mainVm };

            // WPF assigns Application.MainWindow to the first window it creates
            // -- here that is the sign-in window, which is gone by now. Left
            // stale, the shell restores/foregrounds the launcher incorrectly
            // after a show-desktop (Win+D) minimise/restore cycle. Point it at
            // the real window.
            MainWindow = main;

            var signOut = false;
            var shuttingDown = false;
            ChatWindow? activeChat = null;
            ChatWindow? preloadedChat = null;
            mainVm.SignOut += (_, _) =>
            {
                lastMainBounds = new Rect(main.Left, main.Top, main.ActualWidth, main.ActualHeight);
                signOut = true;
                main.Close();
            };
            mainVm.ExitRequested += (_, _) => main.Close();

            mainVm.SessionStarted += (_, bridge) =>
            {
                // Every window is an independent top-level window: opening or
                // closing the chat never locks, hides or pushes the launcher
                // around. The viewer is NOT shown here: it shows itself the
                // moment the first screen-share frame arrives, also independent.
                // No Owner: an owned window is permanently glued above its owner,
                // which made the launcher impossible to bring in front of the chat.
                var chat = preloadedChat is { IsClosed: false } ? preloadedChat : new ChatWindow(_settings);
                preloadedChat = null;

                var viewer = new ViewerWindow(bridge);

                // The connection is dropped first; both surfaces then fade out
                // together instead of snapping off one after the other.
                chat.SessionCloseRequested += async (_, _) =>
                {
                    try
                    {
                        await Task.WhenAll(
                            viewer.FadeOutAndCloseAsync(),
                            chat.FadeOutAndCloseAsync());
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"session close sequence failed: {ex.Message}");
                    }
                };

                // The chat is unowned now, so closing the launcher must end the
                // session explicitly instead of relying on owner teardown.
                main.Closed += (_, _) => chat.Close();

                chat.Closed += (_, _) =>
                {
                    if (viewer.IsLoaded)
                    {
                        viewer.CloseForSession();
                    }
                    else
                    {
                        viewer.Dispose();
                    }

                    // Releasing the session lock lets the launcher join again.
                    mainVm.IsSessionActive = false;
                    if (chat.EndedUnexpectedly)
                    {
                        mainVm.NotifySessionEnded();
                    }

                    Log.Write("session chat closed");

                    if (!shuttingDown)
                    {
                        var next = new ChatWindow(_settings);
                        next.Prewarm();
                        preloadedChat = next;
                    }
                };

                // Joining is locked while a session is live so two sessions can
                // never run at once.
                mainVm.IsSessionActive = true;
                activeChat = chat;
                chat.ShowForSession(bridge);
            };

            // The main window is a normal top-level window too (not a modal
            // dialog), so opening/closing the other windows never hides or
            // disables it. We still block here until it closes to drive the
            // sign-out -> sign-in cycle.
            var mainClosed = new TaskCompletionSource();
            main.Closed += (_, _) =>
            {
                shuttingDown = true;
                preloadedChat?.Close();
                preloadedChat = null;
                mainClosed.TrySetResult();
            };

            // Load the session loader before it is needed so Join can reveal the
            // already-painted page immediately instead of a blank WebView.
            preloadedChat = new ChatWindow(_settings);
            preloadedChat.Prewarm();

            Log.Write("[flow] showing main studio");
            main.Show();
            await mainClosed.Task;

            // Closing the launcher ends any session with it.
            activeChat?.Close();

            if (!signOut)
            {
                Shutdown();
                return;
            }
        }
    }
}
