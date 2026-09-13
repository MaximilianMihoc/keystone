using System.Text;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Assets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Hubix.IntegrationTests;

/// <summary>
/// Uploading project files, and the floor plan inheritance that is the point of them.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class AssetTests(DatabaseFixture fixture) : IDisposable
{
    private static readonly Guid Actor = Guid.CreateVersion7();

    private readonly string _store = Path.Combine(
        Path.GetTempPath(), $"hubix-assets-{Guid.NewGuid():N}");

    private AssetOptions Options() => new()
    {
        LocalPath = _store,
        SigningKey = "a-key-for-tests-only",
        LinkLifetimeMinutes = 30,
        MaxUploadMegabytes = 1
    };

    private LocalAssetStorage Storage() =>
        new(Microsoft.Extensions.Options.Options.Create(Options()), TimeProvider.System);

    private AssetService Service(HubixDbContext db) =>
        new(db, Storage(), Microsoft.Extensions.Options.Options.Create(Options()),
            TimeProvider.System, NullLogger<AssetService>.Instance);

    private static Stream Content(string text = "a pretend drawing") =>
        new MemoryStream(Encoding.UTF8.GetBytes(text));

    private async Task<AssetOutcome.Saved> UploadAsync(
        string fileName = "etaj-1.pdf",
        AssetKind kind = AssetKind.FloorPlan)
    {
        await using var db = fixture.CreateContext();
        using var content = Content();

        return (await Service(db).UploadAsync(
                fixture.ProjectId, kind, fileName, content, content.Length,
                null, Actor, "dev@hubix.invalid", CancellationToken.None))
            .ShouldBeOfType<AssetOutcome.Saved>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_store))
        {
            Directory.Delete(_store, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    // --- The index this milestone existed to fix -------------------------------------

    [Fact]
    public async Task A_project_with_one_building_cannot_have_two_floor_ones()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();

        db.Floors.Add(new Floor { ProjectId = fixture.ProjectId, Building = null, Level = 1 });
        await db.SaveChangesAsync();

        db.Floors.Add(new Floor { ProjectId = fixture.ProjectId, Building = null, Level = 1 });

        // EF filters a unique index over a nullable column to "IS NOT NULL" unless told not
        // to, which left a single-building project with no uniqueness at all — and two floor
        // ones means "which floor plan?" has two answers.
        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Two_buildings_may_each_have_a_floor_one()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();

        db.Floors.Add(new Floor { ProjectId = fixture.ProjectId, Building = "E", Level = 1 });
        db.Floors.Add(new Floor { ProjectId = fixture.ProjectId, Building = "F", Level = 1 });

        await Should.NotThrowAsync(() => db.SaveChangesAsync());
    }

    // --- Uploading -------------------------------------------------------------------

    [Fact]
    public async Task An_uploaded_file_is_stored_and_audited()
    {
        await fixture.ResetUnitAsync();

        var saved = await UploadAsync();

        await using var db = fixture.CreateContext();
        var asset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == saved.AssetId);

        asset.FileName.ShouldBe("etaj-1.pdf");
        asset.ContentType.ShouldBe("application/pdf");
        asset.SizeBytes.ShouldBeGreaterThan(0);

        // Keyed by id, not by the name somebody typed: two "plan.pdf" uploads must not
        // overwrite each other, and a form value has no business becoming a path.
        asset.StorageKey.ShouldContain(saved.AssetId.ToString());
        asset.StorageKey.ShouldNotContain("etaj-1");

        (await db.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == "asset.uploaded")).ShouldBeTrue();

        // And it really is on disk.
        await using var stream = await Storage().OpenAsync(asset.StorageKey, CancellationToken.None);
        stream.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("plan.exe")]
    [InlineData("plan.html")]
    [InlineData("plan.docx")]
    [InlineData("plan")]
    public async Task Only_drawings_and_documents_may_be_uploaded(string fileName)
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        using var content = Content();

        // An allow list. Anything else is a mistake at best and something the browser might
        // execute at worst.
        var result = await Service(db).UploadAsync(
            fixture.ProjectId, AssetKind.FloorPlan, fileName, content, content.Length,
            null, Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AssetOutcome.Refused>();
    }

    [Fact]
    public async Task A_file_too_large_to_be_a_drawing_is_refused()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        using var content = Content();

        var result = await Service(db).UploadAsync(
            fixture.ProjectId, AssetKind.Render, "huge.png", content,
            sizeBytes: 5L * 1024 * 1024, null, Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AssetOutcome.Refused>().Reason.ShouldContain("1 MB");
    }

    // --- Attaching -------------------------------------------------------------------

    [Fact]
    public async Task A_floor_plan_is_uploaded_once_and_the_floor_carries_it()
    {
        await fixture.ResetUnitAsync();

        var saved = await UploadAsync();

        await using var db = fixture.CreateContext();

        (await Service(db).SetFloorPlanAsync(
                fixture.ProjectId, null, 1, saved.AssetId, CancellationToken.None))
            .ShouldBeOfType<AssetOutcome.Attached>();

        var floor = await db.Floors.AsNoTracking()
            .SingleAsync(f => f.ProjectId == fixture.ProjectId && f.Level == 1);

        floor.FloorPlanAssetId.ShouldBe(saved.AssetId);
    }

    [Fact]
    public async Task Naming_the_building_as_blank_does_not_create_a_second_floor()
    {
        await fixture.ResetUnitAsync();

        var saved = await UploadAsync();

        await using var db = fixture.CreateContext();
        var service = Service(db);

        // A form sends "" where the model means null. One floor, set twice.
        await service.SetFloorPlanAsync(fixture.ProjectId, null, 1, saved.AssetId, CancellationToken.None);
        await service.SetFloorPlanAsync(fixture.ProjectId, "  ", 1, saved.AssetId, CancellationToken.None);

        (await db.Floors.CountAsync(f => f.ProjectId == fixture.ProjectId && f.Level == 1))
            .ShouldBe(1);
    }

    [Fact]
    public async Task A_plan_on_a_floor_cannot_be_refiled_until_it_comes_off()
    {
        await fixture.ResetUnitAsync();

        var saved = await UploadAsync();

        await using var db = fixture.CreateContext();
        var service = Service(db);

        await service.SetFloorPlanAsync(fixture.ProjectId, null, 1, saved.AssetId, CancellationToken.None);

        (await service.ChangeKindAsync(saved.AssetId, AssetKind.Render, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<AssetOutcome.Refused>();

        await service.SetFloorPlanAsync(fixture.ProjectId, null, 1, null, CancellationToken.None);

        (await service.ChangeKindAsync(saved.AssetId, AssetKind.Render, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<AssetOutcome.Changed>();

        (await db.Assets.AsNoTracking().SingleAsync(a => a.Id == saved.AssetId)).Kind.ShouldBe(AssetKind.Render);
        (await db.AuditLogs.AsNoTracking().AnyAsync(a => a.EntityId == saved.AssetId && a.Action == "asset.kind_changed"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task An_rlv_attaches_to_one_apartment()
    {
        await fixture.ResetUnitAsync();

        var saved = await UploadAsync("rlv-ap101.pdf", AssetKind.Rlv);

        await using var db = fixture.CreateContext();
        var service = Service(db);

        (await service.AttachToUnitAsync(fixture.UnitId, saved.AssetId, CancellationToken.None))
            .ShouldBeOfType<AssetOutcome.Attached>();

        // Attaching twice is a double click, not an error.
        (await service.AttachToUnitAsync(fixture.UnitId, saved.AssetId, CancellationToken.None))
            .ShouldBeOfType<AssetOutcome.Attached>();

        (await db.UnitAssets.CountAsync(ua => ua.UnitId == fixture.UnitId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_file_from_another_project_cannot_be_attached()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();

        var other = new Project
        {
            Slug = $"other-{Guid.NewGuid():N}",
            Name = "Other",
            City = "Iasi",
            PublishedAt = DateTimeOffset.UtcNow
        };

        var foreign = new Asset
        {
            Project = other,
            Kind = AssetKind.Rlv,
            StorageKey = $"other/{Guid.CreateVersion7()}.pdf",
            FileName = "not-ours.pdf"
        };

        db.Projects.Add(other);
        db.Assets.Add(foreign);
        await db.SaveChangesAsync();

        (await Service(db).AttachToUnitAsync(fixture.UnitId, foreign.Id, CancellationToken.None))
            .ShouldBeOfType<AssetOutcome.Refused>()
            .Reason.ShouldContain("another project");
    }

    [Fact]
    public async Task Deleting_a_file_clears_everything_pointing_at_it()
    {
        await fixture.ResetUnitAsync();

        var saved = await UploadAsync();
        string key;

        await using (var db = fixture.CreateContext())
        {
            var service = Service(db);
            await service.SetFloorPlanAsync(fixture.ProjectId, null, 1, saved.AssetId, CancellationToken.None);
            await service.AttachToUnitAsync(fixture.UnitId, saved.AssetId, CancellationToken.None);

            key = await db.Assets.Where(a => a.Id == saved.AssetId)
                .Select(a => a.StorageKey).SingleAsync();

            (await service.DeleteAsync(saved.AssetId, Actor, "dev", CancellationToken.None))
                .ShouldBeOfType<AssetOutcome.Removed>();
        }

        await using var verify = fixture.CreateContext();

        // A floor pointing at a file that is gone renders as a broken plan on a public page.
        (await verify.Floors.AnyAsync(f => f.FloorPlanAssetId == saved.AssetId)).ShouldBeFalse();
        (await verify.UnitAssets.AnyAsync(ua => ua.AssetId == saved.AssetId)).ShouldBeFalse();
        (await verify.Assets.AnyAsync(a => a.Id == saved.AssetId)).ShouldBeFalse();

        (await Storage().OpenAsync(key, CancellationToken.None)).ShouldBeNull();
    }
}

/// <summary>
/// Read links, which are the only way anything gets out of the store.
/// </summary>
public class LocalAssetStorageTests : IDisposable
{
    private readonly string _store = Path.Combine(
        Path.GetTempPath(), $"hubix-links-{Guid.NewGuid():N}");

    private readonly FakeClock _clock = new(DateTimeOffset.UtcNow);

    private LocalAssetStorage Storage() =>
        new(Options.Create(new AssetOptions { LocalPath = _store, SigningKey = "test-key" }), _clock);

    public void Dispose()
    {
        if (Directory.Exists(_store))
        {
            Directory.Delete(_store, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Pulls a link apart the way the route does.
    /// </summary>
    /// <remarks>
    /// Deliberately no unescaping. A catch-all route hands the handler the path verbatim, and
    /// an earlier version of this helper unescaped it — which quietly undid a bug in the link
    /// builder and let every asset on the site 404 with a green suite.
    /// </remarks>
    private static (string Key, long Expires, string Signature) Parse(string url)
    {
        var uri = new Uri("http://x" + url);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

        return (uri.AbsolutePath["/assets/".Length..],
            long.Parse(query["expires"]!),
            query["signature"]!);
    }

    [Fact]
    public async Task A_link_for_a_key_with_folders_validates_as_the_route_will_read_it()
    {
        var storage = Storage();

        // Keys are "<project>/<asset>.png". The path segment separator has to survive the
        // round trip through the URL untouched, or the signature is checked against a
        // different string than it was made from.
        var url = await storage.GetReadUrlAsync(
            "a1b2/c3d4.png", TimeSpan.FromMinutes(30), CancellationToken.None);

        url.ShouldContain("/assets/a1b2/c3d4.png");
        url.ShouldNotContain("%2F");

        var (key, expires, signature) = Parse(url);
        storage.IsValid(key, expires, signature).ShouldBeTrue();
    }

    [Fact]
    public async Task A_link_works_and_then_stops_working()
    {
        var storage = Storage();
        var url = await storage.GetReadUrlAsync("a/b.pdf", TimeSpan.FromMinutes(30), CancellationToken.None);

        var (key, expires, signature) = Parse(url);
        storage.IsValid(key, expires, signature).ShouldBeTrue();

        // A plan that leaks out of a forwarded email should become a dead link, not a file
        // that is public for ever.
        _clock.Advance(TimeSpan.FromMinutes(31));
        storage.IsValid(key, expires, signature).ShouldBeFalse();
    }

    [Fact]
    public async Task A_signature_for_one_file_does_not_open_another()
    {
        var storage = Storage();
        var url = await storage.GetReadUrlAsync("a/mine.pdf", TimeSpan.FromMinutes(30), CancellationToken.None);

        var (_, expires, signature) = Parse(url);

        storage.IsValid("a/somebody-elses.pdf", expires, signature).ShouldBeFalse();
    }

    [Fact]
    public async Task An_expiry_cannot_be_extended_by_editing_the_url()
    {
        var storage = Storage();
        var url = await storage.GetReadUrlAsync("a/b.pdf", TimeSpan.FromMinutes(5), CancellationToken.None);

        var (key, expires, signature) = Parse(url);

        storage.IsValid(key, expires + 86_400, signature).ShouldBeFalse();
    }

    [Fact]
    public async Task A_key_that_climbs_out_of_the_store_is_refused()
    {
        var storage = Storage();

        // Keys are generated, never typed — but this path is reachable from a URL.
        await Should.ThrowAsync<InvalidOperationException>(
            () => storage.OpenAsync("../../secrets.txt", CancellationToken.None));
    }

    [Fact]
    public async Task A_saved_file_reads_back_exactly()
    {
        var storage = Storage();
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("floor plan bytes"));

        var stored = await storage.SaveAsync("p/one.pdf", content, "application/pdf", CancellationToken.None);
        stored.SizeBytes.ShouldBe(16);

        await using var read = await storage.OpenAsync("p/one.pdf", CancellationToken.None);
        using var reader = new StreamReader(read!);
        (await reader.ReadToEndAsync()).ShouldBe("floor plan bytes");
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
