// Filtering the apartment list without losing your place.
//
// The chips are ordinary links and the page works with this file absent: the server renders
// the filtered list either way. What this adds is swapping the list in place, because a full
// load returns the reader to the top of a long page — past the elevation board they were
// scrolled well below.
(() => {
    'use strict';

    const region = document.getElementById('apartment-list');

    if (!region || !window.history.pushState) {
        return;
    }

    // Clicks outrun their responses. Each one takes a number and only the newest may paint,
    // so a second choice cannot be overwritten by the first one landing late — which would
    // leave the table disagreeing with both the address bar and the highlighted chip.
    let latest = 0;

    /** Replace the list with the same region from another rendering of this page. */
    async function show(url, focusIndex) {
        const request = ++latest;
        let html;

        region.classList.add('is-busy');
        region.setAttribute('aria-busy', 'true');

        try {
            const response = await fetch(url, { headers: { 'X-Requested-With': 'fetch' } });

            if (!response.ok) {
                throw new Error(String(response.status));
            }

            html = await response.text();
        } catch {
            // Hand it to the browser, error page and all. replace() rather than assign():
            // this URL is already the current history entry, and pressing Back should reach
            // the filter before it rather than this same one again.
            if (request === latest) {
                location.replace(url);
            }

            return;
        }

        if (request !== latest) {
            return; // a newer click is on its way; it owns the region and the busy state
        }

        const next = new DOMParser()
            .parseFromString(html, 'text/html')
            .getElementById('apartment-list');

        if (!next) {
            location.replace(url);
            return;
        }

        region.innerHTML = next.innerHTML;
        region.classList.remove('is-busy');
        region.removeAttribute('aria-busy');

        // The chip that was clicked no longer exists. Its replacement sits at the same
        // position, so keyboard focus lands where the reader left it rather than on <body>,
        // and a screen reader announces the newly current filter. preventScroll matters:
        // focusing scrolls the element into view by default, which would move the page —
        // the one thing this whole file exists to avoid.
        if (focusIndex !== null) {
            region.querySelectorAll('.filters a')[focusIndex]?.focus({ preventScroll: true });
        }
    }

    region.addEventListener('click', (event) => {
        const chip = event.target.closest('.filters a');

        // Let the browser handle anything that is not a plain left click on a chip:
        // opening in a new tab is still opening in a new tab.
        if (!chip || event.button !== 0 || event.metaKey || event.ctrlKey ||
            event.shiftKey || event.altKey) {
            return;
        }

        event.preventDefault();

        const chips = [...region.querySelectorAll('.filters a')];

        history.pushState(null, '', chip.href);
        show(chip.href, chips.indexOf(chip));
    });

    // Back and forward have to mean something, since the filter is in the URL.
    window.addEventListener('popstate', () => show(location.href, null));
})();
