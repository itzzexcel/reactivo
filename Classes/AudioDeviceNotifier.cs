using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace reactivo.Classes;

public class AudioDeviceNotifier : IMMNotificationClient, IDisposable
{
    private MMDeviceEnumerator? _deviceEnumerator;
    private bool _disposed;

    public event Action<DataFlow, Role, string>? DefaultDeviceChanged;
    public event Action<string, DeviceState>? DeviceStateChanged;
    public event Action<string>? DeviceAdded;
    public event Action<string>? DeviceRemoved;

    public AudioDeviceNotifier()
    {
        _deviceEnumerator = new MMDeviceEnumerator();
        _deviceEnumerator.RegisterEndpointNotificationCallback(this);
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        try
        {
            if (flow == DataFlow.Render && (role == Role.Multimedia || role == Role.Console))
            {
                ConsoleManager.LogEvent($"Default output device changed: {defaultDeviceId}");
                DefaultDeviceChanged?.Invoke(flow, role, defaultDeviceId);
            }
        }
        catch (Exception ex)
        {
            ConsoleManager.LogError($"AudioDeviceNotifier.OnDefaultDeviceChanged failed: {ex.Message}");
        }
    }

    public void OnDeviceAdded(string pwstrDeviceId)
    {
        try
        {
            ConsoleManager.LogEvent($"Audio device added: {pwstrDeviceId}");
            DeviceAdded?.Invoke(pwstrDeviceId);
        }
        catch (Exception ex)
        {
            ConsoleManager.LogError($"AudioDeviceNotifier.OnDeviceAdded failed: {ex.Message}");
        }
    }

    public void OnDeviceRemoved(string deviceId)
    {
        try
        {
            ConsoleManager.LogEvent($"Audio device removed: {deviceId}");
            DeviceRemoved?.Invoke(deviceId);
        }
        catch (Exception ex)
        {
            ConsoleManager.LogError($"AudioDeviceNotifier.OnDeviceRemoved failed: {ex.Message}");
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        try
        {
            ConsoleManager.LogEvent($"Audio device state changed ({deviceId}): {newState}");
            DeviceStateChanged?.Invoke(deviceId, newState);
        }
        catch (Exception ex)
        {
            ConsoleManager.LogError($"AudioDeviceNotifier.OnDeviceStateChanged failed: {ex.Message}");
        }
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _deviceEnumerator?.UnregisterEndpointNotificationCallback(this);
        }
        catch (Exception ex)
        {
            ConsoleManager.LogError($"UnregisterEndpointNotificationCallback failed: {ex.Message}");
        }

        try
        {
            _deviceEnumerator?.Dispose();
        }
        catch { }

        _deviceEnumerator = null;
    }
}
