using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Rainmeter;

namespace RemoteCoreTempMeter
{
    internal enum MeasureType
    {
        TjMax,
        Temperature,
        Speed
    }

    internal class Measure
    {
        public int Temperature { get; set; }

        public int CoreIndex { get; set; }

        public MeasureType Type { get; set; }

        public int MaxTemp { get; set; }

        public bool DoNothing { get; set; }

        public API Api { get; set; }

        static public implicit operator Measure(IntPtr data)
        {
            return (Measure)GCHandle.FromIntPtr(data).Target;
        }
        public IntPtr buffer = IntPtr.Zero;
    }

    public class Plugin
    {
        private static readonly double DEFAULT_MAX_VALUE = 100;

        private static BufferedTcpClient _client;

        [DllExport]
        public static void Initialize(ref IntPtr data, IntPtr rm)
        {
            data = GCHandle.ToIntPtr(GCHandle.Alloc(new Measure()));
            Measure measure = (Measure)data;
            measure.Api = new API(rm);
            string hostname = measure.Api.ReadString("CoreTempHostname", String.Empty);
            string portRaw = measure.Api.ReadString("CoreTempPort", "5200");
            if (!Int32.TryParse(portRaw, out int port))
            {
                measure.Api.LogF(API.LogType.Error, "RemoteCoreTempMeter.dll: CoreTempPort={0} not valid", portRaw);
            }

            if (!String.IsNullOrEmpty(hostname))
            {
                try
                {
                    _client = new BufferedTcpClient(hostname, port);
                    _client.Start();
                }
                catch (Exception ex)
                {
                    measure.Api.LogF(API.LogType.Error, "RemoteCoreTempMeter.dll: Could not connect to remote server: {0}", ex.Message);
                }
            }
        }

        [DllExport]
        public static void Finalize(IntPtr data)
        {
            if (_client != null)
            {
                _client.Stop();
                _client.Dispose();
            }
            Measure measure = (Measure)data;
            if (measure.buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(measure.buffer);
            }
            GCHandle.FromIntPtr(data).Free();
        }

        [DllExport]
        public static void Reload(IntPtr data, IntPtr rm, ref double maxValue)
        {
            try
            {
                ReloadInternal(data, rm, ref maxValue);
            }
            catch (System.Exception ex)
            {
                new API(rm).LogF(API.LogType.Error, "RemoteCoreTempMeter.dll: Unable to reload: {0}", ex.Message);
            }
        }

        public static void ReloadInternal(IntPtr data, IntPtr rm, ref double maxValue)
        {
            Measure measure = (Measure)data;
            string measureType = measure.Api.ReadString("CoreTempType", String.Empty).ToUpperInvariant();
            switch (measureType)
            {
                case "TJMAX":
                    measure.Type = MeasureType.TjMax;
                    if (_client.LatestPayload.HasValue)
                    {
                        measure.MaxTemp = _client.LatestPayload.Value.CpuInfo.uiTjMax[0];
                    }
                    else
                    {
                        measure.MaxTemp = 100;
                    }
                    break;
                case "TEMPERATURE":
                    measure.Type = MeasureType.Temperature;
                    break;
                case "CPUSPEED":
                    measure.Type = MeasureType.Speed;
                    break;
                default:
                    measure.Api.LogF(API.LogType.Error, "RemoteCoreTempMeter.dll: Type={0} not valid", measureType);
                    measure.DoNothing = true;
                    return;
            }

            string coreIndexRaw = measure.Api.ReadString("CoreTempIndex", "0");
            if (!Int32.TryParse(coreIndexRaw, out int coreIndex))
            {
                measure.Api.LogF(API.LogType.Error, "RemoteCoreTempMeter.dll: CoreTempIndex={0} not valid", coreIndexRaw);
                measure.DoNothing = true;
                return;
            }
            measure.CoreIndex = coreIndex;

            string maxValueRaw = measure.Api.ReadString("MaxValue", DEFAULT_MAX_VALUE.ToString());
            if (!Double.TryParse(maxValueRaw, out maxValue))
            {
                measure.Api.LogF(API.LogType.Error, "RemoteCoreTempMeter.dll: MaxValue={0} not valid, defaulting to {1}", maxValueRaw, DEFAULT_MAX_VALUE);
            }
        }

