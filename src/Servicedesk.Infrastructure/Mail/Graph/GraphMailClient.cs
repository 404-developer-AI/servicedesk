using System.Diagnostics;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Mail.Graph;

public sealed class GraphMailClient : IGraphMailClient
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];
    private readonly ISettingsService _settings;
    private readonly IProtectedSecretStore _secrets;

    public GraphMailClient(ISettingsService settings, IProtectedSecretStore secrets)
    {
        _settings = settings;
        _secrets = secrets;
    }

    public async Task<GraphDeltaPage> ListInboxDeltaAsync(
        string mailbox, string? deltaLink, int maxPageSize, CancellationToken ct)
    {
        var graph = await BuildClientAsync(ct);

        var messages = new List<GraphMailSummary>();
        string? nextLink = null;
        string? finalDeltaLink = null;

        var firstPage = await FetchPageAsync(graph, mailbox, deltaLink, maxPageSize, ct);
        AppendPage(firstPage, messages);
        nextLink = firstPage?.OdataNextLink;
        finalDeltaLink = firstPage?.OdataDeltaLink;

        while (nextLink is not null)
        {
            var page = await graph.Users[mailbox].MailFolders["inbox"].Messages.Delta
                .WithUrl(nextLink)
                .GetAsDeltaGetResponseAsync(cancellationToken: ct);
            AppendPage(page, messages);
            nextLink = page?.OdataNextLink;
            finalDeltaLink = page?.OdataDeltaLink ?? finalDeltaLink;
        }

        return new GraphDeltaPage(messages, finalDeltaLink);
    }

    public async Task<TimeSpan> PingAsync(string mailbox, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var graph = await BuildClientAsync(ct);
        _ = await graph.Users[mailbox].MailFolders["inbox"].Messages.Delta
            .GetAsDeltaGetResponseAsync(config =>
            {
                config.QueryParameters.Top = 1;
                config.QueryParameters.Select = new[] { "id" };
            }, ct);
        sw.Stop();
        return sw.Elapsed;
    }

    private async Task<GraphServiceClient> BuildClientAsync(CancellationToken ct)
    {
        var tenantId = await _settings.GetAsync<string>(SettingKeys.Graph.TenantId, ct);
        var clientId = await _settings.GetAsync<string>(SettingKeys.Graph.ClientId, ct);
        var clientSecret = await _secrets.GetAsync(ProtectedSecretKeys.GraphClientSecret, ct);

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException(
                "Microsoft Graph is not fully configured. Set Graph.TenantId, Graph.ClientId, and the client secret via Settings.");

        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        return new GraphServiceClient(credential, Scopes);
    }

    private static Task<Microsoft.Graph.Users.Item.MailFolders.Item.Messages.Delta.DeltaGetResponse?> FetchPageAsync(
        GraphServiceClient graph, string mailbox, string? deltaLink, int maxPageSize, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(deltaLink))
        {
            return graph.Users[mailbox].MailFolders["inbox"].Messages.Delta
                .WithUrl(deltaLink)
                .GetAsDeltaGetResponseAsync(cancellationToken: ct);
        }

        return graph.Users[mailbox].MailFolders["inbox"].Messages.Delta
            .GetAsDeltaGetResponseAsync(config =>
            {
                config.QueryParameters.Top = maxPageSize;
                config.QueryParameters.Select = new[]
                {
                    "id", "subject", "from", "internetMessageId", "receivedDateTime"
                };
            }, ct);
    }

    private static void AppendPage(
        Microsoft.Graph.Users.Item.MailFolders.Item.Messages.Delta.DeltaGetResponse? page,
        List<GraphMailSummary> into)
    {
        if (page?.Value is null) return;
        foreach (var m in page.Value)
        {
            into.Add(new GraphMailSummary(
                Id: m.Id ?? string.Empty,
                InternetMessageId: m.InternetMessageId,
                Subject: m.Subject,
                FromAddress: m.From?.EmailAddress?.Address,
                FromName: m.From?.EmailAddress?.Name,
                ReceivedUtc: m.ReceivedDateTime));
        }
    }
}
