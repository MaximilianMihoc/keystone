import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  api,
  SessionExpired,
  type Pipeline,
  type Reservation,
  type ReservationEdit,
  type ReservationsResponse,
} from './api';

interface Props {
  projectId: string;
  canEdit: boolean;
  onSessionExpired: () => void;
}

const money = new Intl.NumberFormat('en-GB', {
  style: 'currency',
  currency: 'EUR',
  maximumFractionDigits: 0,
});

const day = new Intl.DateTimeFormat('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });

/** How long a sale has sat where it is. The board's real question. */
function daysAt(iso: string): number {
  return Math.floor((Date.now() - Date.parse(iso)) / 86_400_000);
}

function showDate(iso: string | null): string {
  return iso ? day.format(new Date(iso)) : '—';
}

/** An ISO instant as the value a datetime-local input wants, in the viewer's own time. */
function forInput(iso: string | null): string {
  if (!iso) return '';

  const at = new Date(iso);
  const local = new Date(at.getTime() - at.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
}

export function PipelineBoard({ projectId, canEdit, onSessionExpired }: Props) {
  const [pipeline, setPipeline] = useState<Pipeline | null>(null);
  const [data, setData] = useState<ReservationsResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [startUnitId, setStartUnitId] = useState('');
  const [startEmail, setStartEmail] = useState('');
  const [showCancelled, setShowCancelled] = useState(false);

  const fail = useCallback(
    (e: unknown) => {
      if (e instanceof SessionExpired) onSessionExpired();
      else setError((e as Error).message);
    },
    [onSessionExpired],
  );

  const reload = useCallback(async () => {
    try {
      setData(await api.reservations(projectId));
    } catch (e) {
      fail(e);
    }
  }, [projectId, fail]);

  useEffect(() => {
    void (async () => {
      try {
        setPipeline(await api.pipeline());
      } catch (e) {
        fail(e);
      }
    })();
  }, [fail]);

  useEffect(() => {
    setData(null);
    setSelectedId(null);
    void reload();
  }, [reload]);

  const byStage = useMemo(() => {
    const map: Record<string, Reservation[]> = {};
    for (const r of data?.reservations ?? []) {
      (map[r.state] ??= []).push(r);
    }
    return map;
  }, [data]);

  const selected = data?.reservations.find((r) => r.id === selectedId) ?? null;

  const move = useCallback(
    async (reservation: Reservation, state: string, reason?: string) => {
      setBusy(reservation.id);
      setError(null);

      try {
        // Not optimistic. A move can be refused for reasons staff need to read — the notary
        // date is missing, the unit moved underneath — and a card that slid across and then
        // slid back would say less than the sentence does.
        await api.moveReservation(reservation.id, state, reason);
        await reload();
      } catch (e) {
        fail(e);
      } finally {
        setBusy(null);
      }
    },
    [reload, fail],
  );

  const edit = useCallback(
    async (reservation: Reservation, change: ReservationEdit) => {
      setError(null);

      try {
        await api.editReservation(reservation.id, change);
        await reload();
      } catch (e) {
        fail(e);
      }
    },
    [reload, fail],
  );

  const start = useCallback(async () => {
    if (!startUnitId) return;

    const candidate = data?.candidates.find((c) => c.id === startUnitId);
    const needsBuyer = candidate?.state !== 'Held';

    setBusy(startUnitId);
    setError(null);

    try {
      await api.startReservation(
        startUnitId,
        needsBuyer ? { email: startEmail.trim() } : undefined,
      );

      setStartUnitId('');
      setStartEmail('');
      await reload();
    } catch (e) {
      fail(e);
    } finally {
      setBusy(null);
    }
  }, [startUnitId, startEmail, data, reload, fail]);

  if (!pipeline || !data) {
    return <p className="empty">Loading the pipeline…</p>;
  }

  const cancelled = data.reservations.filter((r) => r.state === pipeline.cancelled);
  const candidate = data.candidates.find((c) => c.id === startUnitId);
  const buyerNeeded = Boolean(candidate) && candidate!.state !== 'Held';

  return (
    <>
      {error && (
        <p className="banner error">
          {error}
          <button className="link" type="button" onClick={() => setError(null)}>
            Dismiss
          </button>
        </p>
      )}

      {canEdit && data.candidates.length > 0 && (
        <div className="toolbar start-row">
          <select
            className="project-picker"
            value={startUnitId}
            onChange={(e) => setStartUnitId(e.target.value)}
          >
            <option value="">Start a reservation on…</option>
            {data.candidates.map((c) => (
              <option key={c.id} value={c.id}>
                {c.code} — {c.heldBy ?? `marked ${c.state.toLowerCase()}, no record`}
              </option>
            ))}
          </select>

          {buyerNeeded && (
            <input
              className="search"
              style={{ width: 260 }}
              type="email"
              placeholder="Buyer's email address"
              value={startEmail}
              onChange={(e) => setStartEmail(e.target.value)}
            />
          )}

          <button
            className="btn btn-small"
            type="button"
            disabled={!startUnitId || (buyerNeeded && !startEmail.trim()) || busy !== null}
            onClick={() => void start()}
          >
            Start
          </button>

          <span className="count">
            {data.candidates.length} unit{data.candidates.length === 1 ? '' : 's'} off the market
            with no reservation behind them
          </span>
        </div>
      )}

      <div className="board">
        {pipeline.stages.map((stage) => {
          const cards = byStage[stage.state] ?? [];

          return (
            <section className="column" key={stage.state}>
              <header className="column-head" title={stage.sourceColumn ?? undefined}>
                <span className="column-label">{stage.label}</span>
                <span className="column-count">{cards.length}</span>
              </header>

              <div className="column-body">
                {cards.map((r) => (
                  <Card
                    key={r.id}
                    reservation={r}
                    stages={pipeline.stages.map((s) => s.state)}
                    canEdit={canEdit}
                    busy={busy === r.id}
                    selected={r.id === selectedId}
                    onOpen={() => setSelectedId(r.id === selectedId ? null : r.id)}
                    onMove={(state) => void move(r, state)}
                  />
                ))}
              </div>
            </section>
          );
        })}
      </div>

      {cancelled.length > 0 && (
        <div className="cancelled">
          <button className="link" type="button" onClick={() => setShowCancelled(!showCancelled)}>
            {showCancelled ? 'Hide' : 'Show'} {cancelled.length} cancelled
          </button>

          {showCancelled && (
            <ul className="cancelled-list">
              {cancelled.map((r) => (
                <li key={r.id}>
                  <strong>{r.unitCode}</strong> · {r.buyerEmail} · {showDate(r.cancelledAt)}
                  {r.cancellationReason ? ` · ${r.cancellationReason}` : ''}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      {data.reservations.length === 0 && (
        <p className="empty">
          Nothing in the pipeline yet. A reservation starts from a hold, or from a unit the
          inventory already calls reserved.
        </p>
      )}

      {selected && (
        <Detail
          reservation={selected}
          pipeline={pipeline}
          canEdit={canEdit}
          onClose={() => setSelectedId(null)}
          onEdit={(change) => void edit(selected, change)}
          onMove={(state, reason) => void move(selected, state, reason)}
        />
      )}
    </>
  );
}

function Card({
  reservation,
  stages,
  canEdit,
  busy,
  selected,
  onOpen,
  onMove,
}: {
  reservation: Reservation;
  stages: string[];
  canEdit: boolean;
  busy: boolean;
  selected: boolean;
  onOpen: () => void;
  onMove: (state: string) => void;
}) {
  const at = stages.indexOf(reservation.state);
  const waited = daysAt(reservation.stateChangedAt);

  return (
    <article className={`card${selected ? ' card-selected' : ''}${busy ? ' card-busy' : ''}`}>
      <button className="card-open" type="button" onClick={onOpen}>
        <span className="card-code">{reservation.unitCode}</span>
        <span className="card-total">{money.format(reservation.totalGross)}</span>
        <span className="card-buyer">{reservation.buyerName ?? reservation.buyerEmail}</span>
        <span className={`card-waited${waited >= 14 ? ' stale' : ''}`}>
          {waited === 0 ? 'today' : `${waited}d here`}
          {reservation.contractNumber ? ` · ${reservation.contractNumber}` : ''}
        </span>
      </button>

      {canEdit && (
        <div className="card-move">
          <button
            className="step"
            type="button"
            disabled={busy || at <= 0}
            title="Back one stage"
            onClick={() => onMove(stages[at - 1])}
          >
            &lt;
          </button>
          <button
            className="step"
            type="button"
            disabled={busy || at < 0 || at >= stages.length - 1}
            title="On one stage"
            onClick={() => onMove(stages[at + 1])}
          >
            &gt;
          </button>
        </div>
      )}
    </article>
  );
}

function Detail({
  reservation,
  pipeline,
  canEdit,
  onClose,
  onEdit,
  onMove,
}: {
  reservation: Reservation;
  pipeline: Pipeline;
  canEdit: boolean;
  onClose: () => void;
  onEdit: (change: ReservationEdit) => void;
  onMove: (state: string, reason?: string) => void;
}) {
  const [reason, setReason] = useState('');
  const [cancelling, setCancelling] = useState(false);

  // Reset the cancellation form when a different card is opened.
  useEffect(() => {
    setReason('');
    setCancelling(false);
  }, [reservation.id]);

  // Each stage names its own date field, so this list follows the pipeline rather than
  // repeating it.
  const dates = pipeline.stages
    .filter((s) => s.dateField)
    .map((s) => ({
      label: s.label,
      value: (reservation as unknown as Record<string, string | null>)[s.dateField!] ?? null,
    }));

  return (
    <aside className="drawer">
      <header className="drawer-head">
        <div>
          <h2>{reservation.unitCode}</h2>
          <p className="drawer-sub">
            {reservation.unitType}
            {reservation.floorLevel !== null ? ` · floor ${reservation.floorLevel}` : ''}
          </p>
        </div>
        <button className="link" type="button" onClick={onClose}>
          Close
        </button>
      </header>

      <dl className="facts">
        <dt>Buyer</dt>
        <dd>
          {reservation.buyerName ?? '—'}
          <br />
          <span className="muted">{reservation.buyerEmail}</span>
        </dd>

        <dt>Agreed price</dt>
        <dd>
          {money.format(reservation.totalGross)}
          <span className="muted"> incl. VAT, frozen on the day</span>
        </dd>

        <dt>Reservation fee</dt>
        <dd>{reservation.reservationFee ? money.format(reservation.reservationFee) : '—'}</dd>

        <dt>Buyer type</dt>
        <dd>
          <select
            className="project-picker"
            value={reservation.buyerType}
            disabled={!canEdit || reservation.state !== 'PendingFee'}
            title={
              reservation.state === 'PendingFee'
                ? 'Sets the VAT rate, so changing it redoes the price'
                : 'The VAT rate is part of the agreed price and cannot change now'
            }
            onChange={(e) => onEdit({ buyerType: e.target.value })}
          >
            <option value="Individual">Individual</option>
            <option value="Company">Company</option>
          </select>
        </dd>
      </dl>

      <h3>Paperwork</h3>

      <label className="field">
        <span>Contract number</span>
        <input
          defaultValue={reservation.contractNumber ?? ''}
          disabled={!canEdit}
          onBlur={(e) =>
            e.target.value !== (reservation.contractNumber ?? '') &&
            onEdit({ contractNumber: e.target.value })
          }
        />
      </label>

      <label className="field">
        <span>Notary appointment</span>
        <input
          type="datetime-local"
          defaultValue={forInput(reservation.notaryAppointmentAt)}
          disabled={!canEdit}
          onBlur={(e) =>
            e.target.value !== forInput(reservation.notaryAppointmentAt) &&
            e.target.value !== '' &&
            onEdit({ notaryAppointmentAt: new Date(e.target.value).toISOString() })
          }
        />
      </label>

      <label className="field">
        <span>Where (locatie cesiune)</span>
        <input
          defaultValue={reservation.notaryLocation ?? ''}
          disabled={!canEdit}
          onBlur={(e) =>
            e.target.value !== (reservation.notaryLocation ?? '') &&
            onEdit({ notaryLocation: e.target.value })
          }
        />
      </label>

      <label className="field">
        <span>Notes</span>
        <textarea
          rows={3}
          defaultValue={reservation.internalNotes ?? ''}
          disabled={!canEdit}
          onBlur={(e) =>
            e.target.value !== (reservation.internalNotes ?? '') &&
            onEdit({ internalNotes: e.target.value })
          }
        />
      </label>

      <h3>Progress</h3>

      <dl className="facts">
        {dates.map((d) => (
          <div key={d.label} className="fact-row">
            <dt>{d.label}</dt>
            <dd>{showDate(d.value)}</dd>
          </div>
        ))}
      </dl>

      {canEdit && reservation.state !== pipeline.cancelled && (
        <>
          <h3>Move to</h3>
          <select
            className="project-picker full"
            value=""
            onChange={(e) => e.target.value && onMove(e.target.value)}
          >
            <option value="">Choose a stage…</option>
            {pipeline.stages
              .filter((s) => s.state !== reservation.state)
              .map((s) => (
                <option key={s.state} value={s.state}>
                  {s.label}
                </option>
              ))}
          </select>

          {cancelling ? (
            <div className="cancel-form">
              <input
                placeholder="Why is it being cancelled?"
                value={reason}
                onChange={(e) => setReason(e.target.value)}
              />
              <button
                className="btn btn-small danger"
                type="button"
                disabled={!reason.trim()}
                onClick={() => onMove(pipeline.cancelled, reason.trim())}
              >
                Cancel it
              </button>
              <button className="link" type="button" onClick={() => setCancelling(false)}>
                Keep it
              </button>
            </div>
          ) : (
            <button className="link danger" type="button" onClick={() => setCancelling(true)}>
              Cancel this reservation
            </button>
          )}
        </>
      )}
    </aside>
  );
}
