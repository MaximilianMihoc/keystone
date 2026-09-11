import { useCallback, useEffect, useState } from 'react';
import {
  api,
  ApiError,
  SessionExpired,
  PROJECT_STATUSES,
  RENTAL_PROFILES,
  type ProjectEdit,
  type ProjectSetup,
  type ProjectStatus,
  type ProjectTranslationRow,
  type RentalProfile,
} from './api';
import { PricingPanel } from './PricingPanel';
import { StructurePanel } from './StructurePanel';

interface Props {
  projectId: string;
  canEdit: boolean;
  onSessionExpired: () => void;
  /** The picker and the header read the project's name and state, so they reload with it. */
  onProjectsChanged: () => void;
  onProjectDeleted: () => void;
}

type Section = 'details' | 'text' | 'pricing' | 'structure';

const SECTIONS: { key: Section; label: string }[] = [
  { key: 'details', label: 'Details' },
  { key: 'text', label: 'Text' },
  { key: 'pricing', label: 'Payment plan' },
  { key: 'structure', label: 'Floors' },
];

const CULTURE_NAMES: Record<string, string> = {
  'ro-RO': 'Romanian',
  'en-GB': 'English',
};

/**
 * A project's own record: what it is, and the words the public site renders about it.
 *
 * Everything here used to live in the seeder, which is why adding a second project meant a code
 * change and a redeploy.
 */
