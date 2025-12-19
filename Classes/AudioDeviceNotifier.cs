using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace reactivo.Classes;

public class AudioDeviceNotifier : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _deviceEnumerator;

    public event Action<DataFlow, Role, string>? DefaultDeviceChanged;

    public AudioDeviceNotifier()
    {
        _deviceEnumerator = new MMDeviceEnumerator();
        _deviceEnumerator.RegisterEndpointNotificationCallback(this);
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        //if (flow == DataFlow.Render && role == Role.Console)
        //{
        //    ConsoleManager.Log($"Default output device changed!\nNew Device: {defaultDeviceId}");
        //    DefaultDeviceChanged?.Invoke(flow, role, defaultDeviceId);
        //}
    }

    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        _deviceEnumerator?.UnregisterEndpointNotificationCallback(this);
        _deviceEnumerator?.Dispose();
    }
}
