import { useCallback, useMemo, useState } from 'react';
import { api, ApiError, SessionExpired, type Unit, type UnitEdit, type UnitType } from './api';

type SaveState = 'idle' | 'saving' | 'saved' | 'error';

interface Props {
  units: Unit[];
  types: UnitType[];
  canEdit: boolean;
  projectId: string;
  onSessionExpired: () => void;
  /** Adding or removing an apartment changes the tallies, so the shell reloads. */
  onInventoryChanged: () => void;
}

/** Fields that hold money or measurements, so the input can be numeric and right-aligned. */
const NUMERIC = new Set([
  'areaUsable',
  'areaBalcony',
  'areaBuilt',
  'basePriceNet',
  'cesiunePriceNet',
]);

interface Column {
  key: keyof Unit;
  label: string;
  editable: boolean;
  width?: number;
}

const COLUMNS: Column[] = [
  { key: 'code', label: 'Apartment', editable: true, width: 96 },
  { key: 'unitTypeId', label: 'Type', editable: true, width: 130 },
  { key: 'building', label: 'Block', editable: true, width: 70 },
  { key: 'stair', label: 'Stair', editable: true, width: 62 },
  { key: 'floorLevel', label: 'Floor', editable: true, width: 62 },
  { key: 'orientation', label: 'Facing', editable: true, width: 74 },
  { key: 'areaUsable', label: 'Usable', editable: true, width: 84 },
  { key: 'areaBalcony', label: 'Balcony', editable: true, width: 84 },
  { key: 'areaTotal', label: 'Total', editable: false, width: 84 },
  { key: 'basePriceNet', label: 'Price net', editable: true, width: 108 },
  { key: 'cesiunePriceNet', label: 'Cesiune net', editable: true, width: 108 },
  { key: 'reducedVatEligible', label: 'Red. VAT', editable: true, width: 78 },
  { key: 'state', label: 'State', editable: false, width: 108 },
  { key: 'adminNotes', label: 'Notes', editable: true, width: 200 },
];

