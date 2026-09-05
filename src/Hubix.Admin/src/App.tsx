import { useCallback, useEffect, useState } from 'react';
import { api, SessionExpired, type Me, type ProjectSummary, type UnitsResponse } from './api';
import { UnitsGrid } from './UnitsGrid';
import { PipelineBoard } from './PipelineBoard';
import { WaitlistPanel } from './WaitlistPanel';
import { ImportPanel } from './ImportPanel';
import { AssetsPanel } from './AssetsPanel';
import { ThemeSwitch } from './ThemeSwitch';

type View = 'units' | 'pipeline' | 'waitlist' | 'files' | 'import';

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

  useEffect(() => {
    void (async () => {
      try {
        setMe({ status: 'ready', data: await api.me() });
        const list = await api.projects();
        setProjects({ status: 'ready', data: list });

        // One project today. Skipping a chooser that chooses nothing.
        if (list.length === 1) setSelected(list[0].id);
      } catch (e) {
        if (e instanceof SessionExpired) setExpired(true);
        else setProjects({ status: 'failed', message: (e as Error).message });
      }
    })();
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

        {projects.status === 'ready' && projects.data.length > 1 && (
          <select
            className="project-picker"
            value={selected ?? ''}
            onChange={(e) => setSelected(e.target.value || null)}
          >
            <option value="">Choose a project…</option>
            {projects.data.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name} — {p.city}
              </option>
            ))}
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

        {!selected && projects.status === 'ready' && (
          <p className="empty">Choose a project to see its apartments.</p>
        )}

        {selected && (
          <nav className="views">
            <button
              className={`view${view === 'units' ? ' view-on' : ''}`}
              type="button"
              onClick={() => setView('units')}
            >
              Apartments
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
          </nav>
        )}

        {view === 'units' && (
          <>
            {units?.status === 'loading' && <p className="empty">Loading apartments…</p>}
            {units?.status === 'failed' && <p className="banner error">{units.message}</p>}

            {units?.status === 'ready' && me.status === 'ready' && (
              <UnitsGrid
                key={units.data.project.id}
                units={units.data.units}
                types={units.data.types}
                canEdit={me.data.canEdit}
                onSessionExpired={() => setExpired(true)}
              />
            )}
          </>
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