export function SetupPanel({
  projectId,
  canEdit,
  onSessionExpired,
  onProjectsChanged,
  onProjectDeleted,
}: Props) {
  const [section, setSection] = useState<Section>('details');
  const [data, setData] = useState<ProjectSetup | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [form, setForm] = useState<ProjectEdit | null>(null);

  const load = useCallback(async () => {
    try {
      const setup = await api.projectSetup(projectId);
      setData(setup);
      setForm(toForm(setup));
      setError(null);
    } catch (e) {
      if (e instanceof SessionExpired) onSessionExpired();
      else setError(e instanceof ApiError ? e.message : 'Could not load the project.');
    }
  }, [projectId, onSessionExpired]);

  useEffect(() => {
    void load();
  }, [load]);

  /**
   * Runs a write, then re-reads what it changed.
   *
   * Deleting is the exception: re-reading a project that no longer exists just asks the server
   * for a 404, so it opts out.
   */
  const run = useCallback(
    async (action: () => Promise<unknown>, reread = true) => {
      setBusy(true);
      setError(null);
      setNotice(null);

      try {
        await action();
        if (reread) await load();
        onProjectsChanged();
      } catch (e) {
        if (e instanceof SessionExpired) onSessionExpired();
        else setError(e instanceof ApiError ? e.message : 'That did not save.');
      } finally {
        setBusy(false);
      }
    },
    [load, onSessionExpired, onProjectsChanged],
  );

  if (!data || !form) {
    return error ? <p className="banner error">{error}</p> : <p className="empty">Loading the project…</p>;
  }

  const project = data.project;
  const set = <K extends keyof ProjectEdit>(key: K, value: ProjectEdit[K]) =>
    setForm({ ...form, [key]: value });

  return (
    <>
      {error && <p className="banner error">{error}</p>}
      {notice && <p className="banner">{notice}</p>}

      <nav className="views">
        {SECTIONS.map((s) => (
          <button
            key={s.key}
            className={`view${section === s.key ? ' view-on' : ''}`}
            type="button"
            onClick={() => setSection(s.key)}
          >
            {s.label}
          </button>
        ))}
      </nav>

      {section === 'details' && (
        <>
          <section className="panel">
            <h2>On the public site</h2>
            <p className="hint">
              {project.publishedAt
                ? `Published, and visible at /projects/${project.slug}. Its web address cannot change while it is published.`
                : 'Not published. Nobody outside this admin can see it.'}
            </p>
            <div className="row">
              <button
                className={project.publishedAt ? 'btn ghost' : 'btn'}
                type="button"
                disabled={!canEdit || busy}
                onClick={() =>
                  void run(() => api.setProjectPublished(projectId, !project.publishedAt))
                }
              >
                {project.publishedAt ? 'Unpublish' : 'Publish'}
              </button>

              {/* Publishing is refused below one apartment, because everything the public page
                  shows about a project is derived from its units. Saying so up front beats
                  finding out from a refusal. */}
              {!project.publishedAt && project.units === 0 && (
                <span className="hint">
                  It has no apartments yet. Add them from the Import tab before publishing.
                </span>
              )}
            </div>
          </section>

          <section className="panel">
            <h2>Details</h2>
            <div className="row">
              <label className="field">
                <span>Name</span>
                <input value={form.name} onChange={(e) => set('name', e.target.value)} />
              </label>
              <label className="field">
                <span>City</span>
                <input value={form.city} onChange={(e) => set('city', e.target.value)} />
              </label>
              <label className="field">
                <span>Web address</span>
                <input
                  value={form.slug}
                  disabled={project.publishedAt !== null}
                  onChange={(e) => set('slug', e.target.value)}
                />
              </label>
              <label className="field">
                <span>Developer</span>
                <select
                  value={form.developerId ?? ''}
                  onChange={(e) => set('developerId', e.target.value === '' ? null : e.target.value)}
                >
                  <option value="">Not set</option>
                  {data.developers.map((d) => (
                    <option key={d.id} value={d.id}>
                      {d.name}
                    </option>
                  ))}
                </select>
              </label>
              <label className="field">
                <span>Status</span>
                <select
                  value={form.status}
                  onChange={(e) => set('status', e.target.value as ProjectStatus)}
                >
                  {PROJECT_STATUSES.map((s) => (
                    <option key={s} value={s}>
                      {s}
                    </option>
                  ))}
                </select>
              </label>
              <label className="field">
                <span>Letting</span>
                <select
                  value={form.rentalProfile ?? ''}
                  onChange={(e) =>
                    set('rentalProfile', e.target.value === '' ? null : (e.target.value as RentalProfile))
                  }
                >
                  <option value="">Not set</option>
                  {RENTAL_PROFILES.map((r) => (
                    <option key={r} value={r}>
                      {r}
                    </option>
                  ))}
                </select>
              </label>
            </div>

            <div className="row">
              <label className="field">
                <span>Apartments committed</span>
                <input
                  type="number"
                  value={form.unitsCommitted ?? ''}
                  onChange={(e) => set('unitsCommitted', number(e.target.value))}
                />
              </label>
              <label className="field">
                <span>Delivery</span>
                <input
                  type="date"
                  value={form.deliveryDate ?? ''}
                  onChange={(e) => set('deliveryDate', e.target.value === '' ? null : e.target.value)}
                />
              </label>
              <label className="field">
                <span>Grace months</span>
                <input
                  type="number"
                  value={form.deliveryGraceMonths}
                  onChange={(e) => set('deliveryGraceMonths', Number(e.target.value))}
                />
              </label>
              <label className="field">
                <span>Reservation fee</span>
                <input
                  type="number"
                  value={form.reservationFee ?? ''}
                  onChange={(e) => set('reservationFee', number(e.target.value))}
                />
              </label>
              <label className="field">
                <span>Currency</span>
                <input
                  value={form.currency}
                  maxLength={3}
                  onChange={(e) => set('currency', e.target.value.toUpperCase())}
                />
              </label>
              <label className="field">
                <span>Hold lasts (hours)</span>
                <input
                  type="number"
                  value={form.holdDurationHours}
                  onChange={(e) => set('holdDurationHours', Number(e.target.value))}
                />
              </label>
            </div>

            <label className="field">
              <span>Map link</span>
              <input
                value={form.mapsUrl ?? ''}
                onChange={(e) => set('mapsUrl', e.target.value === '' ? null : e.target.value)}
              />
            </label>

            <div className="row">
              <button
                className="btn"
                type="button"
                disabled={!canEdit || busy}
                onClick={() =>
                  void run(async () => {
                    await api.editProject(projectId, form);
                    setNotice('Saved.');
                  })
                }
              >
                Save
              </button>
            </div>
          </section>

          {canEdit && (
            <section className="panel">
              <h2>Delete this project</h2>
              <p className="hint">
                Only while it is empty and unpublished. Once it has apartments, files or anybody
                in its history, deleting it would take that history with it, so it is refused.
              </p>
              <button
                className="btn danger"
                type="button"
                disabled={busy}
                onClick={() => {
                  if (!window.confirm(`Delete ${project.name}? This cannot be undone.`)) return;

                  void run(async () => {
                    await api.deleteProject(projectId);
                    onProjectDeleted();
                  }, false);
                }}
              >
                Delete
              </button>
            </section>
          )}
        </>
      )}

      {section === 'pricing' && (
        <PricingPanel projectId={projectId} canEdit={canEdit} onSessionExpired={onSessionExpired} />
      )}

      {section === 'structure' && (
        <StructurePanel projectId={projectId} canEdit={canEdit} onSessionExpired={onSessionExpired} />
      )}

      {section === 'text' &&
        data.translations.map((t) => (
          <Prose
            key={t.culture}
            row={t}
            canEdit={canEdit}
            busy={busy}
            onSave={(edit) =>
              void run(async () => {
                await api.saveProjectTranslation(projectId, t.culture, edit);
                setNotice(`Saved the ${CULTURE_NAMES[t.culture] ?? t.culture} text.`);
              })
            }
          />
        ))}
    </>
  );
}

