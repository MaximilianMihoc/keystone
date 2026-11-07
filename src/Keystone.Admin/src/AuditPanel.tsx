import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  api,
  SessionExpired,
  type AuditFilter,
  type AuditKinds,
  type AuditRow,
} from './api';

interface Props {
  onSessionExpired: () => void;
}

const stamp = new Intl.DateTimeFormat('en-GB', {
  day: '2-digit',
  month: 'short',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
});

/** A change to one field, as the two JSON blobs describe it between them. */
interface FieldChange {
  field: string;
  before: string | null;
  after: string | null;
}

/** Either a readable diff, or the raw blobs when the row is not shaped like one. */
type Detail =
  | { kind: 'fields'; changes: FieldChange[] }
  | { kind: 'raw'; before: string | null; after: string | null };

/**
 * A field's value for display, or null where there is nothing to show.
 *
 * A JSON null counts as nothing rather than as the word "null". Services write the whole shape
 * every time, so a cancellation with no reason records `Reason: null` — real in the blob, and
 * noise in a list of what changed.
 */
const show = (value: unknown): string | null =>
  value === null || value === undefined
    ? null
    : typeof value === 'string'
      ? value
      : JSON.stringify(value);

function parse(json: string | null): Record<string, unknown> | null {
  if (!json) return null;

  try {
    const parsed: unknown = JSON.parse(json);

    // Arrays and bare values are real — the import writes a list of apartment codes — and
    // they have no fields to line up, so they belong in the raw view.
    return parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)
      ? (parsed as Record<string, unknown>)
      : null;
  } catch {
    return null;
  }
}

/**
 * Lines the two blobs up field by field.
 *
 * Falls back to raw whenever the pair is not two flat objects. Guessing at a shape and showing
 * a confident diff of it would be worse than showing what was actually stored: this is the
 * record people reach for when they distrust what the system says happened.
 */
export function detailOf(before: string | null, after: string | null): Detail {
  const b = parse(before);
  const a = parse(after);

  if (!b && !a) return { kind: 'raw', before, after };

  const fields = [...new Set([...Object.keys(b ?? {}), ...Object.keys(a ?? {})])].sort();

  const changes = fields
    .map((field) => ({
      field,
      before: b ? show(b[field]) : null,
      after: a ? show(a[field]) : null,
    }))
    .filter((c) => c.before !== c.after);

  return changes.length > 0
    ? { kind: 'fields', changes }
    : { kind: 'raw', before, after };
}

