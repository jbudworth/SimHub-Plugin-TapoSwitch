using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SimHub.Plugin.TapoSwitch.Tapo
{
    /// <summary>
    /// Client for the original TP-Link "Kasa" smart plug protocol used by older
    /// devices such as the HS100, HS103, HS105, HS110, HS200, HS210, KP1xx/KP3xx
    /// series, and any device that hasn't been pushed the 2021+ KLAP firmware
    /// update. This is a plain TCP connection on port 9999 - there is no
    /// encryption in any meaningful sense (a running single-byte XOR "cipher",
    /// starting from key 171, is applied purely to obfuscate casual packet
    /// sniffing) and no authentication or account credentials are involved.
    ///
    /// Wire format: a 4-byte big-endian length prefix, followed by that many
    /// XOR-obfuscated bytes of UTF8 JSON.
    ///
    /// This is the same protocol used by github.com/jneilliii/OctoPrint-TPLinkSmartplug
    /// and Home Assistant's legacy Kasa support.
    /// </summary>
    public sealed class LegacyKasaClient : ISmartSwitchClient
    {
        private readonly string _host;
        private readonly int _port;
        private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(5);

        public LegacyKasaClient(string host, int port = 9999)
        {
            _host = host;
            _port = port;
        }

        public async Task<bool> GetIsOnAsync(CancellationToken ct = default)
        {
            var response = await SendAsync("{\"system\":{\"get_sysinfo\":{}}}", ct).ConfigureAwait(false);
            var sysinfo = response["system"]?["get_sysinfo"] as JObject;
            if (sysinfo == null)
            {
                throw new TapoException($"Device {_host} returned an unexpected get_sysinfo response.");
            }

            var relayState = sysinfo.Value<int?>("relay_state");
            if (relayState == null)
            {
                // Multi-socket power strips (HS300, KP303, ...) report state per
                // child socket instead of a top-level relay_state - not handled here.
                throw new TapoException(
                    $"Device {_host} has no top-level 'relay_state'. If this is a multi-socket " +
                    "power strip, per-socket control isn't supported by this plugin yet.");
            }
            return relayState.Value != 0;
        }

        public Task SetOnAsync(bool on, CancellationToken ct = default)
        {
            string cmd = "{\"system\":{\"set_relay_state\":{\"state\":" + (on ? "1" : "0") + "}}}";
            return SendAsync(cmd, ct);
        }

        public async Task<bool> ToggleAsync(CancellationToken ct = default)
        {
            bool isOn = await GetIsOnAsync(ct).ConfigureAwait(false);
            await SetOnAsync(!isOn, ct).ConfigureAwait(false);
            return !isOn;
        }

        private async Task<JObject> SendAsync(string json, CancellationToken ct)
        {
            byte[] request = Encrypt(Encoding.UTF8.GetBytes(json));

            using (var tcp = new TcpClient())
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // NetworkStream.ReadAsync ignores its CancellationToken on
                // .NET Framework, so closing the socket is the only reliable
                // way to abort a connect/read against a device that accepts
                // the connection but never replies. The timeout covers the
                // whole exchange, not just the connect.
                timeout.CancelAfter(IoTimeout);
                using (timeout.Token.Register(() => { try { tcp.Close(); } catch { } }))
                {
                    try
                    {
                        await tcp.ConnectAsync(_host, _port).ConfigureAwait(false);

                        using (var stream = tcp.GetStream())
                        {
                            await stream.WriteAsync(request, 0, request.Length, ct).ConfigureAwait(false);

                            byte[] lengthBuffer = await ReadExactAsync(stream, 4, ct).ConfigureAwait(false);
                            int length = (lengthBuffer[0] << 24) | (lengthBuffer[1] << 16)
                                       | (lengthBuffer[2] << 8) | lengthBuffer[3];
                            if (length <= 0 || length > 65536)
                            {
                                throw new TapoException($"Device {_host} returned an implausible response length ({length}).");
                            }

                            byte[] body = await ReadExactAsync(stream, length, ct).ConfigureAwait(false);
                            string responseJson = Encoding.UTF8.GetString(Decrypt(body));
                            return JObject.Parse(responseJson);
                        }
                    }
                    catch (Exception ex) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        throw new TapoException(
                            $"Timed out communicating with {_host}:{_port}. Check the IP address and that the " +
                            "switch is powered on and reachable on the network.", ex);
                    }
                }
            }
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset, ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new TapoException("Connection closed before the full response was received.");
                }
                offset += read;
            }
            return buffer;
        }

        private static byte[] Encrypt(byte[] data)
        {
            byte[] result = new byte[data.Length + 4];
            result[0] = (byte)(data.Length >> 24);
            result[1] = (byte)(data.Length >> 16);
            result[2] = (byte)(data.Length >> 8);
            result[3] = (byte)data.Length;

            byte key = 171;
            for (int i = 0; i < data.Length; i++)
            {
                byte b = (byte)(key ^ data[i]);
                result[i + 4] = b;
                key = b;
            }
            return result;
        }

        private static byte[] Decrypt(byte[] data)
        {
            byte[] result = new byte[data.Length];
            byte key = 171;
            for (int i = 0; i < data.Length; i++)
            {
                result[i] = (byte)(key ^ data[i]);
                key = data[i];
            }
            return result;
        }

        public void Dispose()
        {
            // Nothing persistent to release - each call opens its own short-lived socket.
        }
    }
}