export function UnitsGrid({
  units: initial,
  types,
  canEdit,
  projectId,
  onSessionExpired,
  onInventoryChanged,
}: Props) {
  const [units, setUnits] = useState(initial);
  const [editing, setEditing] = useState<{ id: string; key: keyof Unit } | null>(null);
  const [saving, setSaving] = useState<Record<string, SaveState>>({});
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState('');
  const [adding, setAdding] = useState(false);
  const [draft, setDraft] = useState({
    code: '',
    unitTypeId: '',
    areaUsable: '',
    basePriceNet: '',
    floorLevel: '',
    building: '',
  });

  const typeName = useMemo(
    () => Object.fromEntries(types.map((t) => [t.id, t.name])),
    [types],
  );

  const visible = useMemo(() => {
    const needle = filter.trim().toLowerCase();
    if (!needle) return units;

    return units.filter((u) =>
      [u.code, u.orientation, u.building, typeName[u.unitTypeId], u.state, u.adminNotes]
        .filter(Boolean)
        .some((value) => String(value).toLowerCase().includes(needle)),
    );
  }, [units, filter, typeName]);

  const save = useCallback(
    async (unit: Unit, key: keyof Unit, raw: string | boolean) => {
      setEditing(null);

      let value: unknown = raw;

      if (typeof raw === 'string') {
        if (NUMERIC.has(key)) {
          const trimmed = raw.trim();
          value = trimmed === '' ? null : Number(trimmed);
          if (value !== null && Number.isNaN(value)) {
            setError(`"${raw}" is not a number.`);
            return;
          }
        } else if (key === 'floorLevel') {
          const trimmed = raw.trim();
          value = trimmed === '' ? null : Number.parseInt(trimmed, 10);
          if (value !== null && Number.isNaN(value)) {
            setError(`"${raw}" is not a floor number.`);
            return;
          }
        } else if (raw.trim() === '') {
          value = null;
        }
      }

      if (value === unit[key]) return;

      const cellId = `${unit.id}:${key}`;
      setSaving((s) => ({ ...s, [cellId]: 'saving' }));
      setError(null);

      // Optimistic: the grid should feel like a spreadsheet. On failure the previous
      // value comes back, so a refused edit never silently looks accepted.
      const previous = unit[key];
      setUnits((all) => all.map((u) => (u.id === unit.id ? { ...u, [key]: value } : u)));

      try {
        // The code is the apartment's name rather than one of its properties — the importer
        // matches on it and a reservation refers to it — so it has its own endpoint, which
        // refuses the change once anybody has held or reserved the apartment.
        if (key === 'code') {
          await api.renameUnit(unit.id, String(value ?? ''));
        } else {
          await api.editUnit(unit.id, { [key]: value } as UnitEdit);
        }

        setSaving((s) => ({ ...s, [cellId]: 'saved' }));
        setTimeout(() => setSaving((s) => ({ ...s, [cellId]: 'idle' })), 1200);

        // AreaTotal is computed in the database, so recompute it here rather than
        // showing a stale figure until the next reload.
        if (key === 'areaUsable' || key === 'areaBalcony') {
          setUnits((all) =>
            all.map((u) =>
              u.id === unit.id
                ? { ...u, areaTotal: Number(u.areaUsable ?? 0) + Number(u.areaBalcony ?? 0) }
                : u,
            ),
          );
        }
      } catch (e) {
        setUnits((all) => all.map((u) => (u.id === unit.id ? { ...u, [key]: previous } : u)));
        setSaving((s) => ({ ...s, [cellId]: 'error' }));

        if (e instanceof SessionExpired) onSessionExpired();
        else setError(e instanceof ApiError ? e.message : 'Could not save that change.');
      }
    },
    [onSessionExpired],
  );

  /**
   * Takes an apartment off sale, or puts it back. The only state change the grid may make:
   * everything else moves through a hold or a reservation, which needs a person on the
   * other end of it.
   */
  const withdraw = useCallback(
    async (unit: Unit, off: boolean) => {
      setError(null);

      try {
        await api.setWithdrawn(unit.id, off);

        setUnits((all) =>
          all.map((u) => (u.id === unit.id ? { ...u, state: off ? 'Withdrawn' : 'Available' } : u)),
        );

        // The tallies in the header count by state, so they are now a state behind.
        onInventoryChanged();
      } catch (e) {
        if (e instanceof SessionExpired) onSessionExpired();
        else setError(e instanceof ApiError ? e.message : 'Could not change that apartment.');
      }
    },
    [onInventoryChanged, onSessionExpired],
  );

  function render(unit: Unit, column: Column) {
    const value = unit[column.key];
    const cellId = `${unit.id}:${column.key}`;
    const status = saving[cellId] ?? 'idle';
    const isEditing = editing?.id === unit.id && editing.key === column.key;

    if (column.key === 'state') {
      const held = unit.holdExpiresAt
        ? ` · until ${new Date(unit.holdExpiresAt).toLocaleString()}`
        : '';
      return (
        <span className={`state state-${String(value).toLowerCase()}`} title={(unit.heldBy ?? '') + held}>
          {String(value)}
        </span>
      );
    }

    if (column.key === 'unitTypeId') {
      if (!isEditing || !canEdit) {
        return (
          <span onDoubleClick={() => canEdit && setEditing({ id: unit.id, key: column.key })}>
            {typeName[String(value)] ?? '—'}
          </span>
        );
      }
      return (
        <select
          autoFocus
          defaultValue={String(value)}
          onBlur={(e) => save(unit, column.key, e.target.value)}
          onChange={(e) => save(unit, column.key, e.target.value)}
        >
          {types.map((t) => (
            <option key={t.id} value={t.id}>
              {t.name}
            </option>
          ))}
        </select>
      );
    }

    if (typeof value === 'boolean') {
      return (
        <input
          type="checkbox"
          checked={value}
          disabled={!canEdit || status === 'saving'}
          onChange={(e) => save(unit, column.key, e.target.checked)}
        />
      );
    }

    if (isEditing && canEdit && column.editable) {
      return (
        <input
          autoFocus
          defaultValue={value === null ? '' : String(value)}
          inputMode={NUMERIC.has(column.key) ? 'decimal' : undefined}
          className={NUMERIC.has(column.key) ? 'num' : undefined}
          onBlur={(e) => save(unit, column.key, e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') (e.target as HTMLInputElement).blur();
            if (e.key === 'Escape') setEditing(null);
          }}
        />
      );
    }

    const display =
      value === null || value === ''
        ? '—'
        : NUMERIC.has(column.key)
          ? Number(value).toLocaleString(undefined, { minimumFractionDigits: 2 })
          : String(value);

    return (
      <span
        className={[
          column.editable && canEdit ? 'editable' : '',
          NUMERIC.has(column.key) ? 'num' : '',
          status === 'saved' ? 'just-saved' : '',
          status === 'error' ? 'save-failed' : '',
        ]
          .filter(Boolean)
          .join(' ')}
        title={column.editable && canEdit ? 'Double-click to edit' : undefined}
        onDoubleClick={() => column.editable && canEdit && setEditing({ id: unit.id, key: column.key })}
      >
        {display}
      </span>
    );
  }

  return (
    <>
      <div className="toolbar">
        <input
          className="search"
          placeholder="Filter by code, type, facing, notes…"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
        />
        <span className="count">
          {visible.length === units.length
            ? `${units.length} apartments`
            : `${visible.length} of ${units.length}`}
        </span>
        {!canEdit && <span className="readonly-badge">Read only</span>}
        {canEdit && (
          <button className="btn ghost" type="button" onClick={() => setAdding(!adding)}>
            {adding ? 'Cancel' : 'Add an apartment'}
          </button>
        )}
      </div>

      {adding && canEdit && (
        <section className="panel">
          <h2>Add an apartment</h2>
          <p className="hint">
            For the one that was missed, or a floor added late. A whole building still belongs in
            the Import tab. It needs what the importer needs: a code, a type, a usable area and a
            price. It starts available.
          </p>
          <div className="row">
            <label>
              Code
              <input
                value={draft.code}
                size={10}
                onChange={(e) => setDraft({ ...draft, code: e.target.value })}
              />
            </label>
            <label>
              Type
              <select
                value={draft.unitTypeId}
                onChange={(e) => setDraft({ ...draft, unitTypeId: e.target.value })}
              >
                <option value="">Choose…</option>
                {types.map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.name}
                  </option>
                ))}
              </select>
            </label>
            <label>
              Block
              <input
                value={draft.building}
                size={5}
                onChange={(e) => setDraft({ ...draft, building: e.target.value })}
              />
            </label>
            <label>
              Floor
              <input
                type="number"
                value={draft.floorLevel}
                size={4}
                onChange={(e) => setDraft({ ...draft, floorLevel: e.target.value })}
              />
            </label>
            <label>
              Usable area
              <input
                type="number"
                step="0.01"
                value={draft.areaUsable}
                size={7}
                onChange={(e) => setDraft({ ...draft, areaUsable: e.target.value })}
              />
            </label>
            <label>
              Price excl. VAT
              <input
                type="number"
                value={draft.basePriceNet}
                size={10}
                onChange={(e) => setDraft({ ...draft, basePriceNet: e.target.value })}
              />
            </label>
            <button
              className="btn"
              type="button"
              disabled={
                draft.code.trim() === '' ||
                draft.unitTypeId === '' ||
                draft.areaUsable === '' ||
                draft.basePriceNet === ''
              }
              onClick={() =>
                void (async () => {
                  setError(null);

                  try {
                    await api.createUnit(projectId, {
                      code: draft.code.trim(),
                      unitTypeId: draft.unitTypeId,
                      areaUsable: Number(draft.areaUsable),
                      basePriceNet: Number(draft.basePriceNet),
                      crt: null,
                      building: draft.building.trim() === '' ? null : draft.building.trim(),
                      stair: null,
                      floorLevel: draft.floorLevel === '' ? null : Number(draft.floorLevel),
                      orientation: null,
                      areaBuilt: null,
                      areaBalcony: null,
                      cesiunePriceNet: null,
                      reducedVatEligible: false,
                      adminNotes: null,
                    });

                    setDraft({ ...draft, code: '', areaUsable: '', basePriceNet: '' });
                    setAdding(false);
                    onInventoryChanged();
                  } catch (e) {
                    if (e instanceof SessionExpired) onSessionExpired();
                    else setError(e instanceof ApiError ? e.message : 'Could not add that apartment.');
                  }
                })()
              }
            >
              Add
            </button>
          </div>
        </section>
      )}

      {error && (
        <div className="banner error" role="alert">
          {error}
          <button onClick={() => setError(null)}>Dismiss</button>
        </div>
      )}

      <div className="grid-wrap">
        <table className="grid">
          <thead>
            <tr>
              {COLUMNS.map((c) => (
                <th key={String(c.key)} style={{ width: c.width }}>
                  {c.label}
                </th>
              ))}
              {canEdit && <th style={{ width: 150 }} />}
            </tr>
          </thead>
          <tbody>
            {visible.map((unit) => (
              <tr key={unit.id}>
                {COLUMNS.map((c) => (
                  <td key={String(c.key)} className={NUMERIC.has(c.key) ? 'num' : undefined}>
                    {render(unit, c)}
                  </td>
                ))}
                {canEdit && (
                  <td>
                    <button
                      className="btn-small quiet"
                      type="button"
                      disabled={unit.state !== 'Available' && unit.state !== 'Withdrawn'}
                      title={
                        unit.state === 'Available' || unit.state === 'Withdrawn'
                          ? undefined
                          : `${unit.code} is ${unit.state.toLowerCase()}, so it is already off the market.`
                      }
                      onClick={() => void withdraw(unit, unit.state !== 'Withdrawn')}
                    >
                      {unit.state === 'Withdrawn' ? 'Put back' : 'Withdraw'}
                    </button>{' '}
                    {/* Only ever an apartment nobody has touched. A real one that is off the
                        market is withdrawn, which keeps it and its history. */}
                    <button
                      className="btn-small danger"
                      type="button"
                      disabled={unit.state !== 'Available' && unit.state !== 'Withdrawn'}
                      onClick={() => {
                        if (!window.confirm(`Delete ${unit.code}? This cannot be undone.`)) return;

                        void (async () => {
                          setError(null);

                          try {
                            await api.deleteUnit(unit.id);
                            setUnits((all) => all.filter((u) => u.id !== unit.id));
                            onInventoryChanged();
                          } catch (e) {
                            if (e instanceof SessionExpired) onSessionExpired();
                            else setError(e instanceof ApiError ? e.message : 'Could not delete that apartment.');
                          }
                        })();
                      }}
                    >
                      Delete
                    </button>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>

        {/* A project can now exist before its apartments do, so "nothing matches that filter"
            would be a confusing answer to a project that simply has nothing in it yet. */}
        {visible.length === 0 && (
          <p className="empty">
            {units.length === 0
              ? 'No apartments here yet. Add them from the Import tab.'
              : 'Nothing matches that filter.'}
          </p>
        )}
      </div>
    </>
  );
}
