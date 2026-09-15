using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using Xunit;

namespace MeetCap.Core.Tests.Capture;

public class AudioDeviceResolverTests
{
    private static readonly CaptureDeviceInfo Default = new("id-default", "Default Mic", true);
    private static readonly CaptureDeviceInfo Other = new("id-other", "USB Mic", false);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public void Resolve_TreatsEmptyAndDefaultTokenAsTheSystemDefault(string? configured)
    {
        var resolved = AudioDeviceResolver.Resolve(new FakeDeviceEnumerator(Default, Other), configured);

        Assert.Equal("id-default", resolved.Id);
    }

    [Fact]
    public void Resolve_ByExplicitId_ReturnsThatDevice()
    {
        var resolved = AudioDeviceResolver.Resolve(new FakeDeviceEnumerator(Default, Other), "id-other");

        Assert.Equal("id-other", resolved.Id);
        Assert.False(resolved.IsDefault);
    }

    [Fact]
    public void Resolve_ByExplicitId_IsCaseInsensitive()
    {
        var resolved = AudioDeviceResolver.Resolve(new FakeDeviceEnumerator(Default, Other), "ID-OTHER");

        Assert.Equal("id-other", resolved.Id);
    }

    [Fact]
    public void Resolve_WithNoDefaultDevice_ThrowsActionableError()
    {
        var enumerator = new FakeDeviceEnumerator((CaptureDeviceInfo?)null, Other);

        var error = Assert.Throws<DeviceUnavailableException>(() => AudioDeviceResolver.Resolve(enumerator, "default"));

        Assert.Contains("No default capture device", error.Message, StringComparison.Ordinal);
        Assert.Contains("meetcap devices", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WithUnknownConfiguredId_ListsAvailableDevices()
    {
        var enumerator = new FakeDeviceEnumerator(Default, Other);

        var error = Assert.Throws<DeviceUnavailableException>(
            () => AudioDeviceResolver.Resolve(enumerator, "id-missing"));

        Assert.Contains("id-missing", error.Message, StringComparison.Ordinal);
        Assert.Contains("USB Mic", error.Message, StringComparison.Ordinal);
        Assert.Contains("id-default", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolve_ReturnsNullInsteadOfThrowing()
    {
        Assert.Null(AudioDeviceResolver.TryResolve(new FakeDeviceEnumerator((CaptureDeviceInfo?)null), "missing"));
        Assert.NotNull(AudioDeviceResolver.TryResolve(new FakeDeviceEnumerator(Default), "default"));
    }

    private sealed class FakeDeviceEnumerator : IAudioDeviceEnumerator
    {
        private readonly List<CaptureDeviceInfo> _devices = new();

        public FakeDeviceEnumerator(params CaptureDeviceInfo?[] devices)
        {
            foreach (var device in devices)
            {
                if (device is not null)
                {
                    _devices.Add(device);
                }
            }
        }

        public IReadOnlyList<CaptureDeviceInfo> EnumerateCaptureDevices() => _devices;

        public CaptureDeviceInfo? GetDefaultCaptureDevice() => _devices.FirstOrDefault(d => d.IsDefault);

        public CaptureDeviceInfo? FindCaptureDevice(string deviceId)
            => _devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
    }
}
