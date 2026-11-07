using Keystone.Domain;
using Keystone.Domain.Entities;
using Keystone.Infrastructure;
using Keystone.Infrastructure.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Keystone.IntegrationTests;

/// <summary>
/// The contact form, and the data rights that arrive through it.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class EnquiryTests(DatabaseFixture fixture)
{
    private static EnquiryService Service(KeystoneDbContext db) =>
        new(db, TimeProvider.System, NullLogger<EnquiryService>.Instance);

    private static ProspectDetails Person(string email) =>
        new(email, "Ana", "Popescu", "+40 700 111 222", null, null, Cultures.Romanian, false, "enquiry-form");

    [Fact]
    public async Task An_ordinary_enquiry_is_kept_against_the_person_who_sent_it()
    {
        await fixture.ResetUnitAsync();
        const string email = "asking@example.ro";

        await using var db = fixture.CreateContext();

        var receipt = await Service(db).SubmitAsync(
            Person(email), fixture.ProjectId, "Is parking included?", null, CancellationToken.None);

        receipt.Request.ShouldBeNull();

        var enquiry = await db.Enquiries.AsNoTracking().SingleAsync(e => e.Id == receipt.EnquiryId);
        var prospect = await db.Prospects.AsNoTracking().SingleAsync(p => p.Email == email);

        enquiry.ProspectId.ShouldBe(prospect.Id);
        enquiry.ProjectId.ShouldBe(fixture.ProjectId);
        prospect.Source.ShouldBe("enquiry");

        // Asking a question is not consenting to anything.
        prospect.GdprConsentAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_data_right_starts_the_clock_the_moment_it_is_asked_for()
    {
        await fixture.ResetUnitAsync();
        const string email = "forgetme@example.ro";

        await using var db = fixture.CreateContext();

        var receipt = await Service(db).SubmitAsync(
            Person(email), null, "Please delete everything you have about me.",
            PrivacyRequestKind.Erasure, CancellationToken.None);

        var request = receipt.Request.ShouldNotBeNull();
        request.Kind.ShouldBe(PrivacyRequestKind.Erasure);
        request.IsOpen.ShouldBeTrue();
        request.DueAt.ShouldBe(request.RequestedAt.AddMonths(1));

        (await db.PrivacyRequests.AsNoTracking()
            .CountAsync(r => r.Email == email && r.CompletedAt == null)).ShouldBe(1);

        // Somebody we have never heard of asked to be forgotten. Writing them down in order
        // to forget them would be the request's own opposite.
        (await db.Prospects.AsNoTracking().AnyAsync(p => p.Email == email)).ShouldBeFalse();

        var enquiry = await db.Enquiries.AsNoTracking().SingleAsync(e => e.Id == receipt.EnquiryId);
        enquiry.ProspectId.ShouldBeNull();

        // Audited as the person themselves, under the address erasure later redacts by.
        var audit = await db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.EntityId == request.Id);

        audit.ActorType.ShouldBe("prospect");
        audit.ActorLabel.ShouldBe(email);
        audit.Action.ShouldBe("privacy.request.logged");
    }

    [Fact]
    public async Task A_message_cannot_rewrite_a_buyer_already_on_file()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();

        db.Prospects.Add(new Prospect
        {
            Email = "buyer@example.ro",
            FirstName = "Real",
            Surname = "Buyer",
            Phone = "0722222222",
            Locale = Cultures.English,
            Source = "website"
        });

        await db.SaveChangesAsync();

        // Anybody can type anybody's address into the contact form, so it may fill in what is
        // missing and nothing else — otherwise sales ring a stranger's number (issue #30).
        await Service(db).SubmitAsync(
            Person("buyer@example.ro"), fixture.ProjectId, "Is it still free?", null, CancellationToken.None);

        var buyer = await db.Prospects.AsNoTracking().SingleAsync(p => p.Email == "buyer@example.ro");

        buyer.FirstName.ShouldBe("Real");
        buyer.Surname.ShouldBe("Buyer");
        buyer.Phone.ShouldBe("0722222222");
        buyer.Locale.ShouldBe(Cultures.English);

        // What was blank is worth having.
        buyer.City.ShouldBeNull();
    }
}
