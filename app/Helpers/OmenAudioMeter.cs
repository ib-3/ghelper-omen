using System;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GHelper.Helpers
{
    public class OmenAudioMeter : IDisposable, NAudio.CoreAudioApi.Interfaces.IMMNotificationClient
    {
        private WasapiLoopbackCapture? _audioDevice;
        private MMDeviceEnumerator? _audioDeviceEnum;
        private readonly object _audioLock = new();
        private volatile bool _isListening;
        private volatile bool _isRestarting;
        private string? _audioDeviceId;
        private DateTime _lastUpdate = DateTime.MinValue;
        
        public event Action<float>? OnVolumeUpdated;

        public void Start()
        {
            lock (_audioLock)
            {
                if (_isListening) return;

                try
                {
                    _audioDeviceEnum = new MMDeviceEnumerator();
                    _audioDeviceEnum.RegisterEndpointNotificationCallback(this);
                    
                    using (MMDevice device = _audioDeviceEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console))
                    {
                        _audioDevice = new WasapiLoopbackCapture(device);
                        _audioDeviceId = device.ID;
                        _audioDevice.DataAvailable += WaveIn_DataAvailable;
                        _audioDevice.StartRecording();
                        _isListening = true;
                        Logger.WriteLine("OmenAudioMeter: Subscribed to Audio (" + _audioDeviceId + ")");
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("OmenAudioMeter failed to start: " + ex.Message);
                }
            }
        }

        public void Stop()
        {
            lock (_audioLock)
            {
                if (!_isListening) return;

                try
                {
                    if (_audioDeviceEnum != null)
                    {
                        try { _audioDeviceEnum.UnregisterEndpointNotificationCallback(this); } catch { }
                    }

                    if (_audioDevice != null)
                    {
                        _audioDevice.StopRecording();
                        _audioDevice.DataAvailable -= WaveIn_DataAvailable;
                        _audioDevice.Dispose();
                        _audioDevice = null;
                    }

                    if (_audioDeviceEnum != null)
                    {
                        _audioDeviceEnum.Dispose();
                        _audioDeviceEnum = null;
                    }
                    
                    _audioDeviceId = null;
                    _isListening = false;
                    Logger.WriteLine("OmenAudioMeter: Unsubscribed from Audio");
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("OmenAudioMeter failed to stop: " + ex.Message);
                }
            }
        }

        private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
        {
            if (!_isListening) return;
            
            // Throttle to roughly 30 FPS (33ms)
            if ((DateTime.Now - _lastUpdate).TotalMilliseconds < 33)
                return;
                
            _lastUpdate = DateTime.Now;
            
            float max = 0;
            var buffer = new WaveBuffer(e.Buffer);

            // interpret as 32 bit floating point audio
            for (int index = 0; index < e.BytesRecorded / 4; index++)
            {
                var sample = buffer.FloatBuffer[index];
                if (sample < 0) sample = -sample;
                if (sample > max) max = sample;
            }

            OnVolumeUpdated?.Invoke(max);
        }
        
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (!_isListening || _isRestarting) return;
            if (flow != DataFlow.Render || role != Role.Console) return;
            
            if (_audioDeviceId != null && _audioDeviceId == defaultDeviceId) return;
            
            Logger.WriteLine("OmenAudioMeter: Default Output changed to " + defaultDeviceId);
            _audioDeviceId = defaultDeviceId;
            
            _isRestarting = true;
            Task.Run(() => {
                Stop();
                Start();
                _isRestarting = false;
            });
        }

        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        public void Dispose()
        {
            Stop();
        }
    }
}