        [DllExport]
        public static IntPtr GetString(IntPtr data)
        {
            Measure measure = (Measure)data;
            if (measure.buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(measure.buffer);
                measure.buffer = IntPtr.Zero;
            }

            string value = String.Empty;
            if (measure.Type == MeasureType.TjMax)
            {
                value = measure.MaxTemp.ToString();
            }
            else
            {
                CoreTempPayload? payload = _client.LatestPayload;
                if (payload.HasValue)
                {
                    switch (measure.Type)
                    {
                        case MeasureType.Temperature:
                            value = payload.Value.CpuInfo.fTemp[measure.CoreIndex].ToString();
                            break;
                        case MeasureType.TjMax:
                            value = payload.Value.CpuInfo.uiTjMax[0].ToString();
                            break;
                        case MeasureType.Speed:
                            value = payload.Value.CpuInfo.fCPUSpeed.ToString();
                            break;
                    }
                }
            }

            measure.buffer = Marshal.StringToHGlobalUni(value);
            return measure.buffer;
        }

        [DllExport]
        public static double Update(IntPtr data)
        {
            Measure measure = (Measure)data;
            if (measure.DoNothing)
            {
                return 0.0;
            }


            CoreTempPayload? payload = _client.LatestPayload;
            if (payload.HasValue)
            {
                switch (measure.Type)
                {
                    case MeasureType.Temperature:
                        return payload.Value.CpuInfo.fTemp[measure.CoreIndex];
                    case MeasureType.Speed:
                        return payload.Value.CpuInfo.fCPUSpeed;
                    case MeasureType.TjMax:
                        return measure.MaxTemp;
                }
            }

            return 0.0;
        }
    }

    internal class BufferedTcpClient : IDisposable
    {
        public string Hostname { get; private set; }

        public int Port { get; private set; }

        public string LatestMessage { get; private set; }

        public CoreTempPayload? LatestPayload { get; private set; }

        private TcpClient _client;

        private int _openBracketCount;

        private readonly StringBuilder _messageBuilder;

        private bool _running;

        public BufferedTcpClient(string hostname, int port)
        {
            Hostname = hostname;
            Port = port;
            _messageBuilder = new StringBuilder();
        }

        public void Start()
        {
            NetworkStream stream = null;
            _running = true;
            Thread t = new Thread(new ThreadStart((() =>
            {
                while (_running)
                {
                    if (stream == null)
                    {
                        try
                        {
                            _client = new TcpClient(Hostname, Port);
                            stream = _client.GetStream();
                        }
                        catch
                        {
                            Thread.Sleep(TimeSpan.FromMinutes(5));
                        }
                    }
                    try
                    {
                        AssembleMessage(stream);
                    }
                    catch
                    {
                        LatestPayload = null;
                        stream = null;
                        _client.Close();
                        _client.Dispose();
                    }
                }
            })));
            t.Start();
        }

        public void Stop()
        {
            _running = false;
        }

        private void AssembleMessage(NetworkStream stream)
        {
            while (stream != null && stream.CanRead)
            {
                int raw = stream.ReadByte();
                if (raw < 0)
                    throw new EndOfStreamException();

                char next = (char)raw;
                if (Char.IsWhiteSpace(next))
                {
                    continue;
                }

                if (next == '{')
                {
                    _openBracketCount++;
                }
                else if (next == '}')
                {
                    _openBracketCount--;
                }
                _messageBuilder.Append(next);
                if (_openBracketCount == 0)
                {
                    LatestMessage = _messageBuilder.ToString();
                    _messageBuilder.Clear();
                    try
                    {
                        LatestPayload = LatestMessage.FromJson<CoreTempPayload>();
                    }
                    catch
                    {
                    }
                }
            }
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                _running = false;

                if (disposing)
                {
                    // TODO: dispose managed state (managed objects).
                    _client.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~BufferedTcpClient()
        // {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion

    }

    internal struct CoreTempPayload
    {
        public CpuInfo CpuInfo { get; set; }
    }

    internal struct CpuInfo
    {
        public int[] uiTjMax { get; set; }

        public double fCPUSpeed { get; set; }

        public int[] fTemp { get; set; }
    }
}
