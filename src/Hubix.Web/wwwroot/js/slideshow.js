// The project slideshow: moves through the Gallery images on its own, with arrows and dots.
//
// The page works with this file absent: the first image is rendered as the current one and
// simply stays. The slow zoom is CSS on the current slide, so it restarts whenever the class
// moves. Visitors who ask for reduced motion get neither the zoom nor the autoplay.
(() => {
    'use strict';

    const INTERVAL_MS = 6000;

    for (const show of document.querySelectorAll('[data-slideshow]')) {
        const slides = [...show.querySelectorAll('.slide')];
        const dots = [...show.querySelectorAll('.slide-dots button')];
        const still = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        let current = 0;
        let timer = 0;

        // Two reasons to hold still, kept apart so the mouse leaving does not restart the show
        // under a keyboard user who is still on an arrow. Only keyboard focus counts: a mouse
        // click also focuses the button, and that alone should not stop the show for good.
        let hovered = false;
        let focused = false;

        const go = (index) => {
            current = (index + slides.length) % slides.length;

            slides.forEach((slide, i) => {
                slide.classList.toggle('is-current', i === current);
                slide.setAttribute('aria-hidden', String(i !== current));
            });

            dots.forEach((dot, i) => dot.setAttribute('aria-current', String(i === current)));
            schedule();
        };

        // One timer, restarted on every move, so a click never lands just before an automatic
        // step and skips the image the visitor asked for.
        const schedule = () => {
            clearTimeout(timer);

            if (!still && !hovered && !focused) {
                timer = setTimeout(() => go(current + 1), INTERVAL_MS);
            }
        };

        show.querySelector('.prev').addEventListener('click', () => go(current - 1));
        show.querySelector('.next').addEventListener('click', () => go(current + 1));
        dots.forEach((dot, i) => dot.addEventListener('click', () => go(i)));

        show.addEventListener('mouseenter', () => { hovered = true; schedule(); });
        show.addEventListener('mouseleave', () => { hovered = false; schedule(); });
        show.addEventListener('focusin', (e) => { focused = e.target.matches(':focus-visible'); schedule(); });
        show.addEventListener('focusout', (e) => { focused = show.contains(e.relatedTarget); schedule(); });

        show.querySelectorAll('[hidden]').forEach((control) => { control.hidden = false; });
        schedule();
    }
})();
