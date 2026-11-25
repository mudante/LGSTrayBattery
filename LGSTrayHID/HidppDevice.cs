using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using LGSTrayHID.Features;
using System.Text;

using static LGSTrayHID.HidppDevices;

#if DEBUG
using Log = System.Console;
#else
using Log = System.Diagnostics.Debug;
#endif

namespace LGSTrayHID
{
    public class HidppDevice
    {
        private readonly SemaphoreSlim _initSemaphore = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private bool _initialised = false;

        private const int PING_TIMEOUT = 500;
        private const int INIT_MAX_ATTEMPTS = 6;
        private Func<HidppDevice, Task<BatteryUpdateReturn?>>? _getBatteryAsync;

        public string DeviceName { get; private set; } = string.Empty;
        public int DeviceType { get; private set; } = 3;
        public string Identifier { get; private set; } = string.Empty;

        private BatteryUpdateReturn lastBatteryReturn;
        private DateTimeOffset lastUpdate = DateTimeOffset.MinValue;

        private readonly HidppDevices _parent;
        public HidppDevices Parent => _parent;

        private readonly byte _deviceIdx;
        public byte DeviceIdx => _deviceIdx;

        private readonly Dictionary<ushort, byte> _featureMap = [];
        public Dictionary<ushort, byte> FeatureMap => _featureMap;

        public HidppDevice(HidppDevices parent, byte deviceIdx)
        {
            _parent = parent;
            _deviceIdx = deviceIdx;
        }

        /// <summary>
        /// Stops this instance's background polling, e.g. when the receiver slot
        /// has been re-announced and a fresh instance takes over.
        /// </summary>
        public void Cancel()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        private bool Stopped => _cts.IsCancellationRequested || _parent.Disposed;

        /// <summary>
        /// Initialise the device, retrying with backoff. A device that is asleep
        /// when the app starts does not answer pings, so a single attempt is not
        /// enough to pick it up.
        /// </summary>
        public async Task InitWithRetryAsync()
        {
            int delay = 2000;
            for (int attempt = 0; attempt < INIT_MAX_ATTEMPTS && !Stopped; attempt++)
            {
                try
                {
                    if (await InitAsync()) { return; }
                }
                catch (ObjectDisposedException) { return; }
                catch (Exception) { }

                try
                {
                    await Task.Delay(delay, _cts.Token);
                }
                catch (OperationCanceledException) { return; }
                delay = Math.Min(delay * 2, 30_000);
            }
        }

        /// <returns>true once the device has been initialised (or deliberately ignored), false to retry</returns>
        public async Task<bool> InitAsync()
        {
            await _initSemaphore.WaitAsync();
            try
            {
                if (_initialised) { return true; }

                Hidpp20 ret;

                // Sync Ping
                int successCount = 0;
                int successThresh = 3;
                for (int i = 0; i < 10; i++)
                {
                    if (Stopped) { return true; }

                    var ping = await _parent.Ping20(_deviceIdx, PING_TIMEOUT);
                    if (ping)
                    {
                        successCount++;
                    }
                    else
                    {
                        successCount = 0;
                    }

                    if (successCount >= successThresh) { break; }
                }

                if (successCount < successThresh) { return false; }

                // Find 0x0001 IFeatureSet
                ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, 0x00, 0x00 | SW_ID, 0x00, 0x01, 0x00 });
                if (ret.Length < 5) { return false; }
                _featureMap[0x0001] = ret.GetParam(0);

