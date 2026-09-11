import { useCallback, useEffect, useState } from 'react';
import { api, SessionExpired, type EmailSettings } from './api';

interface Props {
  onSessionExpired: () => void;
}

/** One address per line is easier to read and to paste than a comma-separated box. */
const toLines = (values: string[]) => values.join('\n');

const fromLines = (value: string) =>
  value
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0);

/**
 * Where sales mail goes, and whether mail may leave the machine at all.
 *
 * Admin only, and deliberately apart from the rest of editing: a price is a number, and this
 * is the decision about whether this system speaks to real people. Every save is audited with
 * what the values were beforehand.
 *
 * The values shown are the ones actually in use — configuration with whatever has been saved
 * laid over it — rather than the saved row alone, which would read as blank on a fresh install
 * while mail was quietly going somewhere.
 */
export function SettingsPanel({ onSessionExpired }: Props) {
  const [form, setForm] = useState<EmailSettings | null>(null);
  const [allowed, setAllowed] = useState('');
  const [blocked, setBlocked] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const fail = useCallback(
    (e: unknown) => {
      if (e instanceof SessionExpired) onSessionExpired();
      else setError((e as Error).message);
    },
    [onSessionExpired],
  );

  const load = useCallback(async () => {
    try {
      const current = await api.emailSettings();
      setForm(current);
      setAllowed(toLines(current.allowedRecipients));
      setBlocked(toLines(current.blockedRecipients));
    } catch (e) {
      fail(e);
    }
  }, [fail]);

  useEffect(() => {
    void load();
  }, [load]);

  if (!form) {
    return <p className="muted">{error ?? 'Loading settings…'}</p>;
  }

  const set = <K extends keyof EmailSettings>(key: K, value: EmailSettings[K]) =>
    setForm((f) => (f ? { ...f, [key]: value } : f));

  const save = async () => {
    setBusy(true);
    setError(null);
    setSaved(null);

    try {
      const result = await api.saveEmailSettings({
        salesInbox: form.salesInbox,
        publicBaseUrl: form.publicBaseUrl,
        liveSendingEnabled: form.liveSendingEnabled,
        redirectAllTo: form.redirectAllTo,
        allowedRecipients: fromLines(allowed),
        blockedRecipients: fromLines(blocked),
      });

      setSaved(result.savedAt);
      await load();
    } catch (e) {
      fail(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="panel">
      <h2>Email</h2>

      <p className="muted">
        {form.saved
          ? 'Saved here, which is what the system uses. Configuration is only the fallback.'
          : 'Nothing saved yet, so these are the values from configuration. Saving takes them over.'}
      </p>

      {!form.sendingPossible && (
        <p className="muted">
          No mail provider is registered, so nothing leaves this machine whatever is set below.
          Messages are written to the outbox instead.
        </p>
      )}

      <label className="field">
        <span>Portal sales inbox</span>
        <input
          type="email"
          value={form.salesInbox ?? ''}
          onChange={(e) => set('salesInbox', e.target.value === '' ? null : e.target.value)}
        />
        <small className="muted">
          Used for every development that does not name its own, on its Setup screen.
        </small>
      </label>

      <label className="field">
        <span>Public base URL</span>
        <input
          value={form.publicBaseUrl ?? ''}
          onChange={(e) => set('publicBaseUrl', e.target.value === '' ? null : e.target.value)}
        />
        <small className="muted">Every link in every email is built from this.</small>
      </label>

      <h3>Sending</h3>

      <label className="check">
        <input
          type="checkbox"
          checked={form.liveSendingEnabled}
          onChange={(e) => set('liveSendingEnabled', e.target.checked)}
        />
        <span>Send mail for real</span>
      </label>

      {form.liveSendingEnabled && (
        <p className="banner error">
          With this on, messages reach the people they are addressed to. Hubix have not been
          approached about this system — keep their addresses on the blocked list.
        </p>
      )}

      <label className="field">
        <span>Redirect everything to</span>
        <input
          type="email"
          value={form.redirectAllTo ?? ''}
          onChange={(e) => set('redirectAllTo', e.target.value === '' ? null : e.target.value)}
        />
        <small className="muted">
          Every message goes here instead, with the intended recipient kept in the subject.
          Leave empty in production.
        </small>
      </label>

      <label className="field">
        <span>Allowed recipients</span>
        <textarea rows={4} value={allowed} onChange={(e) => setAllowed(e.target.value)} />
        <small className="muted">
          One per line. Wildcards allowed, as in <code>*@example.com</code>. Empty means everyone.
        </small>
      </label>

      <label className="field">
        <span>Blocked recipients</span>
        <textarea rows={4} value={blocked} onChange={(e) => setBlocked(e.target.value)} />
        <small className="muted">
          One per line, checked before the allowed list. Never emptied while live sending is on.
        </small>
      </label>

      {error && <p className="banner error">{error}</p>}
      {saved && <p className="muted">Saved.</p>}

      <div className="row">
        <button className="btn" type="button" disabled={busy} onClick={() => void save()}>
          {busy ? 'Saving…' : 'Save'}
        </button>
      </div>
    </section>
  );
}
