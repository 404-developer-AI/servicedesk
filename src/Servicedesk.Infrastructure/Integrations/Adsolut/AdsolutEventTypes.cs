namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Audit event-type strings for the Adsolut OAuth integration. Centralised
/// so endpoint, service and (future) Health/security-activity bucket all
/// agree on the wire-format names.
public static class AdsolutEventTypes
{
    /// Admin-initiated authorize step. No credentials in the payload — only
    /// environment + redirect_uri, both already in Settings.
    public const string ConnectStarted = "integration.adsolut.connect.started";

    /// Code exchange + persist succeeded. Payload carries the authorized
    /// subject + email so an admin can spot-check who linked the account.
    public const string ConnectSucceeded = "integration.adsolut.connect.succeeded";

    /// Callback rejected before tokens were minted. Payload carries the
    /// reject reason + sanitized detail (no codes, no secrets).
    public const string ConnectFailed = "integration.adsolut.connect.failed";

    /// Successful refresh — RT rotated, sliding window reset. Logged at
    /// info-level so a long-running connection produces a steady heartbeat
    /// in the audit trail.
    public const string TokenRefreshed = "integration.adsolut.token.refreshed";

    /// Refresh failed — non-success from the token endpoint or a parse
    /// failure on the response. Payload distinguishes transient (network)
    /// from terminal (invalid_grant) so an admin can decide between waiting
    /// and reconnecting.
    public const string TokenRefreshFailed = "integration.adsolut.token.refresh_failed";

    /// Admin-initiated disconnect. Wipes the refresh-token + metadata; the
    /// client_id / client_secret stay so a quick reconnect is one click.
    public const string Disconnected = "integration.adsolut.disconnected";

    /// Admin updated the client secret (PUT /api/admin/integrations/adsolut/secret).
    /// Mirrors the Graph-secret event; the actual value never leaves the
    /// secret store.
    public const string ClientSecretUpdated = "integration.adsolut.client_secret.updated";

    /// Admin cleared the client secret. Implies the connection is now
    /// inert until a new secret arrives.
    public const string ClientSecretDeleted = "integration.adsolut.client_secret.deleted";

    /// Admin clicked "Reveal access token" in the External client card.
    /// Records the export so a forensic trail exists if the token ever
    /// leaks from a Postman workspace. Payload carries the actor + the
    /// access-token expiry; the token value itself is NEVER written to
    /// the audit payload — only the fact of export. Lands in audit_log
    /// (security trail), not integration_audit, because no outbound HTTP
    /// happens — this is purely a credential disclosure event.
    public const string AccessTokenExported = "integration.adsolut.access_token.exported";

    // ---- integration_audit event types ---------------------------------
    //
    // The constants below land in integration_audit (operational log),
    // not audit_log (security trail). They carry latency + http_status
    // + upstream error_code so an admin can spot a slow or flapping
    // integration without correlating across two tables.

    /// Integration name written to <c>integration_audit.integration</c>.
    /// Centralised so the worker, endpoints and reader all agree.
    public const string Integration = "adsolut";

    /// Outbound HTTP call to the WK token endpoint to exchange an
    /// authorization code for tokens (server-side leg of the OAuth dance).
    /// Latency + http_status are populated; actor_id/role are populated
    /// from the intent-cookie that survived the cross-site redirect.
    public const string OAuthCodeExchange = "oauth.code_exchange";

    /// Outbound HTTP call to the WK token endpoint to rotate the refresh
    /// token. Source is one of: scheduled healthcheck tick, admin-clicked
    /// "Test refresh", or a future API-call path. Payload distinguishes
    /// the source so the table can be filtered.
    public const string OAuthRefresh = "oauth.refresh";

    /// One healthcheck tick. Always produces a row (even when skipped
    /// because the integration isn't configured), so admins can see the
    /// worker is alive. Outcome is the resolved status the SignalR push
    /// would carry — ok = healthy, warn = configured-but-not-connected
    /// or transient failure, error = invalid_grant / sliding window
    /// elapsed.
    public const string HealthcheckTick = "healthcheck.tick";

    // ---- v0.0.26 Companies pull ---------------------------------------

    /// GET /adm/v1/administrations — list dossiers the authorized user owns.
    public const string AdministrationsList = "administrations.list";

    /// POST /adm/v1/administrations/{id}/integrations — activate the
    /// integration on the chosen dossier. Without this every Accounting
    /// API call returns empty (per WK docs).
    public const string AdministrationsActivate = "administrations.activate";

    /// DELETE /adm/v1/administrations/{id}/integrations — fired on
    /// disconnect. Leaving the integration active has financial impact
    /// per WK docs, so this runs before tokens get wiped.
    public const string AdministrationsDeactivate = "administrations.deactivate";

    /// GET /acc/v1/customers — paginated list, optionally with
    /// ?ModifiedSince= for delta-sync. Each page is one audit row so a
    /// slow page is independently visible.
    public const string CustomersList = "customers.list";

    /// GET /acc/v1/suppliers — only used when the IncludeSuppliers toggle
    /// is on. Same pagination + delta-sync mechanics as customers.
    public const string SuppliersList = "suppliers.list";

