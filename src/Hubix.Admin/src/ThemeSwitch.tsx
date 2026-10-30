import { useState, type ReactNode } from 'react';

const THEMES = ['light', 'dark', 'system'] as const;

type Theme = (typeof THEMES)[number];

/** The same cookie the public site writes, so a choice made on either side holds on both. */
const COOKIE = 'hubix.theme';

const LABELS: Record<Theme, string> = { light: 'Light', dark: 'Dark', system: 'Auto' };

/** The server stamped the attribute from the cookie, so the DOM already knows the answer. */
function current(): Theme {
  const value = document.documentElement.dataset.theme;
  return value === 'light' || value === 'dark' ? value : 'system';
}

function apply(theme: Theme) {
  const secure = location.protocol === 'https:' ? '; secure' : '';

  if (theme === 'system') {
    // "system" is a state the document advertises but not a value the cookie stores:
    // following the browser is the absence of a choice, so the cookie goes away.
    document.documentElement.dataset.theme = 'system';
    document.cookie = `${COOKIE}=; path=/; max-age=0; samesite=lax${secure}`;
    return;
  }

  document.documentElement.dataset.theme = theme;
  document.cookie = `${COOKIE}=${theme}; path=/; max-age=31536000; samesite=lax${secure}`;
}

const ICONS: Record<Theme, ReactNode> = {
  light: (
    <>
      <circle cx="12" cy="12" r="4" />
      <path d="M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" />
    </>
  ),
  dark: <path d="M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z" />,
  system: (
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="M12 3a9 9 0 0 1 0 18z" fill="currentColor" />
    </>
  ),
};

/**
 * Light, dark or the browser's choice, as one icon showing the current mode; clicking moves
 * to the next. This is a grid the user works in all day, so it switches in place rather than
 * reloading the way the public site's link does.
 */
export function ThemeSwitch() {
  const [theme, setTheme] = useState<Theme>(current);
  const label = `Appearance: ${LABELS[theme]}`;

  return (
    <button
      type="button"
      className="theme-icon"
      aria-label={label}
      title={label}
      onClick={() => {
        const next = THEMES[(THEMES.indexOf(theme) + 1) % THEMES.length];
        apply(next);
        setTheme(next);
      }}
    >
      <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" aria-hidden="true">
        {ICONS[theme]}
      </svg>
    </button>
  );
}
