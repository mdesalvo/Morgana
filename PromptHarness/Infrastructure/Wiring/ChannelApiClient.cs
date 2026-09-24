using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Morgana.AI;
using Morgana.AI.Services;
using Morgana.Contracts;

namespace PromptHarness.Infrastructure.Wiring;

/// <summary>
/// The REST API as a channel calls it, raw: status codes and bodies come back unread, so the deterministic
/// groups assert exactly what a channel would receive. Conversations are seeded on record, never run.
/// </summary>
/// <param name="fixture">The live host, whose storage, keys and address every call targets.</param>
public sealed class ChannelApiClient(MorganaHostFixture fixture)
{
    /// <summary>A conversation id no other test has used, in the form channels mint them.</summary>
    public static string NewConversationId() => Guid.NewGuid().ToString("N");

    /// <summary>A current token of the harness channel, the credential every admitted call carries.</summary>
    public string HarnessToken() => MintToken(HarnessChannel.IssuerName, fixture.IssuerKey);

    /// <summary>Mints a five-minute token the way any channel mints one, signed under the given issuer and key.</summary>
    public string MintToken(string issuer, string symmetricKey) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = fixture.Configuration["Morgana:Authentication:Audience"],
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, issuer)]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(symmetricKey)), SecurityAlgorithms.HmacSha256)
        });

    /// <summary>
    /// Calls one endpoint for a conversation, with the body a channel would send there. A null token
    /// sends no Authorization header at all.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(string method, string path, string conversationId, string? token)
    {
        using HttpClient httpClient = new HttpClient();
        if (token is not null)
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        string address = $"{fixture.BaseAddress}{path.Replace("{id}", conversationId)}";
        if (method == "GET")
            return await httpClient.GetAsync(address);

        // Start, message and command carry a body; the other endpoints take none. The command named is
        // the one the framework publishes, so the call is well-formed and only its credential is on trial.
        object? body = path.EndsWith("/start")
            ? new { conversationId }
            : path.EndsWith("/message") ? new { text = "Hello" }
            : path.EndsWith("/command") ? new { name = "compact" }
            : null;

        return body is null
            ? await httpClient.PostAsync(address, content: null)
            : await httpClient.PostAsJsonAsync(address, body);
    }

    /// <summary>Posts a raw JSON body as the harness channel, so a malformed request reaches the gate as written.</summary>
    public async Task<HttpResponseMessage> PostJsonAsync(string path, string json)
    {
        using HttpClient httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", HarnessToken());

        return await httpClient.PostAsync($"{fixture.BaseAddress}{path}", new StringContent(json, Encoding.UTF8, "application/json"));
    }

    /// <summary>Asks for a command on the conversation the route names, with the body written as given.</summary>
    public Task<HttpResponseMessage> SendCommandAsync(string routeConversationId, string json) =>
        PostJsonAsync($"/api/morgana/conversation/{routeConversationId}/command", json);

    /// <summary>
    /// Starts a conversation with a well-formed handshake as the harness channel. The callback points
    /// nowhere on purpose: nothing here reads the presentation, whose delivery simply fails.
    /// </summary>
    public Task<HttpResponseMessage> StartAsync(string conversationId) =>
        PostJsonAsync("/api/morgana/conversation/start",
            """{"conversationId":"{id}","channelMetadata":{"coordinates":{"channelName":"harness","deliveryMode":"webhook","callbackUrl":"http://127.0.0.1:1/hook"},"capabilities":{"supportsRichCards":true,"supportsQuickReplies":true,"supportsStreaming":true,"supportsMarkdown":true}}}"""
                .Replace("{id}", conversationId));

    /// <summary>
    /// Puts a conversation on record without starting it, as a previous process would have left it: the
    /// handshake written by the host's own persistence service, plus, when asked, the agent carrying it.
    /// </summary>
    /// <param name="activeAgent">Intent of the agent left mid-exchange; null when none is.</param>
    public async Task SeedConversationOnRecordAsync(string conversationId, string? activeAgent)
    {
        SQLiteConversationPersistenceService persistenceService = new SQLiteConversationPersistenceService(
            Microsoft.Extensions.Options.Options.Create(new Records.ConversationPersistenceOptions
            {
                StoragePath = fixture.StoragePath,
                EncryptionKey = fixture.Configuration["Morgana:ConversationPersistence:EncryptionKey"]!
            }),
            NullLogger.Instance);

        // A SignalR channel with nobody connected takes every push at once: whatever a command or a
        // limit tells the user costs the call no redelivery wait
        await persistenceService.SaveChannelMetadataAsync(conversationId, new ChannelMetadata
        {
            Coordinates = new ChannelCoordinates { ChannelName = "harness", DeliveryMode = "signalr" },
            Capabilities = new ChannelCapabilities(
                SupportsRichCards: true, SupportsQuickReplies: true, SupportsStreaming: true, SupportsMarkdown: true, MaxMessageLength: null)
        });

        if (activeAgent is null)
            return;

        await using SqliteConnection connection = new SqliteConnection(
            $"Data Source={Path.Combine(fixture.StoragePath, $"morgana-{conversationId}.db")}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();

        // Resume and command admission read only the row's standing. Its identifier is left unmatched, so
        // /compact finds no history of the agent and answers without a model call.
        command.CommandText =
            "INSERT INTO morgana (agent_identifier, agent_name, agent_session, creation_date, last_update, is_active) " +
            "VALUES (@identifier, @name, zeroblob(0), @now, @now, 1);";
        command.Parameters.AddWithValue("@identifier", $"{activeAgent}-standing-{conversationId}");
        command.Parameters.AddWithValue("@name", activeAgent);
        command.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Whether the host keeps a record of the conversation: its own database in the run's storage path.</summary>
    public bool ConversationIsOnRecord(string conversationId) =>
        File.Exists(Path.Combine(fixture.StoragePath, $"morgana-{conversationId}.db"));
}
