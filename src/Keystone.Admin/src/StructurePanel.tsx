import { useCallback, useEffect, useState } from 'react';
import { api, ApiError, SessionExpired, type ProjectStructure } from './api';

interface Props {
  projectId: string;
  canEdit: boolean;
  onSessionExpired: () => void;
}

/**
 * The floors of a project, and the plans pinned to them.
 *
 * Apartments join a floor by building and level rather than by key, so this screen also shows
 * where the two disagree. Apartment types used to live here too; they are one catalogue for the
 * whole portal now, and are edited under Organisation.
 */
export function StructurePanel({ projectId, canEdit, onSessionExpired }: Props) {
  const [data, setData] = useState<ProjectStructure | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const [building, setBuilding] = useState('');
  const [level, setLevel] = useState(1);
  const [label, setLabel] = useState('');

  const load = useCallback(async () => {
    try {
      setData(await api.structure(projectId));
      setError(null);
    } catch (e) {
      if (e instanceof SessionExpired) onSessionExpired();
      else setError(e instanceof ApiError ? e.message : 'Could not load the project structure.');
    }
  }, [projectId, onSessionExpired]);

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

  if (!data) {
    return error ? <p className="banner error">{error}</p> : <p className="empty">Loading…</p>;
  }

  // A level the apartments claim with no floor row is a missing label and a missing plan; a floor
  // row nobody lives on is usually a typo in its building or level.
  const orphanLevels = data.occupied.filter(
    (o) => !data.floors.some((f) => f.building === o.building && f.level === o.level),
  );

  return (
    <>
      {error && <p className="banner error">{error}</p>}

      <section className="panel">
        <h2>Floors</h2>
        <p className="hint">
          A floor carries a name and a plan. Apartments are matched to it by block and level rather
          than by a link, so removing one loses the name and the plan, and nothing else.
        </p>

        {canEdit && (
          <div className="row">
            <label>
              Block
              <input
                value={building}
                placeholder="none"
                size={6}
                onChange={(e) => setBuilding(e.target.value)}
              />
            </label>
            <label>
              Level
              <input
                type="number"
                value={level}
                size={4}
                onChange={(e) => setLevel(Number(e.target.value))}
              />
            </label>
            <label>
              Name
              <input value={label} placeholder="Etaj 1" onChange={(e) => setLabel(e.target.value)} />
            </label>
            <button
              className="btn"
              type="button"
              disabled={busy}
              onClick={() =>
                void run(async () => {
                  await api.createFloor(projectId, {
                    building: building.trim() === '' ? null : building.trim(),
                    level,
                    label: label.trim() === '' ? null : label.trim(),
                  });

                  setLabel('');
                })
              }
            >
              Add
            </button>
          </div>
        )}

        {data.floors.length === 0 ? (
          <p className="empty">No floors described yet.</p>
        ) : (
          <table className="grid">
            <thead>
              <tr>
                <th>Block</th>
                <th className="num">Level</th>
                <th>Name</th>
                <th>Plan</th>
                <th className="num">Apartments</th>
                {canEdit && <th />}
              </tr>
            </thead>
            <tbody>
              {data.floors.map((f) => {
                const here = data.occupied.find((o) => o.building === f.building && o.level === f.level);

                return (
                  <tr key={f.id}>
                    <td>{f.building ?? '—'}</td>
                    <td className="num">{f.level}</td>
                    <td>{f.label ?? '—'}</td>
                    <td>{f.hasPlan ? 'Yes' : '—'}</td>
                    <td className="num">{here?.units ?? 0}</td>
                    {canEdit && (
                      <td>
                        <button
                          className="btn danger"
                          type="button"
                          disabled={busy}
                          onClick={() => void run(() => api.deleteFloor(f.id))}
                        >
                          Delete
                        </button>
                      </td>
                    )}
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}

        {orphanLevels.length > 0 && (
          <p className="hint">
            Apartments sit on{' '}
            {orphanLevels
              .map((o) => (o.building ? `${o.building} level ${o.level}` : `level ${o.level}`))
              .join(', ')}{' '}
            with no floor described. Add those to give them a name and a plan.
          </p>
        )}
      </section>
    </>
  );
}
