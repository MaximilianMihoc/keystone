import { useCallback, useEffect, useState } from 'react';
import { api, ApiError, SessionExpired, type ProjectStructure } from './api';

interface Props {
  projectId: string;
  canEdit: boolean;
  onSessionExpired: () => void;
}

const CULTURE_NAMES: Record<string, string> = {
  'ro-RO': 'Romanian',
  'en-GB': 'English',
};

/**
 * How a project's inventory is organised: what its apartment types are called, and what its
 * floors are named.
 *
 * Both are joined to apartments loosely — a type by key, a floor by building and level — so this
 * screen also shows where the two disagree.
 */
export function StructurePanel({ projectId, canEdit, onSessionExpired }: Props) {
  const [data, setData] = useState<ProjectStructure | null>(null);
  const [names, setNames] = useState<Record<string, Record<string, string>>>({});
  const [orders, setOrders] = useState<Record<string, number>>({});
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const [typeName, setTypeName] = useState('');

  const [building, setBuilding] = useState('');
  const [level, setLevel] = useState(1);
  const [label, setLabel] = useState('');

  const load = useCallback(async () => {
    try {
      const structure = await api.structure(projectId);
      setData(structure);

      setNames(Object.fromEntries(structure.types.map((t) => [
        t.id,
        Object.fromEntries(structure.cultures.map((c) => [
          c,
          t.displayNames.find((d) => d.culture === c)?.displayName ?? '',
        ])),
      ])));

      setOrders(Object.fromEntries(structure.types.map((t) => [t.id, t.sortOrder])));
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
        <h2>Apartment types</h2>
        <p className="hint">
          The name on the left is Hubix’s own, and it is what the spreadsheet importer matches on —
          so it stays put. What a visitor reads is the translation next to it. This list is shared
          by every project, so the apartment count is across all of them.
        </p>

        {canEdit && (
          <div className="row">
            <label>
              Name
              <input
                value={typeName}
                placeholder="2 camere"
                onChange={(e) => setTypeName(e.target.value)}
              />
            </label>
            <button
              className="btn"
              type="button"
              disabled={busy || typeName.trim() === ''}
              onClick={() =>
                void run(async () => {
                  await api.createUnitType({
                    name: typeName.trim(),
                    sortOrder: data.types.length,
                  });

                  setTypeName('');
                })
              }
            >
              Add
            </button>
          </div>
        )}

        {data.types.length === 0 ? (
          <p className="empty">
            No apartment types yet. Add them here, or import apartments and they are created for
            you.
          </p>
        ) : (
          <table className="grid">
            <thead>
              <tr>
                <th>Name</th>
                {data.cultures.map((c) => (
                  <th key={c}>{CULTURE_NAMES[c] ?? c}</th>
                ))}
                <th className="num">Order</th>
                <th className="num">Apartments</th>
                {canEdit && <th />}
              </tr>
            </thead>
            <tbody>
              {data.types.map((t) => (
                <tr key={t.id}>
                  <td>{t.name}</td>
                  {data.cultures.map((c) => (
                    <td key={c}>
                      <input
                        value={names[t.id]?.[c] ?? ''}
                        placeholder={t.name}
                        disabled={!canEdit}
                        onChange={(e) =>
                          setNames({ ...names, [t.id]: { ...names[t.id], [c]: e.target.value } })
                        }
                      />
                    </td>
                  ))}
                  <td className="num">
                    <input
                      type="number"
                      value={orders[t.id] ?? 0}
                      disabled={!canEdit}
                      onChange={(e) => setOrders({ ...orders, [t.id]: Number(e.target.value) })}
                    />
                  </td>
                  <td className="num">{t.units}</td>
                  {canEdit && (
                    <td>
                      <button
                        className="btn"
                        type="button"
                        disabled={busy}
                        onClick={() =>
                          void run(() =>
                            api.editUnitType(t.id, {
                              sortOrder: orders[t.id] ?? 0,
                              displayNames: names[t.id] ?? {},
                            }),
                          )
                        }
                      >
                        Save
                      </button>{' '}
                      {/* Restricted by the foreign key too, but saying why beats a constraint
                          violation. Reassigning apartments between types is not offered. */}
                      <button
                        className="btn danger"
                        type="button"
                        disabled={busy || t.units > 0 || t.waiting > 0}
                        onClick={() => void run(() => api.deleteUnitType(t.id))}
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
