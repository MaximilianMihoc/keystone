import { useCallback, useEffect, useState } from 'react';
import {
  api,
  ApiError,
  SessionExpired,
  TRANCHE_BASES,
  TRANCHE_DUES,
  type PricingDraft,
  type PricingPreview,
  type PricingResponse,
  type Tranche,
  type TrancheBasis,
  type TrancheDue,
} from './api';

interface Props {
  projectId: string;
  canEdit: boolean;
  onSessionExpired: () => void;
}

const BASIS_LABELS: Record<TrancheBasis, string> = {
  AdvancePercentage: 'The advance',
  Percentage: 'A percentage',
  Remainder: 'Whatever is left',
};

const DUE_LABELS: Record<TrancheDue, string> = {
  AtSigning: 'On signing',
  Interim: 'Part-way',
  AtDelivery: 'On delivery',
};

const BLANK: PricingDraft = {
  advancePct: 0.15,
  vatStandard: 0.19,
  cesiuneVatPct: 0.19,
  vatReduced: 0.09,
  tranches: [
    { label: 'Transa 1', basis: 'AdvancePercentage', includesCesiune: true, due: 'AtSigning', percentage: null },
    { label: 'Transa 2', basis: 'Remainder', includesCesiune: false, due: 'AtDelivery', percentage: null },
  ],
};

/**
 * The payment plan every price a buyer sees is derived from.
 *
 * The preview is the point of this screen rather than a decoration. A plan can be structurally
 * valid and still wrong — the cesiune collected on the wrong payment, a percentage a decimal place
 * out — and none of that is visible in the fields themselves, only in the money they produce.
 */
