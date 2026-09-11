import { useCallback, useEffect, useState } from 'react';
import {
  api,
  SessionExpired,
  type Prospect,
  type ProspectDetail,
  type ProspectEdit,
} from './api';

interface Props {
  onSessionExpired: () => void;
}

const day = new Intl.DateTimeFormat('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });

const when = (value: string | null) => (value ? day.format(new Date(value)) : '—');

/**
 * The people on the other side of the sale, and whether they may still reserve.
 *
 * The block is applied automatically — a hold that lapses bars the prospect, which is Hubix's
 * rule against reservations cancelled in series. Lifting it had no way in until this screen, so
 * somebody who cancelled once in good faith stayed barred for good. Admin only: deciding that
 * a person is a serial canceller, or that they are not after all, is a judgement about them.
 */
export function ProspectsPanel({ onSessionExpired }: Props) {
  const [rows, setRows] = useState<Prospect[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [blockedOnly, setBlockedOnly] = useState(false);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const fail = useCallback(
    (e: unknown) => {
      if (e instanceof SessionExpired) onSessionExpired();
      else setError((e as Error).message);
    },
    [onSessionExpired],
  );

  const reload = useCallback(async () => {
    try {
      setRows(await api.prospects(search, blockedOnly));
    } catch (e) {
      fail(e);
    }
  }, [search, blockedOnly, fail]);

  // Searching happens in SQL, so a keystroke is a request. Waiting for a pause in the typing
  // is the difference between one query and one per letter.
  useEffect(() => {
    const timer = setTimeout(() => void reload(), 300);
    return () => clearTimeout(timer);
  }, [reload]);

  const blocked = (rows ?? []).filter((p) => !p.canHold).length;

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

      <div className="toolbar">
        <input
          className="search"
          placeholder="Filter by email, name or phone…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />

        <label className="row">
          <input
            type="checkbox"
            checked={blockedOnly}
            onChange={(e) => setBlockedOnly(e.target.checked)}
          />
          <span>Blocked only</span>
        </label>

        <span className="count">
          {rows === null
            ? 'Loading…'
            : `${rows.length} ${rows.length === 1 ? 'person' : 'people'}${
                blocked > 0 && !blockedOnly ? `, ${blocked} blocked` : ''
              }`}
        </span>
      </div>

      {rows !== null && rows.length === 0 && (
        <p className="empty">
          {search || blockedOnly
            ? 'Nobody here matches that.'
            : 'Nobody has held, reserved or queued for a unit yet.'}
        </p>
      )}

      {rows !== null && rows.length > 0 && (
        <div className="grid-wrap">
          <table className="grid">
            <thead>
              <tr>
                <th>Email</th>
                <th>Name</th>
                <th>Phone</th>
                <th>First seen</th>
                <th>Holding</th>
                <th>Reserving</th>
                <th>Lapsed</th>
                <th>Can hold</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((p) => (
                <tr
                  key={p.id}
                  className={`pickable${p.id === selectedId ? ' pickable-on' : ''}`}
                  onClick={() => setSelectedId(p.id)}
                >
                  <td>{p.email}</td>
                  <td>{p.fullName ?? '—'}</td>
                  <td>{p.phone ?? '—'}</td>
                  <td>{when(p.firstSeen)}</td>
                  <td>{p.activeHolds || '—'}</td>
                  <td>{p.openReservations || '—'}</td>
                  <td>{p.lapsedHoldCount || '—'}</td>
                  <td>
                    {/* Blocked is the one worth seeing across a page of rows; allowed is the
                        unremarkable case and stays the plain muted colour. */}
                    <span className={p.canHold ? 'state' : 'state state-withdrawn'}>
                      {p.canHold ? 'Yes' : 'Blocked'}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {selectedId && (
        <Detail
          key={selectedId}
          prospectId={selectedId}
          onClose={() => setSelectedId(null)}
          onChanged={() => void reload()}
          onFail={fail}
        />
      )}
    </>
  );
}

function Detail({
  prospectId,
  onClose,
  onChanged,
  onFail,
}: {
  prospectId: string;
  onClose: () => void;
  onChanged: () => void;
  onFail: (e: unknown) => void;
}) {
  const [person, setPerson] = useState<ProspectDetail | null>(null);
  const [busy, setBusy] = useState(false);
  const [blocking, setBlocking] = useState(false);
  const [reason, setReason] = useState('');
  const [edit, setEdit] = useState<ProspectEdit | null>(null);

  const load = useCallback(async () => {
    try {
      setPerson(await api.prospect(prospectId));
    } catch (e) {
      onFail(e);
    }
  }, [prospectId, onFail]);

  useEffect(() => {
    void load();
  }, [load]);

  const save = useCallback(async () => {
    if (!edit) return;

    setBusy(true);

    try {
      // Not optimistic, for the same reason the block is not: the server refuses an email
      // somebody else already has, and that sentence is the useful part of the answer.
      await api.editProspect(prospectId, edit);
      await load();
      onChanged();
      setEdit(null);
    } catch (e) {
      onFail(e);
    } finally {
      setBusy(false);
    }
  }, [prospectId, edit, load, onChanged, onFail]);

  const setBlocked = useCallback(
    async (blocked: boolean, why?: string) => {
      setBusy(true);

      try {
        // Not optimistic. The server can refuse — a block with no reason behind it — and the
        // sentence it sends back is what staff need to read.
        await api.setProspectBlocked(prospectId, blocked, why);
        await load();
        onChanged();
        setBlocking(false);
        setReason('');
      } catch (e) {
        onFail(e);
      } finally {
        setBusy(false);
      }
    },
    [prospectId, load, onChanged, onFail],
  );

  if (!person) {
    return (
      <aside className="drawer">
        <p className="empty">Loading…</p>
      </aside>
    );
  }

  return (
    <aside className="drawer">
      <header className="drawer-head">
        <div>
          <h2>{person.fullName ?? person.email}</h2>
          {person.fullName && <p className="drawer-sub">{person.email}</p>}
        </div>
        <button className="link" type="button" onClick={onClose}>
          Close
        </button>
      </header>

      <dl className="facts">
        <dt>Holds</dt>
        <dd>
          {person.canHold ? (
            <>
              Allowed
              {person.lapsedHoldCount > 0 && (
                <span className="muted">
                  {' '}
                  · {person.lapsedHoldCount} lapsed before
                </span>
              )}
            </>
          ) : (
            <>
              <strong>Blocked</strong>
              <br />
              <span className="muted">{person.blockReason ?? 'No reason recorded.'}</span>
            </>
          )}
        </dd>

        <dt>Phone</dt>
        <dd>{person.phone ?? '—'}</dd>

        <dt>Lives in</dt>
        <dd>
          {person.city ?? '—'}
          {/* A blank country means Romania. Saying so beats a dash staff have to interpret. */}
          <span className="muted"> · {person.country ?? 'Romania'}</span>
        </dd>

        <dt>First seen</dt>
        <dd>
          {when(person.firstSeen)}
          {person.source && <span className="muted"> · via {person.source}</span>}
        </dd>

        <dt>Language</dt>
        <dd>{person.locale}</dd>

        <dt>Consent</dt>
        <dd>
          {person.gdprConsentAt ? (
            <>
              {when(person.gdprConsentAt)}
              <br />
              <span className="muted">
                {person.gdprConsentPurpose.join(', ') || 'no purpose recorded'}
                {person.gdprConsentSource ? ` · ${person.gdprConsentSource}` : ''}
              </span>
            </>
          ) : (
            <span className="muted">None recorded.</span>
          )}
        </dd>

        {person.deletionRequestedAt && (
          <>
            <dt>Erasure</dt>
            <dd>Asked to be forgotten on {when(person.deletionRequestedAt)}</dd>
          </>
        )}
      </dl>

      {/* What the buyer typed, corrected. Sales hear the right spelling on the phone, and
          the importer splits a three-word Romanian name wrongly as often as not. */}
      {edit ? (
        <div className="panel">
          <label className="field">
            <span>First name</span>
            <input
              value={edit.firstName ?? ''}
              onChange={(e) => setEdit({ ...edit, firstName: e.target.value || null })}
            />
          </label>
          <label className="field">
            <span>Surname</span>
            <input
              value={edit.surname ?? ''}
              onChange={(e) => setEdit({ ...edit, surname: e.target.value || null })}
            />
          </label>
          <label className="field">
            <span>Email</span>
            <input
              type="email"
              value={edit.email}
              onChange={(e) => setEdit({ ...edit, email: e.target.value })}
            />
          </label>
          <label className="field">
            <span>Phone</span>
            <input
              type="tel"
              value={edit.phone ?? ''}
              onChange={(e) => setEdit({ ...edit, phone: e.target.value || null })}
            />
          </label>
          <label className="field">
            <span>City</span>
            <input
              value={edit.city ?? ''}
              onChange={(e) => setEdit({ ...edit, city: e.target.value || null })}
            />
          </label>
          <label className="field">
            <span>Country</span>
            <input
              placeholder="Romania"
              value={edit.country ?? ''}
              onChange={(e) => setEdit({ ...edit, country: e.target.value || null })}
            />
          </label>

          <button
            className="btn btn-small"
            type="button"
            disabled={busy || !edit.email.trim()}
            onClick={() => void save()}
          >
            Save details
          </button>
          <button className="link" type="button" onClick={() => setEdit(null)}>
            Leave it
          </button>
        </div>
      ) : (
        <div className="panel">
          <button
            className="link"
            type="button"
            disabled={busy}
            onClick={() =>
              setEdit({
                email: person.email,
                firstName: person.firstName,
                surname: person.surname,
                phone: person.phone,
                city: person.city,
                country: person.country,
              })
            }
          >
            Correct their details
          </button>
        </div>
      )}

      <div className="panel">
        {person.canHold ? (
          blocking ? (
            <div className="cancel-form">
              <input
                placeholder="Why are they being blocked?"
                value={reason}
                onChange={(e) => setReason(e.target.value)}
              />
              <button
                className="btn btn-small danger"
                type="button"
                disabled={busy || !reason.trim()}
                onClick={() => void setBlocked(true, reason.trim())}
              >
                Block them
              </button>
              <button className="link" type="button" onClick={() => setBlocking(false)}>
                Leave it
              </button>
            </div>
          ) : (
            <button
              className="link danger"
              type="button"
              disabled={busy}
              onClick={() => setBlocking(true)}
            >
              Block them from holding
            </button>
          )
        ) : (
          <button
            className="btn btn-small"
            type="button"
            disabled={busy}
            title="They will be able to reserve again straight away"
            onClick={() => void setBlocked(false)}
          >
            Allow holds again
          </button>
        )}
      </div>

      <h3>Holds</h3>
      {person.holds.length === 0 ? (
        <p className="muted">None.</p>
      ) : (
        <ul className="queue-list">
          {person.holds.map((h) => (
            <li key={h.id}>
              <span className="queue-who">
                <strong>{h.unitCode}</strong>
                <span className="queue-email">{h.project}</span>
              </span>
              <span className="queue-when">
                {h.releasedAt
                  ? `${h.releaseReason?.toLowerCase() ?? 'released'} ${when(h.releasedAt)}`
                  : `held until ${when(h.expiresAt)}`}
              </span>
            </li>
          ))}
        </ul>
      )}

      <h3>Reservations</h3>
      {person.reservations.length === 0 ? (
        <p className="muted">None.</p>
      ) : (
        <ul className="queue-list">
          {person.reservations.map((r) => (
            <li key={r.id}>
              <span className="queue-who">
                <strong>{r.unitCode}</strong>
                <span className="queue-email">{r.project}</span>
              </span>
              <span className="queue-when">
                {r.state}
                {r.cancelledAt && r.cancellationReason ? ` · ${r.cancellationReason}` : ''}
              </span>
            </li>
          ))}
        </ul>
      )}

      <h3>Waiting list</h3>
      {person.waitlist.length === 0 ? (
        <p className="muted">None.</p>
      ) : (
        <ul className="queue-list">
          {person.waitlist.map((w) => (
            <li key={w.id} className={w.removedAt ? 'told' : undefined}>
              <span className="queue-who">
                <strong>{w.typeName ?? 'Any apartment'}</strong>
                <span className="queue-email">{w.project}</span>
              </span>
              <span className="queue-when">
                {w.removedAt
                  ? `left ${when(w.removedAt)}`
                  : w.notifiedAt
                    ? `told ${when(w.notifiedAt)}`
                    : `joined ${when(w.joinedAt)}`}
              </span>
            </li>
          ))}
        </ul>
      )}
    </aside>
  );
}
