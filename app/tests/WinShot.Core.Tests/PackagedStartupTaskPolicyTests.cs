using WinShot.App.Lifecycle;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class PackagedStartupTaskPolicyTests
{
    [Theory]
    [InlineData(
        (int)PackagedStartupRegistrationState.Enabled,
        true,
        true,
        (int)PackagedStartupAttention.None)]
    [InlineData(
        (int)PackagedStartupRegistrationState.Disabled,
        false,
        true,
        (int)PackagedStartupAttention.None)]
    [InlineData(
        (int)PackagedStartupRegistrationState.DisabledByUser,
        false,
        false,
        (int)PackagedStartupAttention.EnableInWindowsSettings)]
    [InlineData(
        (int)PackagedStartupRegistrationState.DisabledByPolicy,
        false,
        false,
        (int)PackagedStartupAttention.ManagedByAdministrator)]
    [InlineData(
        (int)PackagedStartupRegistrationState.EnabledByPolicy,
        true,
        false,
        (int)PackagedStartupAttention.ManagedByAdministrator)]
    public void PresentationReflectsEffectiveWindowsState(
        int stateValue,
        bool expectedChecked,
        bool expectedCanToggle,
        int expectedAttentionValue)
    {
        PackagedStartupSettingsPresentation presentation =
            PackagedStartupTaskPolicy.Present(
                (PackagedStartupRegistrationState)stateValue);

        Assert.Equal(expectedChecked, presentation.IsChecked);
        Assert.Equal(expectedCanToggle, presentation.CanToggle);
        Assert.Equal(
            (PackagedStartupAttention)expectedAttentionValue,
            presentation.Attention);
    }

    [Theory]
    [InlineData((int)PackagedStartupRegistrationState.Unavailable)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void UnavailableOrInvalidState_IsIndeterminateAndDisabled(
        int stateValue)
    {
        PackagedStartupSettingsPresentation presentation =
            PackagedStartupTaskPolicy.Present(
                (PackagedStartupRegistrationState)stateValue);

        Assert.Null(presentation.IsChecked);
        Assert.False(presentation.CanToggle);
        Assert.Equal(
            PackagedStartupAttention.StatusUnavailable,
            presentation.Attention);
    }

    [Theory]
    [InlineData(
        true,
        (int)PackagedStartupRegistrationState.Disabled,
        (int)PackagedStartupMutation.RequestEnable)]
    [InlineData(
        false,
        (int)PackagedStartupRegistrationState.Enabled,
        (int)PackagedStartupMutation.Disable)]
    [InlineData(
        true,
        (int)PackagedStartupRegistrationState.DisabledByUser,
        (int)PackagedStartupMutation.None)]
    [InlineData(
        true,
        (int)PackagedStartupRegistrationState.DisabledByPolicy,
        (int)PackagedStartupMutation.None)]
    [InlineData(
        false,
        (int)PackagedStartupRegistrationState.EnabledByPolicy,
        (int)PackagedStartupMutation.None)]
    [InlineData(
        true,
        (int)PackagedStartupRegistrationState.Unavailable,
        (int)PackagedStartupMutation.None)]
    public void MutationNeverOverridesUserOrPolicyAuthority(
        bool requestedEnabled,
        int stateValue,
        int expectedMutationValue)
    {
        PackagedStartupMutation mutation =
            PackagedStartupTaskPolicy.DecideMutation(
                requestedEnabled,
                (PackagedStartupRegistrationState)stateValue);

        Assert.Equal(
            (PackagedStartupMutation)expectedMutationValue,
            mutation);
    }

    [Theory]
    [InlineData(
        true,
        (int)PackagedStartupRegistrationState.Enabled,
        true)]
    [InlineData(
        true,
        (int)PackagedStartupRegistrationState.EnabledByPolicy,
        true)]
    [InlineData(
        true,
        (int)PackagedStartupRegistrationState.DisabledByUser,
        false)]
    [InlineData(
        false,
        (int)PackagedStartupRegistrationState.Disabled,
        true)]
    [InlineData(
        false,
        (int)PackagedStartupRegistrationState.DisabledByUser,
        true)]
    [InlineData(
        false,
        (int)PackagedStartupRegistrationState.DisabledByPolicy,
        true)]
    [InlineData(
        false,
        (int)PackagedStartupRegistrationState.EnabledByPolicy,
        false)]
    [InlineData(
        true,
        (int)PackagedStartupRegistrationState.Unavailable,
        false)]
    public void PersistedPreferenceRequiresConfirmedEffectiveState(
        bool requestedEnabled,
        int stateValue,
        bool expectedConfirmed)
    {
        Assert.Equal(
            expectedConfirmed,
            PackagedStartupTaskPolicy.ConfirmsPreference(
                requestedEnabled,
                (PackagedStartupRegistrationState)stateValue));
    }
}
