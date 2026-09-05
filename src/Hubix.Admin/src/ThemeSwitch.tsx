import { useState } from 'react';

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

/**
 * Light, dark or the browser's choice. This is a grid the user works in all day, so it
 * switches in place rather than reloading the way the public site's link does.
 */
export function ThemeSwitch() {
  const [theme, setTheme] = useState<Theme>(current);

  return (
    <div className="segmented" role="group" aria-label="Appearance">
      {THEMES.map((option) => (
        <button
          key={option}
          type="button"
          aria-pressed={option === theme}
          onClick={() => {
            apply(option);
            setTheme(option);
          }}
        >
          {LABELS[option]}
        </button>
      ))}
    </div>
  );
}