/**
 * One language's prose, saved on its own.
 *
 * Per language rather than both at once, so a botched English edit cannot roll back good
 * Romanian, and so a failure names the language it belongs to.
 */
function Prose({
  row,
  canEdit,
  busy,
  onSave,
}: {
  row: ProjectTranslationRow;
  canEdit: boolean;
  busy: boolean;
  onSave: (edit: Omit<ProjectTranslationRow, 'culture' | 'isSource'>) => void;
}) {
  const [draft, setDraft] = useState(row);

  useEffect(() => {
    setDraft(row);
  }, [row]);

  const set = (key: keyof ProjectTranslationRow, value: string) =>
    setDraft({ ...draft, [key]: value === '' ? null : value });

  return (
    <section className="panel">
      <h2>{CULTURE_NAMES[row.culture] ?? row.culture}</h2>
      <p className="hint">
        {row.isSource
          ? 'The language Hubix writes in. Anything missing in another language falls back to this one, so leaving it blank leaves a blank page for everybody.'
          : 'Falls back to the Romanian above wherever it is left empty.'}
      </p>

      <label className="field">
        <span>City</span>
        <input value={draft.city ?? ''} onChange={(e) => set('city', e.target.value)} />
      </label>
      <label className="field">
        <span>Summary</span>
        <textarea value={draft.summary ?? ''} rows={3} onChange={(e) => set('summary', e.target.value)} />
      </label>
      <label className="field">
        <span>Prospectus</span>
        <textarea value={draft.body ?? ''} rows={8} onChange={(e) => set('body', e.target.value)} />
      </label>
      <label className="field">
        <span>Penalty clause</span>
        <textarea
          value={draft.penaltyClause ?? ''}
          rows={3}
          onChange={(e) => set('penaltyClause', e.target.value)}
        />
      </label>
      <label className="field">
        <span>Parking</span>
        <textarea value={draft.parking ?? ''} rows={2} onChange={(e) => set('parking', e.target.value)} />
      </label>
      <label className="field">
        <span>Reservation notice</span>
        <textarea
          value={draft.reservationNotice ?? ''}
          rows={4}
          onChange={(e) => set('reservationNotice', e.target.value)}
        />
        <span className="hint">
          Shown at the top of this project's reservation form, before the buyer fills anything
          in. Blank on most projects. One paragraph per line.
        </span>
      </label>

      <button
        className="btn"
        type="button"
        disabled={!canEdit || busy}
        onClick={() =>
          onSave({
            city: draft.city,
            summary: draft.summary,
            body: draft.body,
            penaltyClause: draft.penaltyClause,
            parking: draft.parking,
            reservationNotice: draft.reservationNotice,
          })
        }
      >
        Save {CULTURE_NAMES[row.culture] ?? row.culture}
      </button>
    </section>
  );
}

function toForm(setup: ProjectSetup): ProjectEdit {
  const p = setup.project;

  return {
    name: p.name,
    city: p.city,
    slug: p.slug,
    developerId: p.developerId,
    status: p.status,
    unitsCommitted: p.unitsCommitted,
    deliveryDate: p.deliveryDate,
    deliveryGraceMonths: p.deliveryGraceMonths,
    rentalProfile: p.rentalProfile,
    reservationFee: p.reservationFee,
    currency: p.currency,
    holdDurationHours: p.holdDurationHours,
    mapsUrl: p.mapsUrl,
  };
}

const number = (value: string): number | null => (value === '' ? null : Number(value));
