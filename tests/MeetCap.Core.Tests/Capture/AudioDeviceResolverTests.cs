using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using Xunit;

namespace MeetCap.Core.Tests.Capture;

public class AudioDeviceResolverTests
{
    private static readonly CaptureDeviceInfo Default = new("id-default", "Default Mic", true);
    private static readonly CaptureDeviceInfo Other = new("id-other", "USB Mic", false);

    private static readonly CaptureDeviceInfo DefaultRender = new("render-default", "Default Speakers", true);
    private static readonly CaptureDeviceInfo OtherRender = new("render-hdmi", "HDMI Output", false);

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

    // -- render endpoint resolution (docs/ROADMAP.md M5: loopback capture source) ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public void ResolveRender_TreatsEmptyAndDefaultTokenAsTheSystemDefaultRender(string? configured)
    {
        var enumerator = new FakeDeviceEnumerator(Default).WithRenderDevices(DefaultRender, OtherRender);

        var resolved = AudioDeviceResolver.ResolveRender(enumerator, configured);

        Assert.Equal("render-default", resolved.Id);
    }

    [Fact]
    public void ResolveRender_ByExplicitId_ReturnsThatRenderDevice()
    {
        var enumerator = new FakeDeviceEnumerator(Default).WithRenderDevices(DefaultRender, OtherRender);

        var resolved = AudioDeviceResolver.ResolveRender(enumerator, "render-hdmi");

        Assert.Equal("render-hdmi", resolved.Id);
        Assert.False(resolved.IsDefault);
    }

    [Fact]
    public void ResolveRender_WithNoDefaultRenderDevice_ThrowsActionableError()
    {
        // No render endpoint means loopback cannot capture anything the machine plays, so
        // the error is actionable and points at the online render-device setting rather
        // than silently degrading (docs/RELIABILITY.md section 8).
        var enumerator = new FakeDeviceEnumerator(Default).WithRenderDevices(OtherRender);

        var error = Assert.Throws<DeviceUnavailableException>(() => AudioDeviceResolver.ResolveRender(enumerator, "default"));

        Assert.Contains("No default render device", error.Message, StringComparison.Ordinal);
        Assert.Contains("meetcap devices", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveRender_WithUnknownConfiguredId_ListsAvailableRenderDevices()
    {
        var enumerator = new FakeDeviceEnumerator(Default).WithRenderDevices(DefaultRender, OtherRender);

        var error = Assert.Throws<DeviceUnavailableException>(
            () => AudioDeviceResolver.ResolveRender(enumerator, "render-missing"));

        Assert.Contains("render-missing", error.Message, StringComparison.Ordinal);
        Assert.Contains("Default Speakers", error.Message, StringComparison.Ordinal);
        Assert.Contains("Available render devices", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolveRender_ReturnsNullInsteadOfThrowing()
    {
        var enumerator = new FakeDeviceEnumerator(Default).WithRenderDevices(OtherRender);
        Assert.Null(AudioDeviceResolver.TryResolveRender(enumerator, "default"));
        Assert.NotNull(AudioDeviceResolver.TryResolveRender(enumerator, "render-hdmi"));
    }

    private sealed class FakeDeviceEnumerator : IAudioDeviceEnumerator
    {
        private readonly List<CaptureDeviceInfo> _devices = new();
        private readonly List<CaptureDeviceInfo> _renderDevices = new();

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

        public FakeDeviceEnumerator WithRenderDevices(params CaptureDeviceInfo?[] devices)
        {
            foreach (var device in devices)
            {
                if (device is not null)
                {
                    _renderDevices.Add(device);
                }
            }

            return this;
        }

        public IReadOnlyList<CaptureDeviceInfo> EnumerateCaptureDevices() => _devices;

        public CaptureDeviceInfo? GetDefaultCaptureDevice() => _devices.FirstOrDefault(d => d.IsDefault);

        public CaptureDeviceInfo? FindCaptureDevice(string deviceId)
            => _devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));

        public IReadOnlyList<CaptureDeviceInfo> EnumerateRenderDevices() => _renderDevices;

        public CaptureDeviceInfo? GetDefaultRenderDevice() => _renderDevices.FirstOrDefault(d => d.IsDefault);

        public CaptureDeviceInfo? FindRenderDevice(string deviceId)
            => _renderDevices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
    }
}
