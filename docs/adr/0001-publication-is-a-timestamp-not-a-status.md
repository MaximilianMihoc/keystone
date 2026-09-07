# Publication is a timestamp, not a status

`Project` carries both a `PublishedAt` timestamp and a six-value `ProjectStatus` whose first
member is `Draft`, and until an admin could set them independently the two never disagreed —
only the seeder wrote either, and it wrote them consistently. Adding project CRUD made
`Status = Draft` with `PublishedAt` set reachable: a project simultaneously a draft and publicly
visible.

We decided `PublishedAt` alone governs visibility and `Status` describes only where a project is
in its selling life. Publishing sets `PublishedAt` and, if the project is still `Draft`, advances
it to `Upcoming`; unpublishing clears `PublishedAt` and leaves `Status` untouched.

## Considered options

Collapsing to a single gate — deleting `PublishedAt` and having public queries test
`Status >= Upcoming` — is the cleaner model and was the alternative. It was rejected because it
buys one concept at the cost of a migration and rewriting all six public queries
(`Pages/Index.cshtml.cs`, `Projects/Detail`, `Apartments`, `Apartment`, `Reserve`, `Waitlist`),
and because the two really do answer different questions: a `SoldOut` project is still visible,
and a project can be pulled from public view without any claim about how sales are going.

Leaving them fully independent was also rejected: it keeps "draft" meaning two things and leaves
the contradictory state reachable.

## Consequences

`Status` can no longer be trusted to tell you whether the public can see a project, and a reader
who assumes it can will be wrong in a way nothing in the type system catches. The compensating
constraint is that `Draft` now means exactly "never published", so the two only diverge
deliberately.