export function PricingPanel({ projectId, canEdit, onSessionExpired }: Props) {
  const [data, setData] = useState<PricingResponse | null>(null);
  const [draft, setDraft] = useState<PricingDraft>(BLANK);
  const [preview, setPreview] = useState<PricingPreview | null>(null);
  // Null means "whatever apartment the server picks". Held as a string so the field can be
  // emptied mid-edit without a stray 0 being priced. Deliberately not persisted: it is a
  // what-if, and clearing it returns to the real apartment.
  const [samplePrice, setSamplePrice] = useState<string | null>(null);
  const [sampleCesiune, setSampleCesiune] = useState<string | null>(null);
  const [reducedVat, setReducedVat] = useState(false);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [effectiveFrom, setEffectiveFrom] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      const pricing = await api.pricing(projectId);
      setData(pricing);
      if (pricing.current) setDraft(strip(pricing.current));
      setError(null);
    } catch (e) {
      if (e instanceof SessionExpired) onSessionExpired();
      else setError(e instanceof ApiError ? e.message : 'Could not load the payment plan.');
    }
  }, [projectId, onSessionExpired]);

  useEffect(() => {
    void load();
  }, [load]);

  // Re-priced on every keystroke. The request writes nothing, and seeing the money move as a rate
  // is typed is the whole reason a rate typed wrong gets caught here rather than by a buyer.
  useEffect(() => {
    let cancelled = false;

    void (async () => {
      try {
        const result = await api.previewPricing(projectId, {
          rule: draft,
          samplePriceNet:
            samplePrice === null || samplePrice.trim() === '' ? null : Number(samplePrice),
          sampleCesiuneNet:
            sampleCesiune === null || sampleCesiune.trim() === '' ? null : Number(sampleCesiune),
          reducedVat,
        });

        if (!cancelled) {
          setPreview(result);
          setPreviewError(null);
        }
      } catch (e) {
        if (e instanceof SessionExpired) onSessionExpired();
        else if (!cancelled) {
          setPreview(null);
          // A typed price the plan cannot be worked out against — below the cesiune, say.
          // Saying so beats leaving the reader on "Working it out…" forever.
          setPreviewError(e instanceof ApiError ? e.message : null);
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [projectId, draft, samplePrice, sampleCesiune, reducedVat, onSessionExpired]);

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
    return error ? <p className="banner error">{error}</p> : <p className="empty">Loading the payment plan…</p>;
  }

  const setTranche = (index: number, patch: Partial<Tranche>) =>
    setDraft({
      ...draft,
      tranches: draft.tranches.map((t, i) => (i === index ? { ...t, ...patch } : t)),
    });

  const move = (index: number, by: number) => {
    const next = [...draft.tranches];
    const target = index + by;
    if (target < 0 || target >= next.length) return;
    [next[index], next[target]] = [next[target], next[index]];
    setDraft({ ...draft, tranches: next });
  };

  // An emptied field is not an override: the request sends null and the apartment answers
  // again, so the hint must not claim a figure was typed.
  const typed = (v: string | null) => v !== null && v.trim() !== '';
  const overridden = typed(samplePrice) || typed(sampleCesiune);
  const touched = samplePrice !== null || sampleCesiune !== null;

  const problems = preview?.problems ?? [];
  const blocked = problems.length > 0;

  return (
    <>
      {error && <p className="banner error">{error}</p>}
      {notice && <p className="banner">{notice}</p>}

      {!data.current && (
        <p className="banner">
          This project has no payment plan yet, so no price can be worked out for it. Nothing on the
          public site will show a price until one is saved.
        </p>
      )}

      <section className="panel">
        <h2>Rates</h2>
        <p className="hint">
          Written as proportions: 0.19 means 19%. The advance is measured against the price without
          the cesiune, which is what reaches the developer.
        </p>
        <div className="row">
          <label className="field">
            <span>Advance</span>
            <input
              type="number"
              step="0.0001"
              value={draft.advancePct}
              onChange={(e) => setDraft({ ...draft, advancePct: Number(e.target.value) })}
            />
          </label>
          <label className="field">
            <span>VAT</span>
            <input
              type="number"
              step="0.0001"
              value={draft.vatStandard}
              onChange={(e) => setDraft({ ...draft, vatStandard: Number(e.target.value) })}
            />
          </label>
          <label className="field">
            <span>VAT on the cesiune</span>
            <input
              type="number"
              step="0.0001"
              value={draft.cesiuneVatPct}
              onChange={(e) => setDraft({ ...draft, cesiuneVatPct: Number(e.target.value) })}
            />
          </label>
          <label className="field">
            <span>Reduced VAT</span>
            <input
              type="number"
              step="0.0001"
              value={draft.vatReduced ?? ''}
              placeholder="none"
              onChange={(e) =>
                setDraft({ ...draft, vatReduced: e.target.value === '' ? null : Number(e.target.value) })
              }
            />
          </label>
        </div>
      </section>

      <section className="panel">
        <h2>Payments</h2>
        <p className="hint">
          In the order they fall due. The last one takes whatever is left, so the payments always add
          up to the price exactly. Exactly one collects the cesiune.
        </p>

        <table className="grid">
          <thead>
            <tr>
              <th>Name</th>
              <th>Worked out as</th>
              <th>Percentage</th>
              <th>Due</th>
              <th>Cesiune</th>
              {canEdit && <th />}
            </tr>
          </thead>
          <tbody>
            {draft.tranches.map((t, i) => (
              <tr key={i}>
                <td>
                  <input
                    value={t.label}
                    disabled={!canEdit}
                    onChange={(e) => setTranche(i, { label: e.target.value })}
                  />
                </td>
                <td>
                  <select
                    value={t.basis}
                    disabled={!canEdit}
                    onChange={(e) => setTranche(i, { basis: e.target.value as TrancheBasis })}
                  >
                    {TRANCHE_BASES.map((b) => (
                      <option key={b} value={b}>
                        {BASIS_LABELS[b]}
                      </option>
                    ))}
                  </select>
                </td>
                <td>
                  <input
                    type="number"
                    step="0.01"
                    value={t.percentage ?? ''}
                    disabled={!canEdit || t.basis !== 'Percentage'}
                    onChange={(e) =>
                      setTranche(i, { percentage: e.target.value === '' ? null : Number(e.target.value) })
                    }
                  />
                </td>
                <td>
                  <select
                    value={t.due}
                    disabled={!canEdit}
                    onChange={(e) => setTranche(i, { due: e.target.value as TrancheDue })}
                  >
                    {TRANCHE_DUES.map((d) => (
                      <option key={d} value={d}>
                        {DUE_LABELS[d]}
                      </option>
                    ))}
                  </select>
                </td>
                <td>
                  <input
                    type="checkbox"
                    checked={t.includesCesiune}
                    disabled={!canEdit}
                    onChange={(e) => setTranche(i, { includesCesiune: e.target.checked })}
                  />
                </td>
                {canEdit && (
                  <td>
                    <button className="btn ghost" type="button" onClick={() => move(i, -1)}>
                      ↑
                    </button>{' '}
                    <button className="btn ghost" type="button" onClick={() => move(i, 1)}>
                      ↓
                    </button>{' '}
                    <button
                      className="btn danger"
                      type="button"
                      disabled={draft.tranches.length === 1}
                      onClick={() =>
                        setDraft({ ...draft, tranches: draft.tranches.filter((_, x) => x !== i) })
                      }
                    >
                      Remove
                    </button>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>

        {canEdit && (
          <button
            className="btn ghost"
            type="button"
            onClick={() =>
              setDraft({
                ...draft,
                tranches: [
                  ...draft.tranches,
                  {
                    label: `Transa ${draft.tranches.length + 1}`,
                    basis: 'Remainder',
                    includesCesiune: false,
                    due: 'AtDelivery',
                    percentage: null,
                  },
                ],
              })
            }
          >
            Add a payment
          </button>
        )}
      </section>

      <section className="panel">
        <h2>What this produces</h2>

        {blocked ? (
          <ul className="banner error">
            {problems.map((p) => (
              <li key={p}>{p}</li>
            ))}
          </ul>
        ) : preview?.schedule ? (
          <>
            <p className="hint">
              {overridden
                ? 'Worked out against the figures below, which you have changed.'
                : preview.schedule.against
                  ? `Worked out against ${preview.schedule.against}, the cheapest apartment here.`
                  : 'This project has no apartments yet, so this is against the figures below.'}
            </p>

            <div className="row">
              <label className="field">
                <span>Price excl. VAT</span>
                <input
                  type="number"
                  value={samplePrice ?? preview.schedule.basePriceNet}
                  onChange={(e) => setSamplePrice(e.target.value)}
                />
              </label>
              <label className="field">
                <span>Cesiune excl. VAT</span>
                <input
                  type="number"
                  value={sampleCesiune ?? preview.schedule.cesiuneNet}
                  onChange={(e) => setSampleCesiune(e.target.value)}
                />
              </label>
              {touched && preview.schedule.against && (
                <button
                  className="btn ghost"
                  type="button"
                  onClick={() => {
                    setSamplePrice(null);
                    setSampleCesiune(null);
                  }}
                >
                  Back to {preview.schedule.against}
                </button>
              )}
            </div>

            <label className="check">
              <input
                type="checkbox"
                checked={reducedVat}
                onChange={(e) => setReducedVat(e.target.checked)}
              />
              Show it at the reduced VAT rate
            </label>

            <table className="grid">
              <thead>
                <tr>
                  <th>Payment</th>
                  <th>Due</th>
                  <th className="num">Apartment</th>
                  <th className="num">Cesiune</th>
                  <th className="num">Net</th>
                  <th className="num">VAT</th>
                  <th className="num">Total</th>
                </tr>
              </thead>
              <tbody>
                {preview.schedule.tranches.map((t, i) => (
                  <tr key={i}>
                    <td>
                      {t.label}
                      {t.percentage !== null && ` (${(t.percentage * 100).toFixed(0)}%)`}
                    </td>
                    <td>{DUE_LABELS[t.due]}</td>
                    <td className="num">{money(t.apartmentNet)}</td>
                    <td className="num">{t.cesiuneNet > 0 ? money(t.cesiuneNet) : '—'}</td>
                    <td className="num">{money(t.net)}</td>
                    <td className="num">{money(t.vat)}</td>
                    <td className="num">{money(t.gross)}</td>
                  </tr>
                ))}
                <tr>
                  <td colSpan={4}>
                    <strong>Total</strong>
                  </td>
                  <td className="num">
                    <strong>{money(preview.schedule.totalNet)}</strong>
                  </td>
                  <td className="num">
                    <strong>{money(preview.schedule.totalVat)}</strong>
                  </td>
                  <td className="num">
                    <strong>{money(preview.schedule.totalGross)}</strong>
                  </td>
                </tr>
              </tbody>
            </table>
          </>
        ) : previewError ? (
          <p className="banner error">{previewError}</p>
        ) : (
          <p className="empty">Working it out…</p>
        )}
      </section>

      {canEdit && (
        <section className="panel">
          <h2>Save</h2>
          <p className="hint">
            Saving corrects the plan in effect now. Anything already reserved keeps the figures it
            was sold at, because a reservation records its own payments when it is made.
          </p>
          <div className="row">
            <button
              className="btn"
              type="button"
              disabled={busy || blocked}
              onClick={() =>
                void run(async () => {
                  await api.savePricing(projectId, draft);
                  setNotice('Saved.');
                })
              }
            >
              Save
            </button>

            <label className="field">
              <span>Or start a new plan on</span>
              <input
                type="date"
                value={effectiveFrom}
                onChange={(e) => setEffectiveFrom(e.target.value)}
              />
            </label>

            <button
              className="btn ghost"
              type="button"
              disabled={busy || blocked || effectiveFrom === ''}
              onClick={() =>
                void run(async () => {
                  await api.schedulePricing(projectId, draft, new Date(effectiveFrom).toISOString());
                  setEffectiveFrom('');
                  setNotice('Scheduled. Today’s plan is unchanged.');
                })
              }
            >
              Schedule
            </button>
          </div>
        </section>
      )}

      {data.scheduled.length > 0 && (
        <section className="panel">
          <h2>Scheduled changes</h2>
          <p className="hint">These sit inert until their date, then take over on their own.</p>
          <table className="grid">
            <thead>
              <tr>
                <th>From</th>
                <th>Advance</th>
                <th>VAT</th>
                <th>Payments</th>
                {canEdit && <th />}
              </tr>
            </thead>
            <tbody>
              {data.scheduled.map((r) => (
                <tr key={r.id}>
                  <td>{new Date(r.effectiveFrom).toLocaleDateString()}</td>
                  <td>{(r.advancePct * 100).toFixed(2)}%</td>
                  <td>{(r.vatStandard * 100).toFixed(2)}%</td>
                  <td>{r.tranches.length}</td>
                  {canEdit && (
                    <td>
                      <button
                        className="btn danger"
                        type="button"
                        disabled={busy}
                        onClick={() => void run(() => api.deleteScheduledPricing(r.id))}
                      >
                        Cancel
                      </button>
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}
    </>
  );
}

const strip = (rule: PricingResponse['current']): PricingDraft => ({
  advancePct: rule!.advancePct,
  vatStandard: rule!.vatStandard,
  cesiuneVatPct: rule!.cesiuneVatPct,
  vatReduced: rule!.vatReduced,
  tranches: rule!.tranches,
});

const money = (value: number) =>
  value.toLocaleString('en-GB', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
