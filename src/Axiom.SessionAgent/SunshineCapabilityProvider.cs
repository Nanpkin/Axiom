using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;

namespace Axiom.SessionAgent;

internal sealed class SunshineCapabilityProvider : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [SunshineCapabilities.State];

    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length is < 1 or > 3 || resources.Distinct().Count() != resources.Length ||
            resources.Any(r => r.CapabilityId != SunshineCapabilities.State || !SunshineCapabilities.IsState(r.ResourceId)))
            throw new ArgumentException("Sunshine 串流检测资源无效。");

        var reason = "正在连接本机 Sunshine 状态推送。";
        string? lastStatus = null;
        publish(Update(resources, null, true, reason));
        // Retry an interrupted subscription, then wait for the rules to be re-enabled.
        var retries = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var client = CreateClient();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                using var response = await client.GetAsync("https://127.0.0.1:47990/api/tray/events",
                    HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new InvalidOperationException("请在 Windows 凭据管理器中检查 Axiom/Sunshine 凭据。");
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new InvalidOperationException("本机 Sunshine 未提供托盘状态推送。");
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                    throw new InvalidOperationException("本机 Sunshine 未返回托盘状态推送。");

                using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                await ReadEventsAsync(stream, status =>
                {
                    retries = 0;
                    if (status == lastStatus) return;
                    publish(Update(resources, status, lastStatus is not ("streaming" or "paused" or "idle"), "Sunshine 正在显示配对或托盘通知。"));
                    lastStatus = status;
                }, deadline);
                throw new IOException("Sunshine 状态推送已关闭。");
            }
            catch (InvalidOperationException error)
            {
                publish(Update(resources, null, false, error.Message));
                break;
            }
            catch (Exception error) when (!token.IsCancellationRequested &&
                error is HttpRequestException or IOException or OperationCanceledException or JsonException or CryptographicException)
            {
                lastStatus = null;
                publish(Update(resources, null, false, "本机 Sunshine 推送不可用，或其证书无法确认。"));
                if (++retries > 3) break;
                await Task.Delay(TimeSpan.FromSeconds(1 << retries), token);
            }
        }
        await Task.Delay(Timeout.Infinite, token);
    }

    internal static ProviderUpdate Update(ProviderResource[] resources, string? status, bool baseline, string reason)
    {
        var known = status is "streaming" or "paused" or "idle";
        return new(baseline, [.. resources.Select(resource => new ProviderSample(resource,
            known ? StateQuality.Known : StateQuality.Unknown, "axiom.bool", 1, Boolean: known ? resource.ResourceId switch
            {
                SunshineCapabilities.Connected => status == "streaming",
                SunshineCapabilities.Resumable => status == "paused",
                SunshineCapabilities.Either => status is "streaming" or "paused",
                _ => throw new ArgumentException("Sunshine 串流检测资源无效。")
            } : null, Reason: known ? null : reason))]);
    }

    internal static async Task ReadEventsAsync(Stream stream, Action<string> onStatus, CancellationTokenSource deadline)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        string? eventName = null;
        string? data = null;
        long revision = -1;
        var idleDeadline = TimeSpan.FromSeconds(75);
        while (await reader.ReadLineAsync(deadline.Token) is { } line)
        {
            if (line.Length > 65536) throw new IOException("Sunshine 状态推送超出容量。");
            if (line.StartsWith(':'))
                deadline.CancelAfter(idleDeadline);
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
                eventName = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
                data = line[6..];
            else if (line.Length == 0)
            {
                if (eventName == "tray-state" && data is not null)
                {
                    using var payload = JsonDocument.Parse(data);
                    var snapshot = payload.RootElement;
                    if (snapshot.ValueKind != JsonValueKind.Object ||
                        !snapshot.TryGetProperty("protocol_version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var protocol) || protocol != 1 ||
                        !snapshot.TryGetProperty("revision", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var nextRevision) || nextRevision < 0 ||
                        !snapshot.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
                        throw new IOException("Sunshine 状态推送版本或格式无法识别。");
                    deadline.CancelAfter(idleDeadline);
                    if (nextRevision > revision)
                    {
                        revision = nextRevision;
                        onStatus(status.GetString()!);
                    }
                }
                eventName = null;
                data = null;
            }
        }
    }

    private static HttpClient CreateClient()
    {
        var certificatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Sunshine", "config", "credentials", "cacert.pem");
        string? fingerprint = null;
        if (File.Exists(certificatePath))
        {
            using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(certificatePath));
            fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        }
        var authorization = ReadAuthorization();
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        if (fingerprint is not null)
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None ||
                certificate?.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint;
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Accept.Add(new("text/event-stream"));
        client.DefaultRequestHeaders.Authorization = authorization;
        return client;
    }

    private static AuthenticationHeaderValue? ReadAuthorization()
    {
        if (!OperatingSystem.IsWindows() || !CredRead("Axiom/Sunshine", 1, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var user = Marshal.PtrToStringUni(credential.UserName);
            var password = Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2));
            if (string.IsNullOrWhiteSpace(user) || user.Contains(':') || password is null)
                throw new InvalidOperationException("Windows 凭据 Axiom/Sunshine 缺少有效的用户名或密码。");
            return new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
        }
        finally { CredFree(pointer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags, Type;
        public IntPtr TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes, TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);

    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) =>
        throw new ArgumentException("Sunshine 串流状态只能作为检测。");
}
