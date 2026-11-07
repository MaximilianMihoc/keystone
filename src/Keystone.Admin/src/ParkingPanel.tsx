import { useCallback, useEffect, useState } from 'react';
import {
  api,
  ApiError,
  SessionExpired,
  PARKING_SPACE_KINDS,
  type ParkingPrice,
  type ParkingResponse,
  type ParkingSpaceKind,
} from './api';

interface Props {
  projectId: string;
  canEdit: boolean;
  onSessionExpired: () => void;
}

const KIND_LABELS: Record<ParkingSpaceKind, string> = {
  AboveGroundUncovered: 'Above-ground, uncovered',
  AboveGroundCovered: 'Above-ground, covered',
  Underground: 'Underground',
};

/** Signed levels read badly as bare numbers next to a price. */
function levelLabel(level: number): string {
  if (level === 0) return 'Ground';
  return level < 0 ? `Level -${Math.abs(level)}` : `Level ${level}`;
}

/**
 * The car park: what a space costs, and which bays exist.
 *
 * Spaces are entered here rather than imported, because the operator's sheets price parking per
 * apartment and never enumerate the bays. They are added a range at a time, which is how a car
 * park is actually described.
 */
export function ParkingPanel({ projectId, canEdit, onSessionExpired }: Props) {
  const [data, setData] = useState<ParkingResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  const [prefix, setPrefix] = useState('A');
  const [from, setFrom] = useState(1);
  const [to, setTo] = useState(10);
  const [kind, setKind] = useState<ParkingSpaceKind>('Underground');
  const [level, setLevel] = useState(-1);

  const load = useCallback(async () => {
    try {
      setData(await api.parking(projectId));
      setError(null);
    } catch (e) {
      if (e instanceof SessionExpired) onSessionExpired();
      else setError(e instanceof ApiError ? e.message : 'Could not load the car park.');
    }
  }, [projectId, onSessionExpired]);

  useEffect(() => {
    void load();
  }, [load]);

  const run = useCallback(
    async (action: () => Promise<unknown>) => {
      setBusy(true);
      setError(null);
      setNotice(null);

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
    return error ? <p className="banner error">{error}</p> : <p className="empty">Loading parking…</p>;
  }

  const free = data.spaces.filter((s) => s.state === 'Available').length;

  return (
    <>
      {error && <p className="banner error">{error}</p>}
      {notice && <p className="banner">{notice}</p>}

      <div className="project-head">
        <div className="tallies">
          <Tally label="Free" value={free} />
          <Tally label="Spaces" value={data.spaces.length} />
          <Tally label="Max per sale" value={data.maxPerReservation} />
        </div>
      </div>

      {canEdit && (
        <>
          <section className="panel">
            <h2>How many spaces one buyer may take</h2>
            <p className="hint">
              Zero offers no parking with an apartment, whatever the car park holds.
            </p>
            <div className="row">
              <input
                type="number"
                min={0}
                max={10}
                defaultValue={data.maxPerReservation}
                disabled={busy}
                onBlur={(e) =>
                  void run(() =>
                    api.setMaxParkingPerReservation(projectId, Number(e.target.value)),
                  )
                }
              />
            </div>
          </section>

          <section className="panel">
            <h2>Prices</h2>
            <p className="hint">
              A kind with no price is not for sale. Repricing never moves a reservation that is
              already agreed — those carry their own snapshot.
            </p>

            {PARKING_SPACE_KINDS.map((k) => {
              const current = data.prices.find((p) => p.kind === k);

              return (
                <PriceRow
                  key={k}
                  kind={k}
                  price={current}
                  busy={busy}
                  onSave={(price) => void run(() => api.setParkingPrice(projectId, price))}
                />
              );
            })}
          </section>

          <section className="panel">
            <h2>Add or remove spaces</h2>
            <p className="hint">
              A range at a time, the way a car park is laid out. Adding skips codes already in
              use; removing keeps any space somebody is holding or has bought, and names it.
            </p>
            <div className="row">
              <label>
                Prefix
                <input value={prefix} onChange={(e) => setPrefix(e.target.value)} size={4} />
              </label>
              <label>
                From
                <input
                  type="number"
                  value={from}
                  onChange={(e) => setFrom(Number(e.target.value))}
                  size={4}
                />
              </label>
              <label>
                To
                <input
                  type="number"
                  value={to}
                  onChange={(e) => setTo(Number(e.target.value))}
                  size={4}
                />
              </label>
              <label>
                Kind
                <select
                  value={kind}
                  onChange={(e) => setKind(e.target.value as ParkingSpaceKind)}
                >
                  {PARKING_SPACE_KINDS.map((k) => (
                    <option key={k} value={k}>
                      {KIND_LABELS[k]}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                Level
                <input
                  type="number"
                  value={level}
                  onChange={(e) => setLevel(Number(e.target.value))}
                  size={3}
                />
              </label>
              <button
                className="btn"
                type="button"
                disabled={busy}
                onClick={() =>
                  void run(async () => {
                    const result = await api.addParkingSpaces(projectId, {
                      prefix, from, to, kind, level,
                    });

                    // Skipped codes are the whole reason the range is forgiving; saying
                    // nothing about them looks like they were added.
                    if (result.skipped.length > 0) {
                      setNotice(
                        `Added ${result.created}. Already in use: ${result.skipped.join(', ')}.`,
                      );
                    }
                  })
                }
              >
                Add
              </button>

              {/* Same range, the other direction: a mistyped A01–A100 is undone here rather
                  than a hundred times in the grid. */}
              <button
                className="btn ghost"
                type="button"
                disabled={busy}
                onClick={() =>
                  void run(async () => {
                    const result = await api.removeParkingSpaces(projectId, prefix, from, to);

                    setNotice(
                      `Removed ${result.removed}.` +
                        (result.kept.length > 0
                          ? ` In use, kept: ${result.kept.join(', ')}.`
                          : ''),
                    );
                  })
                }
              >
                Remove
              </button>
            </div>
          </section>
        </>
      )}

      {data.spaces.length === 0 ? (
        <p className="empty">No parking spaces yet. Nothing is offered to buyers until there are.</p>
      ) : (
        <table className="grid">
          <thead>
            <tr>
              <th>Space</th>
              <th>Kind</th>
              <th>Level</th>
              <th>State</th>
              <th>Taken with</th>
              <th>Notes</th>
              {canEdit && <th aria-label="Remove" />}
            </tr>
          </thead>
          <tbody>
            {data.spaces.map((space) => {
              // A space somebody is holding or has bought describes something they were
              // quoted, so only its note stays open. The server refuses the rest anyway; this
              // is so an admin sees why rather than being told after the fact.
              const free = space.state === 'Available';
              const editable = canEdit && free;

              return (
                <tr key={space.id}>
                  <td>
                    {editable ? (
                      <input
                        className="cell-in code-in"
                        defaultValue={space.code}
                        disabled={busy}
                        onBlur={(e) => {
                          if (e.target.value === space.code) return;
                          void run(() =>
                            api.editParkingSpace(space.id, { code: e.target.value }),
                          );
                        }}
                      />
                    ) : (
                      space.code
                    )}
                  </td>
                  <td>
                    {editable ? (
                      <select
                        className="cell-in"
                        value={space.kind}
                        disabled={busy}
                        onChange={(e) =>
                          void run(() =>
                            api.editParkingSpace(space.id, {
                              kind: e.target.value as ParkingSpaceKind,
                            }),
                          )
                        }
                      >
                        {PARKING_SPACE_KINDS.map((k) => (
                          <option key={k} value={k}>
                            {KIND_LABELS[k]}
                          </option>
                        ))}
                      </select>
                    ) : (
                      KIND_LABELS[space.kind]
                    )}
                  </td>
                  <td className="num">
                    {editable ? (
                      <input
                        type="number"
                        className="cell-in level-in num"
                        defaultValue={space.level}
                        disabled={busy}
                        onBlur={(e) => {
                          const next = Number(e.target.value);
                          if (next === space.level) return;
                          void run(() => api.editParkingSpace(space.id, { level: next }));
                        }}
                      />
                    ) : (
                      levelLabel(space.level)
                    )}
                  </td>
                  <td>
                    <span className={`state state-${space.state.toLowerCase()}`}>{space.state}</span>
                  </td>
                  <td>{space.heldForUnit ?? '—'}</td>
                  <td>
                    {canEdit ? (
                      <input
                        className="cell-in"
                        defaultValue={space.adminNotes ?? ''}
                        placeholder="—"
                        disabled={busy}
                        onBlur={(e) => {
                          if (e.target.value === (space.adminNotes ?? '')) return;
                          void run(() =>
                            api.editParkingSpace(space.id, { adminNotes: e.target.value }),
                          );
                        }}
                      />
                    ) : (
                      (space.adminNotes ?? '—')
                    )}
                  </td>
                  {canEdit && (
                    <td>
                      <button
                        className="btn-small danger"
                        type="button"
                        disabled={busy || !free}
                        title={
                          free
                            ? `Remove ${space.code}`
                            : `${space.code} is ${space.state.toLowerCase()}${
                                space.heldForUnit ? ` with ${space.heldForUnit}` : ''
                              }`
                        }
                        onClick={() => void run(() => api.removeParkingSpace(space.id))}
                      >
                        ✕
                      </button>
                    </td>
                  )}
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </>
  );
}

function PriceRow({
  kind,
  price,
  busy,
  onSave,
}: {
  kind: ParkingSpaceKind;
  price: ParkingPrice | undefined;
  busy: boolean;
  onSave: (price: ParkingPrice) => void;
}) {
  const [priceNet, setPriceNet] = useState(price?.priceNet ?? 0);
  const [depositNet, setDepositNet] = useState(price?.depositNet ?? 0);

  return (
    <div className="row">
      <span className="row-label">{KIND_LABELS[kind]}</span>
      <label>
        Price
        <input
          type="number"
          className="num"
          value={priceNet}
          onChange={(e) => setPriceNet(Number(e.target.value))}
        />
      </label>
      <label>
        Deposit
        <input
          type="number"
          className="num"
          value={depositNet}
          onChange={(e) => setDepositNet(Number(e.target.value))}
        />
      </label>
      <button
        className="btn ghost"
        type="button"
        disabled={busy}
        onClick={() => onSave({ kind, priceNet, depositNet })}
      >
        Save
      </button>
    </div>
  );
}

function Tally({ label, value }: { label: string; value: number }) {
  return (
    <div className="tally">
      <span className="tally-value">{value}</span>
      <span className="tally-label">{label}</span>
    </div>
  );
}
