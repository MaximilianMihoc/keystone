import { useCallback, useEffect, useMemo, useState } from 'react';
import { api, SessionExpired, type AssetsResponse, type ProjectAsset, type Unit } from './api';

interface Props {
  projectId: string;
  units: Unit[];
  canEdit: boolean;
  onSessionExpired: () => void;
}

/** The kinds worth offering. Anything else is filed as Other. */
const KINDS = [
  { value: 'FloorPlan', label: 'Floor plan' },
  { value: 'Rlv', label: 'RLV' },
  { value: 'Render', label: 'Render' },
  { value: 'SitePlan', label: 'Site plan' },
  { value: 'Prospectus', label: 'Prospectus' },
  { value: 'Brochure', label: 'Brochure' },
  { value: 'Other', label: 'Other' },
];

function size(bytes: number): string {
  return bytes > 1024 * 1024
    ? `${(bytes / 1024 / 1024).toFixed(1)} MB`
    : `${Math.max(1, Math.round(bytes / 1024))} KB`;
}

function floorKey(building: string | null, level: number): string {
  return `${building ?? ''}|${level}`;
}

export function AssetsPanel({ projectId, units, canEdit, onSessionExpired }: Props) {
  const [data, setData] = useState<AssetsResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [kind, setKind] = useState('FloorPlan');
  const [selected, setSelected] = useState<string | null>(null);

  const fail = useCallback(
    (e: unknown) => {
      if (e instanceof SessionExpired) onSessionExpired();
      else setError((e as Error).message);
    },
    [onSessionExpired],
  );

  const reload = useCallback(async () => {
    try {
      setData(await api.assets(projectId));
    } catch (e) {
      fail(e);
    }
  }, [projectId, fail]);

  useEffect(() => {
    setData(null);
    void reload();
  }, [reload]);

  const upload = useCallback(
    async (file: File | null) => {
      if (!file) return;

      setBusy(true);
      setError(null);

      try {
        await api.uploadAsset(projectId, file, kind);
        await reload();
      } catch (e) {
        fail(e);
      } finally {
        setBusy(false);
      }
    },
    [projectId, kind, reload, fail],
  );

  const act = useCallback(
    async (work: () => Promise<unknown>) => {
      setBusy(true);
      setError(null);

      try {
        await work();
        await reload();
      } catch (e) {
        fail(e);
      } finally {
        setBusy(false);
      }
    },
    [reload, fail],
  );

  const plansByFloor = useMemo(() => {
    const map = new Map<string, string>();
    for (const floor of data?.floors ?? []) {
      if (floor.floorPlanAssetId) map.set(floorKey(floor.building, floor.level), floor.floorPlanAssetId);
    }
    return map;
  }, [data]);

  const floorPlans = data?.assets.filter((a) => a.kind === 'FloorPlan') ?? [];
  const asset = data?.assets.find((a) => a.id === selected) ?? null;

  if (!data) {
    return <p className="empty">Loading files…</p>;
  }

  return (
    <div className="import">
      {error && (
        <p className="banner error">
          {error}
          <button className="link" type="button" onClick={() => setError(null)}>
            Dismiss
          </button>
        </p>
      )}

      {canEdit && (
        <div className="toolbar start-row">
          <select
            className="project-picker"
            value={kind}
            onChange={(e) => setKind(e.target.value)}
          >
            {KINDS.map((k) => (
              <option key={k.value} value={k.value}>
                {k.label}
              </option>
            ))}
          </select>

          <label className="btn btn-small file-btn">
            Upload a file
            <input
              type="file"
              accept=".pdf,.png,.jpg,.jpeg,.webp,.svg"
              hidden
              disabled={busy}
              onChange={(e) => void upload(e.target.files?.[0] ?? null)}
            />
          </label>

          <span className="count">PDF or image, up to 25 MB. Links expire.</span>
        </div>
      )}

      {data.assets.length === 0 ? (
        <p className="empty">
          No files yet. A floor plan uploaded here appears on every apartment on that floor,
          rather than being a link pasted into each row.
        </p>
      ) : (
        <div className="queues">
          <section className="queue">
            <header className="queue-head">
              <span className="queue-type">Files</span>
              <span className="queue-count">{data.assets.length}</span>
            </header>

            <ol className="queue-list">
              {data.assets.map((file) => (
                <li key={file.id} className={file.id === selected ? 'told' : undefined}>
                  <span className="queue-place">
                    {KINDS.find((k) => k.value === file.kind)?.label ?? file.kind}
                  </span>

                  <span className="queue-who">
                    <strong>
                      <a href={file.url} target="_blank" rel="noreferrer">
                        {file.fileName}
                      </a>
                    </strong>
                    <span className="queue-email">
                      {size(file.sizeBytes)}
                      {file.unitIds.length > 0 && ` · on ${file.unitIds.length} apartment(s)`}
                    </span>
                  </span>

                  {canEdit && (
                    <>
                      <button
                        className="link"
                        type="button"
                        onClick={() => setSelected(file.id === selected ? null : file.id)}
                      >
                        {file.id === selected ? 'Done' : 'Attach'}
                      </button>
                      <button
                        className="link danger"
                        type="button"
                        disabled={busy}
                        onClick={() => void act(() => api.deleteAsset(file.id))}
                      >
                        Delete
                      </button>
                    </>
                  )}
                </li>
              ))}
            </ol>
          </section>

          <section className="queue">
            <header className="queue-head">
              <span className="queue-type">Floor plans</span>
              <span className="queue-count">{data.levels.length} floors</span>
            </header>

            <table className="grid map-grid">
              <tbody>
                {data.levels.map((floor) => {
                  const key = floorKey(floor.building, floor.level);

                  return (
                    <tr key={key}>
                      <td className="map-field">
                        {floor.building ? `${floor.building} · ` : ''}Floor {floor.level}
                      </td>
                      <td>
                        <select
                          value={plansByFloor.get(key) ?? ''}
                          disabled={!canEdit || busy}
                          onChange={(e) =>
                            void act(() =>
                              api.setFloorPlan(projectId, {
                                building: floor.building,
                                level: floor.level,
                                assetId: e.target.value || null,
                              }),
                            )
                          }
                        >
                          <option value="">— no plan —</option>
                          {floorPlans.map((plan) => (
                            <option key={plan.id} value={plan.id}>
                              {plan.fileName}
                            </option>
                          ))}
                        </select>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </section>
        </div>
      )}

      {asset && canEdit && (
        <AttachToUnits
          asset={asset}
          units={units}
          busy={busy}
          onAttach={(unitId) => void act(() => api.attachAsset(unitId, asset.id))}
          onDetach={(unitId) => void act(() => api.detachAsset(unitId, asset.id))}
          onClose={() => setSelected(null)}
        />
      )}
    </div>
  );
}

function AttachToUnits({
  asset,
  units,
  busy,
  onAttach,
  onDetach,
  onClose,
}: {
  asset: ProjectAsset;
  units: Unit[];
  busy: boolean;
  onAttach: (unitId: string) => void;
  onDetach: (unitId: string) => void;
  onClose: () => void;
}) {
  const [filter, setFilter] = useState('');

  const shown = units.filter((u) =>
    u.code.toLowerCase().includes(filter.trim().toLowerCase()),
  );

  return (
    <aside className="drawer">
      <header className="drawer-head">
        <div>
          <h2>{asset.fileName}</h2>
          <p className="drawer-sub">Which apartments show this?</p>
        </div>
        <button className="link" type="button" onClick={onClose}>
          Close
        </button>
      </header>

      <input
        className="search"
        style={{ width: '100%', marginBottom: 12 }}
        placeholder="Filter by code…"
        value={filter}
        onChange={(e) => setFilter(e.target.value)}
      />

      <ol className="queue-list">
        {shown.map((unit) => {
          const attached = asset.unitIds.includes(unit.id);

          return (
            <li key={unit.id}>
              <span className="queue-who">
                <strong>{unit.code}</strong>
              </span>
              <button
                className={attached ? 'link danger' : 'link'}
                type="button"
                disabled={busy}
                onClick={() => (attached ? onDetach(unit.id) : onAttach(unit.id))}
              >
                {attached ? 'Remove' : 'Add'}
              </button>
            </li>
          );
        })}
      </ol>
    </aside>
  );
}
