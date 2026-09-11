import { useCallback, useEffect, useState } from 'react';
import { api, SessionExpired, type PrivacyRequest } from './api';

interface Props {
  onSessionExpired: () => void;
}

const day = new Intl.DateTimeFormat('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });

const when = (value: string) => day.format(new Date(value));

const DAY_MS = 86_400_000;

/**
 * How long is left, in the words somebody would use.
 *
 * Whether a request is late is the server's answer, not this one — clocks drift and the
 * browser's is not the record. This only puts a number on the distance.
 */
function remaining(dueAt: string): string {
  const diff = new Date(dueAt).getTime() - Date.now();

  if (diff < 0) {
    const over = Math.ceil(-diff / DAY_MS);
    return `${over} day${over === 1 ? '' : 's'} over`;
  }

  const left = Math.ceil(diff / DAY_MS);
  return left === 0 ? 'due today' : `${left} day${left === 1 ? '' : 's'} left`;
}

/**
 * Everything somebody has asked for and nobody has answered yet, across every project.
 *
 * A prospect belongs to no project — they reach one only through a hold, a reservation or a
 * queue — and neither does a request, so this is ungated like Prospects and Audit. Admin only,
 * for the reason the endpoints behind it are: these are judgements about people.
 *
 * The per-person view in the Prospects drawer answers "what did they ask for". This answers the
 * one that has a deadline attached: is anything late, and what is closest to being late. Oldest
 * first, because that is the same order.
 */
export function PrivacyPanel({ onSessionExpired }: Props) {
  const [rows, setRows] = useState<PrivacyRequest[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const fail = useCallback(
    (e: unknown) => {
      if (e instanceof SessionExpired) onSessionExpired();
      else setError((e as Error).message);
    },
    [onSessionExpired],
  );

  const reload = useCallback(async () => {
    try {
      // No address: everything still open, oldest first. The same endpoint filtered by email
      // is what the Prospects drawer uses.
      setRows(await api.privacyRequests());
    } catch (e) {
      fail(e);
    }
  }, [fail]);

  useEffect(() => {
    void reload();
  }, [reload]);

  const answer = useCallback(
    async (requestId: string) => {
      setBusy(requestId);

      try {
        await api.markPrivacyRequestAnswered(requestId);
        await reload();
      } catch (e) {
        fail(e);
      } finally {
        setBusy(null);
      }
    },
    [reload, fail],
  );

  const overdue = (rows ?? []).filter((r) => r.overdue).length;

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
        <span className="count">
          {rows === null
            ? 'Loading…'
            : `${rows.length} outstanding${overdue > 0 ? `, ${overdue} overdue` : ''}`}
        </span>

        <div className="spacer" />

        <button className="link" type="button" onClick={() => void reload()}>
          Refresh
        </button>
      </div>

      {rows !== null && rows.length === 0 && (
        <p className="empty">
          Nothing outstanding. A request appears here once somebody logs it against a prospect.
        </p>
      )}

      {rows !== null && rows.length > 0 && (
        <>
          <div className="grid-wrap">
            <table className="grid">
              <thead>
                <tr>
                  <th>Asked for</th>
                  <th>Who</th>
                  <th>Arrived</th>
                  <th>Due</th>
                  <th>Left</th>
                  <th>How it arrived</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={r.id}>
                    <td>{r.kind === 'Access' ? 'Copy of their data' : 'Erasure'}</td>
                    <td>{r.email ?? '—'}</td>
                    <td>{when(r.requestedAt)}</td>
                    <td>{when(r.dueAt)}</td>
                    <td>
                      {/* Late is the only state worth colouring. Everything else is simply in
                          hand, and a wall of red teaches people to stop reading it. */}
                      <span className={r.overdue ? 'state state-withdrawn' : 'state'}>
                        {remaining(r.dueAt)}
                      </span>
                    </td>
                    <td>{r.note ?? '—'}</td>
                    <td>
                      <button
                        className="link"
                        type="button"
                        disabled={busy !== null}
                        onClick={() => void answer(r.id)}
                      >
                        {busy === r.id ? 'Saving…' : 'Mark answered'}
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <p className="hint">
            The month comes from GDPR Article 12(3) and runs from the day the request arrived,
            not the day it was logged. Marking one answered records when it was closed; it does
            not send anything. An erasure carried out from a prospect's drawer closes its own
            request and disappears from here on its own.
          </p>
        </>
      )}
    </>
  );
}
