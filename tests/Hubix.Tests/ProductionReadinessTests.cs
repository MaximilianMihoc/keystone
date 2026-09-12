using Hubix.Infrastructure.Assets;
using Hubix.Infrastructure.Email;
using Hubix.Web.Health;
using Hubix.Web.Notifications;
using Shouldly;

namespace Hubix.Tests;

/// <summary>
/// The checks that stop a production instance starting.
/// </summary>
/// <remarks>
/// This code only ever runs in Production, so these tests are the only place it is exercised
/// before the day it matters. Every one of these settings is fine locally and harmful live.
/// </remarks>
public class ProductionReadinessTests
{
    private static EmailOptions SafeEmail() => new()
    {
        LiveSendingEnabled = true,
        From = "portal@example.ro",
        AllowedRecipients = ["*@example.ro"],
        BlockedRecipients = ["*@hubix.ro"]
    };

    private static AssetOptions SafeAssets() => new() { SigningKey = "a-real-key" };

    private static HubixOptions SafeHubix() => new()
    {
        PublicBaseUrl = "https://portal.example.ro",
        SalesInbox = "sales@example.ro"
    };

    private static IReadOnlyList<string> Check(
        EmailOptions? email = null,
        AssetOptions? assets = null,
        HubixOptions? hubix = null,
        string? connection = "Server=real;Database=real",
        ResendOptions? provider = null) =>
        ProductionReadiness.Problems(
            email ?? SafeEmail(), assets ?? SafeAssets(), hubix ?? SafeHubix(), connection, provider);

    [Fact]
    public void A_properly_configured_instance_has_nothing_to_say()
    {
        Check().ShouldBeEmpty();
    }

    [Fact]
    public void Live_email_with_no_allow_list_and_no_redirect_is_refused()
    {
        var email = SafeEmail();
        email.AllowedRecipients = [];
        email.RedirectAllTo = null;

        // Otherwise the first deploy emails real buyers about a system nobody has agreed to.
        Check(email: email).ShouldContain(p => p.Contains("real buyers"));
    }

    [Fact]
    public void Live_email_with_an_empty_blocked_list_is_refused()
    {
        var email = SafeEmail();
        email.BlockedRecipients = [];

        // Hubix have not been approached. Their addresses stay on the blocked list until
        // somebody decides otherwise on purpose.
        Check(email: email).ShouldContain(p => p.Contains("Hubix's own addresses"));
    }

    [Fact]
    public void Live_email_is_the_only_thing_those_two_checks_care_about()
    {
        var email = new EmailOptions
        {
            LiveSendingEnabled = false,
            AllowedRecipients = [],
            BlockedRecipients = []
        };

        // Nothing leaves the machine, so an empty list is not a hazard.
        Check(email: email).ShouldBeEmpty();
    }

    [Fact]
    public void A_public_url_still_pointing_at_localhost_is_refused()
    {
        var hubix = SafeHubix();
        hubix.PublicBaseUrl = "http://localhost:5001";

        // Every link in every email would be dead, and nobody would find out until a buyer
        // clicked one.
        Check(hubix: hubix).ShouldContain(p => p.Contains("localhost"));
    }

    [Fact]
    public void A_placeholder_sales_inbox_is_refused()
    {
        var hubix = SafeHubix();
        hubix.SalesInbox = "sales@hubix.invalid";

        Check(hubix: hubix).ShouldContain(p => p.Contains("SalesInbox"));
    }

    [Fact]
    public void A_missing_asset_signing_key_is_refused_when_there_is_no_blob_storage()
    {
        // Links would be signed with a key generated per process: they would break on every
        // restart and would never work across two instances.
        Check(assets: new AssetOptions { SigningKey = null })
            .ShouldContain(p => p.Contains("Assets:SigningKey"));
    }

    [Fact]
    public void Blob_storage_signs_its_own_links_so_no_key_is_needed()
    {
        Check(assets: new AssetOptions
        {
            SigningKey = null,
            BlobConnectionString = "UseDevelopmentStorage=true"
        }).ShouldBeEmpty();
    }

    [Fact]
    public void A_missing_connection_string_is_refused()
    {
        Check(connection: null).ShouldContain(p => p.Contains("connection string"));
    }

    [Fact]
    public void Every_problem_is_reported_at_once_rather_than_one_per_restart()
    {
        var problems = Check(
            email: new EmailOptions { LiveSendingEnabled = true },
            assets: new AssetOptions(),
            hubix: new HubixOptions(),
            connection: null);

        // Fixing these one restart at a time would be a miserable afternoon.
        problems.Count.ShouldBeGreaterThanOrEqualTo(5);
    }

    /// <summary>
    /// Live sending with no From address, which cannot send anything at all.
    /// </summary>
    [Fact]
    public void Live_email_with_no_sender_address_is_refused()
    {
        var email = SafeEmail();
        email.From = "";

        Check(email: email).ShouldContain(p => p.Contains("Email:From"));
    }

    /// <summary>
    /// The quietest failure of the lot: live sending on, no provider behind it, and every
    /// message going to the outbox with only a warning in the log to say so.
    /// </summary>
    [Fact]
    public void Live_email_with_no_provider_key_is_refused()
    {
        Check(provider: new ResendOptions { ApiKey = "" })
            .ShouldContain(p => p.Contains("Resend:ApiKey"));
    }

    [Fact]
    public void A_configured_provider_satisfies_it()
    {
        Check(provider: new ResendOptions { ApiKey = "re_test_key" }).ShouldBeEmpty();
    }
}
