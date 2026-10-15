using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinShot.Licensing;
using WinShot.Licensing.Accounts;

namespace WinShot.App;

public partial class LicenseWindow
{
    private string? _pendingEmail;
    private bool _accountSignedIn;
    private DateTimeOffset _nextCodeRequest;

    private async Task LoadAccountAsync()
    {
        AccountSnapshot account = await _accountClient!.GetAccountAsync(_lifetime.Token);
        if (_lifetime.IsCancellationRequested) return;
        _accountSignedIn = true;
        SignInPanel.Visibility = CodePanel.Visibility = Visibility.Collapsed;
        SignedInPanel.Visibility = Visibility.Visible;
        SignedInEmail.Text = account.Email;
        AccountLicenses.Children.Clear();
        if (account.Licenses.Count == 0)
        {
            AccountLicenses.Children.Add(AccountNote("No license is linked to this email yet. If you have purchased Stillmark, check that this is the email used at checkout."));
            return;
        }
        foreach (AccountLicense license in account.Licenses)
        {
            var group = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
            bool complimentary = string.Equals(license.Status, "complimentary", StringComparison.Ordinal);
            group.Children.Add(new TextBlock { Text = complimentary
                ? $"Complimentary license · Granted {license.PurchasedAtUtc:MMM d, yyyy}"
                : $"Purchased {license.PurchasedAtUtc:MMM d, yyyy}", FontSize = 13, FontWeight = FontWeights.SemiBold });
            group.Children.Add(AccountNote($"Updates released before {license.UpdatesUntilUtc:MMM d, yyyy HH:mm} UTC are included."));
            bool active = complimentary || string.Equals(license.Status, "paid", StringComparison.Ordinal);
            if (!active) group.Children.Add(AccountNote("This license is inactive."));
            foreach (AccountDevice device in license.Devices)
            {
                var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
                var remove = new Button { Content = "Deactivate", Style = (Style)FindResource("TextCommand"), Padding = new Thickness(8, 4, 8, 4) };
                DockPanel.SetDock(remove, Dock.Right);
                remove.Click += async (_, _) => await RemoveAccountDeviceAsync(device);
                row.Children.Add(remove);
                row.Children.Add(new TextBlock { Text = device.Name, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                group.Children.Add(row);
            }
            bool thisComputer = license.Devices.Any(d => string.Equals(d.Id, _license.Status.License?.ActivationId, StringComparison.Ordinal));
            if (active)
            {
                var activate = new Button
                {
                    Content = thisComputer ? "Refresh activation" : "Activate this computer",
                    HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0),
                };
                activate.Click += async (_, _) => await RunAccountOperationAsync(() => ActivateAccountLicenseAsync(license), "Activating this computer…");
                group.Children.Add(activate);
            }
            AccountLicenses.Children.Add(group);
        }
    }

    private TextBlock AccountNote(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12,
        Foreground = (Brush)FindResource("TextSecondaryBrush"), Margin = new Thickness(0, 5, 0, 0),
    };

    private async Task ActivateAccountLicenseAsync(AccountLicense license)
    {
        if (AppInfo.PublishedAtUtc is not { } releaseDate)
        {
            ShowMessage("Install a published release to activate a purchased license. This is an undated development build.", false);
            return;
        }
        if (releaseDate >= license.UpdatesUntilUtc)
        {
            ShowMessage("This release is outside your update coverage. Your earlier eligible versions remain yours to use.", false);
            return;
        }
        AccountActivation activation = await _accountClient!.ActivateAsync(license.Id, Environment.MachineName, _lifetime.Token);
        if (!LicenseKey.TryVerify(activation.LicenseKey, out LicensePayload? payload)
            || payload is null || payload.Version != 3
            || !string.Equals(payload.Id, license.Id, StringComparison.Ordinal)
            || !LicenseEntitlement.MatchesDevice(payload, LicenseDeviceIdentity.Current)
            || !string.Equals(payload.ActivationId, activation.ActivationId?.ToString("D"), StringComparison.Ordinal))
        {
            ShowMessage("The activation response could not be verified. Your existing license has been kept.", false);
            return;
        }
        ActivationResult result = await _license.ActivateAsync(activation.LicenseKey, _lifetime.Token);
        Render();
        await LoadAccountAsync();
        ShowMessage(result.Message, result.Succeeded);
    }

    private async void SendCode_Click(object sender, RoutedEventArgs e)
    {
        if (DateTimeOffset.UtcNow < _nextCodeRequest)
        {
            ShowMessage("Please wait a minute before requesting another code.", false);
            return;
        }
        string email = EmailBox.Text.Trim();
        await RunAccountOperationAsync(async () =>
        {
            await _accountClient!.RequestEmailCodeAsync(email, _lifetime.Token);
            _pendingEmail = email;
            _nextCodeRequest = DateTimeOffset.UtcNow.AddMinutes(1);
            SignInPanel.Visibility = Visibility.Collapsed;
            CodePanel.Visibility = Visibility.Visible;
            CodeDescription.Text = $"Enter the code sent to {email}.";
            CodeBox.Clear();
            MessageText.Visibility = Visibility.Collapsed;
        }, "Sending your sign-in code…");
        if (CodePanel.IsVisible && !_busy) CodeBox.Focus();
    }

    private async void VerifyCode_Click(object sender, RoutedEventArgs e)
    {
        string code = CodeBox.Text.Trim();
        if (_pendingEmail is null || code.Length is < 6 or > 8 || !code.All(char.IsAsciiDigit))
        {
            ShowMessage("Enter the 6–8 digit code from your email.", false);
            return;
        }
        await RunAccountOperationAsync(async () =>
        {
            await _accountClient!.VerifyEmailCodeAsync(_pendingEmail, code, _lifetime.Token);
            CodeBox.Clear();
            await LoadAccountAsync();
            await _accountClient.RefreshStoredActivationAsync(_license, _lifetime.Token);
            Render();
        }, "Signing in…");
    }

    private void ChangeEmail_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ShowSignIn();
        EmailBox.Focus();
    }

