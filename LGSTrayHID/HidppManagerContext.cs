using static LGSTrayHID.HidApi.HidApi;
using static LGSTrayHID.HidApi.HidApiWinApi;
using static LGSTrayHID.HidApi.HidApiHotPlug;
using LGSTrayHID.HidApi;
using System.Collections.Concurrent;
using LGSTrayPrimitives.MessageStructs;

namespace LGSTrayHID
{
    public sealed class HidppManagerContext
    {
        public static readonly HidppManagerContext _instance = new();
        public static HidppManagerContext Instance => _instance;

        private readonly ConcurrentDictionary<string, Guid> _containerMap = [];
        private readonly ConcurrentDictionary<Guid, HidppDevices> _deviceMap = [];
        private readonly BlockingCollection<QueuedDevice> _deviceQueue = [];

        // Snapshot of the hotplug data we need. The native HidDeviceInfo (and the
        // strings it points to) is freed by hidapi as soon as the callback returns,
        // so it must not be stored and dereferenced later.
        private readonly record struct QueuedDevice(string Path, HidppMessageType MessageType);

        // Keep the delegates handed to native code alive for the lifetime of the
        // process; otherwise the GC can collect them and the next hotplug event
        // calls into freed memory and crashes the HID daemon.
        private static HidApiHotPlugEventCallbackFn? _arrivedCallback;
        private static HidApiHotPlugEventCallbackFn? _leftCallback;

        public delegate void HidppDeviceEventHandler(IPCMessageType messageType, IPCMessage message);

        public event HidppDeviceEventHandler? HidppDeviceEvent;

        private HidppManagerContext()
        {

        }

        static HidppManagerContext()
        {
            _ = HidInit();
        }

        public void SignalDeviceEvent(IPCMessageType messageType, IPCMessage message)
        {
            HidppDeviceEvent?.Invoke(messageType, message);
        }

        private unsafe int EnqueueDevice(HidHotPlugCallbackHandle _, HidDeviceInfo* device, HidApiHotPlugEvent hidApiHotPlugEvent, nint __)
        {
            try
            {
                if (hidApiHotPlugEvent == HidApiHotPlugEvent.HID_API_HOTPLUG_EVENT_DEVICE_ARRIVED)
                {
                    var messageType = (*device).GetHidppMessageType();
                    if (messageType is HidppMessageType.SHORT or HidppMessageType.LONG)
                    {
                        _deviceQueue.Add(new QueuedDevice((*device).GetPath(), messageType));
                    }
                }
            }
            catch (Exception)
            {
                // Never let an exception propagate into native code
            }

            return 0;
        }

        private async Task<int> InitDevice(QueuedDevice deviceInfo)
        {
            var messageType = deviceInfo.MessageType;
            string devPath = deviceInfo.Path;

            HidDevicePtr dev = HidOpenPath(devPath);
            if (dev == IntPtr.Zero)
            {
                // Device vanished or is held exclusively by another process
                return 0;
            }

            _ = HidWinApiGetContainerId(dev, out Guid containerId);

#if DEBUG
            Console.WriteLine(devPath);
            Console.WriteLine(containerId.ToString());
            Console.WriteLine(messageType);
            Console.WriteLine();
#endif

            if (!_deviceMap.TryGetValue(containerId, out HidppDevices? value) || value.Disposed)
            {
                value = new();
                _deviceMap[containerId] = value;
            }
            // Map every interface path (short and long) so removal of either is noticed
            _containerMap[devPath] = containerId;

            switch (messageType)
            {
                case HidppMessageType.SHORT:
                    await value.SetDevShort(dev);
                    break;
                case HidppMessageType.LONG:
                    await value.SetDevLong(dev);
                    break;
            }

            return 0;
        }

        private unsafe int DeviceLeft(HidHotPlugCallbackHandle callbackHandle, HidDeviceInfo* deviceInfo, HidApiHotPlugEvent hidApiHotPlugEvent, nint userData)
        {
            try
            {
                string devPath = (*deviceInfo).GetPath();

                if (_containerMap.TryRemove(devPath, out var containerId)
                    && _deviceMap.TryRemove(containerId, out var devices))
                {
                    devices.Dispose();
                }
            }
            catch (Exception)
            {
                // Never let an exception propagate into native code
            }

            return 0;
        }

        public void Start(CancellationToken cancellationToken)
        {
            new Thread(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    QueuedDevice dev;
                    try
                    {
                        dev = _deviceQueue.Take(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    try
                    {
                        await InitDevice(dev);
                    }
                    catch (Exception ex)
                    {
                        // One misbehaving device must not take down the whole daemon
                        System.Diagnostics.Debug.WriteLine($"Device init failed for {dev.Path}: {ex}");
                    }
                }
            })
            { IsBackground = true }.Start();

            unsafe
            {
                _arrivedCallback ??= EnqueueDevice;
                _leftCallback ??= DeviceLeft;

                HidHotplugRegisterCallback(0x046D, 0x00, HidApiHotPlugEvent.HID_API_HOTPLUG_EVENT_DEVICE_ARRIVED, HidApiHotPlugFlag.HID_API_HOTPLUG_ENUMERATE, _arrivedCallback, IntPtr.Zero, (int*)IntPtr.Zero);
                HidHotplugRegisterCallback(0x046D, 0x00, HidApiHotPlugEvent.HID_API_HOTPLUG_EVENT_DEVICE_LEFT, HidApiHotPlugFlag.NONE, _leftCallback, IntPtr.Zero, (int*)IntPtr.Zero);
            }
        }
    
        public async Task ForceBatteryUpdates()
        {
            foreach (var hidppDevice in _deviceMap.Values.ToArray())
            {
                var tasks = hidppDevice.DeviceCollection
                    .Select(x => x.Value)
                    .Select(x => x.UpdateBattery(true));

                await Task.WhenAll(tasks);
            }
        }
    }
}
