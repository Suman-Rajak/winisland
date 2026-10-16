using System.Management;
using System.Runtime.InteropServices;
using Windows.Media.Control;
using static WinIsland.Interop.CoreAudio;

namespace WinIsland.Services;

internal enum MediaCommand
{
    Play,
    Pause,
    Next,
    Previous,
}

/// <summary>Things the assistant and the menu bar can do to the PC directly: music, volume, brightness, locking, sleep.</summary>
internal static class SystemActions
{
    // ---------------------------------------------------------------- volume

    /// <summary>The default speakers' volume, 0 to 1; null if there are no speakers.</summary>
    public static float? GetVolume() => WithSpeakers(volume =>
        volume.GetMasterVolumeLevelScalar(out float level) == 0 ? level : (float?)null);

    /// <summary>Sets the volume (0 to 1), unmuting unless it's 0. False if there are no speakers.</summary>
    public static bool SetVolume(float level) => WithSpeakers(volume =>
    {
        var context = Guid.Empty;
        level = Math.Clamp(level, 0, 1);
        Marshal.ThrowExceptionForHR(volume.SetMasterVolumeLevelScalar(level, ref context));
        Marshal.ThrowExceptionForHR(volume.SetMute(level == 0, ref context));
        return true;
    });

    /// <summary>Marks the assistant's own volume changes (turning the PC down while it listens), which the island doesn't show.</summary>
    public static readonly Guid QuietChange = new("6E0F3A4B-7C2D-4B8E-9F51-2A9D3C7E1B60");

    /// <summary>Sets the volume (0 to 1) without showing it on the island or touching mute. False if there are no speakers.</summary>
    public static bool SetVolumeQuietly(float level) => WithSpeakers(volume =>
    {
        var context = QuietChange;
        return volume.SetMasterVolumeLevelScalar(Math.Clamp(level, 0, 1), ref context) == 0;
    });

    /// <summary>Whether the default speakers are muted; null if there are none.</summary>
    public static bool? IsMuted() => WithSpeakers(volume =>
        volume.GetMute(out bool muted) == 0 ? muted : (bool?)null);

    public static bool SetMute(bool mute) => WithSpeakers(volume =>
    {
        var context = Guid.Empty;
        Marshal.ThrowExceptionForHR(volume.SetMute(mute, ref context));
        return true;
    });

    private static T? WithSpeakers<T>(Func<IAudioEndpointVolume, T> action)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device) != 0)
            {
                return default;
            }

            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var instance));
                try
                {
                    return action((IAudioEndpointVolume)instance);
                }
                finally
                {
                    Marshal.ReleaseComObject(instance);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    // ---------------------------------------------------------------- brightness

    /// <summary>The built-in screen's brightness, 0 to 100; null on PCs without one (desktop monitors).</summary>
    public static Task<int?> GetBrightnessAsync() => Task.Run(() =>
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new SelectQuery("WmiMonitorBrightness"));
            foreach (ManagementObject monitor in searcher.Get())
            {
                using (monitor)
                {
                    return (int?)Convert.ToInt32(monitor["CurrentBrightness"]);
                }
            }
        }
        catch (ManagementException)
        {
            // no built-in screen
        }
        return null;
    });

    /// <summary>False on PCs whose screen brightness Windows can't set.</summary>
    public static Task<bool> SetBrightnessAsync(int level) => Task.Run(() =>
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new SelectQuery("WmiMonitorBrightnessMethods"));
            foreach (ManagementObject monitor in searcher.Get())
            {
                using (monitor)
                {
                    monitor.InvokeMethod("WmiSetBrightness", [1u, (byte)Math.Clamp(level, 0, 100)]);
                    return true;
                }
            }
        }
        catch (ManagementException)
        {
            // no built-in screen
        }
        return false;
    });

    // ---------------------------------------------------------------- music and videos

    /// <summary>
    /// Sends a command to whatever is playing (or, for Play, whatever played last). False if there's
    /// nothing to send it to, or the app refused.
    /// </summary>
    public static async Task<bool> SendMediaCommandAsync(MediaCommand command)
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var sessions = manager.GetSessions();
        var playing = sessions.FirstOrDefault(s =>
            s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        var session = playing ?? manager.GetCurrentSession();
        if (session is null || (command == MediaCommand.Pause && playing is null))
        {
            return false;
        }

        return command switch
        {
            MediaCommand.Play => await session.TryPlayAsync(),
            MediaCommand.Pause => await session.TryPauseAsync(),
            MediaCommand.Next => await session.TrySkipNextAsync(),
            _ => await session.TrySkipPreviousAsync(),
        };
    }

    /// <summary>Pauses everything that's playing, for a moment (the assistant is listening). Returns what it paused, to resume.</summary>
    public static async Task<List<GlobalSystemMediaTransportControlsSession>> PausePlayingAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var paused = new List<GlobalSystemMediaTransportControlsSession>();
        foreach (var session in manager.GetSessions())
        {
            if (session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                && await session.TryPauseAsync())
            {
                paused.Add(session);
            }
        }
        return paused;
    }

    /// <summary>Plays again what <see cref="PausePlayingAsync"/> paused.</summary>
    public static async Task ResumeAsync(IEnumerable<GlobalSystemMediaTransportControlsSession> sessions)
    {
        foreach (var session in sessions)
        {
            try
            {
                await session.TryPlayAsync();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // the app closed meanwhile
            }
        }
    }

    // ---------------------------------------------------------------- the PC

    public static bool Lock() => LockWorkStation();

    public static void Sleep() => SetSuspendState(hibernate: false, force: false, wakeupEventsDisabled: false);

    /// <summary>
    /// Windows' own "Shut Down Windows" box (shut down, restart, sleep, sign out), so nothing happens
    /// until you choose. Call on the UI thread.
    /// </summary>
    public static void AskToShutDown()
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is not null && Activator.CreateInstance(shellType) is { } shell)
            {
                shellType.InvokeMember("ShutdownWindows", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                Marshal.ReleaseComObject(shell);
            }
        }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException or MissingMethodException)
        {
            Log.Error("Opening the shut down box failed", ex);
        }
    }

    /// <summary>Opens a folder, a Settings page (ms-settings:…) or a program by name, as Run would.</summary>
    public static void Open(string target, string? arguments = null)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
            {
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Opening {target} failed", ex);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll")]
    private static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);
}