    private async void RefreshAccount_Click(object sender, RoutedEventArgs e) =>
        await RunAccountOperationAsync(async () =>
        {
            StoredActivationRefreshResult refresh =
                await _accountClient!.RefreshStoredActivationAsync(_license, _lifetime.Token);
            await LoadAccountAsync();
            Render();
            (string message, bool ok) = refresh.Outcome switch
            {
                StoredActivationRefreshOutcome.Revoked =>
                    ("This computer's activation is no longer active. Sign in to activate it again.", false),
                StoredActivationRefreshOutcome.Unavailable =>
                    ("Your account is current, but this computer's offline allowance could not be refreshed. Please try again.", false),
                StoredActivationRefreshOutcome.InvalidResponse =>
                    ("The activation response could not be verified. Your existing license has been kept.", false),
                _ => ("Your licenses are up to date.", true),
            };
            ShowMessage(message, ok);
        }, "Checking your licenses…");

    private async void SignOut_Click(object sender, RoutedEventArgs e) =>
        await RunAccountOperationAsync(async () =>
        {
            await _accountClient!.SignOutAsync(_lifetime.Token);
            ShowSignIn();
            ShowMessage("Signed out on this computer. An existing activation stays usable until its next required check; sign back in to keep it current.", true);
        }, "Signing out…");

    private void ShowSignIn()
    {
        _accountSignedIn = false;
        _pendingEmail = null;
        CodeBox.Clear();
        SignedInEmail.Text = string.Empty;
        AccountLicenses.Children.Clear();
        SignInPanel.Visibility = Visibility.Visible;
        CodePanel.Visibility = SignedInPanel.Visibility = Visibility.Collapsed;
    }

    private async Task RemoveAccountDeviceAsync(AccountDevice device)
    {
        if (_busy || !Guid.TryParseExact(device.Id, "D", out Guid activationId)) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Deactivate {device.Name}? This frees the license for another computer. An offline copy may keep working until its next check, within 30 days.",
            "Deactivate computer", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await RunAccountOperationAsync(async () =>
        {
            await _accountClient!.DeactivateAsync(activationId, _lifetime.Token);
            if (string.Equals(_license.Status.License?.ActivationId, device.Id, StringComparison.Ordinal))
                await _license.DeactivateAsync(_lifetime.Token);
            await LoadAccountAsync();
            Render();
            ShowMessage("Computer deactivated. You can now activate another computer.", true);
        }, "Deactivating computer…");
    }

    private async Task RunAccountOperationAsync(Func<Task> action, string? progress = null)
    {
        if (_busy || _accountClient is null || _lifetime.IsCancellationRequested) return;
        SetBusy(true);
        if (progress is not null) ShowProgressMessage(progress);
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (AccountAuthenticationException)
        {
            ShowSignIn();
            if (progress is not null) ShowMessage("Sign in again to manage your licenses.", false);
        }
        catch (AccountApiException ex)
        {
            string message = ex.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => "Too many attempts. Please wait a little before trying again.",
                HttpStatusCode.Conflict => "This license is in use on another computer. Deactivate that computer first.",
                HttpStatusCode.BadRequest or HttpStatusCode.Forbidden => "The request could not be completed. Check your email or code and try again.",
                _ => "The account service is unavailable right now. Please try again shortly.",
            };
            ShowMessage(message, false);
        }
        catch (ArgumentException) { ShowMessage("Check the email address or code and try again.", false); }
        catch (Exception)
        {
            // Do not log account responses, email addresses, session tokens or license keys.
            ShowMessage("Could not connect to your account. Your existing license has been kept. Please try again.", false);
        }
        finally { if (!_lifetime.IsCancellationRequested) SetBusy(false); }
    }
}
