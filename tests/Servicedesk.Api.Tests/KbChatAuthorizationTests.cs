using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Integrations.Claude;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Authorization is the #1 priority for the KB chat assistant: the model must
/// never reach knowledge the asking agent could not already see. These tests
/// pin the gate that enforces it — an agent without KB access gets no
/// retrieval and the Messages API is never called, even with the feature on,
/// a key set and zero data retention confirmed.
public sealed class KbChatAuthorizationTests
{
    [Fact]
    public async Task Agent_without_kb_access_gets_no_retrieval_and_no_api_call()
    {
        var api = new ThrowingApiClient();
        var usage = new RecordingUsageStore();
        var service = Build(kbChatEnabled: true, api, usage);

        var result = await service.SendAsync(
            Guid.NewGuid(), Array.Empty<KbChatMessage>(),
            "How do I install the Sophos VPN?", default);

        Assert.Equal(KbChatOutcome.NoKbAccess, result.Outcome);
        Assert.Empty(result.Citations);
        Assert.False(api.Called, "the model must never be called for an agent without KB access");
        Assert.Contains(usage.Entries, e => e.Outcome == "blocked" && e.ErrorCode == "no_kb_access");
    }

    [Fact]
    public async Task Disabled_feature_blocks_before_any_api_call()
    {
        var api = new ThrowingApiClient();
        var service = Build(kbChatEnabled: false, api, new RecordingUsageStore());

        var result = await service.SendAsync(
            Guid.NewGuid(), Array.Empty<KbChatMessage>(), "hello", default);

        Assert.Equal(KbChatOutcome.Disabled, result.Outcome);
        Assert.False(api.Called);
    }

    private static KbChatService Build(bool kbChatEnabled, ThrowingApiClient api, RecordingUsageStore usage)
    {
        var secrets = new InMemorySecretStore();
        secrets.Set(ProtectedSecretKeys.ClaudeApiKey, "test-key");
        // Lazy data source — never connected because these paths short-circuit
        // before any retrieval. Construction does not open a connection.
        var ds = NpgsqlDataSource.Create("Host=localhost;Username=u;Password=p;Database=d");
        return new KbChatService(
            ds,
            new StubSettings(kbChatEnabled),
            secrets,
            api,
            usage,
            new FakeUserService(), // GetKbEnabledAsync => false
            NullLogger<KbChatService>.Instance);
    }

    private sealed class StubSettings : ISettingsService
    {
        private readonly bool _kbChatEnabled;
        public StubSettings(bool kbChatEnabled) => _kbChatEnabled = kbChatEnabled;

        public Task EnsureDefaultsAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<T> GetAsync<T>(string key, CancellationToken ct = default)
        {
            object val = key switch
            {
                SettingKeys.Claude.KbChatEnabled => _kbChatEnabled,
                SettingKeys.Claude.ZeroDataRetentionConfirmed => true,
                _ => default(T)!,
            };
            return Task.FromResult((T)val);
        }

        public Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SettingEntry>>(Array.Empty<SettingEntry>());
    }

    private sealed class ThrowingApiClient : IClaudeApiClient
    {
        public bool Called { get; private set; }

        public Task<ClaudeApiResult> CreateProposalAsync(
            string systemPrompt, string userText, IReadOnlyList<ClaudeImageInput> images, CancellationToken ct)
        {
            Called = true;
            throw new InvalidOperationException("must not be called");
        }

        public Task<ClaudeChatResult> CreateChatTurnAsync(
            string systemPrompt, JsonArray messages, JsonArray tools, string model, int maxTokens, CancellationToken ct)
        {
            Called = true;
            throw new InvalidOperationException("must not be called");
        }
    }

    private sealed class RecordingUsageStore : IClaudeUsageStore
    {
        public List<ClaudeUsageEntry> Entries { get; } = new();

        public Task LogAsync(ClaudeUsageEntry entry, CancellationToken ct)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<long> GetMonthSpendMicroEurAsync(Guid userId, DateTime monthStartUtc, CancellationToken ct) =>
            Task.FromResult(0L);

        public Task<int?> GetUserBudgetOverrideCentsAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<int?>(null);

        public Task SetUserBudgetOverrideCentsAsync(Guid userId, int? cents, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ClaudeAgentUsage>> GetAgentMonthlyUsageAsync(DateTime monthStartUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ClaudeAgentUsage>>(Array.Empty<ClaudeAgentUsage>());
    }
}
