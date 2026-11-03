using System.Globalization;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.RtmpBroadcast;

// Snapshot Jellyfin's selected device and source policy for this broadcast.
// Named devices mirror Jellyfin 12.4 EncodingHelper. Frames cross into CPU
// memory for switching, subtitles and chat, regardless of the selected codec.
internal sealed record HardwareAcceleration(
    HardwareAccelerationType Backend, string? Device, bool Encode, bool LowPowerEncode, string? Decoder, int BitDepth,
    string? VaapiDriver = null, bool NativeAv1 = false, bool AllowProfileMismatch = false)
{
    internal string Encoder => $"h264_{Backend}";

    internal string Name => Backend switch
    {
        HardwareAccelerationType.qsv => "Intel Quick Sync",
        HardwareAccelerationType.nvenc => "NVIDIA NVENC",
        HardwareAccelerationType.vaapi => "VA-API",
        HardwareAccelerationType.amf => "AMD AMF",
        HardwareAccelerationType.videotoolbox => "Apple VideoToolbox",
        HardwareAccelerationType.rkmpp => "Rockchip RKMPP",
        HardwareAccelerationType.v4l2m2m => "V4L2 M2M",
        _ => Backend.ToString(),
    };

    internal string DecoderFamily => Backend switch
    {
        HardwareAccelerationType.qsv => "Intel GPU",
        HardwareAccelerationType.nvenc => "NVIDIA NVDEC",
        HardwareAccelerationType.amf => "AMD GPU",
        _ => Name,
    };

    internal string DecodeLabel => (Backend, Decoder) switch
    {
        (HardwareAccelerationType.qsv, "vaapi") => "Intel GPU (VA-API)",
        (HardwareAccelerationType.qsv, "d3d11va") => "Intel GPU (D3D11VA)",
        (HardwareAccelerationType.nvenc, _) => $"NVIDIA NVDEC ({Decoder})",
        (HardwareAccelerationType.amf, _) => "AMD GPU (D3D11VA)",
        (HardwareAccelerationType.vaapi, _) => "VA-API",
        // VideoToolbox may internally choose a software implementation.
        (HardwareAccelerationType.videotoolbox, _) => "Apple VideoToolbox",
        _ => $"{Name} ({Decoder})",
    };

    internal static bool IsSupportedPlatform(HardwareAccelerationType backend) => backend switch
    {
        HardwareAccelerationType.qsv or HardwareAccelerationType.nvenc => OperatingSystem.IsLinux() || OperatingSystem.IsWindows(),
        HardwareAccelerationType.vaapi or HardwareAccelerationType.rkmpp or HardwareAccelerationType.v4l2m2m => OperatingSystem.IsLinux(),
        HardwareAccelerationType.amf => OperatingSystem.IsWindows(),
        HardwareAccelerationType.videotoolbox => OperatingSystem.IsMacOS(),
        _ => false,
    };

    internal List<string> DeviceArgs()
    {
        switch (Backend)
        {
            case HardwareAccelerationType.qsv:
                // Derive QSV from the chosen child adapter, even for native
                // decoding, so a manually selected D3D device is verified.
                if (OperatingSystem.IsWindows())
                {
                    string adapter = int.TryParse(Device, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                        ? Math.Max(0, index).ToString(CultureInfo.InvariantCulture) : ",vendor=0x8086";
                    return ["-init_hw_device", $"d3d11va=dx11:{adapter}", "-init_hw_device", "qsv=qs@dx11", "-filter_hw_device", "qs"];
                }
                string intelNode = string.IsNullOrWhiteSpace(Device) ? ",vendor_id=0x8086" : Device;
                return ["-init_hw_device", $"vaapi=va:{intelNode},driver=iHD", "-init_hw_device", "qsv=qs@va", "-filter_hw_device", "qs"];
            case HardwareAccelerationType.vaapi:
                string options = (Device ?? "") + (VaapiDriver is null ? "" : ",driver=" + VaapiDriver);
                string va = options.Length == 0 ? "vaapi=va" : "vaapi=va:" + options;
                return ["-init_hw_device", va, "-filter_hw_device", "va"];
            case HardwareAccelerationType.nvenc:
                return ["-init_hw_device", "cuda=cu:0", "-filter_hw_device", "cu"];
            case HardwareAccelerationType.amf:
                return ["-init_hw_device", "d3d11va=dx11:,vendor=0x1002", "-filter_hw_device", "dx11"];
            case HardwareAccelerationType.videotoolbox:
                return ["-init_hw_device", "videotoolbox=vt"];
            case HardwareAccelerationType.rkmpp:
                return ["-init_hw_device", "rkmpp=rk"];
            case HardwareAccelerationType.v4l2m2m:
                return []; // The codec wrapper opens the mem2mem device itself.
            default:
                throw new InvalidOperationException("Unsupported hardware acceleration method.");
        }
    }

    internal List<string> EncoderArgs()
    {
        var args = new List<string> { "-c:v", Encoder };
        switch (Backend)
        {
            case HardwareAccelerationType.qsv:
                args.AddRange(["-vf", "format=nv12", "-preset", "veryfast", "-async_depth", "1"]);
                break;
            case HardwareAccelerationType.nvenc:
                args.AddRange(["-vf", "format=nv12,hwupload", "-preset", "p4", "-tune", "ll", "-rc", "cbr", "-rc-lookahead", "0"]);
                break;
            case HardwareAccelerationType.amf:
                args.AddRange(["-vf", "format=nv12,hwupload", "-usage", "lowlatency", "-rc", "cbr"]);
                break;
            case HardwareAccelerationType.vaapi:
                args.AddRange(["-vf", "format=nv12,hwupload"]);
                break;
            case HardwareAccelerationType.videotoolbox:
                args.AddRange(["-vf", "format=nv12", "-realtime", "1", "-allow_sw", "0"]);
                break;
            // Rockchip MPP and V4L2 M2M accept our existing YUV420P frames.
        }

        if (LowPowerEncode && Backend is HardwareAccelerationType.qsv or HardwareAccelerationType.vaapi)
            args.AddRange(["-low_power", "1"]);
        return args;
    }

    internal List<string> DecodeArgs()
    {
        var args = DeviceArgs();
        var (mode, alias, surface) = Backend switch
        {
            HardwareAccelerationType.qsv when Decoder is not ("d3d11va" or "vaapi") => ("qsv", "qs", "qsv"),
            HardwareAccelerationType.qsv when Decoder == "d3d11va" => ("d3d11va", "dx11", "d3d11"),
            HardwareAccelerationType.qsv or HardwareAccelerationType.vaapi => ("vaapi", "va", "vaapi"),
            HardwareAccelerationType.nvenc => ("cuda", "cu", "cuda"),
            HardwareAccelerationType.amf => ("d3d11va", "dx11", "d3d11"),
            HardwareAccelerationType.videotoolbox => ("videotoolbox", "vt", "videotoolbox_vld"),
            HardwareAccelerationType.rkmpp => ("rkmpp", "rk", "drm_prime"),
            _ => throw new InvalidOperationException("Unsupported hardware decoder."),
        };

        args.AddRange(["-hwaccel", mode, "-hwaccel_device", alias, "-hwaccel_output_format", surface]);
        if (Decoder != mode) args.AddRange(["-c:v", Decoder!]);
        else if (NativeAv1) args.AddRange(["-c:v", "av1"]);
        if (AllowProfileMismatch) args.AddRange(["-hwaccel_flags", "+allow_profile_mismatch"]);
        if (mode == "d3d11va") args.AddRange(["-threads:v", "2"]);
        if (Decoder == "cuda") args.AddRange(["-threads:v", "1"]);
        return args;
    }

    // Require a hardware surface. If FFmpeg silently chooses a regular software
    // decoder, hwdownload fails and the normal explicitly labelled fallback runs.
    internal string Download => BitDepth == 10 ? "hwdownload,format=p010le" : "hwdownload,format=nv12";
}
