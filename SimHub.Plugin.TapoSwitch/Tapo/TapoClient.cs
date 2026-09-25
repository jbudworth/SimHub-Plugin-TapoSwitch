using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SimHub.Plugin.TapoSwitch.Tapo
{
    /// <summary>
    /// Minimal local-network client for Tapo (and KLAP-firmware Kasa) smart plugs.
    ///
    /// Implements the two-stage KLAP handshake and encrypted request/response
    /// cycle documented by the python-kasa project, using the account email and
    /// password that the device was registered with in the Tapo app.
    ///
    /// This targets simple on/off plugs (P100, P105, P110, P115, KP125M, etc).
    /// Devices with child sockets (power strips) are not handled here.
    /// </summary>
    public sealed class TapoClient : ISmartSwitchClient
    {
        private const string SessionCookieName = "TP_SESSIONID";

        private readonly string _host;
        private readonly int _port;
        private readonly string _email;
        private readonly string _password;

        private readonly HttpClient _http;
        private readonly HttpClientHandler _handler;
        private readonly CookieContainer _cookies;

        private KlapCipher _cipher;
        private DateTime _handshakeAt = DateTime.MinValue;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public TapoClient(string host, string email, string password, int port = 80)
        {
            _host = host;
            _port = port;
            _email = email ?? string.Empty;
            _password = password ?? string.Empty;

            // Many embedded/IoT HTTP servers (Tapo's included) don't implement the
            // HTTP "Expect: 100-continue" handshake .NET sends by default on POSTs
            // with a body. When the server just ignores it instead of responding,
            // .NET Framework's HttpClientHandler (which is backed by HttpWebRequest)
            // can stall noticeably before giving up and sending the body anyway.
            // Disable it only for this device's ServicePoint - setting the static
            // ServicePointManager.Expect100Continue would silently change HTTP
            // behavior for all of SimHub and every other plugin in the process.
            ServicePointManager.FindServicePoint(AppUrl).Expect100Continue = false;

            _cookies = new CookieContainer();
            _handler = new HttpClientHandler
            {
                CookieContainer = _cookies,
                UseCookies = true,
                UseProxy = false,
                Proxy = null,
                ServerCertificateCustomValidationCallback = (m, c, ch, e) => true
            };
            _http = new HttpClient(_handler)
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
        }

        // Trailing slash matters: Uri's relative-combine constructor replaces the
        // last path segment of the base if it doesn't end in "/", which would
        // otherwise turn "/app" + "handshake1" into "/handshake1" instead of
        // "/app/handshake1".
        private Uri AppUrl => new Uri($"http://{_host}:{_port}/app/");

        /// <summary>Performs the KLAP handshake if needed (first call, or after expiry/auth failure).</summary>
        private async Task EnsureHandshakeAsync(CancellationToken ct)
        {
            if (_cipher != null && (DateTime.UtcNow - _handshakeAt) < TimeSpan.FromHours(20))
            {
                return;
            }

            byte[] authHash = KlapCipher.Sha256(KlapCipher.Concat(
                KlapCipher.Sha1(Encoding.UTF8.GetBytes(_email)),
                KlapCipher.Sha1(Encoding.UTF8.GetBytes(_password))));

            byte[] localSeed = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(localSeed);
            }

            // --- handshake1 ---
            HttpResponseMessage hs1Response;
            try
            {
                hs1Response = await _http.PostAsync(
                    new Uri(AppUrl, "handshake1"),
                    new ByteArrayContent(localSeed),
                    ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TapoException(
                    $"Timed out connecting to {_host}:{_port}. Check the IP address is correct, the " +
                    "switch is powered on and on the same network, and nothing (e.g. Windows Firewall, " +
                    "a VPN, or AP client isolation on your router/access point) is blocking the connection.");
            }
            catch (HttpRequestException ex)
            {
                throw new TapoException(
                    $"Could not reach {_host}:{_port} ({ex.Message}). Check the IP address and that the " +
                    "switch is on the same network as this PC.", ex);
            }

            if (hs1Response.StatusCode != HttpStatusCode.OK)
            {
                throw new TapoException($"Device {_host} responded with {(int)hs1Response.StatusCode} to handshake1.");
            }

            byte[] hs1Body = await hs1Response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (hs1Body.Length < 48)
            {
                throw new TapoException($"Device {_host} returned an unexpected handshake1 response.");
            }

            byte[] remoteSeed = new byte[16];
            Array.Copy(hs1Body, 0, remoteSeed, 0, 16);
            byte[] serverHash = new byte[32];
            Array.Copy(hs1Body, 16, serverHash, 0, 32);

            byte[] expected = KlapCipher.Sha256(KlapCipher.Concat(localSeed, remoteSeed, authHash));
            if (!ByteArraysEqual(expected, serverHash))
            {
                throw new TapoAuthenticationException(
                    $"Device {_host} rejected the supplied Tapo account email/password (handshake1 hash mismatch).");
            }

            // --- handshake2 ---
            byte[] hs2Payload = KlapCipher.Sha256(KlapCipher.Concat(remoteSeed, localSeed, authHash));
            HttpResponseMessage hs2Response;
            try
            {
                hs2Response = await _http.PostAsync(
                    new Uri(AppUrl, "handshake2"),
                    new ByteArrayContent(hs2Payload),
                    ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TapoException($"Timed out during handshake2 with {_host}:{_port}.");
            }
            catch (HttpRequestException ex)
            {
                throw new TapoException($"Connection to {_host}:{_port} dropped during handshake2 ({ex.Message}).", ex);
            }

            if (hs2Response.StatusCode != HttpStatusCode.OK)
            {
                throw new TapoException($"Device {_host} responded with {(int)hs2Response.StatusCode} to handshake2.");
            }

            _cipher = KlapCipher.Create(localSeed, remoteSeed, authHash);
            _handshakeAt = DateTime.UtcNow;
        }

        /// <summary>Sends a single JSON request and returns the parsed "result" object.</summary>
        private async Task<JObject> SendAsync(object requestObject, CancellationToken ct)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string requestJson = JsonConvert.SerializeObject(requestObject);

                await EnsureHandshakeAsync(ct).ConfigureAwait(false);

                var (payload, seq) = _cipher.Encrypt(requestJson);
                var requestUri = new Uri(AppUrl, $"request?seq={seq}");

                var response = await _http.PostAsync(requestUri, new ByteArrayContent(payload), ct)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    // Session expired/invalid - force a fresh handshake and retry once.
                    _cipher = null;
                    await EnsureHandshakeAsync(ct).ConfigureAwait(false);

                    (payload, seq) = _cipher.Encrypt(requestJson);
                    requestUri = new Uri(AppUrl, $"request?seq={seq}");
                    response = await _http.PostAsync(requestUri, new ByteArrayContent(payload), ct)
                        .ConfigureAwait(false);
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new TapoException($"Device {_host} responded with {(int)response.StatusCode} to request.");
                }

                byte[] respBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                string json = _cipher.Decrypt(respBytes);

                var parsed = JObject.Parse(json);
                int errorCode = parsed.Value<int?>("error_code") ?? 0;
                if (errorCode != 0)
                {
                    throw new TapoException($"Device {_host} returned error_code {errorCode} for method "
                        + $"'{parsed["method"] ?? "?"}'.");
                }

                return parsed["result"] as JObject ?? new JObject();
            }
            finally
            {
                _lock.Release();
            }
        }

        private static long NowMillis() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public Task<JObject> GetDeviceInfoAsync(CancellationToken ct = default)
        {
            return SendAsync(new
            {
                method = "get_device_info",
                requestTimeMils = NowMillis(),
                terminalUUID = TerminalUuid
            }, ct);
        }

        public Task SetDeviceOnAsync(bool on, CancellationToken ct = default)
        {
            return SendAsync(new
            {
                method = "set_device_info",
                @params = new { device_on = on },
                requestTimeMils = NowMillis(),
                terminalUUID = TerminalUuid
            }, ct);
        }

        /// <summary>Reads current on/off state, then flips it.</summary>
        public async Task<bool> ToggleAsync(CancellationToken ct = default)
        {
            var info = await GetDeviceInfoAsync(ct).ConfigureAwait(false);
            bool isOn = info.Value<bool?>("device_on") ?? false;
            await SetDeviceOnAsync(!isOn, ct).ConfigureAwait(false);
            return !isOn;
        }

        // ISmartSwitchClient - thin wrappers so callers can treat this and
        // LegacyKasaClient interchangeably.
        public async Task<bool> GetIsOnAsync(CancellationToken ct = default)
        {
            var info = await GetDeviceInfoAsync(ct).ConfigureAwait(false);
            return info.Value<bool?>("device_on") ?? false;
        }

        public Task SetOnAsync(bool on, CancellationToken ct = default) => SetDeviceOnAsync(on, ct);

        // Stable per-instance terminal id, required by the SMART protocol on every request.
        private readonly string TerminalUuid = Guid.NewGuid().ToString();

        private static bool ByteArraysEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public void Dispose()
        {
            _http?.Dispose();
            _handler?.Dispose();
            _lock?.Dispose();
        }
    }

    public class TapoException : Exception
    {
        public TapoException(string message) : base(message) { }
        public TapoException(string message, Exception inner) : base(message, inner) { }
    }

    public sealed class TapoAuthenticationException : TapoException
    {
        public TapoAuthenticationException(string message) : base(message) { }
    }
}
