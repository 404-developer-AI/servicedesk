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
    private static readonly string[] MessageSelect =
    {
        "id", "internetMessageId", "subject", "from",
        "toRecipients", "ccRecipients", "bccRecipients",
        "body", "bodyPreview", "receivedDateTime",
        "internetMessageHeaders", "hasAttachments"
    };

    private readonly ISettingsService _settings;
    private readonly IProtectedSecretStore _secrets;

    public GraphMailClient(ISettingsService settings, IProtectedSecretStore secrets)
    {
        _settings = settings;
        _secrets = secrets;
    }

    public async Task<GraphDeltaPage> ListInboxDeltaAsync(
        string mailbox, string folderId, string? deltaLink, int maxPageSize, CancellationToken ct)
    {
        var graph = await BuildClientAsync(ct);

        var messages = new List<GraphMailSummary>();
        string? nextLink;
        string? finalDeltaLink;

        var firstPage = await FetchPageAsync(graph, mailbox, folderId, deltaLink, maxPageSize, ct);
        AppendPage(firstPage, messages);
        nextLink = firstPage?.OdataNextLink;
        finalDeltaLink = firstPage?.OdataDeltaLink;

        while (nextLink is not null)
        {
            var page = await graph.Users[mailbox].MailFolders[folderId].Messages.Delta
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
        // Use the well-known "inbox" name for connectivity tests — no folder selection needed.
        _ = await graph.Users[mailbox].MailFolders["inbox"].Messages
            .GetAsync(config =>
            {
                config.QueryParameters.Top = 1;
                config.QueryParameters.Select = new[] { "id" };
            }, ct);
        sw.Stop();
        return sw.Elapsed;
    }

    public async Task<GraphFullMessage> FetchMessageAsync(string mailbox, string graphMessageId, CancellationToken ct)
    {
        var graph = await BuildClientAsync(ct);
        var msg = await graph.Users[mailbox].Messages[graphMessageId].GetAsync(cfg =>
        {
            cfg.QueryParameters.Select = MessageSelect;
        }, ct);

        if (msg is null)
            throw new InvalidOperationException($"Graph message {graphMessageId} not found in mailbox {mailbox}.");

        var headers = msg.InternetMessageHeaders ?? new List<InternetMessageHeader>();
        string? header(string name) => headers
            .FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

        var bodyContent = msg.Body?.Content;
        var bodyHtml = msg.Body?.ContentType == BodyType.Html ? bodyContent : null;
        var bodyText = msg.Body?.ContentType == BodyType.Text ? bodyContent : null;

        // Always fetch the attachments list: Graph reports HasAttachments = false
        // for mails with only inline images, which would otherwise skip cid rewriting
        // and leave broken <img src="cid:..."> tags in the ticket body.
        var attachments = await FetchAttachmentMetadataAsync(graph, mailbox, graphMessageId, ct);

        return new GraphFullMessage(
            Id: msg.Id ?? graphMessageId,
            InternetMessageId: msg.InternetMessageId ?? string.Empty,
            InReplyTo: header("In-Reply-To"),
            References: header("References"),
            Subject: msg.Subject ?? string.Empty,
            From: ToRecipient(msg.From) ?? new GraphRecipient(string.Empty, string.Empty),
            To: ToRecipients(msg.ToRecipients),
            Cc: ToRecipients(msg.CcRecipients),
            Bcc: ToRecipients(msg.BccRecipients),
            BodyHtml: bodyHtml,
            BodyText: bodyText,
            ReceivedUtc: msg.ReceivedDateTime ?? DateTimeOffset.UtcNow,
            // X-Auto-Submitted is the trigger send-path's loop marker:
            // Microsoft Graph only persists custom headers prefixed with
            // x-, so our outbound trigger mails carry X-Auto-Submitted in
            // place of the bare RFC-3834 name. Treat both as equivalent
            // here so the ingest skip-check sees them.
            AutoSubmitted: header("Auto-Submitted") ?? header("X-Auto-Submitted"),
            Attachments: attachments);
    }

    private static async Task<IReadOnlyList<GraphAttachmentInfo>> FetchAttachmentMetadataAsync(
        GraphServiceClient graph, string mailbox, string graphMessageId, CancellationToken ct)
    {
        // Metadata-only: omit contentBytes so the list call stays cheap even for
        // large attachments. Bytes are downloaded per-attachment by the worker.
        var page = await graph.Users[mailbox].Messages[graphMessageId].Attachments.GetAsync(cfg =>
        {
            // `contentId` is declared only on `microsoft.graph.fileAttachment`,
            // not on the polymorphic `attachment` base — Graph rejects a plain
            // `contentId` projection with "Could not find a property named …".
            // Cast-segment syntax selects the derived property.
            cfg.QueryParameters.Select = new[]
            {
                "id", "name", "contentType", "size", "isInline",
                "microsoft.graph.fileAttachment/contentId"
            };
        }, ct);

        if (page?.Value is null || page.Value.Count == 0)
            return Array.Empty<GraphAttachmentInfo>();

        var results = new List<GraphAttachmentInfo>(page.Value.Count);
        foreach (var a in page.Value)
        {
            // Skip item-attachments (nested messages) and reference-attachments (OneDrive
            // links) for now — 6b handles file-attachments only. Later steps can widen this.
            if (a is not FileAttachment fa) continue;
            if (string.IsNullOrWhiteSpace(fa.Id)) continue;

            results.Add(new GraphAttachmentInfo(
                Id: fa.Id!,
                Name: fa.Name ?? string.Empty,
                ContentType: fa.ContentType ?? "application/octet-stream",
                Size: fa.Size ?? 0,
                IsInline: fa.IsInline ?? false,
                ContentId: fa.ContentId));
        }
        return results;
    }

    public async Task<Stream> FetchAttachmentBytesAsync(
        string mailbox, string graphMessageId, string graphAttachmentId, CancellationToken ct)
    {
        var graph = await BuildClientAsync(ct);
        var attachment = await graph.Users[mailbox].Messages[graphMessageId]
            .Attachments[graphAttachmentId].GetAsync(cancellationToken: ct);

        if (attachment is not FileAttachment fa || fa.ContentBytes is null)
            throw new InvalidOperationException(
                $"Graph attachment {graphAttachmentId} on message {graphMessageId} is not a file-attachment or has no content.");

        return new MemoryStream(fa.ContentBytes, writable: false);
    }

    public async Task<Stream> FetchRawMessageAsync(string mailbox, string graphMessageId, CancellationToken ct)
    {
        var graph = await BuildClientAsync(ct);
        var stream = await graph.Users[mailbox].Messages[graphMessageId].Content.GetAsync(cancellationToken: ct);
        if (stream is null)
            throw new InvalidOperationException($"Graph $value stream null for message {graphMessageId}.");
        return stream;
    }

    public async Task MarkAsReadAsync(string mailbox, string graphMessageId, CancellationToken ct)
    {
        var graph = await BuildClientAsync(ct);
        await graph.Users[mailbox].Messages[graphMessageId]
            .PatchAsync(new Message { IsRead = true }, cancellationToken: ct);
    }

    public async Task MoveAsync(string mailbox, string graphMessageId, string destinationFolderId, CancellationToken ct)
    {
        var graph = await BuildClientAsync(ct);
        await graph.Users[mailbox].Messages[graphMessageId].Move
            .PostAsync(new Microsoft.Graph.Users.Item.Messages.Item.Move.MovePostRequestBody
            {
                DestinationId = destinationFolderId,
            }, cancellationToken: ct);
    }

    public async Task<string> EnsureFolderAsync(string mailbox, string folderName, CancellationToken ct)
    {
        var graph = await BuildClientAsync(ct);
        var existing = await graph.Users[mailbox].MailFolders.GetAsync(cfg =>
        {
            cfg.QueryParameters.Filter = $"displayName eq '{folderName.Replace("'", "''")}'";
            cfg.QueryParameters.Top = 1;
            cfg.QueryParameters.Select = new[] { "id", "displayName" };
        }, ct);

        var hit = existing?.Value?.FirstOrDefault();
        if (hit?.Id is not null) return hit.Id;

        var created = await graph.Users[mailbox].MailFolders.PostAsync(
            new MailFolder { DisplayName = folderName }, cancellationToken: ct);
        if (created?.Id is null)
            throw new InvalidOperationException($"Failed to create mail folder '{folderName}' in {mailbox}.");
        return created.Id;
    }

    public async Task<IReadOnlyList<GraphMailFolderInfo>> ListMailFoldersAsync(string mailbox, CancellationToken ct)
    {
        var graph = await BuildClientAsync(ct);
        var folders = new List<GraphMailFolderInfo>();

        var page = await graph.Users[mailbox].MailFolders.GetAsync(cfg =>
        {
            cfg.QueryParameters.Top = 100;
            cfg.QueryParameters.Select = new[] { "id", "displayName", "totalItemCount" };
        }, ct);

        while (page?.Value is not null)
        {
            foreach (var f in page.Value)
            {
                if (f.Id is null || f.DisplayName is null) continue;
                folders.Add(new GraphMailFolderInfo(f.Id, f.DisplayName, f.TotalItemCount ?? 0));
            }

            if (page.OdataNextLink is null) break;
            page = await graph.Users[mailbox].MailFolders.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: ct);
        }

        return folders;
    }

    public async Task<GraphSentMailResult> SendMailAsync(GraphOutboundMessage message, CancellationToken ct)
    {
        var graph = await BuildClientAsync(ct);

        // Draft-then-send: creating the draft first lets Graph assign the
        // internet-message-id which we capture before sending. That id is
        // what inbound replies reference via In-Reply-To / References.
        //
        // Microsoft.Graph SDK quirk: the Kiota-generated Message tracks
        // "set" properties via a backing store. Setting a property to null
        // still puts it in the serialized JSON ("internetMessageHeaders":
        // null), which Graph rejects with "unable to deserialize". So we
        // must only assign when there is at least one header to send.
        var draft = new Message
        {
            Subject = message.Subject,
            Body = new ItemBody { ContentType = BodyType.Html, Content = message.BodyHtml },
            ToRecipients = ToRecipientList(message.To),
            CcRecipients = ToRecipientList(message.Cc),
            BccRecipients = ToRecipientList(message.Bcc),
            ReplyTo = ToRecipientList(message.ReplyTo),
        };
        var headers = ToHeaderList(message.InternetMessageHeaders);
        if (headers is not null)
        {
            draft.InternetMessageHeaders = headers;
        }

        var created = await graph.Users[message.FromMailbox].Messages.PostAsync(draft, cancellationToken: ct);
        var draftId = created?.Id
            ?? throw new InvalidOperationException($"Graph returned no id for draft in mailbox {message.FromMailbox}.");
        var internetMessageId = created.InternetMessageId;
        if (string.IsNullOrWhiteSpace(internetMessageId))
        {
            // Extremely rare: draft exists without a pre-assigned id. Re-fetch
            // with an explicit select so we never persist an empty string as
            // message_id (it's UNIQUE NOT NULL).
            var refreshed = await graph.Users[message.FromMailbox].Messages[draftId]
                .GetAsync(cfg => cfg.QueryParameters.Select = new[] { "internetMessageId" }, ct);
            internetMessageId = refreshed?.InternetMessageId;
        }
        if (string.IsNullOrWhiteSpace(internetMessageId))
            throw new InvalidOperationException($"Graph did not assign an internetMessageId to draft {draftId}.");

        // Attach files *before* send. fileAttachment carries contentBytes
        // base64-encoded in the draft body — only safe for items <3 MB
        // total, which is why OutboundMailService caps via
        // Mail.MaxOutboundTotalBytes. Larger payloads need uploadSession
        // (deferred — see ROADMAP).
        if (message.Attachments is { Count: > 0 } items)
        {
            foreach (var a in items)
            {
                var attachment = new FileAttachment
                {
                    Name = string.IsNullOrWhiteSpace(a.FileName) ? "attachment" : a.FileName,
                    ContentType = string.IsNullOrWhiteSpace(a.ContentType) ? "application/octet-stream" : a.ContentType,
                    ContentBytes = a.Bytes,
                    IsInline = a.IsInline,
                    // ContentId on inline parts links the file to the cid:
                    // reference rewritten into the body. Outbound mail
                    // clients render the image inline iff the cid matches.
                    ContentId = string.IsNullOrWhiteSpace(a.ContentId) ? null : a.ContentId,
                };
                await graph.Users[message.FromMailbox].Messages[draftId].Attachments.PostAsync(attachment, cancellationToken: ct);
            }
        }

        await graph.Users[message.FromMailbox].Messages[draftId].Send.PostAsync(cancellationToken: ct);
        return new GraphSentMailResult(internetMessageId, DateTimeOffset.UtcNow);
    }

    private static List<InternetMessageHeader>? ToHeaderList(IReadOnlyList<GraphOutboundHeader>? source)
    {
        if (source is null || source.Count == 0) return null;
        var list = new List<InternetMessageHeader>(source.Count);
        foreach (var h in source)
        {
            if (string.IsNullOrWhiteSpace(h.Name)) continue;
            list.Add(new InternetMessageHeader { Name = h.Name, Value = h.Value ?? string.Empty });
        }
        return list.Count == 0 ? null : list;
    }

    private static List<Recipient> ToRecipientList(IReadOnlyList<GraphRecipient> source)
    {
        if (source is null || source.Count == 0) return new List<Recipient>();
        var list = new List<Recipient>(source.Count);
        foreach (var r in source)
        {
            if (string.IsNullOrWhiteSpace(r.Address)) continue;
            list.Add(new Recipient
            {
                EmailAddress = new EmailAddress
                {
                    Address = r.Address,
                    Name = string.IsNullOrWhiteSpace(r.Name) ? r.Address : r.Name,
                },
            });
        }
        return list;
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
        GraphServiceClient graph, string mailbox, string folderId, string? deltaLink, int maxPageSize, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(deltaLink))
        {
            return graph.Users[mailbox].MailFolders[folderId].Messages.Delta
                .WithUrl(deltaLink)
                .GetAsDeltaGetResponseAsync(cancellationToken: ct);
        }

        return graph.Users[mailbox].MailFolders[folderId].Messages.Delta
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
            // Delta-feed quirk: when a message is moved out of Inbox (e.g. our
            // own post-ingest move to Processed), the next delta page returns
            // the id as a placeholder with either an "@removed" marker in
            // AdditionalData or with all metadata null. Fetching that id by
            // inbox-scoped path then 404s. Drop those placeholders — the
            // original ingest already succeeded.
            if (m.AdditionalData is not null && m.AdditionalData.ContainsKey("@removed"))
                continue;
            if (m.Subject is null && m.From is null && m.ReceivedDateTime is null)
                continue;

            into.Add(new GraphMailSummary(
                Id: m.Id ?? string.Empty,
                InternetMessageId: m.InternetMessageId,
                Subject: m.Subject,
                FromAddress: m.From?.EmailAddress?.Address,
                FromName: m.From?.EmailAddress?.Name,
                ReceivedUtc: m.ReceivedDateTime));
        }
    }

    private static GraphRecipient? ToRecipient(Recipient? r)
    {
        var addr = r?.EmailAddress?.Address;
        if (string.IsNullOrWhiteSpace(addr)) return null;
        return new GraphRecipient(addr, r?.EmailAddress?.Name ?? string.Empty);
    }

    private static IReadOnlyList<GraphRecipient> ToRecipients(List<Recipient>? list)
    {
        if (list is null || list.Count == 0) return Array.Empty<GraphRecipient>();
        return list.Select(ToRecipient).Where(r => r is not null).Cast<GraphRecipient>().ToList();
    }
}
