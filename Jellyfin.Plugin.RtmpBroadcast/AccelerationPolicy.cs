using System.Runtime.InteropServices;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.RtmpBroadcast;

internal static class AccelerationPolicy
{
    internal static HardwareAcceleration? Select(EncodingOptions options, MediaSourceInfo source, IMediaEncoder encoder, EncodingHelper helper)
    {
        var backend = options.HardwareAccelerationType;
        if (!HardwareAcceleration.IsSupportedPlatform(backend)) return null;
        var hwaccel = backend switch
        {
            HardwareAccelerationType.nvenc => "cuda",
            HardwareAccelerationType.amf => "d3d11va",
            HardwareAccelerationType.v4l2m2m => null, // FFmpeg exposes this as an encoder only.
            _ => backend.ToString()
        };
        if (hwaccel is not null && !encoder.SupportsHwaccel(hwaccel)) return null;

        // The movie decoder maps 0:V:0. Jellyfin classifies embedded pictures separately from video.
        var video = helper.GetMediaStream(source.MediaStreams.Where(s => !s.IsExternal), null, MediaStreamType.Video);
        var job = new EncodingJobInfo(TranscodingJobType.Progressive)
        {
            MediaSource = source, VideoType = source.VideoType ?? VideoType.VideoFile, VideoStream = video,
            // Decode at source size. The broadcast compositor scales after downloading the frame.
            BaseRequest = new BaseEncodingJobOptions { Width = video?.Width, Height = video?.Height }
        };
        var encode = helper.GetH264Encoder(job, options) == $"h264_{backend}";
        var depth = EncodingHelper.GetVideoColorBitDepth(job);
        var (decoder, nativeAv1, allowProfileMismatch) = SelectDecoder(helper, job, options, depth);
        if (!encode && decoder is null) return null;

        string? device = backend switch
        {
            HardwareAccelerationType.qsv => options.QsvDevice,
            HardwareAccelerationType.vaapi => options.VaapiDevice,
            _ => null
        };
        string? driver = null;
        if (backend == HardwareAccelerationType.vaapi)
            driver = encoder.IsVaapiDeviceInteliHD ? "iHD" : encoder.IsVaapiDeviceInteli965 ? "i965" : null;
        var lowPower = options.EnableIntelLowPowerH264HwEncoder && (backend == HardwareAccelerationType.qsv || driver is not null);
        return new(backend, device, encode, lowPower, decoder, depth, driver, nativeAv1, allowProfileMismatch);
    }

    private static (string? Decoder, bool NativeAv1, bool AllowProfileMismatch) SelectDecoder(
        EncodingHelper helper, EncodingJobInfo job, EncodingOptions options, int depth)
    {
        var video = job.VideoStream;
        // Let FFmpeg's software autorotation handle display matrices before scaling.
        if (video is null || options.HardwareDecodingCodecs is not { Length: > 0 } || (video.Rotation ?? 0) % 360 != 0) return default;

        // The CPU compositing path can download NV12 and P010 surfaces. Other chroma formats
        // or depths still benefit from hardware publishing when H.264 encoding is enabled.
        if (depth is not (8 or 10) || video.PixelFormat?.ToLowerInvariant() is not ("yuv420p" or "yuvj420p" or "yuv420p10le"))
            return default;
        var backend = options.HardwareAccelerationType;
        // Rockchip's packed 10-bit NV15 cannot be downloaded as P010.
        if (backend == HardwareAccelerationType.rkmpp && depth != 8) return default;
        if (video.Codec?.ToLowerInvariant() is "h264" or "avc")
        {
            bool appleSilicon = backend == HardwareAccelerationType.videotoolbox && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            if (depth == 10 && !(appleSilicon && OperatingSystem.IsMacOSVersionAtLeast(14, 6))) return default;
            if (!appleSilicon && (video.Profile?.Contains("4:2:2", StringComparison.OrdinalIgnoreCase) == true
                || video.Profile?.Contains("4:4:4", StringComparison.OrdinalIgnoreCase) == true)) return default;
        }

        // These per-backend helpers check Jellyfin's codec, profile, bit-depth and decoder
        // preferences. Use their input selection without adopting the server's filter graph.
        var args = backend switch
        {
            HardwareAccelerationType.qsv => helper.GetQsvHwVidDecoder(job, options, video, depth),
            HardwareAccelerationType.nvenc => helper.GetNvdecVidDecoder(job, options, video, depth),
            HardwareAccelerationType.vaapi => helper.GetVaapiVidDecoder(job, options, video, depth),
            HardwareAccelerationType.amf => helper.GetAmfVidDecoder(job, options, video, depth),
            HardwareAccelerationType.videotoolbox => helper.GetVideotoolboxVidDecoder(job, options, video, depth),
            HardwareAccelerationType.rkmpp => helper.GetRkmppVidDecoder(job, options, video, depth),
            _ => null // Jellyfin 12.4 only selects a V4L2 M2M encoder.
        };
        var words = args?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words is not ["-hwaccel", var mode, ..]) return default;
        if (mode == "qsv" || (mode == "cuda" && !options.EnableEnhancedNvdecDecoder))
            return words is [.., "-c:v", var decoder] ? (decoder, false, false) : default;
        return (mode, words is [.., "-c:v", "av1"], words.Contains("+allow_profile_mismatch"));
    }
}
