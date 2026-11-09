using SvuSessionsLauncher.Localization;
using SvuSessionsLauncher.Services;

namespace SvuSessionsLauncher.ViewModels;

/// <summary>Backs the sign-in window.</summary>
public sealed class LoginViewModel : ObservableObject
{
    private readonly IWebDemoService _service;
    private readonly AppSettings _settings;
    private readonly ICredentialStore _credentials;
    private readonly bool _allowAutoSignIn;

    private string _username = string.Empty;
    private string _password = string.Empty;
    private bool _savePassword;
    private bool _isBusy;
    private bool _showPassword;
    private string? _errorMessage;

    public LoginViewModel(
        IWebDemoService service,
        AppSettings settings,
        ICredentialStore credentials,
        bool allowAutoSignIn = true)
    {
        _service = service;
        _settings = settings;
        _credentials = credentials;
        _allowAutoSignIn = allowAutoSignIn;

        SignInCommand = new RelayCommand(SignInAsync, () => CanSignIn);

        _username = _settings.LastUserName ?? string.Empty;
        _savePassword = _settings.RememberPassword;
        if (_savePassword && !string.IsNullOrEmpty(_username))
        {
            _password = _credentials.Load(_username) ?? string.Empty;
        }
    }

    /// <summary>Raised once credentials are accepted.</summary>
    public event EventHandler? SignedIn;

    /// <summary>Raised when the account is a tutor, which this student-only build does not support.</summary>
    public event EventHandler? TutorBlocked;

    /// <summary>
    /// Raised as soon as a sign-in attempt starts, before the service responds.
    /// The host hides the sign-in window and shows the greeting from this event.
    /// </summary>
    public event EventHandler? SignInStarted;

    /// <summary>
    /// Raised when an attempt finishes without accepting the credentials. The
    /// host dismisses the greeting and shows the sign-in window again.
    /// </summary>
    public event EventHandler? SignInFailed;

    /// <summary>The shared settings instance, used by the sign-in surface for theming.</summary>
    public AppSettings Settings => _settings;

    public RelayCommand SignInCommand { get; }

    /// <summary>The validated username, available after a successful sign-in.</summary>
    public string? AuthenticatedUser { get; private set; }

    public string Username
    {
        get => _username;
        set
        {
            if (SetProperty(ref _username, value))
            {
                SignInCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Password
    {
        get => _password;
        set
        {
            if (SetProperty(ref _password, value))
            {
                SignInCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool SavePassword
    {
        get => _savePassword;
        set
        {
            if (!SetProperty(ref _savePassword, value))
            {
                return;
            }

            // Auto sign-in needs the stored password, so dropping the password
            // must drop auto sign-in with it (otherwise it would silently fail).
            if (!value && AutoSignIn)
            {
                AutoSignIn = false;
            }
        }
    }

    /// <summary>
    /// When true the launcher signs the user in automatically on the next
    /// start, using the saved credentials (no prompts). Persisted immediately.
    /// </summary>
    public bool AutoSignIn
    {
        get => _settings.AutoSignIn;
        set
        {
            if (_settings.AutoSignIn == value)
            {
                return;
            }

            _settings.AutoSignIn = value;
            _settings.Save();

            // Auto sign-in needs the password, so keep "remember" on with it.
            if (value && !_savePassword)
            {
                _savePassword = true;
                OnPropertyChanged(nameof(SavePassword));
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Signs in silently when auto sign-in is enabled and saved credentials are
    /// present. Returns true when a sign-in was attempted (the window will close
    /// on success, or show the error on failure); false when it did not apply.
    /// Auto sign-in only runs on the first (startup) prompt: after an explicit
    /// sign-out the user must enter their password again, so the setting stays
    /// saved for the next launch instead of looping them straight back in.
    /// </summary>
    public async Task<bool> TryAutoSignInAsync()
    {
        if (!_allowAutoSignIn ||
            !_settings.AutoSignIn ||
            string.IsNullOrWhiteSpace(_username) ||
            string.IsNullOrWhiteSpace(_password))
        {
            return false;
        }

        await SignInAsync();
        return true;
    }

    public bool ShowPassword
    {
        get => _showPassword;
        set => SetProperty(ref _showPassword, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                SignInCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool CanSignIn =>
        !IsBusy && !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    private async Task SignInAsync()
    {
        ErrorMessage = null;
        IsBusy = true;
        SignInStarted?.Invoke(this, EventArgs.Empty);
        try
        {
            var user = Username.Trim();
            var result = await _service.CheckUserPasswordAsync(user, Password);
            if (!result.Success)
            {
                ErrorMessage = Loc.Instance[result.Message ?? "login.error.invalid"];
                SignInFailed?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (await _service.IsUserTrainerAsync() != 0)
            {
                ErrorMessage = Loc.Instance["login.error.tutorBlocked"];
                TutorBlocked?.Invoke(this, EventArgs.Empty);
                SignInFailed?.Invoke(this, EventArgs.Empty);
                return;
            }

            AuthenticatedUser = user;
            _settings.LastUserName = user;
            var remember = SavePassword || AutoSignIn;
            _settings.RememberPassword = remember;
            if (remember)
            {
                _credentials.Save(user, Password);
            }
            else
            {
                _credentials.Remove(user);
            }

            _settings.Save();
            SignedIn?.Invoke(this, EventArgs.Empty);
        }
        catch (WebDemoServiceException ex)
        {
            // The message is a localisation key, an HTTP status or a raw server
            // fault; the Loc indexer passes unknown keys through so unexpected
            // replies remain visible while testing against the live service.
            Log.Write($"WebDemo service error during sign-in: {ex}");
            ErrorMessage = ex.InnerException is null
                ? Loc.Instance[ex.Message]
                : Loc.Instance.Format(
                    "login.error.networkDetail",
                    ex.Message,
                    ex.GetBaseException().Message);
            SignInFailed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // Surface the underlying cause while testing; it is logged too so
            // failures can be diagnosed even when the message is trimmed.
            Log.Write($"Sign-in transport failure: {ex}");
            var detail = ex.GetBaseException().Message;
            ErrorMessage = Loc.Instance.Format(
                "login.error.networkDetail",
                Loc.Instance["login.error.network"],
                detail);
            SignInFailed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
