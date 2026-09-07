import { useCallback, useEffect, useState } from 'react';
import {
  api,
  ApiError,
  SessionExpired,
  type Developer,
  type DeveloperEdit,
  type ProjectSummary,
} from './api';

interface Props {
  projects: ProjectSummary[];
  canEdit: boolean;
  /** Which half to open on, so the project picker's "+ New project" lands in the right place. */
  initialSection?: Section;
  onSessionExpired: () => void;
  onProjectCreated: (projectId: string) => void;
}

type Section = 'projects' | 'developers';

const EMPTY: DeveloperEdit = {
  name: '',
  description: null,
  yearsActive: null,
  unitsBuilt: null,
  website: null,
};

/**
 * The things that exist above any one project: the projects themselves, and who built them.
 *
 * This is the screen that did not exist, and its absence is why onboarding a second project
 * meant editing the seeder and redeploying.
 */
export function OrganisationPanel({
  projects,
  canEdit,
  initialSection = 'projects',
  onSessionExpired,
  onProjectCreated,
}: Props) {
  const [section, setSection] = useState<Section>(initialSection);
  const [developers, setDevelopers] = useState<Developer[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const [name, setName] = useState('');
  const [city, setCity] = useState('');
  const [slug, setSlug] = useState('');
  const [slugTouched, setSlugTouched] = useState(false);
  const [developerId, setDeveloperId] = useState('');

  const [draft, setDraft] = useState<DeveloperEdit>(EMPTY);
  const [editing, setEditing] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setDevelopers(await api.developers());
      setError(null);
    } catch (e) {
      if (e instanceof SessionExpired) onSessionExpired();
      else setError(e instanceof ApiError ? e.message : 'Could not load the developers.');
    }
  }, [onSessionExpired]);

  useEffect(() => {
    void load();
  }, [load]);

  const run = useCallback(
    async (action: () => Promise<unknown>) => {
      setBusy(true);
      setError(null);

      try {
        await action();
        await load();
      } catch (e) {
        if (e instanceof SessionExpired) onSessionExpired();
        else setError(e instanceof ApiError ? e.message : 'That did not save.');
      } finally {
        setBusy(false);
      }
    },
    [load, onSessionExpired],
  );

  // The address follows the name until somebody types their own, and then stops interfering.
  const onName = (value: string) => {
    setName(value);
    if (!slugTouched) setSlug(slugify(value));
  };

  return (
    <>
      {error && <p className="banner error">{error}</p>}

      <nav className="views">
        <button
          className={`view${section === 'projects' ? ' view-on' : ''}`}
          type="button"
          onClick={() => setSection('projects')}
        >
          Projects
        </button>
        <button
          className={`view${section === 'developers' ? ' view-on' : ''}`}
          type="button"
          onClick={() => setSection('developers')}
        >
          Developers
        </button>
      </nav>

      {section === 'projects' && (
        <>
          {canEdit && (
            <section className="panel">
              <h2>New project</h2>
              <p className="hint">
                A name and a city are enough to start. Everything else — dates, fees, the
                payment plan, the prose — is filled in on the project's own Setup tab, and it
                stays invisible to the public until you publish it.
              </p>
              <div className="row">
                <label>
                  Name
                  <input value={name} onChange={(e) => onName(e.target.value)} size={24} />
                </label>
                <label>
                  City
                  <input value={city} onChange={(e) => setCity(e.target.value)} size={16} />
                </label>
                <label>
                  Web address
                  <input
                    value={slug}
                    onChange={(e) => {
                      setSlugTouched(true);
                      setSlug(e.target.value);
                    }}
                    size={22}
                  />
                </label>
                <label>
                  Developer
                  <select value={developerId} onChange={(e) => setDeveloperId(e.target.value)}>
                    <option value="">Not set</option>
                    {(developers ?? []).map((d) => (
                      <option key={d.id} value={d.id}>
                        {d.name}
                      </option>
                    ))}
                  </select>
                </label>
                <button
                  className="btn"
                  type="button"
                  disabled={busy || name.trim() === '' || city.trim() === ''}
                  onClick={() =>
                    void run(async () => {
                      const created = await api.createProject({
                        name: name.trim(),
                        city: city.trim(),
                        slug: slug.trim() === '' ? null : slug.trim(),
                        developerId: developerId === '' ? null : developerId,
                      });

                      setName('');
                      setCity('');
                      setSlug('');
                      setSlugTouched(false);
                      setDeveloperId('');
                      onProjectCreated(created.id);
                    })
                  }
                >
                  Create
                </button>
              </div>
              {slug !== '' && <p className="hint">It will live at /projects/{slug}</p>}
            </section>
          )}

          <section className="panel">
            <h2>Projects</h2>
            {projects.length === 0 ? (
              <p className="empty">No projects yet.</p>
            ) : (
              <table className="grid">
                <thead>
                  <tr>
                    <th>Name</th>
                    <th>City</th>
                    <th>Address</th>
                    <th>Status</th>
                    <th>Apartments</th>
                    <th>Public</th>
                  </tr>
                </thead>
                <tbody>
                  {projects.map((p) => (
                    <tr key={p.id}>
                      <td>{p.name}</td>
                      <td>{p.city}</td>
                      <td>/projects/{p.slug}</td>
                      <td>{p.status}</td>
                      <td>{p.total}</td>
                      <td>{p.publishedAt ? 'Published' : 'Not published'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            <p className="hint">
              Choose a project at the top of the page to edit it, publish it or delete it.
            </p>
          </section>
        </>
      )}

      {section === 'developers' && (
        <>
          {canEdit && (
            <section className="panel">
              <h2>{editing ? 'Edit developer' : 'New developer'}</h2>
              <div className="row">
                <label>
                  Name
                  <input
                    value={draft.name}
                    onChange={(e) => setDraft({ ...draft, name: e.target.value })}
                    size={24}
                  />
                </label>
                <label>
                  Years active
                  <input
                    type="number"
                    value={draft.yearsActive ?? ''}
                    onChange={(e) => setDraft({ ...draft, yearsActive: number(e.target.value) })}
                    size={4}
                  />
                </label>
                <label>
                  Homes built
                  <input
                    type="number"
                    value={draft.unitsBuilt ?? ''}
                    onChange={(e) => setDraft({ ...draft, unitsBuilt: number(e.target.value) })}
                    size={6}
                  />
                </label>
                <label>
                  Website
                  <input
                    value={draft.website ?? ''}
                    onChange={(e) => setDraft({ ...draft, website: blank(e.target.value) })}
                    size={26}
                  />
                </label>
              </div>
              <div className="row">
                <label className="field">
                  Description
                  <textarea
                    value={draft.description ?? ''}
                    onChange={(e) => setDraft({ ...draft, description: blank(e.target.value) })}
                    rows={2}
                  />
                </label>
              </div>
              <div className="row">
                <button
                  className="btn"
                  type="button"
                  disabled={busy || draft.name.trim() === ''}
                  onClick={() =>
                    void run(async () => {
                      if (editing) await api.editDeveloper(editing, draft);
                      else await api.createDeveloper(draft);

                      setDraft(EMPTY);
                      setEditing(null);
                    })
                  }
                >
                  {editing ? 'Save' : 'Add'}
                </button>
                {editing && (
                  <button
                    className="btn ghost"
                    type="button"
                    onClick={() => {
                      setDraft(EMPTY);
                      setEditing(null);
                    }}
                  >
                    Cancel
                  </button>
                )}
              </div>
            </section>
          )}

          <section className="panel">
            <h2>Developers</h2>
            {developers === null ? (
              <p className="empty">Loading developers…</p>
            ) : developers.length === 0 ? (
              <p className="empty">No developers yet.</p>
            ) : (
              <table className="grid">
                <thead>
                  <tr>
                    <th>Name</th>
                    <th>Years</th>
                    <th>Homes built</th>
                    <th>Website</th>
                    <th>Projects</th>
                    {canEdit && <th />}
                  </tr>
                </thead>
                <tbody>
                  {developers.map((d) => (
                    <tr key={d.id}>
                      <td>{d.name}</td>
                      <td>{d.yearsActive ?? ''}</td>
                      <td>{d.unitsBuilt ?? ''}</td>
                      <td>{d.website ?? ''}</td>
                      <td>{d.projectCount}</td>
                      {canEdit && (
                        <td>
                          <button
                            className="btn ghost"
                            type="button"
                            onClick={() => {
                              setEditing(d.id);
                              setDraft({
                                name: d.name,
                                description: d.description,
                                yearsActive: d.yearsActive,
                                unitsBuilt: d.unitsBuilt,
                                website: d.website,
                              });
                            }}
                          >
                            Edit
                          </button>{' '}
                          {/* A developer on a project cannot go: the foreign key is set-null,
                              so deleting one would quietly strip it off a live project. */}
                          <button
                            className="btn danger"
                            type="button"
                            disabled={busy || d.projectCount > 0}
                            onClick={() => void run(() => api.deleteDeveloper(d.id))}
                          >
                            Delete
                          </button>
                        </td>
                      )}
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </section>
        </>
      )}
    </>
  );
}

/**
 * The same folding the server does, so the field shows what will actually be saved.
 *
 * The server remains the authority — it re-slugifies whatever arrives — but a preview that
 * disagreed with the result would be worse than no preview at all.
 */
function slugify(name: string): string {
  return name
    .normalize('NFD')
    // \p{Mn} is the nonspacing marks NFD just split off, and is exactly what the server drops.
    .replace(/\p{Mn}/gu, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 120)
    .replace(/-+$/, '');
}

const number = (value: string): number | null => (value === '' ? null : Number(value));

const blank = (value: string): string | null => (value.trim() === '' ? null : value);