/** Adds a day, so a "to" date the user picked includes that whole day. */
function dayAfter(date: string): string {
  const d = new Date(`${date}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + 1);
  return d.toISOString();
}

/**
 * The filterable log the build spec asks for, and the answer to "who released that hold".
 *
 * Admin only, and read-only: the table is append-only by design, so there is nothing here that
 * changes a row. Filtering happens on the server — the log outlives every other list in the
 * system, and a page that could only search the most recent screenful would send people back
 * to the SQL prompt the runbook documents.
 */
export function AuditPanel({ onSessionExpired }: Props) {
  const [rows, setRows] = useState<AuditRow[] | null>(null);
  const [kinds, setKinds] = useState<AuditKinds | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState<string | null>(null);

  const [entityType, setEntityType] = useState('');
  const [action, setAction] = useState('');
  const [actorType, setActorType] = useState('');
  const [actor, setActor] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [take, setTake] = useState(50);

  const fail = useCallback(
    (e: unknown) => {
      if (e instanceof SessionExpired) onSessionExpired();
      else setError((e as Error).message);
    },
    [onSessionExpired],
  );

  const filter = useMemo<AuditFilter>(
    () => ({
      entityType,
      action,
      actorType,
      actor,
      from: from ? `${from}T00:00:00Z` : undefined,
      to: to ? dayAfter(to) : undefined,
      take,
    }),
    [entityType, action, actorType, actor, from, to, take],
  );

  const reload = useCallback(async () => {
    try {
      setRows(await api.audit(filter));
    } catch (e) {
      fail(e);
    }
  }, [filter, fail]);

  // The actor box is typed into, so the same debounce the prospects list uses.
  useEffect(() => {
    const timer = setTimeout(() => void reload(), 300);
    return () => clearTimeout(timer);
  }, [reload]);

  useEffect(() => {
    void (async () => {
      try {
        setKinds(await api.auditKinds());
      } catch (e) {
        fail(e);
      }
    })();
  }, [fail]);

  // The actions a chosen entity type actually has, so the two filters cannot be combined into
  // something that matches nothing. Narrowed on the recorded pairing rather than the action's
  // prefix: floor.created and pricing.created are both filed under `project`, and a prefix
  // test would hide them the moment somebody filtered to it.
  const actions = useMemo(() => {
    const pairs = kinds?.actions ?? [];
    const wanted = entityType ? pairs.filter((a) => a.entityType === entityType) : pairs;

    return [...new Set(wanted.map((a) => a.action))].sort();
  }, [kinds, entityType]);

  const narrowed =
    entityType !== '' || action !== '' || actorType !== '' || actor !== '' || from !== '' || to !== '';

  const clear = () => {
    setEntityType('');
    setAction('');
    setActorType('');
    setActor('');
    setFrom('');
    setTo('');
  };

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
        <select
          className="project-picker"
          value={entityType}
          onChange={(e) => {
            setEntityType(e.target.value);
            setAction('');
          }}
        >
          <option value="">Anything</option>
          {kinds?.entityTypes.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>

        <select
          className="project-picker"
          value={action}
          onChange={(e) => setAction(e.target.value)}
        >
          <option value="">Any action</option>
          {actions.map((a) => (
            <option key={a} value={a}>
              {a}
            </option>
          ))}
        </select>

        <select
          className="project-picker"
          value={actorType}
          onChange={(e) => setActorType(e.target.value)}
        >
          <option value="">Anyone</option>
          {kinds?.actorTypes.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>

        <input
          className="search"
          placeholder="Who, or part of their address…"
          value={actor}
          onChange={(e) => setActor(e.target.value)}
        />

        <label className="row">
          <span>From</span>
          <input type="date" value={from} max={to || undefined} onChange={(e) => setFrom(e.target.value)} />
        </label>

        <label className="row">
          <span>To</span>
          <input type="date" value={to} min={from || undefined} onChange={(e) => setTo(e.target.value)} />
        </label>

        <select
          className="project-picker"
          value={take}
          onChange={(e) => setTake(Number(e.target.value))}
        >
          {[50, 100, 200, 500].map((n) => (
            <option key={n} value={n}>
              last {n}
            </option>
          ))}
        </select>

        {narrowed && (
          <button className="link" type="button" onClick={clear}>
            Clear
          </button>
        )}

        <span className="count">
          {rows === null ? 'Loading…' : `${rows.length} ${rows.length === 1 ? 'entry' : 'entries'}`}
          {rows !== null && rows.length === take && ' (capped)'}
        </span>
      </div>

      {rows !== null && rows.length === 0 && (
        <p className="empty">
          {narrowed
            ? 'Nothing in the log matches that.'
            : 'The log is empty. Every state change writes to it, so this fills up as the system is used.'}
        </p>
      )}

      {rows !== null && rows.length > 0 && (
        <div className="grid-wrap">
          <table className="grid">
            <thead>
              <tr>
                <th>When</th>
                <th>Who</th>
                <th>Action</th>
                <th>Entity</th>
                <th>Changed</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row, i) => {
                // The table has no id of its own — an append-only log needs none — so a row is
                // identified by the things that cannot repeat together.
                const key = `${row.at}-${row.entityId}-${row.action}-${i}`;
                const showing = open === key;

                return (
                  <Entry
                    key={key}
                    row={row}
                    showing={showing}
                    onToggle={() => setOpen(showing ? null : key)}
                    onFilterEntity={() => {
                      setEntityType(row.entityType);
                      setAction('');
                    }}
                  />
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

function Entry({
  row,
  showing,
  onToggle,
  onFilterEntity,
}: {
  row: AuditRow;
  showing: boolean;
  onToggle: () => void;
  onFilterEntity: () => void;
}) {
  const detail = useMemo(() => detailOf(row.before, row.after), [row.before, row.after]);

  const summary =
    detail.kind === 'fields'
      ? detail.changes.map((c) => c.field).join(', ')
      : row.before || row.after
        ? 'details'
        : '—';

  return (
    <>
      <tr className={`pickable${showing ? ' pickable-on' : ''}`} onClick={onToggle}>
        <td>{stamp.format(new Date(row.at))}</td>
        <td className="audit-who">
          {row.actor}
          {row.actorType !== 'staff' && <span className="muted"> · {row.actorType}</span>}
        </td>
        <td>{row.action}</td>
        <td>
          <button
            className="link"
            type="button"
            title={`Show only ${row.entityType} entries`}
            onClick={(e) => {
              e.stopPropagation();
              onFilterEntity();
            }}
          >
            {row.entityType}
          </button>
        </td>
        <td className="muted">{summary}</td>
      </tr>

      {showing && (
        <tr className="audit-detail">
          <td colSpan={5}>
            {detail.kind === 'fields' ? (
              <dl className="facts">
                {detail.changes.map((c) => (
                  <div key={c.field} className="audit-change">
                    <dt>{c.field}</dt>
                    <dd>
                      {c.before !== null && <span className="audit-was">{c.before}</span>}
                      {c.before !== null && c.after !== null && <span className="muted"> → </span>}
                      {c.after !== null && <span>{c.after}</span>}
                      {c.after === null && <span className="muted"> removed</span>}
                    </dd>
                  </div>
                ))}
              </dl>
            ) : (
              <div className="audit-raw">
                {row.before && (
                  <div>
                    <span className="muted">before</span>
                    <pre>{row.before}</pre>
                  </div>
                )}
                {row.after && (
                  <div>
                    <span className="muted">after</span>
                    <pre>{row.after}</pre>
                  </div>
                )}
                {!row.before && !row.after && (
                  <span className="muted">This action recorded no detail.</span>
                )}
              </div>
            )}

            <p className="muted audit-id">{row.entityId}</p>
          </td>
        </tr>
      )}
    </>
  );
}
