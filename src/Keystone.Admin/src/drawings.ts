import { previewImage } from './previewImage';

/**
 * Plans and drawings on the public apartment page, shown the way the admin Files screen shows
 * them: PDFs as their first page, and a full-screen preview instead of a new tab.
 *
 * Built with the admin client so both use one previewImage and one pinned pdf.js, but it is
 * plain TypeScript: the public site is server-rendered and has no React. Every tile is already
 * a link to its file, so without this script the page still works.
 */

const sheet = document.querySelector<HTMLElement>('[data-drawings]');

if (sheet) {
  const tiles = [...sheet.querySelectorAll<HTMLAnchorElement>('a.drawing')];

  for (const tile of tiles) {
    if (tile.dataset.format !== 'pdf') continue;

    const thumb = tile.querySelector<HTMLElement>('.drawing-thumb')!;
    thumb.classList.add('is-loading');

    // A PDF that will not draw keeps its "PDF" placeholder; the link still opens it.
    previewImage(tile.href, tile.href, true)
      .then((src) => thumb.replaceChildren(image(src)))
      .catch(() => undefined)
      .finally(() => thumb.classList.remove('is-loading'));
  }

  tiles.forEach((tile, index) =>
    tile.addEventListener('click', (e) => {
      // A modified click still means "open it in a new tab", as the link promises.
      if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;

      e.preventDefault();
      openPreview(sheet, tiles, index);
    }),
  );
}

function image(src: string): HTMLImageElement {
  const img = document.createElement('img');
  img.src = src;
  img.alt = '';
  img.draggable = false;
  return img;
}

function openPreview(sheet: HTMLElement, tiles: HTMLAnchorElement[], start: number) {
  const words = sheet.dataset;
  let index = start;

  // Static markup only. Everything taken from a tile goes in through textContent, because a
  // file name is whatever somebody typed when saving it.
  const box = document.createElement('div');
  box.className = 'lightbox';
  box.setAttribute('role', 'dialog');
  box.setAttribute('aria-modal', 'true');
  box.innerHTML = `
    <div class="lb-top">
      <span class="drawing-kind"></span>
      <strong class="lb-name"></strong>
      <span class="lb-count lb-position"></span>
      <span class="lb-spacer"></span>
      <span class="lb-count lb-hint"></span>
      <a class="lb-btn lb-original" target="_blank" rel="noreferrer"></a>
      <button class="lb-btn lb-close" type="button"></button>
    </div>
    <div class="lb-stage">
      <button class="lb-nav lb-prev" type="button">‹</button>
      <span class="lb-image"></span>
      <button class="lb-nav lb-next" type="button">›</button>
    </div>`;

  const part = <T extends HTMLElement>(selector: string) => box.querySelector<T>(selector)!;
  const stage = part('.lb-image');
  const kind = part('.drawing-kind');

  part('.lb-hint').textContent = words.hint ?? '';
  part('.lb-original').textContent = words.openOriginal ?? '';
  part('.lb-close').textContent = words.close ?? '';
  part('.lb-prev').setAttribute('aria-label', words.previous ?? '');
  part('.lb-next').setAttribute('aria-label', words.next ?? '');
  part('.lb-prev').hidden = tiles.length < 2;
  part('.lb-next').hidden = tiles.length < 2;

  const show = () => {
    const tile = tiles[index];
    const label = tile.querySelector<HTMLElement>('.drawing-kind')!;
    const name = tile.querySelector('.drawing-name')!.textContent ?? '';

    kind.textContent = label.textContent;
    kind.dataset.kind = label.dataset.kind;
    part('.lb-name').textContent = name;
    part('.lb-position').textContent = `${index + 1} / ${tiles.length}`;
    part<HTMLAnchorElement>('.lb-original').href = tile.href;
    box.setAttribute('aria-label', name);

    stage.classList.remove('zoomed');
    stage.replaceChildren();

    if (tile.dataset.format !== 'pdf') {
      stage.replaceChildren(image(tile.href));
      return;
    }

    // A PDF that will not draw leaves the stage empty; "Open original" still reaches it.
    const shown = index;
    previewImage(tile.href, tile.href, true)
      .then((src) => {
        if (shown === index) stage.replaceChildren(image(src));
      })
      .catch(() => undefined);
  };

  const step = (by: number) => {
    index = (index + by + tiles.length) % tiles.length;
    show();
  };

  const close = () => {
    box.remove();
    document.removeEventListener('keydown', onKey);
    document.body.style.overflow = '';
    tiles[index].focus();
  };

  const onKey = (e: KeyboardEvent) => {
    if (e.key === 'Escape') close();
    else if (e.key === 'ArrowLeft') step(-1);
    else if (e.key === 'ArrowRight') step(1);
  };

  const origin = (e: MouseEvent) => {
    const rect = stage.getBoundingClientRect();
    stage.style.transformOrigin =
      `${((e.clientX - rect.left) / rect.width) * 100}% ${((e.clientY - rect.top) / rect.height) * 100}%`;
  };

  part('.lb-close').addEventListener('click', close);
  part('.lb-prev').addEventListener('click', () => step(-1));
  part('.lb-next').addEventListener('click', () => step(1));

  stage.addEventListener('click', (e) => {
    origin(e);
    stage.classList.toggle('zoomed');
  });

  stage.addEventListener('mousemove', (e) => {
    if (stage.classList.contains('zoomed')) origin(e);
  });

  document.addEventListener('keydown', onKey);
  document.body.style.overflow = 'hidden';
  document.body.append(box);
  show();
  part('.lb-close').focus();
}
