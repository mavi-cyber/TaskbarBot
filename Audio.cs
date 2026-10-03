using System;
using System.Runtime.InteropServices;

namespace TaskbarBot;

/// <summary>
/// What the PC's speakers are doing, read from Windows Core Audio: how loud the sound playing
/// right now is, and where the volume is set. Read-only: nothing is recorded and nothing is changed.
/// </summary>
static class Audio
{
    static IAudioMeterInformation? meter;
    static IAudioEndpointVolume? volume;
    static DateTime retryAt;

    static bool Connect()
    {
        if (meter is not null && volume is not null) return true;
        if (DateTime.UtcNow < retryAt) return false;
        retryAt = DateTime.UtcNow.AddSeconds(30);

        var devices = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        if (devices.GetDefaultAudioEndpoint(0 /* playback */, 1 /* multimedia */, out IMMDevice speakers) != 0) return false;
        Guid meterId = typeof(IAudioMeterInformation).GUID, volumeId = typeof(IAudioEndpointVolume).GUID;
        if (speakers.Activate(ref meterId, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object m) != 0) return false;
        if (speakers.Activate(ref volumeId, 23, IntPtr.Zero, out object v) != 0) return false;
        meter = (IAudioMeterInformation)m;
        volume = (IAudioEndpointVolume)v;
        return true;
    }

    /// <summary>Loudness of what is playing this instant, 0 (silence) to 1. 0 when it cannot be read.</summary>
    public static float Peak()
    {
        try
        {
            return Connect() && meter!.GetPeakValue(out float peak) == 0 ? peak : 0;
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            meter = null;       // the device went away (headphones unplugged); look again later
            volume = null;
            return 0;
        }
    }

    /// <summary>Where the volume slider is (0 to 1) and whether sound is muted. Null when it cannot be read.</summary>
    public static (float Level, bool Muted)? Volume()
    {
        try
        {
            if (!Connect()) return null;
            if (volume!.GetMasterVolumeLevelScalar(out float level) != 0 || volume.GetMute(out bool muted) != 0) return null;
            return (level, muted);
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            meter = null;
            volume = null;
            return null;
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    // The methods must be listed in the interface's own order; only the two readers are ever called.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float decibels, IntPtr context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr context);
        [PreserveSig] int GetMasterVolumeLevel(out float decibels);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float decibels, IntPtr context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float decibels);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}