                // Get Feature Count
                ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, _featureMap[0x0001], 0x00 | SW_ID, 0x00, 0x00, 0x00 });
                if (ret.Length < 5) { return false; }
                int featureCount = ret.GetParam(0);

                // Enumerate Features
                for (byte i = 0; i <= featureCount; i++)
                {
                    ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, _featureMap[0x0001], 0x10 | SW_ID, i, 0x00, 0x00 });
                    if (ret.Length < 6) { return false; }
                    ushort featureId = (ushort)((ret.GetParam(0) << 8) + ret.GetParam(1));

                    _featureMap[featureId] = i;
                }

                _initialised = await InitPopulateAsync();
                return _initialised;
            }
            finally
            {
                _initSemaphore.Release();
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0018:Inline variable declaration")]
        private async Task<bool> InitPopulateAsync()
        {
            Hidpp20 ret;
            byte featureId;

            // Device name
            if (_featureMap.TryGetValue(0x0005, out featureId))
            {
                ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, featureId, 0x00 | SW_ID, 0x00, 0x00, 0x00 });
                if (ret.Length < 5) { return false; }
                int nameLength = ret.GetParam(0);

                string name = "";

                while (name.Length < nameLength)
                {
                    ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, featureId, 0x10 | SW_ID, (byte)name.Length, 0x00, 0x00 });
                    if (ret.Length <= 4) { return false; }
                    name += Encoding.UTF8.GetString(ret.GetParams());
                }

                DeviceName = name.TrimEnd('\0');

                foreach (var tag in GlobalSettings.settings.DisabledDevices)
                {
                    if (DeviceName.Contains(tag))
                    {
                        Log.WriteLine($"{DeviceName} is marked as disabled");
                        return true;
                    }
                };

                ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, featureId, 0x20 | SW_ID, 0x00, 0x00, 0x00 });
                if (ret.Length >= 5)
                {
                    DeviceType = ret.GetParam(0);
                }
            }
            else
            {
                // Device does not have a name/Hidpp error ignore it
                return true;
            }

            if (_featureMap.TryGetValue(0x0003, out featureId))
            {
                ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, featureId, 0x00 | SW_ID, 0x00, 0x00, 0x00 });
                if (ret.Length < 20) { return false; }

                string unitId = BitConverter.ToString(ret.GetParams().ToArray(), 1, 4).Replace("-", string.Empty);
                string modelId = BitConverter.ToString(ret.GetParams().ToArray(), 7, 5).Replace("-", string.Empty);

                bool serialNumberSupported = (ret.GetParam(14) & 0x1) == 0x1;
                string? serialNumber = null;
                if (serialNumberSupported)
                {
                    ret = await _parent.WriteRead20(_parent.DevShort, new byte[7] { 0x10, _deviceIdx, featureId, 0x20 | SW_ID, 0x00, 0x00, 0x00 });
                    if (ret.Length < 15) { return false; }
                    serialNumber = BitConverter.ToString(ret.GetParams().ToArray(), 0, 11).Replace("-", string.Empty);
                }

                Identifier = serialNumber ?? $"{unitId}-{modelId}";
            }
            else
            {
                // Device does not have a serial identifier the device name as a hash identifier
                Identifier = $"{DeviceName.GetHashCode():X04}";
            }

#if DEBUG
            Log.WriteLine("---");
            Log.WriteLine(DeviceName + " Ready");
            Log.WriteLine(Identifier);
            foreach ((ushort featureIdItr, string featureDesc) in new (ushort, string)[]
            {
                (0x1000, "Battery Unified Level"),
                (0x1001, "Battery Voltage"),
                (0x1004, "Unified Battery"),
            })
            {
                if (_featureMap.ContainsKey(featureIdItr))
                {
                    Log.WriteLine($"0x{featureIdItr:X} - {featureDesc} Found");
                }
            }
            Log.WriteLine("---");
#endif

            _getBatteryAsync = FeatureMap switch
            {
                { } when FeatureMap.ContainsKey(0x1000) => Battery1000.GetBatteryAsync,
                { } when FeatureMap.ContainsKey(0x1001) => Battery1001.GetBatteryAsync,
                { } when FeatureMap.ContainsKey(0x1004) => Battery1004.GetBatteryAsync,
                _ => null
            };

            HidppManagerContext.Instance.SignalDeviceEvent(
                IPCMessageType.INIT,
                new InitMessage(Identifier, DeviceName, _getBatteryAsync != null, (DeviceType)DeviceType)
            );

            _ = Task.Run(async () =>
            {
                if (_getBatteryAsync == null) { return; }

                var token = _cts.Token;
                try
                {
                    await Task.Delay(1000, token);

                    // Stop when this slot was re-announced or the receiver was unplugged
                    while (!Stopped)
                    {
                        var now = DateTimeOffset.Now;
#if DEBUG
                        var expectedUpdateTime = lastUpdate.AddSeconds(1);
#else
                        var expectedUpdateTime = lastUpdate.AddSeconds(GlobalSettings.settings.PollPeriod);
#endif
                        if (now < expectedUpdateTime)
                        {
                            await Task.Delay(expectedUpdateTime - now, token);
                        }

                        try
                        {
                            await UpdateBattery();
                        }
                        catch (ObjectDisposedException) { break; }
                        catch (Exception)
                        {
                            // Transient HID failure (device asleep, malformed reply); keep polling
                        }

                        await Task.Delay(GlobalSettings.settings.RetryTime * 1000, token);
                    }
                }
                catch (OperationCanceledException) { }
            });

            return true;
        }

        public async Task UpdateBattery(bool forceIpcUpdate = false)
        {
            if (Stopped) { return; }
            if (_getBatteryAsync == null) { return; }

            var ret = await _getBatteryAsync.Invoke(this);

            if (ret == null) { return; }

            var batStatus = ret.Value;
            lastUpdate = DateTimeOffset.Now;

            if (!forceIpcUpdate && (batStatus == lastBatteryReturn))
            {
                // Don't report if no change
                return;
            }

            lastBatteryReturn = batStatus;
            HidppManagerContext.Instance.SignalDeviceEvent(
                IPCMessageType.UPDATE,
                new UpdateMessage(Identifier, batStatus.batteryPercentage, batStatus.status, batStatus.batteryMVolt, lastUpdate)
            );
        }
    }
}
