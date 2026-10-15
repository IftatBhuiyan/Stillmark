using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Threading;
using WinShot.App.Commercial;
using WinShot.Licensing;
using WinShot.Licensing.Accounts;

namespace WinShot.App;

public partial class LicenseWindow : System.Windows.Controls.UserControl, IDisposable
{
    private readonly LicenseService _license;
    private readonly DesktopAccountClient? _accountClient;
    private readonly CommercialStorefrontPolicy _storefront;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _trialCountdownTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _busy;
    private string? _lastAnnouncedStatus;
    private int _liveRegionAnnouncementVersion;

    public LicenseWindow(LicenseService license, DesktopAccountClient? accountClient = null)
    {
        _license = license;
        _accountClient = license.EnforcementMode == LicenseEnforcementMode.OfflineSigned ? accountClient : null;
        _storefront = CommercialStorefrontConfiguration.Create(_license.EnforcementMode);
        InitializeComponent();
        AccountSection.Visibility = _accountClient is null ? Visibility.Collapsed : Visibility.Visible;
        KeyExpander.IsExpanded = _accountClient is null;
        EmailBox.KeyDown += (sender, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; SendCode_Click(sender, e); } };
        CodeBox.KeyDown += (sender, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; VerifyCode_Click(sender, e); } };
        ApplyStorefrontPolicy();
        Render();
        Loaded += async (_, _) =>
        {
            FocusPreferredControl();
            AnnounceStatusIfChanged();
            if (_accountClient is not null) await RunAccountOperationAsync(LoadAccountAsync);
        };
        _trialCountdownTimer.Tick += (_, _) => Render();
        _trialCountdownTimer.Start();
        _license.Changed += OnLicenseChanged;
    }

    public event Action? ReturnRequested;

    public void Dispose()
    {
        _trialCountdownTimer.Stop();
        _license.Changed -= OnLicenseChanged;
        _lifetime.Cancel();
    }

    private void OnLicenseChanged()
    {
        if (Dispatcher.CheckAccess())
            Render();
        else if (!Dispatcher.HasShutdownStarted)
            _ = Dispatcher.BeginInvoke(Render);
    }

    private void Render()
    {
        var status = _license.Status;
        MessageText.Visibility = Visibility.Collapsed;
        TrialCountdownText.Visibility = status.State == LicenseState.Trial
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (status.State == LicenseState.Trial)
            TrialCountdownText.Text = $"Trial time remaining: {FormatTrialRemaining(status.TrialRemaining)}";

        switch (status.State)
        {
            case LicenseState.Licensed:
                StatusText.Text = "Stillmark is activated";
                StatusDetail.Text = _storefront.LicensedDetail;
                if (status.License is { UpdatesUntil: > 0 } datedLicense)
                {
                    var cutoff = DateTimeOffset.FromUnixTimeSeconds(datedLicense.UpdatesUntil);
                    StatusDetail.Text = $"This version is yours to keep. Updates released before {cutoff:MMM d, yyyy HH:mm} UTC are included. No automatic renewal.";
                }
                SetKeyEntryVisible(false);
                DeactivateButton.Visibility = Visibility.Visible;
                ContinueButton.Content = "Close";
                ContinueButton.Visibility = Visibility.Visible;
                BuyButton.Visibility = Visibility.Collapsed;
                break;

            case LicenseState.Trial:
                StatusText.Text = status.TrialDaysLeft == 1
                    ? "Trial — 1 day left"
                    : $"Trial — {status.TrialDaysLeft} days left";
                StatusDetail.Text = _accountClient is null ? _storefront.TrialDetail : "Already purchased? Sign in below to activate this computer.";
                SetKeyEntryVisible(true);
                DeactivateButton.Visibility = Visibility.Collapsed;
                ContinueButton.Content = "Continue trial";
                ContinueButton.Visibility = Visibility.Visible;
                BuyButton.Content = "See purchase options";
                BuyButton.Visibility = Visibility.Visible;
                BuyButton.IsEnabled = true;
                break;

            default: // Expired
                StatusText.Text = "Your free trial has ended";
                StatusDetail.Text = _accountClient is null ? _storefront.ExpiredDetail : "Sign in with your purchase email to activate this computer.";
                SetKeyEntryVisible(true);
                DeactivateButton.Visibility = Visibility.Collapsed;
                ContinueButton.Visibility = Visibility.Collapsed; // no dismissing an expired trial
                BuyButton.Content = "Buy a license";
                BuyButton.Visibility = Visibility.Visible;
                BuyButton.IsEnabled = true;
                break;
        }

        if (status.AccessIssue == LicenseAccessIssue.ReleaseNotCovered)
        {
            StatusText.Text = "This version isn't included in your license";
            StatusDetail.Text = "Your license is still yours. Use an earlier eligible version, or enter a license that includes this release."
                + (status.State == LicenseState.Trial ? $" You can try this version for {status.TrialDaysLeft} more day{(status.TrialDaysLeft == 1 ? "" : "s")}." : "");
            DeactivateButton.Visibility = Visibility.Visible;
        }
        else if (status.AccessIssue == LicenseAccessIssue.ReleaseDateMissing)
        {
            StatusText.Text = "Install a published version to use your license";
            StatusDetail.Text = "This development build has no release date. Your saved license has been kept.";
            DeactivateButton.Visibility = Visibility.Visible;
        }

        if (status.AccessIssue == LicenseAccessIssue.DifferentComputer)
        {
            StatusText.Text = "Activate this computer";
            StatusDetail.Text = "This activation belongs to another computer. Sign in to move your license here.";
        }
        else if (status.AccessIssue == LicenseAccessIssue.ActivationCheckRequired)
        {
            StatusText.Text = "Check your activation";
            StatusDetail.Text = "Your purchased versions are still included. Connect to the internet and sign in to renew this computer's offline allowance.";
        }
        AnnounceStatusIfChanged();
    }

    private void SetKeyEntryVisible(bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        KeyExpander.Visibility = v;
        KeyLabel.Visibility = v;
        KeyInputBorder.Visibility = v;
        ActivateButton.Visibility = v;
    }

    private void ApplyStorefrontPolicy()
    {
        BuyButton.Visibility = Visibility.Collapsed;
        BuyButton.IsEnabled = false;

        AccountButton.Visibility = _storefront.AccountAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;
        AccountButton.IsEnabled = _storefront.AccountAvailable;
        ActivationHelpText.Text = _accountClient is null ? _storefront.ActivationHelp : "Your captures stay on this computer. Signing in manages your license; it does not upload screenshots.";
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        SetBusy(true);
        ShowProgressMessage("Activating license...");
        try
        {
            ActivationResult result = await _license.ActivateAsync(
                KeyBox.Text,
                _lifetime.Token);
            if (result.Succeeded)
            {
                Render();
                ShowMessage(result.Message, ok: true);
            }
            else
            {
                ShowMessage(result.Message, ok: false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowMessage(
                "Activation could not be completed. Check your connection and try again.",
                ok: false);
        }
        finally
        {
            if (!_lifetime.IsCancellationRequested)
            {
                SetBusy(false);
                FocusPreferredControl();
            }
        }
    }

    private void ShowMessage(string text, bool ok)
    {
        MessageText.Foreground = ok
            ? new SolidColorBrush(Color.FromRgb(0x3F, 0xC0, 0x6A))
            : (Brush)FindResource("DangerBrush");
        MessageText.Visibility = Visibility.Visible;
        MessageText.Text = text;
        RaiseLiveRegionChanged(MessageText);
    }

    private void ShowProgressMessage(string text)
    {
        MessageText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        MessageText.Visibility = Visibility.Visible;
        MessageText.Text = text;
        RaiseLiveRegionChanged(MessageText);
    }

    private void Buy_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(_storefront.CheckoutUri ?? ProductPricingUri);
    }

    private static readonly Uri ProductPricingUri = new("https://getstillmark.com/pricing");

    private static string FormatTrialRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return "less than a minute";

        int days = (int)Math.Floor(remaining.TotalDays);
        int hours = remaining.Hours;
        int minutes = remaining.Minutes;
        if (days > 0)
            return $"{days} day{(days == 1 ? "" : "s")}, {hours} hour{(hours == 1 ? "" : "s")}, and {minutes} min{(minutes == 1 ? "ute" : "utes")}";
        if (hours > 0)
            return $"{hours} hour{(hours == 1 ? "" : "s")} and {minutes} min{(minutes == 1 ? "ute" : "utes")}";
        return $"{Math.Max(1, minutes)} minute{(minutes == 1 ? "" : "s")}";
    }

    private void Account_Click(object sender, RoutedEventArgs e)
    {
        if (_storefront.AccountUri is { } accountUri)
            OpenUrl(accountUri);
    }

    private async void Deactivate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        var confirm = MessageBox.Show(Window.GetWindow(this),
            _license.Status.License?.Version == 3 ? "Deactivate this computer and free its license for another? You will need to sign in to activate again." : "Remove the license from this computer? You can re-activate later with the same key.",
            "Remove license?", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK)
            return;

        SetBusy(true);
        ShowProgressMessage("Deactivating license...");
        try
        {
            if (_license.Status.License is { Version: 3 } payload)
            {
                if (_accountClient is null || !Guid.TryParseExact(payload.ActivationId, "D", out Guid activationId))
                {
                    ShowMessage("Sign in to deactivate this computer and free its license.", false);
                    return;
                }
                await _accountClient.DeactivateAsync(activationId, _lifetime.Token);
            }
            DeactivationResult result = await _license.DeactivateAsync(_lifetime.Token);
            if (result.Succeeded)
            {
                KeyBox.Clear();
                Render();
                ShowMessage(result.Message, ok: true);
            }
            else
            {
                ShowMessage(result.Message, ok: false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (AccountAuthenticationException)
        {
            ShowSignIn();
            ShowMessage("Sign in to deactivate this computer and free its license.", ok: false);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowMessage(
                "Deactivation could not be completed. This device remains activated.",
                ok: false);
        }
        finally
        {
            if (!_lifetime.IsCancellationRequested)
            {
                SetBusy(false);
                FocusPreferredControl();
            }
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        AccountSection.IsEnabled = !busy;
        KeyBox.IsEnabled = !busy;
        ActivateButton.IsEnabled = !busy;
        DeactivateButton.IsEnabled = !busy;
        ContinueButton.IsEnabled = !busy;
        BuyButton.IsEnabled = !busy && _storefront.CheckoutAvailable;
        AccountButton.IsEnabled = !busy && _storefront.AccountAvailable;
    }

    private void FocusPreferredControl()
    {
        if (_accountClient is not null && !_accountSignedIn && SignInPanel.IsVisible)
        {
            EmailBox.Focus();
            return;
        }
        if (KeyBox.IsVisible && KeyBox.IsEnabled)
        {
            KeyBox.Focus();
            return;
        }

        if (ContinueButton.IsVisible && ContinueButton.IsEnabled)
        {
            ContinueButton.Focus();
            return;
        }

        if (DeactivateButton.IsVisible && DeactivateButton.IsEnabled)
            DeactivateButton.Focus();
    }

    private void AnnounceStatusIfChanged()
    {
        string status = $"{StatusText.Text}\n{StatusDetail.Text}";
        if (!IsLoaded
            || string.Equals(status, _lastAnnouncedStatus, StringComparison.Ordinal))
        {
            return;
        }

        _lastAnnouncedStatus = status;
        RaiseLiveRegionChanged(StatusText);
    }

    private void RaiseLiveRegionChanged(UIElement element)
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        int version = ++_liveRegionAnnouncementVersion;
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                if (version != _liveRegionAnnouncementVersion || !element.IsVisible)
                    return;
                AutomationPeer? peer = UIElementAutomationPeer.FromElement(element)
                    ?? UIElementAutomationPeer.CreatePeerForElement(element);
                peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            },
            DispatcherPriority.Background);
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (_license.Status.State == LicenseState.Expired)
        {
            Render();
            return;
        }

        ReturnRequested?.Invoke();
    }

    private void OpenUrl(Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }
}
