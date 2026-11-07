import { useCallback, useEffect, useState } from 'react';
import {
  api,
  SessionExpired,
  type ImportFieldInfo,
  type ImportOutcome,
  type ImportPreview,
  type ImportProfile,
  type PlannedRow,
} from './api';

interface Props {
  projectId: string;
  canEdit: boolean;
  onSessionExpired: () => void;
  onImported: () => void;
}

/** Shown against each row so the outcome is readable before the numbers are. */
const ACTION_LABEL: Record<PlannedRow['action'], string> = {
  Create: 'new',
  Update: 'changed',
  Unchanged: 'same',
  Skipped: 'left alone',
  Failed: 'cannot read',
};

function show(value: unknown): string {
  if (value === null || value === undefined || value === '') return '—';
  if (typeof value === 'boolean') return value ? 'DA' : 'NU';
  return String(value);
}

export function ImportPanel({ projectId, canEdit, onSessionExpired, onImported }: Props) {
  const [fields, setFields] = useState<ImportFieldInfo[]>([]);
  const [profiles, setProfiles] = useState<ImportProfile[]>([]);
  const [file, setFile] = useState<File | null>(null);
  const [preview, setPreview] = useState<ImportPreview | null>(null);
  const [mapping, setMapping] = useState<Record<string, string>>({});
  const [worksheet, setWorksheet] = useState<string | undefined>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [outcome, setOutcome] = useState<ImportOutcome | null>(null);
  const [profileName, setProfileName] = useState('');

  const fail = useCallback(
    (e: unknown) => {
      if (e instanceof SessionExpired) onSessionExpired();
      else setError((e as Error).message);
    },
    [onSessionExpired],
  );

  useEffect(() => {
    void (async () => {
      try {
        setFields(await api.importFields());
        setProfiles(await api.importProfiles(projectId));
      } catch (e) {
        fail(e);
      }
    })();
  }, [projectId, fail]);

  /** Re-reads the file with whatever mapping is on screen. Nothing is written. */
  const refresh = useCallback(
    async (
      chosen: File,
      options: { mapping?: Record<string, string>; worksheet?: string } = {},
    ) => {
      setBusy(true);
      setError(null);
      setOutcome(null);

      try {
        const next = await api.previewImport(projectId, chosen, options);
        setPreview(next);
        setMapping(next.mapping);
        setWorksheet(next.worksheet);
      } catch (e) {
        fail(e);
        setPreview(null);
      } finally {
        setBusy(false);
      }
    },
    [projectId, fail],
  );

  const choose = useCallback(
    async (chosen: File | null) => {
      setFile(chosen);
      setPreview(null);

      if (!chosen) return;

      // Start from the most recently used saved mapping, if there is one. Re-mapping thirty
      // columns on every upload is how an importer goes unused.
      const remembered = profiles[0];

      await refresh(chosen, {
        mapping: remembered?.columns,
        worksheet: remembered?.worksheetName ?? undefined,
      });
    },
    [profiles, refresh],
  );

  const remap = useCallback(
    async (field: string, letter: string) => {
      if (!file) return;

      const next = { ...mapping };
      if (letter) next[field] = letter;
      else delete next[field];

      setMapping(next);
      await refresh(file, { mapping: next, worksheet });
    },
    [file, mapping, worksheet, refresh],
  );

  const commit = useCallback(async () => {
    if (!file || !preview) return;

    setBusy(true);
    setError(null);

    try {
      const result = await api.commitImport(projectId, file, {
        mapping,
        worksheet,
        hash: preview.plan.fileHash,
      });

      setOutcome(result);
      setPreview(null);
      setFile(null);
      onImported();
    } catch (e) {
      fail(e);
    } finally {
      setBusy(false);
    }
  }, [file, preview, projectId, mapping, worksheet, fail, onImported]);

  const saveProfile = useCallback(async () => {
    if (!profileName.trim()) return;

    try {
      await api.saveImportProfile(projectId, {
        name: profileName.trim(),
        columns: mapping,
        worksheet: worksheet ?? null,
      });

      setProfiles(await api.importProfiles(projectId));
      setProfileName('');
    } catch (e) {
      fail(e);
    }
  }, [projectId, profileName, mapping, worksheet, fail]);

  const plan = preview?.plan;
  const interesting = plan?.rows.filter((r) => r.action !== 'Unchanged') ?? [];

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

      <div className="toolbar start-row">
        {canEdit && (
          <label className="btn btn-small file-btn">
            {file ? 'Choose another file' : 'Choose a spreadsheet'}
            <input
              type="file"
              accept=".xlsx,.xlsm"
              hidden
              onChange={(e) => void choose(e.target.files?.[0] ?? null)}
            />
          </label>
        )}

        <a className="btn ghost btn-small" href={api.exportUrl(projectId)}>
          Export as a spreadsheet
        </a>

        {file && <span className="count">{file.name}</span>}
        {busy && <span className="count">Reading…</span>}
      </div>

      {outcome && (
        <div className="banner done">
          <span>
            Imported. {outcome.created} added, {outcome.updated} changed
            {outcome.skipped > 0 ? `, ${outcome.skipped} left alone` : ''}.
          </span>
          {outcome.notes.length > 0 && (
            <ul className="notes">
              {outcome.notes.map((n) => (
                <li key={n}>{n}</li>
              ))}
            </ul>
          )}
        </div>
      )}

      {!file && !outcome && (
        <p className="empty">
          Upload the project's inventory workbook to see what it would change. Nothing is
          written until you have read the changes and said so.
        </p>
      )}

      {preview && plan && (
        <>
          {preview.worksheets.length > 1 && (
            <div className="toolbar">
              <span className="count">Sheet</span>
              <select
                className="project-picker"
                value={worksheet ?? ''}
                onChange={(e) => file && void refresh(file, { mapping, worksheet: e.target.value })}
              >
                {preview.worksheets.map((w) => (
                  <option key={w} value={w}>
                    {w}
                  </option>
                ))}
              </select>
            </div>
          )}

          <section className="mapping">
            <header className="queue-head">
              <span className="queue-type">Columns</span>
              <span className="queue-count">
                header on row {preview.headerRow} · {preview.rowCount} rows
              </span>
            </header>

            <table className="grid map-grid">
              <thead>
                <tr>
                  <th>Field</th>
                  <th>Column in your file</th>
                  <th>What is in it</th>
                </tr>
              </thead>
              <tbody>
                {fields.map((field) => {
                  const letter = mapping[field.field] ?? '';
                  const column = preview.columns.find((c) => c.letter === letter);

                  return (
                    <tr key={field.field}>
                      <td className="map-field">
                        {field.label}
                        {field.required && <span className="required" title="Required"> *</span>}
                      </td>
                      <td>
                        <select
                          value={letter}
                          disabled={!canEdit || busy}
                          onChange={(e) => void remap(field.field, e.target.value)}
                        >
                          <option value="">— not in this file —</option>
                          {preview.columns.map((c) => (
                            <option key={c.letter} value={c.letter}>
                              {c.letter}
                              {c.header ? ` · ${c.header}` : ''}
                              {c.looksLikeData ? ' (no header)' : ''}
                            </option>
                          ))}
                        </select>
                      </td>
                      <td className="map-sample">
                        {column ? column.sample.join(' · ') || '—' : '—'}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>

            {preview.columns.some((c) => c.looksLikeData) && (
              <p className="note-line">
                One column has a record where its heading should be, so it could not be matched
                automatically. Pick what it is above, or leave it out.
              </p>
            )}

            {canEdit && (
              <div className="toolbar">
                <input
                  className="search"
                  style={{ width: 220 }}
                  placeholder="Remember this mapping as…"
                  value={profileName}
                  onChange={(e) => setProfileName(e.target.value)}
                />
                <button
                  className="link"
                  type="button"
                  disabled={!profileName.trim()}
                  onClick={() => void saveProfile()}
                >
                  Save mapping
                </button>
                {profiles.length > 0 && (
                  <span className="count">
                    Saved: {profiles.map((p) => p.name).join(', ')}
                  </span>
                )}
              </div>
            )}
          </section>

          <section className="plan">
            <header className="queue-head">
              <span className="queue-type">What this would do</span>
              <span className="queue-count">
                {plan.creates} new · {plan.updates} changed · {plan.unchanged} unchanged
                {plan.skipped > 0 && ` · ${plan.skipped} left alone`}
                {plan.failed > 0 && ` · ${plan.failed} unreadable`}
              </span>
            </header>

            {plan.problems.map((p) => (
              <p className="banner error" key={p}>
                {p}
              </p>
            ))}

            {plan.newTypes.length > 0 && (
              <p className="note-line">
                New apartment types would be created: <strong>{plan.newTypes.join(', ')}</strong>.
                Check these are not typos.
              </p>
            )}

            {plan.notInFile.length > 0 && (
              <p className="note-line">
                {plan.notInFile.length} apartment{plan.notInFile.length === 1 ? '' : 's'} here
                {plan.notInFile.length === 1 ? ' is' : ' are'} not in the file
                {' '}({plan.notInFile.map((u) => u.code).join(', ')}). Nothing will be deleted.
              </p>
            )}

            {interesting.length === 0 ? (
              <p className="empty">This file matches what is already here.</p>
            ) : (
              <table className="grid plan-grid">
                <thead>
                  <tr>
                    <th>Row</th>
                    <th>Apartment</th>
                    <th>Outcome</th>
                    <th>Changes</th>
                  </tr>
                </thead>
                <tbody>
                  {interesting.map((row) => (
                    <tr key={row.sourceRow} className={`plan-${row.action.toLowerCase()}`}>
                      <td className="num">{row.sourceRow}</td>
                      <td>{row.code ?? '—'}</td>
                      <td>{ACTION_LABEL[row.action]}</td>
                      <td className="plan-changes">
                        {row.reason && <span className="reason">{row.reason}</span>}
                        {row.staleNote && <span className="reason">{row.staleNote}</span>}
                        {Object.entries(row.changes).map(([field, change]) => (
                          <span className="change" key={field}>
                            {fields.find((f) => f.field === field)?.label ?? field}:{' '}
                            <s>{show(change.before)}</s> {show(change.after)}
                          </span>
                        ))}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}

            {canEdit && (
              <div className="toolbar">
                <button
                  className="btn btn-small"
                  type="button"
                  disabled={!plan.canCommit || busy}
                  onClick={() => void commit()}
                >
                  Apply {plan.creates + plan.updates} change
                  {plan.creates + plan.updates === 1 ? '' : 's'}
                </button>

                {!plan.canCommit && (
                  <span className="count">
                    {plan.failed > 0
                      ? 'Fix the unreadable rows in the file first.'
                      : plan.problems.length > 0
                        ? 'Map the missing columns first.'
                        : 'Nothing to apply.'}
                  </span>
                )}
              </div>
            )}
          </section>
        </>
      )}
    </div>
  );
}
