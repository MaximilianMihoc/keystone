/// <reference types="vite/client" />
import workerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url';

/**
 * A file as something an <img> can show: the image itself, or the first page of a PDF.
 *
 * Plans, RLVs and brochures mostly arrive as PDFs, and a screen of file names is no way to
 * pick a floor plan. PDFs are rendered here rather than at upload, so nothing is stored and
 * nothing changes on the server. pdf.js is loaded on first use; the rest of the admin never
 * pays for it.
 *
 * Both kinds are read once into a blob URL, keyed by asset id. The signed link is re-issued on
 * every reload, so showing it directly would refetch every render after each attach.
 */
const previews = new Map<string, Promise<string>>();

export function previewImage(assetId: string, url: string, isPdf: boolean): Promise<string> {
  let preview = previews.get(assetId);

  if (!preview) {
    preview = isPdf ? renderFirstPage(url) : readImage(url);
    previews.set(assetId, preview);
    preview.catch(() => previews.delete(assetId));
  }

  return preview;
}

async function readImage(url: string): Promise<string> {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`The file could not be read (${response.status}).`);

  return URL.createObjectURL(await response.blob());
}

/** Long edge in pixels. Big enough for the full-screen preview, so one render serves both. */
const LONG_EDGE = 1800;

async function renderFirstPage(url: string): Promise<string> {
  const pdfjs = await import('pdfjs-dist');
  pdfjs.GlobalWorkerOptions.workerSrc = workerUrl;

  const loading = pdfjs.getDocument({ url });

  try {
    const pdf = await loading.promise;
    const page = await pdf.getPage(1);
    const natural = page.getViewport({ scale: 1 });
    const viewport = page.getViewport({ scale: LONG_EDGE / Math.max(natural.width, natural.height) });

    const canvas = document.createElement('canvas');
    canvas.width = Math.round(viewport.width);
    canvas.height = Math.round(viewport.height);

    await page.render({ canvas, viewport }).promise;

    const blob = await new Promise<Blob | null>((done) => canvas.toBlob(done, 'image/jpeg', 0.9));
    if (!blob) throw new Error('The page could not be drawn.');

    return URL.createObjectURL(blob);
  } finally {
    await loading.destroy();
  }
}