    /// One sync-worker tick summary. Carries the totals (seen / upserted /
    /// skipped) + duration + outcome so admins can see whether the last
    /// tick did real work or was a noop.
    public const string SyncTick = "sync.tick";

    /// Admin-triggered raw-API probe from the Adsolut settings page debug
    /// card. Hits /customers or /suppliers with a Code= filter; the response
    /// body is shown back to the admin verbatim. Distinct from the regular
    /// CustomersList / SuppliersList ticks so audit filters can separate
    /// diagnostic noise from operational sync traffic.
    public const string DebugLookup = "debug.lookup";

    // ---- v0.0.27 Companies push (Servicedesk → Adsolut) ----------------

    /// POST /acc/v1/customers — push-tak created a new customer in the
    /// active dossier. One audit row per row pushed; admins can spot a
    /// burst of creations from the integrations audit page.
    public const string CustomersCreate = "customers.create";

    /// PUT /acc/v1/customers/{id} — push-tak applied a local edit to an
    /// already-linked customer. One row per push.
    public const string CustomersUpdate = "customers.update";

    /// GET /acc/v1/customers/{id} — read-back fallback the write-client
    /// uses when POST/PUT response body did not carry a lastModified. Kept
    /// distinct from CustomersList because filters on "list vs. by-id"
    /// are easier to read in the audit table.
    public const string CustomersGet = "customers.get";

    /// Admin-triggered debug GET on /customers/{id} from the settings page
    /// PUT-preview card. The response body is shown back to the admin
    /// verbatim and (separately) transformed into the canonical PUT body
    /// so they can review and edit before sending. Distinct from
    /// CustomersGet so operational read-back traffic is not drowned out
    /// by debug noise.
    public const string DebugPutPreview = "debug.put_preview";

    /// Admin-triggered debug PUT on /customers/{id} from the settings page
    /// PUT-preview card. The body is supplied verbatim by the admin (after
    /// optional in-browser editing) and forwarded to Adsolut without any
    /// SD-managed overlay. Distinct from CustomersUpdate so push-traffic
    /// stays separable from one-off admin probes.
    public const string DebugCustomerPut = "debug.customer_put";

    // ---- v0.0.28 Contacts pull (Adsolut → SD) -------------------------

    /// GET /acc/v1/adm/{adm}/customers/{customer}/contacts — full contacts
    /// list for one customer. The sub-resource has no pagination and no
    /// ModifiedSince filter, so each call returns the complete set; the
    /// sync worker only fires it for customers whose own lastModified
    /// advanced this tick (or for the slower reconcile-pass).
    public const string CustomerContactsList = "customer_contacts.list";

    /// GET /acc/v1/adm/{adm}/suppliers/{supplier}/contacts — same as
    /// CustomerContactsList for the supplier branch. Only fires when the
    /// IncludeSuppliers toggle is on (force-OFF in v0.0.28).
    public const string SupplierContactsList = "supplier_contacts.list";

    /// One contacts-reconcile tick — slower walk of every Adsolut-linked
    /// customer that catches active-flips (no lastModified bump from
    /// Adsolut) and hard-deletes that the fast delta-loop missed. Outcome
    /// summary lands in integration_audit alongside the regular sync.tick.
    public const string ContactsReconcileTick = "contacts_reconcile.tick";

    /// Admin-triggered manual "Resync contacts for this company" from the
    /// company-detail page. One audit row per click so an admin can see
    /// who triggered an out-of-band probe.
    public const string ContactsResyncRequested = "contacts_resync.requested";

    // ---- v0.0.29 Contacts push (Servicedesk → Adsolut) ----------------

    /// POST /acc/v1/adm/{adm}/customers/{customer}/contacts — push-tak
    /// created a new contact under an existing Adsolut customer. One row
    /// per row pushed; admins can spot a burst of creations from the
    /// integrations audit page.
    public const string CustomerContactsCreate = "customer_contacts.create";

    /// PUT /acc/v1/adm/{adm}/customers/{customer}/contacts/{contact} —
    /// push-tak applied a local edit on one of the four mirrored fields
    /// (first_name, last_name, phone, mobile_phone) to an already-linked
    /// contact. One row per push.
    public const string CustomerContactsUpdate = "customer_contacts.update";

    /// GET /acc/v1/adm/{adm}/customers/{customer}/contacts/{contact} —
    /// read-back fallback the contacts write-client uses on POST/PUT
    /// because both write responses lack a lastModified, plus the
    /// pre-update overlay GET that lets the PUT body be a true no-op
    /// when nothing changed semantically. Distinct from the
    /// CustomerContactsList (full-list pull) so audit filters separate
    /// per-contact reads from list reads.
    public const string CustomerContactsGet = "customer_contacts.get";

    // ---- v0.0.30 Sync coverage admin actions --------------------------

    /// Admin manually linked an SD company to an existing Adsolut customer
    /// UUID via the coverage page. One audit row per click; payload
    /// carries the SD companyId + Adsolut UUID + lastModified so the
    /// admin can spot a wrong-row-linked mistake later.
    public const string CoverageLinkCompany = "coverage.link.company";

