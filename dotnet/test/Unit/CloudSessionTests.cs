/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *--------------------------------------------------------------------------------------------*/

#if NET8_0_OR_GREATER
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GitHub.Copilot.Test.Unit;

/// <summary>
/// Unit tests for <see cref="CopilotClient.CreateCloudSessionAsync"/> and the rejection guard
/// on <see cref="CopilotClient.CreateSessionAsync"/> for cloud configs.
/// </summary>
public sealed class CloudSessionTests
{
    // -------------------------------------------------------------------------
    // 1. CreateSessionAsync rejects cloud config
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CreateSessionAsync_Rejects_CloudConfig()
    {
        await using var server = await FakeCloudServer.StartAsync();
        await using var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForUri(server.Url) });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CreateSessionAsync(new SessionConfig
            {
                OnPermissionRequest = PermissionHandler.ApproveAll,
                Cloud = new CloudSessionOptions { Repository = new CloudSessionRepository { Owner = "github", Name = "copilot-sdk" } }
            }));

        Assert.Contains("CreateCloudSessionAsync", ex.Message);
    }

    // -------------------------------------------------------------------------
    // 2. CreateCloudSessionAsync sends session.create with cloud and without sessionId
    //    (wire-shape correctness: assert sessionId absent from serialized JSON)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CreateCloudSessionAsync_Sends_Create_With_Cloud_And_Without_SessionId()
    {
        await using var server = await FakeCloudServer.StartAsync(cloudSessionId: "remote-cloud-session");
        await using var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForUri(server.Url) });

        await using var session = await client.CreateCloudSessionAsync(new SessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Cloud = new CloudSessionOptions
            {
                Repository = new CloudSessionRepository { Owner = "github", Name = "copilot-sdk", Branch = "main" }
            }
        });

        // Verify the session id is the runtime-assigned one.
        Assert.Equal("remote-cloud-session", session.SessionId);

        // Verify the wire payload captured by the server had no sessionId and had cloud.
        var payload = server.LastCreatePayload;
        Assert.NotNull(payload);
        Assert.False(payload!.Value.TryGetProperty("sessionId", out _),
            "session.create payload must not contain 'sessionId' on the cloud path.");
        Assert.True(payload.Value.TryGetProperty("cloud", out var cloud));
        Assert.Equal("github", cloud.GetProperty("repository").GetProperty("owner").GetString());
        Assert.Equal("copilot-sdk", cloud.GetProperty("repository").GetProperty("name").GetString());
        Assert.Equal("main", cloud.GetProperty("repository").GetProperty("branch").GetString());
    }

    // Supplementary serialization-layer assertion: JsonSerializer.Serialize on a
    // CreateSessionRequest with SessionId=null must not emit the key.
    [Fact]
    public void CreateSessionRequest_WithNullSessionId_DoesNotEmitSessionIdKey()
    {
        // Retrieve the private serializer options the SDK uses (same approach as SerializationTests).
        var prop = typeof(CopilotClient)
            .GetProperty("SerializerOptionsForMessageFormatter",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var options = (JsonSerializerOptions?)prop?.GetValue(null);
        Assert.NotNull(options);

        var requestType = typeof(CopilotClient)
            .GetNestedType("CreateSessionRequest", System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(requestType);

        // Build a request with SessionId = null (the cloud path).
        var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(requestType!);
        var cloudField = requestType!.GetField("<Cloud>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        cloudField?.SetValue(instance, new CloudSessionOptions
        {
            Repository = new CloudSessionRepository { Owner = "o", Name = "r" }
        });

        var json = JsonSerializer.Serialize(instance, requestType, options!);

        Assert.DoesNotContain("\"sessionId\"", json);
        Assert.Contains("\"cloud\"", json);
    }

    // -------------------------------------------------------------------------
    // 3. Rejects caller-provided SessionId
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CreateCloudSessionAsync_Rejects_CallerSessionId()
    {
        await using var server = await FakeCloudServer.StartAsync();
        await using var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForUri(server.Url) });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CreateCloudSessionAsync(new SessionConfig
            {
                SessionId = "caller-id",
                OnPermissionRequest = PermissionHandler.ApproveAll,
                Cloud = new CloudSessionOptions { Repository = new CloudSessionRepository { Owner = "github", Name = "copilot-sdk" } }
            }));

        Assert.Contains("SessionId", ex.Message);
    }

    // -------------------------------------------------------------------------
    // 4. Rejects caller-provided Provider
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CreateCloudSessionAsync_Rejects_CallerProvider()
    {
        await using var server = await FakeCloudServer.StartAsync();
        await using var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForUri(server.Url) });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CreateCloudSessionAsync(new SessionConfig
            {
                Provider = new ProviderConfig { BaseUrl = "https://api.example.com/v1" },
                OnPermissionRequest = PermissionHandler.ApproveAll,
                Cloud = new CloudSessionOptions { Repository = new CloudSessionRepository { Owner = "github", Name = "copilot-sdk" } }
            }));

        Assert.Contains("Provider", ex.Message);
    }

    // -------------------------------------------------------------------------
    // 5. Requires Cloud option
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CreateCloudSessionAsync_Requires_Cloud()
    {
        await using var server = await FakeCloudServer.StartAsync();
        await using var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForUri(server.Url) });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CreateCloudSessionAsync(new SessionConfig
            {
                OnPermissionRequest = PermissionHandler.ApproveAll
                // Cloud deliberately absent
            }));

        Assert.Contains("Cloud", ex.Message);
    }

    // -------------------------------------------------------------------------
    // 6. Buffers early session.event notifications until session id is registered
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CreateCloudSessionAsync_Buffers_Early_Notifications_Until_Registration()
    {
        const string cloudId = "remote-cloud-session";

        // Server is configured to send a session.event notification before
        // responding to session.create.
        await using var server = await FakeCloudServer.StartAsync(
            cloudSessionId: cloudId,
            earlyNotification: new Dictionary<string, object?>
            {
                ["method"] = "session.event",
                ["params"] = new Dictionary<string, object?>
                {
                    ["sessionId"] = cloudId,
                    ["event"] = new Dictionary<string, object?>
                    {
                        ["type"] = "capabilities.changed",
                        ["data"] = new Dictionary<string, object?>
                        {
                            ["ui"] = new Dictionary<string, object?> { ["elicitation"] = true }
                        }
                    }
                }
            });

        var receivedEvents = new List<CapabilitiesChangedEvent>();

        await using var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForUri(server.Url) });
        await using var session = await client.CreateCloudSessionAsync(new SessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Cloud = new CloudSessionOptions
            {
                Repository = new CloudSessionRepository { Owner = "github", Name = "copilot-sdk" }
            },
            OnEvent = evt =>
            {
                if (evt is CapabilitiesChangedEvent capEvt)
                {
                    receivedEvents.Add(capEvt);
                }
            }
        });

        // Allow the event channel to drain.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (receivedEvents.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Single(receivedEvents);
        Assert.True(receivedEvents[0].Data?.Ui?.Elicitation == true);
    }

    // -------------------------------------------------------------------------
    // 7. Parks inbound requests until session id is registered
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CreateCloudSessionAsync_Parks_Inbound_Requests_Until_Registration()
    {
        const string cloudId = "remote-cloud-session";

        // Server sends a userInput.request before responding to session.create.
        await using var server = await FakeCloudServer.StartAsync(
            cloudSessionId: cloudId,
            earlyInboundRequest: new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 301,
                ["method"] = "userInput.request",
                ["params"] = new Dictionary<string, object?>
                {
                    ["sessionId"] = cloudId,
                    ["question"] = "Pick a color",
                    ["choices"] = new object?[] { "red", "blue" },
                    ["allowFreeform"] = true
                }
            });

        await using var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForUri(server.Url) });
        await using var session = await client.CreateCloudSessionAsync(new SessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Cloud = new CloudSessionOptions
            {
                Repository = new CloudSessionRepository { Owner = "github", Name = "copilot-sdk" }
            },
            OnUserInputRequest = (_, _) => Task.FromResult(new UserInputResponse { Answer = "blue", WasFreeform = true })
        });

        // Wait for the server to receive the userInput response.
        var response = await server.WaitForUserInputResponse(TimeSpan.FromSeconds(5));
        Assert.NotNull(response);
        Assert.Equal("blue", response!["answer"]?.ToString());
    }

    // -------------------------------------------------------------------------
    // 8. Pending-waiter overflow: oldest is rejected, remaining 128 succeed
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PendingRequestWaiterOverflow_RejectsOldestWithOverflowMessage()
    {
        const string cloudId = "overflow-session";
        const int requestCount = 129; // one beyond the 128-waiter cap

        await using var server = await FakeCloudServer.StartAsync(
            cloudSessionId: cloudId,
            earlyInboundRequestCount: requestCount);

        await using var client = new CopilotClient(new CopilotClientOptions
        { Connection = RuntimeConnection.ForUri(server.Url) });

        await using var _ = await client.CreateCloudSessionAsync(new SessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Cloud = new CloudSessionOptions
            {
                Repository = new CloudSessionRepository { Owner = "github", Name = "copilot-sdk" }
            },
            OnUserInputRequest = (_, _) => Task.FromResult(new UserInputResponse { Answer = "yes", WasFreeform = false })
        });

        var responses = await server.WaitForAllInboundResponses(requestCount, TimeSpan.FromSeconds(10));

        // Exactly one overflow eviction, 128 successful completions.
        Assert.Equal(1, responses.Count(r => r.IsError));
        Assert.Equal(128, responses.Count(r => !r.IsError));

        var err = responses.Single(r => r.IsError);
        Assert.Contains("pending session buffer overflow", err.ErrorMessage ?? "");
    }

    // -------------------------------------------------------------------------
    // 9. Guard-drop path: parked requests are rejected with distinct message
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PendingSessionGuardDrop_RejectsParkedRequestWithDistinctMessage()
    {
        const string cloudId = "guard-drop-session";
        const int inboundRequestId = 500;

        await using var server = await FakeCloudServer.StartAsync(
            cloudSessionId: cloudId,
            failSessionCreate: true,
            earlyInboundRequest: new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = inboundRequestId,
                ["method"] = "userInput.request",
                ["params"] = new Dictionary<string, object?>
                {
                    ["sessionId"] = cloudId,
                    ["question"] = "Color?",
                    ["choices"] = new object?[] { "red", "blue" },
                    ["allowFreeform"] = false
                }
            });

        await using var client = new CopilotClient(new CopilotClientOptions
        { Connection = RuntimeConnection.ForUri(server.Url) });

        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.CreateCloudSessionAsync(new SessionConfig
            {
                OnPermissionRequest = PermissionHandler.ApproveAll,
                Cloud = new CloudSessionOptions
                {
                    Repository = new CloudSessionRepository { Owner = "github", Name = "copilot-sdk" }
                }
            }));

        // The parked request must have been rejected with the guard-drop message (not the overflow message).
        var responses = await server.WaitForAllInboundResponses(1, TimeSpan.FromSeconds(5));

        Assert.Single(responses);
        Assert.True(responses[0].IsError);
        Assert.Contains(
            "pending session routing ended before session was registered",
            responses[0].ErrorMessage ?? "");
    }

    // =========================================================================
    // Fake server infrastructure
    // =========================================================================

    private sealed class FakeCloudServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly Task _serverTask;
        private readonly string _cloudSessionId;
        private readonly Dictionary<string, object?>? _earlyNotification;
        private readonly Dictionary<string, object?>? _earlyInboundRequest;
        private readonly int _earlyInboundRequestCount;
        private readonly bool _failSessionCreate;
        private readonly TaskCompletionSource<Dictionary<string, object?>?> _userInputResponseTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Response tracking for overflow / guard-drop tests.
        private readonly object _inboundResponsesLock = new();
        private readonly List<InboundResponse> _collectedInboundResponses = [];
        private int _waitForInboundResponseCount;
        private TaskCompletionSource<IReadOnlyList<InboundResponse>>? _allInboundResponsesTcs;

        public JsonElement? LastCreatePayload { get; private set; }

        public record InboundResponse(int RequestId, bool IsError, string? ErrorMessage);

        private FakeCloudServer(
            TcpListener listener,
            string cloudSessionId,
            Dictionary<string, object?>? earlyNotification,
            Dictionary<string, object?>? earlyInboundRequest,
            int earlyInboundRequestCount,
            bool failSessionCreate)
        {
            _listener = listener;
            _cloudSessionId = cloudSessionId;
            _earlyNotification = earlyNotification;
            _earlyInboundRequest = earlyInboundRequest;
            _earlyInboundRequestCount = earlyInboundRequestCount;
            _failSessionCreate = failSessionCreate;
            _serverTask = RunAsync();
        }

        public string Url
        {
            get
            {
                var endpoint = (IPEndPoint)_listener.LocalEndpoint;
                return $"http://127.0.0.1:{endpoint.Port}";
            }
        }

        public static Task<FakeCloudServer> StartAsync(
            string cloudSessionId = "cloud-session-id",
            Dictionary<string, object?>? earlyNotification = null,
            Dictionary<string, object?>? earlyInboundRequest = null,
            int earlyInboundRequestCount = 0,
            bool failSessionCreate = false)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new FakeCloudServer(
                listener, cloudSessionId, earlyNotification, earlyInboundRequest,
                earlyInboundRequestCount, failSessionCreate));
        }

        public Task<Dictionary<string, object?>?> WaitForUserInputResponse(TimeSpan timeout)
            => _userInputResponseTcs.Task.WaitAsync(timeout);

        /// <summary>
        /// Waits until the server has collected <paramref name="count"/> responses (error or success)
        /// from the client for inbound requests. Used by overflow and guard-drop tests.
        /// </summary>
        public Task<IReadOnlyList<InboundResponse>> WaitForAllInboundResponses(int count, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<IReadOnlyList<InboundResponse>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_inboundResponsesLock)
            {
                _waitForInboundResponseCount = count;
                _allInboundResponsesTcs = tcs;
                if (_collectedInboundResponses.Count >= count)
                    tcs.TrySetResult(new List<InboundResponse>(_collectedInboundResponses));
            }
            return tcs.Task.WaitAsync(timeout);
        }

        private void RecordInboundResponse(InboundResponse response)
        {
            TaskCompletionSource<IReadOnlyList<InboundResponse>>? tcs = null;
            IReadOnlyList<InboundResponse>? snapshot = null;
            lock (_inboundResponsesLock)
            {
                _collectedInboundResponses.Add(response);
                if (_allInboundResponsesTcs != null &&
                    _collectedInboundResponses.Count >= _waitForInboundResponseCount)
                {
                    tcs = _allInboundResponsesTcs;
                    snapshot = new List<InboundResponse>(_collectedInboundResponses);
                }
            }
            tcs?.TrySetResult(snapshot!);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();

            try { await _serverTask; }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException) { }

            _cts.Dispose();
            _writeLock.Dispose();
        }

        private async Task RunAsync()
        {
            using var tcpClient = await _listener.AcceptTcpClientAsync(_cts.Token);
            using var stream = tcpClient.GetStream();

            while (!_cts.Token.IsCancellationRequested)
            {
                using var request = await ReadMessageAsync(stream, _cts.Token);
                if (request is null) return;
                await HandleRequestAsync(stream, request.RootElement, _cts.Token);
            }
        }

        private async Task HandleRequestAsync(Stream stream, JsonElement request, CancellationToken cancellationToken)
        {
            // Identify the message type:
            // - Response from SDK: has "id" and "result"/"error" but no "method"
            // - Request from SDK: has "id" and "method"
            // - Notification from SDK: has "method" but no "id"

            bool hasId = request.TryGetProperty("id", out var idElement);
            bool hasMethod = request.TryGetProperty("method", out var methodEl);
            bool hasResult = request.TryGetProperty("result", out var resultEl);

            if (hasId && !hasMethod)
            {
                // This is a response from the SDK (e.g. userInput reply).
                if (hasResult)
                {
                    var dict = new Dictionary<string, object?>();
                    foreach (var prop in resultEl.EnumerateObject())
                    {
                        dict[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => prop.Value.GetRawText()
                        };
                    }
                    _userInputResponseTcs.TrySetResult(dict);

                    if (idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt32(out var successId))
                        RecordInboundResponse(new InboundResponse(successId, IsError: false, null));
                }
                else if (request.TryGetProperty("error", out var errorEl))
                {
                    var requestId = idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt32(out var errId)
                        ? errId : -1;
                    var msg = errorEl.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
                    RecordInboundResponse(new InboundResponse(requestId, IsError: true, msg));
                }
                return;
            }

            if (!hasId)
            {
                // Notification — nothing to respond to.
                return;
            }

            var id = idElement.Clone();
            var method = methodEl.GetString();

            if (method == "connect")
            {
                await WriteMessageAsync(stream, new Dictionary<string, object?>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["result"] = new Dictionary<string, object?>
                    {
                        ["ok"] = true,
                        ["protocolVersion"] = 3,
                        ["version"] = "test"
                    }
                }, cancellationToken);
                return;
            }

            if (method == "session.create")
            {
                // Capture the params for assertions.
                if (request.TryGetProperty("params", out var paramsEl))
                {
                    LastCreatePayload = paramsEl.Clone();
                }

                // Optionally send an early notification before responding.
                if (_earlyNotification != null)
                {
                    await WriteMessageAsync(stream, _earlyNotification, cancellationToken);
                }

                // Optionally send an early inbound request before responding.
                if (_earlyInboundRequest != null)
                {
                    await WriteMessageAsync(stream, _earlyInboundRequest, cancellationToken);
                    // Give the SDK a moment to park the request before we unblock create.
                    await Task.Delay(50, cancellationToken);
                }

                // For overflow tests: send N inbound requests to exercise the buffer cap.
                if (_earlyInboundRequestCount > 0)
                {
                    for (var i = 1; i <= _earlyInboundRequestCount; i++)
                    {
                        await WriteMessageAsync(stream, new Dictionary<string, object?>
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = i,
                            ["method"] = "userInput.request",
                            ["params"] = new Dictionary<string, object?>
                            {
                                ["sessionId"] = _cloudSessionId,
                                ["question"] = $"Question {i}",
                                ["choices"] = new object?[] { "yes", "no" },
                                ["allowFreeform"] = false
                            }
                        }, cancellationToken);
                    }

                    // Give the client time to park/overflow all requests before responding.
                    await Task.Delay(100, cancellationToken);
                }

                if (_failSessionCreate)
                {
                    await WriteMessageAsync(stream, new Dictionary<string, object?>
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id,
                        ["error"] = new Dictionary<string, object?>
                        {
                            ["code"] = -32603,
                            ["message"] = "session.create failed (test-induced failure)"
                        }
                    }, cancellationToken);
                    return;
                }

                await WriteMessageAsync(stream, new Dictionary<string, object?>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["result"] = new Dictionary<string, object?>
                    {
                        ["sessionId"] = _cloudSessionId,
                        ["workspacePath"] = null,
                        ["capabilities"] = null
                    }
                }, cancellationToken);
                return;
            }

            if (method == "session.destroy")
            {
                await WriteMessageAsync(stream, new Dictionary<string, object?>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["result"] = new Dictionary<string, object?>()
                }, cancellationToken);
                return;
            }

            // Default: return an empty success result.
            await WriteMessageAsync(stream, new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new Dictionary<string, object?>()
            }, cancellationToken);
        }

        private async Task WriteMessageAsync(Stream stream, object payload, CancellationToken cancellationToken)
        {
            using var bodyStream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bodyStream))
            {
                WriteJsonValue(writer, payload);
            }

            var body = bodyStream.ToArray();
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await stream.WriteAsync(header, cancellationToken);
                await stream.WriteAsync(body, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
        {
            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    break;
                case string s:
                    writer.WriteStringValue(s);
                    break;
                case bool b:
                    writer.WriteBooleanValue(b);
                    break;
                case int i:
                    writer.WriteNumberValue(i);
                    break;
                case long l:
                    writer.WriteNumberValue(l);
                    break;
                case JsonElement je:
                    je.WriteTo(writer);
                    break;
                case Dictionary<string, object?> dict:
                    writer.WriteStartObject();
                    foreach (var (k, v) in dict)
                    {
                        writer.WritePropertyName(k);
                        WriteJsonValue(writer, v);
                    }
                    writer.WriteEndObject();
                    break;
                case object?[] arr:
                    writer.WriteStartArray();
                    foreach (var item in arr)
                    {
                        WriteJsonValue(writer, item);
                    }
                    writer.WriteEndArray();
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected JSON value type '{value.GetType().Name}'.");
            }
        }

        private static async Task<JsonDocument?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
        {
            var headerBytes = new List<byte>();
            while (true)
            {
                var value = await ReadByteAsync(stream, cancellationToken);
                if (value < 0) return null;

                headerBytes.Add((byte)value);
                var count = headerBytes.Count;
                if (count >= 4 &&
                    headerBytes[count - 4] == '\r' &&
                    headerBytes[count - 3] == '\n' &&
                    headerBytes[count - 2] == '\r' &&
                    headerBytes[count - 1] == '\n')
                {
                    break;
                }
            }

            var header = Encoding.ASCII.GetString([.. headerBytes]);
            var contentLength = header
                .Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2 && parts[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                .Select(parts => int.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture))
                .Single();

            var body = new byte[contentLength];
            var offset = 0;
            while (offset < body.Length)
            {
                var read = await stream.ReadAsync(body.AsMemory(offset, body.Length - offset), cancellationToken);
                if (read == 0) return null;
                offset += read;
            }

            return JsonDocument.Parse(body);
        }

        private static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[1];
            var read = await stream.ReadAsync(buffer, cancellationToken);
            return read == 0 ? -1 : buffer[0];
        }
    }
}
#endif
