import { useCallback, useEffect, useState } from 'react';
import { api, SessionExpired, type Me, type ProjectSummary, type UnitsResponse } from './api';
import { ParkingPanel } from './ParkingPanel';
import { UnitsGrid } from './UnitsGrid';
import { PipelineBoard } from './PipelineBoard';
import { WaitlistPanel } from './WaitlistPanel';
import { ImportPanel } from './ImportPanel';
import { AssetsPanel } from './AssetsPanel';
import { OrganisationPanel } from './OrganisationPanel';
import { ProspectsPanel } from './ProspectsPanel';
import { PrivacyPanel } from './PrivacyPanel';
import { SettingsPanel } from './SettingsPanel';
import { AuditPanel } from './AuditPanel';
import { SetupPanel } from './SetupPanel';
import { ThemeSwitch } from './ThemeSwitch';

type View =
  | 'units'
  | 'parking'
  | 'pipeline'
  | 'waitlist'
  | 'files'
  | 'import'
  | 'setup'
  | 'organisation'
  | 'prospects'
  | 'privacy'
  | 'audit'
  | 'settings';

/** The picker's last entry, which opens the create form rather than choosing a project. */
const NEW_PROJECT = '__new';

/**
 * Views that belong to no project.
 *
 * Organisation is where the first project is created, the next three are about people rather
 * than inventory — a prospect reaches a project only through a hold, a reservation or a queue,
 * and a request for their data reaches none at all — and Email is portal-wide. Telling somebody
 * to "choose a project to see its apartments" on any of these is answering a question they did
 * not ask.
 */
const UNGATED: View[] = ['organisation', 'prospects', 'privacy', 'audit', 'settings'];

type Load<T> =
  | { status: 'loading' }
  | { status: 'ready'; data: T }
  | { status: 'failed'; message: string };