    /// Admin manually linked a contact-company link row to an existing
    /// Adsolut customer-contact UUID via the coverage page.
    public const string CoverageLinkContact = "coverage.link.contact";

    /// Admin manually triggered a force-push of one company from the
    /// coverage page (drift bucket). Bypasses the push toggle but still
    /// respects hash-no-op + no-drift gates.
    public const string CoverageForcePushCompany = "coverage.force_push.company";

    /// Admin manually triggered a force-push of one contact-link from
    /// the coverage page.
    public const string CoverageForcePushContact = "coverage.force_push.contact";

    // ---- ERP SalesReceipts (verkoopbonnen) ----------------------------

    /// Admin-triggered raw-API preview of the ERP SalesReceiptInfos list
    /// endpoint (GET /erp/v1/adm/{adm}/SalesReceiptInfos) from the Adsolut
    /// settings page. Hits a single small page and shows the response body
    /// verbatim so an admin can confirm the real `state` codes + which
    /// fields WK actually serialises before the mirror/sync slice is built.
    /// Distinct from the operational sync ticks so diagnostic probes stay
    /// separable in the audit table.
    public const string DebugErpSalesReceipts = "debug.erp_sales_receipts";

    /// GET /erp/v1/adm/{adm}/SalesReceiptInfos — one page of the cursor-paged
    /// list during a SalesReceipts sync tick. Each page is one audit row.
    public const string ErpSalesReceiptsList = "erp.sales_receipts.list";

    /// GET /erp/v1/adm/{adm}/SalesReceiptInfos/{id} — full by-id fetch the
    /// sync does per receipt (the list view omits performance lines).
    public const string ErpSalesReceiptsGet = "erp.sales_receipts.get";

    /// One SalesReceipts sync-worker tick summary (seen / upserted / skipped
    /// + duration + outcome).
    public const string ErpSalesReceiptsSyncTick = "erp.sales_receipts.sync_tick";

    // ---- v0.0.59 ERP Orders (bestellingen) ----------------------------

    /// GET /erp/v1/adm/{adm}/OrderInfos — one page of the cursor-paged orders
    /// list during an Orders sync tick. Each page is one audit row. The list
    /// item already carries the full order incl. lines, so there is no
    /// per-order by-id fetch during a normal sync.
    public const string ErpOrdersList = "erp.orders.list";

    /// GET /erp/v1/adm/{adm}/OrderInfos/{id} — full by-id fetch used only for
    /// a manual per-row resync and the ::-link single fetch.
    public const string ErpOrdersGet = "erp.orders.get";

    /// One Orders sync-worker tick summary (seen / upserted + duration +
    /// outcome).
    public const string ErpOrdersSyncTick = "erp.orders.sync_tick";

    /// GET /erp/v1/adm/{adm}/SupplierOrderInfos — one page of the cursor-paged
    /// supplier-orders (bestellingen / Doc "BL") list during the Orders sync
    /// tick's second pass. Carries the real per-line procurement status.
    public const string ErpSupplierOrdersList = "erp.supplier_orders.list";

    /// GET /erp/v1/adm/{adm}/Warehouses — one page of the cursor-paged
    /// warehouses ("magazijnen") reference list. Mirrored so supplier-order
    /// lines resolve their warehouse/location code+id to a display name
    /// (Stock / Location columns).
    public const string ErpWarehousesList = "erp.warehouses.list";

    // ---- v0.0.76 ERP Articles (artikels) ------------------------------

    /// GET /erp/v1/adm/{adm}/Articles — one page of the cursor-paged article
    /// catalogue list during an Articles sync tick. Each page is one audit row;
    /// the list item already carries the full article (code, name, vat), so
    /// there is no per-article by-id fetch during a normal sync.
    public const string ErpArticlesList = "erp.articles.list";

    /// GET /erp/v1/adm/{adm}/Articles/{id} — full by-id fetch used only for a
    /// manual per-row resync.
    public const string ErpArticlesGet = "erp.articles.get";

    /// One Articles sync-worker tick summary (seen / upserted + duration +
    /// outcome).
    public const string ErpArticlesSyncTick = "erp.articles.sync_tick";

    // ---- v0.0.76 ERP Contracts (contracten) ---------------------------

    /// GET /erp/v1/adm/{adm}/Contracts — one page of the cursor-paged contracts
    /// list during a Contracts sync tick. Each page is one audit row; the list
    /// item already carries the full contract incl. its article lines, so there
    /// is no per-contract by-id fetch during a normal sync.
    public const string ErpContractsList = "erp.contracts.list";

    /// GET /erp/v1/adm/{adm}/Contracts/{id} — full by-id fetch used only for a
    /// manual per-row resync.
    public const string ErpContractsGet = "erp.contracts.get";

    /// One Contracts sync-worker tick summary (seen / upserted + duration +
    /// outcome).
    public const string ErpContractsSyncTick = "erp.contracts.sync_tick";
}
