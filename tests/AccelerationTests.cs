using System.Reflection;
using Jellyfin.Plugin.RtmpBroadcast;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Xunit;

namespace RtmpBroadcast.Tests;

public class AccelerationTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void EncodingAndSourceCodecPermissionsAreIndependent(bool encode, bool allowH264)
    {
        var backend = OperatingSystem.IsMacOS() ? HardwareAccelerationType.videotoolbox : HardwareAccelerationType.nvenc;
        var result = Select(backend, encode, allowH264);
        Assert.Equal(encode, result?.Encode == true);
        Assert.Equal(allowH264, result?.Decoder is not null);
        if (!encode && !allowH264) Assert.Null(result);
    }

    [Theory]
    [InlineData(HardwareAccelerationType.qsv, "vaapi", "d3d11va", null)]
    [InlineData(HardwareAccelerationType.vaapi, "vaapi", null, null)]
    [InlineData(HardwareAccelerationType.nvenc, "cuda", "cuda", null)]
    [InlineData(HardwareAccelerationType.amf, null, "d3d11va", null)]
    [InlineData(HardwareAccelerationType.videotoolbox, null, null, "videotoolbox")]
    [InlineData(HardwareAccelerationType.rkmpp, "rkmpp", null, null)]
    [InlineData(HardwareAccelerationType.v4l2m2m, null, null, null)]
    public void H264UsesTheServerBackendOnSupportedPlatforms(HardwareAccelerationType backend, string? linux, string? windows, string? mac)
    {
        var result = Select(backend, true, true);
        var decoder = OperatingSystem.IsLinux() ? linux : OperatingSystem.IsWindows() ? windows : mac;
        bool encodeOnly = backend == HardwareAccelerationType.v4l2m2m && OperatingSystem.IsLinux();
        if (decoder is null && !encodeOnly)
        {
            Assert.Null(result);
            return;
        }
        Assert.NotNull(result);
        Assert.True(result.Encode);
        Assert.Equal($"h264_{backend}", result.Encoder);
        Assert.Equal(decoder, result.Decoder);
    }

    private static HardwareAcceleration? Select(HardwareAccelerationType backend, bool encode, bool allowH264)
    {
        var encoder = DispatchProxy.Create<IMediaEncoder, EncoderCapabilities>();
        var helper = new EncodingHelper(null!, encoder, null!, null!, null!, null!);
        var options = new EncodingOptions
        {
            HardwareAccelerationType = backend,
            EnableHardwareEncoding = encode,
            HardwareDecodingCodecs = allowH264 ? ["h264"] : ["hevc"]
        };
        var source = new MediaSourceInfo
        {
            MediaStreams = [new() { Type = MediaStreamType.Video, Codec = "h264", PixelFormat = "yuv420p", Width = 1920, Height = 1080 }]
        };
        return AccelerationPolicy.Select(options, source, encoder, helper);
    }

    // The real Jellyfin 12.4 EncodingHelper queries the server's FFmpeg capabilities.
    public class EncoderCapabilities : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method?.Name switch
        {
            "get_EncoderVersion" => new Version(8, 1),
            "get_IsVaapiDeviceInteliHD" or "get_IsVaapiDeviceInteli965" => false,
            nameof(IMediaEncoder.SupportsEncoder) or nameof(IMediaEncoder.SupportsDecoder) or nameof(IMediaEncoder.SupportsHwaccel)
                or nameof(IMediaEncoder.SupportsFilter) or nameof(IMediaEncoder.SupportsFilterWithOption) => true,
            _ => throw new NotSupportedException(method?.Name)
        };
    }

    [Fact]
    public void VaapiUsesSelectedRenderNodeAndDriverInOneArgument()
    {
        var hardware = new HardwareAcceleration(HardwareAccelerationType.vaapi, "/dev/dri/by-path/pci-0000:00:02.0-render",
            true, false, "vaapi", 10, "iHD");
        Assert.Contains("vaapi=va:/dev/dri/by-path/pci-0000:00:02.0-render,driver=iHD", hardware.DeviceArgs());
        Assert.Equal("hwdownload,format=p010le", hardware.Download);
    }

    [Theory]
    [InlineData(HardwareAccelerationType.vaapi)]
    [InlineData(HardwareAccelerationType.nvenc)]
    [InlineData(HardwareAccelerationType.amf)]
    public void EncoderReceivesUploadedNv12Frames(HardwareAccelerationType backend)
    {
        var hardware = new HardwareAcceleration(backend, null, true, false, null, 8);
        Assert.Contains("format=nv12,hwupload", hardware.EncoderArgs());
    }

    [Fact]
    public void VideoToolboxPublisherRequiresHardware()
    {
        var hardware = new HardwareAcceleration(HardwareAccelerationType.videotoolbox, null, true, false, null, 8);
        var args = hardware.EncoderArgs();
        Assert.Contains("-allow_sw", args);
        Assert.Equal("0", args[args.IndexOf("-allow_sw") + 1]);
    }
}