export function App() {
  const [me, setMe] = useState<Load<Me>>({ status: 'loading' });
  const [projects, setProjects] = useState<Load<ProjectSummary[]>>({ status: 'loading' });
  const [selected, setSelected] = useState<string | null>(null);
  const [units, setUnits] = useState<Load<UnitsResponse> | null>(null);
  const [expired, setExpired] = useState(false);
  const [view, setView] = useState<View>('units');
  const [organisationSection, setOrganisationSection] = useState<'projects' | 'developers'>('projects');

  useEffect(() => {
    void (async () => {
      try {
        setMe({ status: 'ready', data: await api.me() });
        const list = await api.projects();
        setProjects({ status: 'ready', data: list });

        // One project today. Skipping a chooser that chooses nothing. With none chosen, every
        // project view would only say "choose a project", so open on Organisation instead.
        if (list.length === 1) setSelected(list[0].id);
        else setView('organisation');
      } catch (e) {
        if (e instanceof SessionExpired) setExpired(true);
        else setProjects({ status: 'failed', message: (e as Error).message });
      }
    })();
  }, []);

  /** Re-reads the picker and the header, after a rename, a publish or a new project. */
  const reloadProjects = useCallback(async () => {
    try {
      setProjects({ status: 'ready', data: await api.projects() });
    } catch (e) {
      if (e instanceof SessionExpired) setExpired(true);
      else setProjects({ status: 'failed', message: (e as Error).message });
    }
  }, []);

  /** Re-reads the grid and the tallies. An import can change most of both at once. */
  const reload = useCallback(async (projectId: string) => {
    try {
      setUnits({ status: 'ready', data: await api.units(projectId) });
      setProjects({ status: 'ready', data: await api.projects() });
    } catch (e) {
      if (e instanceof SessionExpired) setExpired(true);
      else setUnits({ status: 'failed', message: (e as Error).message });
    }
  }, []);

  useEffect(() => {
    if (!selected) return;

    setUnits({ status: 'loading' });
    void reload(selected);
  }, [selected, reload]);

  if (expired) {
    return (
      <div className="centred">
        <h1>Your session has ended</h1>
        <p>Sign in again to carry on. Nothing you saved has been lost.</p>
        <a className="btn" href="/admin/login">
          Sign in
        </a>
      </div>
    );
  }

  const project =
    projects.status === 'ready' ? projects.data.find((p) => p.id === selected) : undefined;

  return (
    <>
      <header className="topbar">
        <a className="wordmark" href="/admin">
          HUBIX<span>ADMIN</span>
        </a>

        {/* Rendered whatever the count. It used to hide below two projects, which also hid it
            at zero — leaving a fresh install with no picker, no tabs and no way to create the
            first project. */}
        {projects.status === 'ready' && (
          <select
            className="project-picker"
            value={selected ?? ''}
            onChange={(e) => {
              if (e.target.value === NEW_PROJECT) {
                setOrganisationSection('projects');
                setView('organisation');
                return;
              }

              setSelected(e.target.value || null);
            }}
          >
            <option value="">Choose a project…</option>
            {projects.data.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name} — {p.city}
                {p.publishedAt ? '' : ' (not published)'}
              </option>
            ))}
            <option value={NEW_PROJECT}>+ New project…</option>
          </select>
        )}

        <div className="spacer" />

        <ThemeSwitch />

        <a className="link" href="/" target="_blank" rel="noreferrer">
          View the site
        </a>

        {me.status === 'ready' && (
          <span className="who" title={me.data.email}>
            {me.data.name} · {me.data.role}
          </span>
        )}

        <form method="post" action="/admin/logout">
          <button className="link" type="submit">
            Sign out
          </button>
        </form>
      </header>

      <main>
        {project && (
          <div className="project-head">
            <h1>{project.name}</h1>
            <div className="tallies">
              <Tally label="Available" value={project.available} tone="free" />
              <Tally label="On hold" value={project.held} />
              <Tally label="Reserved" value={project.reserved} />
              <Tally label="Sold" value={project.sold} />
              <Tally label="Total" value={project.total} />
            </div>
          </div>
        )}

        {projects.status === 'failed' && <p className="banner error">{projects.message}</p>}

        {!selected && projects.status === 'ready' && !UNGATED.includes(view) && (
          <p className="empty">
            {projects.data.length === 0
              ? 'No projects yet. Create one under Organisation.'
              : 'Choose a project to see its apartments.'}
          </p>
        )}

        {/* The tab strip is no longer gated on a selected project: Organisation has to be
            reachable before the first project exists, or there is no way to create it. */}
        {projects.status === 'ready' && (
          <nav className="views">
            {selected && (
              <>
              <button
                className={`view${view === 'setup' ? ' view-on' : ''}`}
                type="button"
                onClick={() => setView('setup')}
              >
                Setup
              </button>
              <button
                className={`view${view === 'units' ? ' view-on' : ''}`}
                type="button"
                onClick={() => setView('units')}
              >
                Apartments
              </button>
              <button
                className={`view${view === 'parking' ? ' view-on' : ''}`}
                type="button"
                onClick={() => setView('parking')}
              >
                Parking
              </button>
              <button
                className={`view${view === 'pipeline' ? ' view-on' : ''}`}
                type="button"
                onClick={() => setView('pipeline')}
              >
                Pipeline
              </button>
              <button
                className={`view${view === 'waitlist' ? ' view-on' : ''}`}
                type="button"
                onClick={() => setView('waitlist')}
              >
                Waiting list
              </button>
              <button
                className={`view${view === 'files' ? ' view-on' : ''}`}
                type="button"
                onClick={() => setView('files')}
              >
                Plans &amp; files
              </button>
              <button
                className={`view${view === 'import' ? ' view-on' : ''}`}
                type="button"
                onClick={() => setView('import')}
              >
                Import
              </button>
              </>
            )}

            {/* Last, and never gated: this is where projects and developers are created. */}
            <button
              className={`view${view === 'organisation' ? ' view-on' : ''}`}
              type="button"
              onClick={() => setView('organisation')}
            >
              Organisation
            </button>

            {/* A prospect belongs to no project — they are joined to one only through a hold,
                a reservation or a queue — so this is ungated too. Hidden rather than shown and
                refused, the way the People section is. */}
            {me.status === 'ready' && me.data.role === 'Admin' && (
              <button
                className={`view${view === 'prospects' ? ' view-on' : ''}`}
                type="button"
                onClick={() => setView('prospects')}
              >
                Prospects
              </button>
            )}

            {/* Next to Prospects, because it is the same people seen by their deadline
                rather than by their history. Ungated and admin-only for the same reasons. */}
            {me.status === 'ready' && me.data.role === 'Admin' && (
              <button
                className={`view${view === 'privacy' ? ' view-on' : ''}`}
                type="button"
                onClick={() => setView('privacy')}
              >
                Data requests
              </button>
            )}

            {/* Also ungated and also admin-only: the log spans every project and names
                colleagues, so reading all of it is a supervisory view. */}
            {me.status === 'ready' && me.data.role === 'Admin' && (
              <button
                className={`view${view === 'audit' ? ' view-on' : ''}`}
                type="button"
                onClick={() => setView('audit')}
              >
                Audit
              </button>
            )}

            {/* Last, and admin-only like the rest of this row: these settings decide whether
                the system speaks to real people at all. */}
            {me.status === 'ready' && me.data.role === 'Admin' && (
              <button
                className={`view${view === 'settings' ? ' view-on' : ''}`}
                type="button"
                onClick={() => setView('settings')}
              >
                Email
              </button>
            )}
          </nav>
        )}

        {view === 'prospects' && me.status === 'ready' && me.data.role === 'Admin' && (
          <ProspectsPanel onSessionExpired={() => setExpired(true)} />
        )}

        {view === 'privacy' && me.status === 'ready' && me.data.role === 'Admin' && (
          <PrivacyPanel onSessionExpired={() => setExpired(true)} />
        )}

        {view === 'audit' && me.status === 'ready' && me.data.role === 'Admin' && (
          <AuditPanel onSessionExpired={() => setExpired(true)} />
        )}

        {view === 'settings' && me.status === 'ready' && me.data.role === 'Admin' && (
          <SettingsPanel onSessionExpired={() => setExpired(true)} />
        )}

        {view === 'organisation' && me.status === 'ready' && projects.status === 'ready' && (
          <OrganisationPanel
            projects={projects.data}
            canEdit={me.data.canEdit}
            isAdmin={me.data.role === 'Admin'}
            initialSection={organisationSection}
            onSessionExpired={() => setExpired(true)}
            onProjectCreated={(id) => {
              void reloadProjects();
              setSelected(id);
              setView('setup');
            }}
          />
        )}

        {view === 'setup' && selected && me.status === 'ready' && (
          <SetupPanel
            key={selected}
            projectId={selected}
            canEdit={me.data.canEdit}
            onSessionExpired={() => setExpired(true)}
            onProjectsChanged={() => void reloadProjects()}
            onProjectDeleted={() => {
              // The grid still holds the deleted project's rows, so it has to go too.
              setSelected(null);
              setUnits(null);
              setOrganisationSection('projects');
              setView('organisation');
            }}
          />
        )}

        {view === 'units' && selected && (
          <>
            {units?.status === 'loading' && <p className="empty">Loading apartments…</p>}
            {units?.status === 'failed' && <p className="banner error">{units.message}</p>}

            {units?.status === 'ready' && me.status === 'ready' && (
              <UnitsGrid
                // The grid keeps its own copy of the rows so editing feels like a spreadsheet,
                // which means a reload alone does not reach it. Adding the count remounts it
                // when an apartment is added or removed, and never for an ordinary edit.
                key={`${units.data.project.id}:${units.data.units.length}`}
                units={units.data.units}
                types={units.data.types}
                canEdit={me.data.canEdit}
                projectId={selected}
                onSessionExpired={() => setExpired(true)}
                onInventoryChanged={() => void reload(selected)}
              />
            )}
          </>
        )}

        {view === 'parking' && selected && me.status === 'ready' && (
          <ParkingPanel
            key={selected}
            projectId={selected}
            canEdit={me.data.canEdit}
            onSessionExpired={() => setExpired(true)}
          />
        )}

        {view === 'pipeline' && selected && me.status === 'ready' && (
          <PipelineBoard
            key={selected}
            projectId={selected}
            canEdit={me.data.canEdit}
            onSessionExpired={() => setExpired(true)}
          />
        )}

        {view === 'waitlist' && selected && me.status === 'ready' && (
          <WaitlistPanel
            key={selected}
            projectId={selected}
            canEdit={me.data.canEdit}
            onSessionExpired={() => setExpired(true)}
          />
        )}

        {view === 'files' && selected && me.status === 'ready' && (
          <AssetsPanel
            key={selected}
            projectId={selected}
            units={units?.status === 'ready' ? units.data.units : []}
            canEdit={me.data.canEdit}
            onSessionExpired={() => setExpired(true)}
          />
        )}

        {view === 'import' && selected && me.status === 'ready' && (
          <ImportPanel
            key={selected}
            projectId={selected}
            canEdit={me.data.canEdit}
            onSessionExpired={() => setExpired(true)}
            onImported={() => void reload(selected)}
          />
        )}
      </main>
    </>
  );
}

function Tally({ label, value, tone }: { label: string; value: number; tone?: 'free' }) {
  return (
    <div className={`tally${tone ? ` tally-${tone}` : ''}`}>
      <span className="tally-value">{value}</span>
      <span className="tally-label">{label}</span>
    </div>
  );
}
