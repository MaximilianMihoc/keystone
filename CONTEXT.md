# Context

The project's vocabulary. A glossary, not a spec: what a word means here, and which nearby word
it is not. Implementation lives in the code and in `docs/adr/`.

The operator's own terminology wins wherever it exists, spelling mistakes aside.

## Inventory and Configuration

The two halves of a project's data, distinguished by how they arrive.

**Inventory** is what is for sale: `Unit` rows, and the `UnitType` rows they hang off. It arrives
by spreadsheet import, by the sheetful, and changes often.

**Configuration** is everything a unit needs in order to mean anything: the `Project` itself, its
`Developer`, its prose, its `PricingRule`. It is entered by hand, once, and rarely changes.

The distinction is load-bearing because the two have different front doors, and because for a long
time only inventory had one.

## Project

One development, at one address, sold as one thing. The single source of truth for how many homes
it contains — `UnitsCommitted` exists exactly once so that "20 apartments here, 30 there" cannot
be said twice differently.

## Developer

The company that builds a project. Attached to a project, never the other way round: a developer
with no projects is legitimate, a project may have no developer recorded yet.

## Published

Visible to the public. A project is published when `PublishedAt` is set, and every public page
gates on precisely that. Publishing is an act, not a field an editor types into.

A project with no apartments cannot be published, because everything the public site says about a
project is derived from its units.

## Draft

A project that has never been published — a `Status`, not a synonym for invisible. See
[ADR-0001](docs/adr/0001-publication-is-a-timestamp-not-a-status.md): visibility and sales
lifecycle are two different questions, and "draft" answers only the second.

## Status

Where a project is in its selling life: Draft, Upcoming, Selling, SoldOut, Delivered, Archived. It
says nothing about whether anyone can see the project.

## Unreferenced

The precondition for deleting something. A row is unreferenced when nothing points at it and
nothing depends on it having existed.

For a developer, that means no project names it. For a project, it means it is unpublished and has
no apartments, parking spaces, files, reservations, holds, or anyone on its waiting list. Deletion
is refused otherwise, and the refusal names what is in the way.

The word matters because the database is more permissive than the rule: a developer's foreign key
is set-null, so the database would happily strip it off a live project rather than object.

## Source language

Romanian. The language the operator writes in, and what every other language falls back to when a
translation is missing — a Romanian sentence beats an empty page.

English is a translation, not the other way round.

## Cesiune

The operator's margin on a unit, taken out of what the buyer pays rather than added on top. Their word,
kept deliberately: it is real terminology in this market, and translating it would lose the
meaning the paperwork depends on. It can never exceed the price it is part of.

## Tranche

One scheduled payment in a project's payment plan. Calculated from the pricing rule at the moment
it is read, never stored on a unit — which is the fix for the column drift across the operator's five
spreadsheets, where a typed formula had gone missing from some rows.

## Snapshot

A price copied onto a reservation at the moment it is made. Prices change; history must not. A
snapshotted number is never recomputed.
