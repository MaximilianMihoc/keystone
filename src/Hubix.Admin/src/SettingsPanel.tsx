import { useCallback, useEffect, useState } from 'react';
import { api, SessionExpired, type EmailSettings, type TestSendResult } from './api';

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
  const [testTo, setTestTo] = useState('');
  const [tested, setTested] = useState<TestSendResult | null>(null);
  const [testing, setTesting] = useState(false);

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

      // Put the screen back to what was actually saved. A refused save used to leave the
      // box ticked, so the page said live sending was on while the database said it was
      // off — and the test button, telling the truth, looked like the thing that was wrong.
      await load();
    } finally {
      setBusy(false);
    }
  };

  const test = async () => {
    setTesting(true);
    setTested(null);
    setError(null);

    try {
      setTested(await api.sendTestEmail(testTo.trim() === '' ? null : testTo.trim()));
    } catch (e) {
      fail(e);
    } finally {
      setTesting(false);
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
          No mail provider is configured, so nothing leaves this machine whatever is set below.
          Messages are written to the outbox instead. Both an API key and a sender address are
          needed, and neither is set here — they come from configuration.
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
        <span>Live sending</span>
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

      <h3>Test</h3>

      <p className="muted">
        Sends one message the same way every other message goes — the same guards, the same
        provider — and says what became of it. Leave the address empty to send it to yourself.
      </p>

      <label className="field">
        <span>Send a test to</span>
        <input
          type="email"
          placeholder="Your own address"
          value={testTo}
          onChange={(e) => setTestTo(e.target.value)}
        />
      </label>

      {tested && (
        <p className={tested.ok ? 'banner done' : 'banner error'}>
          {!tested.ok
            ? `The provider refused it: ${tested.error}`
            : tested.transmitted
              ? `Sent to ${tested.to}. If it does not arrive, the problem is delivery, not this.`
              : `Kept in the outbox for ${tested.to} — ${tested.reason}.`}
        </p>
      )}

      <div className="row">
        <button className="btn" type="button" disabled={testing} onClick={() => void test()}>
          {testing ? 'Sending…' : 'Send test message'}
        </button>
      </div>
    </section>
  );
}
