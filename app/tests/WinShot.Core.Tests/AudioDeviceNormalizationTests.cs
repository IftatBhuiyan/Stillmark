using WinShot.App.Recording;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class AudioDeviceNormalizationTests
{
    [Fact]
    public void NullCatalogReturnsOnlySafeSystemDefault()
    {
        IReadOnlyList<AudioDeviceEntry> devices =
            AudioDeviceNormalization.Normalize(candidates: null);

        AudioDeviceEntry device = Assert.Single(devices);
        Assert.Null(device.Id);
        Assert.Equal("System default", device.Name);
    }

    [Fact]
    public void HostileValuesAreBoundedBeforeTheyReachTheUi()
    {
        string millionCharacters = new('x', 1_000_000);
        var candidates = new[]
        {
            new AudioDeviceCandidate(millionCharacters, "oversized ID"),
            new AudioDeviceCandidate("valid-id", millionCharacters),
            new AudioDeviceCandidate("fallback-id", "   "),
        };

        IReadOnlyList<AudioDeviceEntry> devices =
            AudioDeviceNormalization.Normalize(candidates);

        Assert.Equal(3, devices.Count);
        Assert.DoesNotContain(devices, device => device.Id == millionCharacters);
        Assert.Equal(
            AudioDeviceNormalization.MaximumDisplayNameLength,
            devices[1].Name.Length);
        Assert.Equal("fallback-id", devices[2].Name);
        Assert.All(
            devices.Skip(1),
            device => Assert.InRange(
                device.Id!.Length,
                1,
                AudioDeviceNormalization.MaximumIdLength));
    }

    [Fact]
    public void DuplicateIdsAreRemovedCaseInsensitivelyInFirstSeenOrder()
    {
        var candidates = new[]
        {
            new AudioDeviceCandidate("  FIRST  ", "First name"),
            new AudioDeviceCandidate("first", "Duplicate"),
            new AudioDeviceCandidate("second", "Second name"),
            new AudioDeviceCandidate("SECOND", "Other duplicate"),
        };

        IReadOnlyList<AudioDeviceEntry> devices =
            AudioDeviceNormalization.Normalize(candidates);

        Assert.Collection(
            devices,
            device =>
            {
                Assert.Null(device.Id);
                Assert.Equal("System default", device.Name);
            },
            device =>
            {
                Assert.Equal("FIRST", device.Id);
                Assert.Equal("First name", device.Name);
            },
            device =>
            {
                Assert.Equal("second", device.Id);
                Assert.Equal("Second name", device.Name);
            });
    }

    [Fact]
    public void OutputAndCandidateTraversalHaveIndependentHardLimits()
    {
        int outputCandidatesVisited = 0;
        IEnumerable<AudioDeviceCandidate> ValidCandidates()
        {
            for (int i = 0; i < 1_000; i++)
            {
                outputCandidatesVisited++;
                yield return new AudioDeviceCandidate($"id-{i}", $"Device {i}");
            }
        }

        IReadOnlyList<AudioDeviceEntry> devices =
            AudioDeviceNormalization.Normalize(ValidCandidates());

        Assert.Equal(
            AudioDeviceNormalization.MaximumDevicesPerSource + 1,
            devices.Count);
        Assert.Equal(
            AudioDeviceNormalization.MaximumDevicesPerSource,
            outputCandidatesVisited);

        int invalidCandidatesVisited = 0;
        IEnumerable<AudioDeviceCandidate> InvalidCandidates()
        {
            for (int i = 0; i < 1_000; i++)
            {
                invalidCandidatesVisited++;
                yield return new AudioDeviceCandidate(null, $"Invalid {i}");
            }
        }

        IReadOnlyList<AudioDeviceEntry> fallbackOnly =
            AudioDeviceNormalization.Normalize(InvalidCandidates());

        Assert.Single(fallbackOnly);
        Assert.Equal(
            AudioDeviceNormalization.MaximumCandidatesExamined,
            invalidCandidatesVisited);
    }
}
