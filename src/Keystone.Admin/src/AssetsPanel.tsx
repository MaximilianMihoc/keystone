import { useCallback, useEffect, useMemo, useRef, useState, type DragEvent, type MouseEvent } from 'react';
import { api, SessionExpired, type AssetsResponse, type ProjectAsset, type Unit } from './api';
import { previewImage } from './previewImage';

interface Props {
  projectId: string;
  units: Unit[];
  canEdit: boolean;
  onSessionExpired: () => void;
}

/** The kinds worth offering. Anything else is filed as Other. */
const KINDS = [
  { value: 'FloorPlan', label: 'Floor plan' },
  { value: 'Rlv', label: 'RLV' },
  { value: 'Render', label: 'Render' },
  { value: 'SitePlan', label: 'Site plan' },
  { value: 'Prospectus', label: 'Prospectus' },
  { value: 'Brochure', label: 'Brochure' },
  { value: 'Gallery', label: 'Gallery' },
  { value: 'Other', label: 'Other' },
];

interface Tab {
  id: string;
  label: string;
  kinds: string[] | null;
  /** What a file dropped while this tab is open is filed as. Null means guess from its name. */
  uploadAs: string | null;
}

const TABS: Tab[] = [
  { id: 'all', label: 'All', kinds: null, uploadAs: null },
  { id: 'plans', label: 'Floor plans', kinds: ['FloorPlan'], uploadAs: 'FloorPlan' },
  { id: 'renders', label: 'Renders', kinds: ['Render'], uploadAs: 'Render' },
  { id: 'gallery', label: 'Gallery', kinds: ['Gallery'], uploadAs: 'Gallery' },
  { id: 'drawings', label: 'Drawings', kinds: ['Rlv', 'SitePlan'], uploadAs: 'SitePlan' },
  { id: 'documents', label: 'Documents', kinds: ['Prospectus', 'Brochure', 'Other'], uploadAs: 'Other' },
];

/** The server's allow list, less .svg: an SVG opened on its own runs script (issue #31). */
const ACCEPTED = ['.pdf', '.png', '.jpg', '.jpeg', '.webp'];

interface Level {
  building: string | null;
  level: number;
}

interface Upload {
  key: string;
  name: string;
  kind: string;
  sizeBytes: number;
  progress: number;
}

const kindLabel = (kind: string) => KINDS.find((k) => k.value === kind)?.label ?? kind;

function size(bytes: number): string {
  return bytes > 1024 * 1024
    ? `${(bytes / 1024 / 1024).toFixed(1)} MB`
    : `${Math.max(1, Math.round(bytes / 1024))} KB`;
}

function floorKey(building: string | null, level: number): string {
  return `${building ?? ''}|${level}`;
}

const levelMark = (level: number) => (level === 0 ? 'G' : String(level));

function floorName(level: number): string {
  if (level === 0) return 'Ground floor';
  return level < 0 ? `Basement ${-level}` : `Floor ${level}`;
}

const isPdf = (asset: ProjectAsset) =>
  asset.contentType === 'application/pdf' || asset.fileName.toLowerCase().endsWith('.pdf');

/** Drawings sit on a sheet and must be seen whole; renders fill their frame; documents are pages. */
function shape(kind: string): 'sheet' | 'photo' | 'page' {
  if (kind === 'Render' || kind === 'Gallery') return 'photo';
  return kind === 'FloorPlan' || kind === 'Rlv' || kind === 'SitePlan' ? 'sheet' : 'page';
}

function guessKind(fileName: string, tab: Tab): string {
  if (tab.uploadAs) return tab.uploadAs;
  if (/situati|site/i.test(fileName)) return 'SitePlan';
  if (/rlv|releveu/i.test(fileName)) return 'Rlv';
  if (/plan|etaj|parter|floor|nivel/i.test(fileName)) return 'FloorPlan';
  if (/brosur|brochure/i.test(fileName)) return 'Brochure';
  return /\.(png|jpe?g|webp)$/i.test(fileName) ? 'Render' : 'Other';
}

/** "A 1–4, 6 · B G" — compact enough for a tile. */
function describeFloors(levels: Level[]): string {
  const byBuilding = new Map<string, number[]>();

  for (const { building, level } of levels) {
    byBuilding.set(building ?? '', [...(byBuilding.get(building ?? '') ?? []), level]);
  }

  return [...byBuilding]
    .map(([building, list]) => {
      const ranges: [number, number][] = [];

      for (const level of [...list].sort((a, b) => a - b)) {
        const last = ranges.at(-1);
        if (last && last[1] === level - 1) last[1] = level;
        else ranges.push([level, level]);
      }

      const text = ranges
        .map(([from, to]) => (from === to ? levelMark(from) : `${levelMark(from)}–${levelMark(to)}`))
        .join(', ');

      return building ? `${building} ${text}` : text;
    })
    .join(' · ');
}

const hasFiles = (e: DragEvent) => e.dataTransfer.types.includes('Files');

export function AssetsPanel({ projectId, units, canEdit, onSessionExpired }: Props) {
  const [data, setData] = useState<AssetsResponse | null>(null);
  const [errors, setErrors] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [tabId, setTabId] = useState('all');
  const [selected, setSelected] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<string | null>(null);
  const [preview, setPreview] = useState<string | null>(null);
  const [openFloor, setOpenFloor] = useState<string | null>(null);
  const [uploads, setUploads] = useState<Upload[]>([]);
  const [dropOn, setDropOn] = useState(false);
  const [overFloor, setOverFloor] = useState<string | null>(null);
  const [flash, setFlash] = useState<string | null>(null);
  const dragging = useRef<string | null>(null);
  const [draggingPlan, setDraggingPlan] = useState(false);
  const picker = useRef<HTMLInputElement>(null);

  const tab = TABS.find((t) => t.id === tabId) ?? TABS[0];

  const fail = useCallback(
    (e: unknown) => {
      if (e instanceof SessionExpired) onSessionExpired();
      else setErrors([(e as Error).message]);
    },
    [onSessionExpired],
  );

  const reload = useCallback(async () => {
    try {
      setData(await api.assets(projectId));
    } catch (e) {
      fail(e);
    }
  }, [projectId, fail]);

  useEffect(() => {
    setData(null);
    void reload();
  }, [reload]);

  const act = useCallback(
    async (work: () => Promise<unknown>) => {
      setBusy(true);
      setErrors([]);

      try {
        await work();
      } catch (e) {
        fail(e);
      } finally {
        await reload();
        setBusy(false);
      }
    },
    [reload, fail],
  );

  const flashFloor = useCallback((key: string) => {
    setFlash(key);
    setTimeout(() => setFlash((current) => (current === key ? null : current)), 700);
  }, []);

  const uploadFiles = useCallback(
    async (files: File[], floor?: Level) => {
      if (!canEdit || files.length === 0) return;

      const refused: string[] = [];
      const accepted = files.filter((file) => {
        const ok = ACCEPTED.some((ext) => file.name.toLowerCase().endsWith(ext));
        if (!ok) refused.push(`${file.name} was not uploaded. Use a PDF, PNG, JPG or WebP file.`);
        return ok;
      });

      setErrors(refused);
      setOpenFloor(null);
      if (accepted.length === 0) return;

      const queued = accepted.map((file) => ({
        file,
        upload: {
          key: crypto.randomUUID(),
          name: file.name,
          kind: floor ? 'FloorPlan' : guessKind(file.name, tab),
          sizeBytes: file.size,
          progress: 0,
        },
      }));

      setUploads((current) => [...queued.map((q) => q.upload), ...current]);

      const outcomes = await Promise.all(
        queued.map(async ({ file, upload }) => {
          try {
            const saved = await api.uploadAsset(projectId, file, upload.kind, (progress) =>
              setUploads((current) =>
                current.map((u) => (u.key === upload.key ? { ...u, progress } : u)),
              ),
            );

            if (floor) {
              await api.setFloorPlan(projectId, { ...floor, assetId: saved.id });
            }

            return null;
          } catch (e) {
            if (e instanceof SessionExpired) {
              onSessionExpired();
              return null;
            }

            return `${file.name}: ${(e as Error).message}`;
          }
        }),
      );

      await reload();

      const done = new Set<string>(queued.map((q) => q.upload.key));
      setUploads((current) => current.filter((u) => !done.has(u.key)));
      setErrors((current) => [...current, ...outcomes.filter((o): o is string => o !== null)]);

      if (floor) flashFloor(floorKey(floor.building, floor.level));
    },
    [canEdit, tab, projectId, reload, onSessionExpired, flashFloor],
  );

  const setFloorPlan = useCallback(
    (floor: Level, assetId: string | null) => {
      setOpenFloor(null);
      void act(() => api.setFloorPlan(projectId, { ...floor, assetId })).then(() =>
        flashFloor(floorKey(floor.building, floor.level)),
      );
    },
    [act, projectId, flashFloor],
  );

  // A file dropped beside the drop zones would otherwise open in the tab and lose the admin.
  useEffect(() => {
    const swallow = (e: globalThis.DragEvent) => {
      if (e.dataTransfer?.types.includes('Files')) e.preventDefault();
    };

    window.addEventListener('dragover', swallow);
    window.addEventListener('drop', swallow);

    return () => {
      window.removeEventListener('dragover', swallow);
      window.removeEventListener('drop', swallow);
    };
  }, []);

  const plansByFloor = useMemo(() => {
    const map = new Map<string, string>();
    for (const floor of data?.floors ?? []) {
      if (floor.floorPlanAssetId) map.set(floorKey(floor.building, floor.level), floor.floorPlanAssetId);
    }
    return map;
  }, [data]);

  const unitsByFloor = useMemo(() => {
    const map = new Map<string, Unit[]>();
    for (const unit of [...units].sort((a, b) => a.code.localeCompare(b.code, undefined, { numeric: true }))) {
      if (unit.floorLevel === null) continue;
      const key = floorKey(unit.building, unit.floorLevel);
      map.set(key, [...(map.get(key) ?? []), unit]);
    }
    return map;
  }, [units]);

  /** Top floor first, as the building stands. */
  const levels = useMemo(
    () =>
      [...(data?.levels ?? [])].sort(
        (a, b) => (a.building ?? '').localeCompare(b.building ?? '') || b.level - a.level,
      ),
    [data],
  );

  const floorsOf = useCallback(
    (assetId: string) =>
      levels.filter((l) => plansByFloor.get(floorKey(l.building, l.level)) === assetId),
    [levels, plansByFloor],
  );

  /** Where a file actually shows: its own apartments, plus every apartment on a floor it plans. */
  const shownOn = useCallback(
    (asset: ProjectAsset) => {
      const ids = new Set(asset.unitIds);

      if (asset.kind === 'FloorPlan') {
        for (const l of floorsOf(asset.id)) {
          for (const unit of unitsByFloor.get(floorKey(l.building, l.level)) ?? []) ids.add(unit.id);
        }
      }

      return ids;
    },
    [floorsOf, unitsByFloor],
  );

  const usage = useCallback(
    (asset: ProjectAsset): { text: string; unused: boolean } => {
      if (asset.kind === 'Gallery') return { text: 'In the project slideshow', unused: false };

      if (asset.kind === 'FloorPlan') {
        const floors = floorsOf(asset.id);
        if (floors.length > 0) return { text: `Floors ${describeFloors(floors)}`, unused: false };
      }

      const count = shownOn(asset).size;

      if (count > 0) return { text: `On ${count} apartment${count === 1 ? '' : 's'}`, unused: false };

      return { text: asset.kind === 'FloorPlan' ? 'Not on a floor yet' : 'Not on any apartment', unused: true };
    },
    [floorsOf, shownOn],
  );

  const visible = (data?.assets ?? []).filter((a) => !tab.kinds || tab.kinds.includes(a.kind));
  const selectedAsset = data?.assets.find((a) => a.id === selected) ?? null;
  const floorPlans = data?.assets.filter((a) => a.kind === 'FloorPlan') ?? [];

  // Escape backs out of whatever is most recently open; arrows page through the preview.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (preview) {
        const index = visible.findIndex((a) => a.id === preview);

        if (e.key === 'Escape') setPreview(null);
        else if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
          const step = e.key === 'ArrowLeft' ? -1 : 1;
          setPreview(visible[(index + step + visible.length) % visible.length]?.id ?? null);
        }

        return;
      }

      if (e.key !== 'Escape') return;
      if (confirm) setConfirm(null);
      else if (openFloor) setOpenFloor(null);
      else if (selected) setSelected(null);
    };

    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });

  if (!data) {
    return <p className="empty">Loading files…</p>;
  }

  const plannedCount = levels.filter((l) => plansByFloor.has(floorKey(l.building, l.level))).length;
  const buildings = [...new Set(levels.map((l) => l.building))];

  return (
    <div className="files">
      {errors.length > 0 && (
        <div className="banner error" role="alert">
          <ul className="notes">
            {errors.map((message) => (
              <li key={message}>{message}</li>
            ))}
          </ul>
          <button className="link" type="button" onClick={() => setErrors([])}>
            Dismiss
          </button>
        </div>
      )}

      <div className="files-layout">
        <section
          className={`library${dropOn ? ' drop-on' : ''}`}
          aria-label="Files"
          onDragOver={(e) => {
            if (!canEdit || !hasFiles(e)) return;
            e.preventDefault();
            setDropOn(true);
          }}
          onDragLeave={(e) => {
            if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setDropOn(false);
          }}
          onDrop={(e) => {
            if (!hasFiles(e)) return;
            e.preventDefault();
            setDropOn(false);
            void uploadFiles([...e.dataTransfer.files]);
          }}
        >
          <div className="lib-bar">
            <div className="lib-tabs" role="tablist" aria-label="File type">
              {TABS.map((t) => (
                <button
                  key={t.id}
                  className="lib-tab"
                  type="button"
                  role="tab"
                  aria-selected={t.id === tab.id}
                  onClick={() => {
                    setTabId(t.id);
                    setConfirm(null);
                  }}
                >
                  {t.label}
                  <span className="n">
                    {data.assets.filter((a) => !t.kinds || t.kinds.includes(a.kind)).length}
                  </span>
                </button>
              ))}
            </div>

            <div className="lib-actions">
              <span className="count">Links to files expire</span>
              {canEdit && (
                <button className="btn btn-small" type="button" onClick={() => picker.current?.click()}>
                  Upload files
                </button>
              )}
              <input
                ref={picker}
                type="file"
                accept={ACCEPTED.join(',')}
                multiple
                hidden
                onChange={(e) => {
                  void uploadFiles([...(e.target.files ?? [])]);
                  e.target.value = '';
                }}
              />
            </div>
          </div>

          <div className="tiles">
            {uploads
              .filter((u) => !tab.kinds || tab.kinds.includes(u.kind))
              .map((u) => (
                <article key={u.key} className="tile uploading">
                  <span className={`thumb ${shape(u.kind)}`}>
                    <span className="thumb-loading" />
                  </span>
                  <div className="tile-progress">
                    <i style={{ width: `${Math.round(u.progress * 100)}%` }} />
                  </div>
                  <span className="kind" data-k={u.kind}>
                    {kindLabel(u.kind)}
                  </span>
                  <div className="tile-body">
                    <span className="tile-name">{u.name}</span>
                    <div className="tile-meta">
                      <span>{size(u.sizeBytes)}</span>
                      <span>{u.progress < 1 ? `Uploading ${Math.round(u.progress * 100)}%` : 'Saving…'}</span>
                    </div>
                  </div>
                </article>
              ))}

            {visible.map((asset) => {
              const used = usage(asset);
              const draggable = canEdit && asset.kind === 'FloorPlan';

              return (
                <article
                  key={asset.id}
                  className={`tile${asset.id === selected ? ' selected' : ''}`}
                  draggable={draggable}
                  onDragStart={(e) => {
                    if (!draggable) return;
                    dragging.current = asset.id;
                    e.dataTransfer.setData('text/plain', asset.fileName);
                    e.dataTransfer.effectAllowed = 'link';
                    setDraggingPlan(true);
                  }}
                  onDragEnd={() => {
                    dragging.current = null;
                    setDraggingPlan(false);
                    setOverFloor(null);
                  }}
                >
                  <button
                    className="tile-open"
                    type="button"
                    aria-label={`Details for ${asset.fileName}`}
                    onClick={() => setSelected(asset.id === selected ? null : asset.id)}
                  >
                    <span className={`thumb ${shape(asset.kind)}`}>
                      <Preview asset={asset} />
                    </span>
                  </button>

                  <span className="kind" data-k={asset.kind}>
                    {kindLabel(asset.kind)}
                  </span>

                  <div className="tile-actions">
                    <button
                      className="icon-btn"
                      type="button"
                      title="Preview"
                      aria-label={`Preview ${asset.fileName}`}
                      onClick={() => setPreview(asset.id)}
                    >
                      <EyeIcon />
                    </button>
                    {canEdit && (
                      <button
                        className="icon-btn danger"
                        type="button"
                        title="Delete"
                        aria-label={`Delete ${asset.fileName}`}
                        onClick={() => setConfirm(asset.id)}
                      >
                        <TrashIcon />
                      </button>
                    )}
                  </div>

                  <div className="tile-body">
                    <button
                      className="tile-name"
                      type="button"
                      onClick={() => setSelected(asset.id === selected ? null : asset.id)}
                    >
                      {asset.fileName}
                    </button>
                    <div className="tile-meta">
                      <span>{size(asset.sizeBytes)}</span>
                      <span className={used.unused ? 'unused' : undefined}>{used.text}</span>
                    </div>
                  </div>

                  {confirm === asset.id && (
                    <div className="tile-confirm" role="alertdialog" aria-label={`Delete ${asset.fileName}`}>
                      <strong>Delete this file?</strong>
                      <p>
                        {used.unused
                          ? 'It is not shown anywhere yet.'
                          : 'It comes off every apartment and floor showing it.'}{' '}
                        This cannot be undone.
                      </p>
                      <div>
                        <button
                          className="btn btn-small btn-danger"
                          type="button"
                          disabled={busy}
                          onClick={() => {
                            setConfirm(null);
                            if (selected === asset.id) setSelected(null);
                            void act(() => api.deleteAsset(asset.id));
                          }}
                        >
                          Delete
                        </button>
                        <button className="btn btn-small btn-ghost" type="button" onClick={() => setConfirm(null)}>
                          Keep
                        </button>
                      </div>
                    </div>
                  )}
                </article>
              );
            })}

            {canEdit && (
              <button className="tile-add" type="button" onClick={() => picker.current?.click()}>
                <UploadIcon />
                <strong>Add {tab.id === 'all' ? 'files' : tab.label.toLowerCase()}</strong>
                <span>
                  Drop them anywhere here
                  <br />
                  PDF, PNG, JPG or WebP · up to 25 MB
                </span>
              </button>
            )}

            {!canEdit && visible.length === 0 && (
              <p className="empty tiles-empty">No {tab.id === 'all' ? 'files' : tab.label.toLowerCase()} yet.</p>
            )}
          </div>
        </section>

        <aside className={`stack${draggingPlan ? ' dragging-plan' : ''}`} aria-label="Floor plans by floor">
          <div className="stack-head">
            <h2>
              Floor plans
              <span className="count">
                {plannedCount} of {levels.length} floors
              </span>
            </h2>
            {levels.length > 0 && (
              <div className="coverage" role="img" aria-label={`${plannedCount} of ${levels.length} floors have a plan`}>
                {levels.map((l) => (
                  <i
                    key={floorKey(l.building, l.level)}
                    className={plansByFloor.has(floorKey(l.building, l.level)) ? 'on' : undefined}
                  />
                ))}
              </div>
            )}
            <p className="stack-hint">
              {levels.length === 0
                ? 'Floors appear here once apartments have a floor number.'
                : canEdit
                  ? 'Drag a floor plan onto a floor, or click a floor to choose. The plan shows on every apartment on that floor.'
                  : 'A floor plan shows on every apartment on its floor.'}
            </p>
          </div>

          {buildings.map((building) => {
            const own = levels.filter((l) => l.building === building);
            const apartments = own.reduce(
              (sum, l) => sum + (unitsByFloor.get(floorKey(l.building, l.level))?.length ?? 0),
              0,
            );

            return (
              <section key={building ?? ''}>
                <header className="bld-head">
                  <span>{building ? `Building ${building}` : 'Floors'}</span>
                  <span>
                    {own.length} floor{own.length === 1 ? '' : 's'} · {apartments} apartments
                  </span>
                </header>

                <ol className="floors">
                  {own.map((floor) => {
                    const key = floorKey(floor.building, floor.level);
                    const plan = data.assets.find((a) => a.id === plansByFloor.get(key));
                    const onFloor = unitsByFloor.get(key) ?? [];
                    const open = openFloor === key;

                    return (
                      <li
                        key={key}
                        className={[
                          'floor',
                          plan ? '' : 'no-plan',
                          open ? 'open' : '',
                          overFloor === key ? 'over' : '',
                          flash === key ? 'flash' : '',
                        ].filter(Boolean).join(' ')}
                        onDragOver={(e) => {
                          if (!canEdit || (!hasFiles(e) && !dragging.current)) return;
                          e.preventDefault();
                          e.stopPropagation();
                          setOverFloor(key);
                        }}
                        onDragLeave={(e) => {
                          if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setOverFloor(null);
                        }}
                        onDrop={(e) => {
                          e.preventDefault();
                          e.stopPropagation();
                          setOverFloor(null);
                          setDraggingPlan(false);

                          if (hasFiles(e)) void uploadFiles([...e.dataTransfer.files], floor);
                          else if (dragging.current) setFloorPlan(floor, dragging.current);
                        }}
                      >
                        <button
                          className="floor-row"
                          type="button"
                          aria-expanded={canEdit ? open : undefined}
                          disabled={!canEdit}
                          onClick={() => setOpenFloor(open ? null : key)}
                        >
                          <span className="lvl">{levelMark(floor.level)}</span>
                          <span className="floor-thumb">{plan ? <Preview asset={plan} /> : '+'}</span>
                          <span className="floor-text">
                            <strong>{floorName(floor.level)}</strong>
                            <span className="floor-sub">
                              {plan ? plan.fileName : canEdit ? 'No plan · drop one here' : 'No plan'}
                            </span>
                          </span>
                          <span className="floor-units">{onFloor.length} apts</span>
                        </button>

                        {open && (
                          <div className="floor-picker">
                            {floorPlans.map((p) => (
                              <button
                                key={p.id}
                                className="pick"
                                type="button"
                                aria-pressed={p.id === plan?.id}
                                disabled={busy}
                                onClick={() => setFloorPlan(floor, p.id)}
                              >
                                <span className="pick-thumb">
                                  <Preview asset={p} />
                                </span>
                                <span className="pick-name">{p.fileName}</span>
                              </button>
                            ))}

                            <div className="picker-foot">
                              <span className="muted">
                                {floorPlans.length === 0
                                  ? 'Upload a file as a floor plan first.'
                                  : onFloor.length > 0
                                    ? `Shows on ${onFloor[0].code} to ${onFloor[onFloor.length - 1].code}`
                                    : 'No apartments on this floor yet.'}
                              </span>
                              {plan && (
                                <button className="link" type="button" disabled={busy} onClick={() => setFloorPlan(floor, null)}>
                                  Remove plan from this floor
                                </button>
                              )}
                            </div>
                          </div>
                        )}
                      </li>
                    );
                  })}
                </ol>
              </section>
            );
          })}
        </aside>
      </div>

      {selectedAsset && (
        <AssetDrawer
          asset={selectedAsset}
          levels={levels}
          unitsByFloor={unitsByFloor}
          plansByFloor={plansByFloor}
          looseUnits={units.filter((u) => u.floorLevel === null)}
          shownCount={shownOn(selectedAsset).size}
          floorsText={describeFloors(floorsOf(selectedAsset.id))}
          totalUnits={units.length}
          canEdit={canEdit}
          busy={busy}
          confirming={confirm === selectedAsset.id}
          onKind={(kind) => void act(() => api.setAssetKind(selectedAsset.id, kind))}
          onUnits={(unitIds, on) =>
            void act(() =>
              Promise.all(
                unitIds.map((id) =>
                  on ? api.attachAsset(id, selectedAsset.id) : api.detachAsset(id, selectedAsset.id),
                ),
              ),
            )
          }
          onPreview={() => setPreview(selectedAsset.id)}
          onConfirm={(on) => setConfirm(on ? selectedAsset.id : null)}
          onDelete={() => {
            setConfirm(null);
            setSelected(null);
            void act(() => api.deleteAsset(selectedAsset.id));
          }}
          onClose={() => setSelected(null)}
        />
      )}

      {preview && (
        <Lightbox
          assets={visible}
          current={preview}
          onShow={setPreview}
          onClose={() => setPreview(null)}
        />
      )}
    </div>
  );
}

/** An image of the file, or a placeholder while pdf.js draws it. */
function Preview({ asset }: { asset: ProjectAsset }) {
  const [src, setSrc] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let live = true;

    previewImage(asset.id, asset.url, isPdf(asset)).then(
      (image) => live && setSrc(image),
      () => live && setFailed(true),
    );

    return () => {
      live = false;
    };
  }, [asset]);

  if (failed) {
    return <span className="thumb-failed">{isPdf(asset) ? 'PDF' : 'Image'} · no preview</span>;
  }

  return src ? <img src={src} alt="" draggable={false} /> : <span className="thumb-loading" />;
}

function AssetDrawer({
  asset,
  levels,
  unitsByFloor,
  plansByFloor,
  looseUnits,
  shownCount,
  floorsText,
  totalUnits,
  canEdit,
  busy,
  confirming,
  onKind,
  onUnits,
  onPreview,
  onConfirm,
  onDelete,
  onClose,
}: {
  asset: ProjectAsset;
  levels: Level[];
  unitsByFloor: Map<string, Unit[]>;
  plansByFloor: Map<string, string>;
  looseUnits: Unit[];
  shownCount: number;
  floorsText: string;
  totalUnits: number;
  canEdit: boolean;
  busy: boolean;
  confirming: boolean;
  onKind: (kind: string) => void;
  onUnits: (unitIds: string[], on: boolean) => void;
  onPreview: () => void;
  onConfirm: (on: boolean) => void;
  onDelete: () => void;
  onClose: () => void;
}) {
  const rows = [
    ...levels.map((l) => {
      const key = floorKey(l.building, l.level);
      return {
        key,
        label: `${l.building ? `${l.building} · ` : ''}${levelMark(l.level)}`,
        units: unitsByFloor.get(key) ?? [],
        viaFloor: asset.kind === 'FloorPlan' && plansByFloor.get(key) === asset.id,
      };
    }),
    ...(looseUnits.length > 0 ? [{ key: 'none', label: 'No floor', units: looseUnits, viaFloor: false }] : []),
  ].filter((row) => asset.kind !== 'Gallery' && row.units.length > 0);

  return (
    <aside className="drawer asset-drawer">
      <header className="drawer-head">
        <div>
          <span className="kind" data-k={asset.kind}>
            {kindLabel(asset.kind)}
          </span>
          <h2>{asset.fileName}</h2>
        </div>
        <button className="link" type="button" onClick={onClose}>
          Close
        </button>
      </header>

      <button
        className={`drawer-preview ${shape(asset.kind)}`}
        type="button"
        aria-label="Open full-screen preview"
        onClick={onPreview}
      >
        <Preview asset={asset} />
        <em>Full screen</em>
      </button>

      <dl className="file-facts">
        <div>
          <dt>Format</dt>
          <dd>{isPdf(asset) ? 'PDF' : (asset.contentType?.replace('image/', '').toUpperCase() ?? '—')}</dd>
        </div>
        <div>
          <dt>Size</dt>
          <dd>{size(asset.sizeBytes)}</dd>
        </div>
        <div>
          <dt>Added</dt>
          <dd>{new Date(asset.createdAt).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })}</dd>
        </div>
      </dl>

      <label className="field file-kind">
        <span>File type</span>
        <select value={asset.kind} disabled={!canEdit || busy} onChange={(e) => onKind(e.target.value)}>
          {KINDS.map((k) => (
            <option key={k.value} value={k.value}>
              {k.label}
            </option>
          ))}
        </select>
      </label>

      <h3 className="drawer-split">
        Shown on
        <span>{asset.kind === 'Gallery' ? 'The project page' : `${shownCount} of ${totalUnits} apartments`}</span>
      </h3>

      <p className="hint">
        {asset.kind === 'Gallery'
          ? 'Plays in the slideshow on the project page, oldest upload first. PDFs are left out.'
          : asset.kind === 'FloorPlan' && floorsText
          ? `Amber apartments get this plan from their floor (${floorsText}). Change that in the floor list.`
          : canEdit
            ? 'Click an apartment to show or hide this file on its page.'
            : 'The apartments whose page shows this file.'}
      </p>

      {rows.map((row) => {
        const own = row.viaFloor ? [] : row.units;
        const allOn = own.every((u) => asset.unitIds.includes(u.id));

        return (
          <div key={row.key} className="unit-row">
            <span className="unit-floor">{row.label}</span>
            <div className="chips">
              {row.units.map((unit) =>
                row.viaFloor ? (
                  <span key={unit.id} className="chip via" title="Shown through this floor's plan">
                    {unit.code}
                  </span>
                ) : (
                  <button
                    key={unit.id}
                    className="chip"
                    type="button"
                    aria-pressed={asset.unitIds.includes(unit.id)}
                    disabled={!canEdit || busy}
                    onClick={() => onUnits([unit.id], !asset.unitIds.includes(unit.id))}
                  >
                    {unit.code}
                  </button>
                ),
              )}
            </div>
            {canEdit && !row.viaFloor ? (
              <button
                className="link"
                type="button"
                disabled={busy}
                onClick={() =>
                  onUnits(
                    own.filter((u) => asset.unitIds.includes(u.id) === allOn).map((u) => u.id),
                    !allOn,
                  )
                }
              >
                {allOn ? 'None' : 'All'}
              </button>
            ) : (
              <span />
            )}
          </div>
        );
      })}

      <div className="drawer-foot">
        <button className="btn btn-small btn-ghost" type="button" onClick={onPreview}>
          Preview
        </button>
        {canEdit &&
          (confirming ? (
            <span className="confirm-inline">
              Delete for good?
              <button className="btn btn-small btn-danger" type="button" disabled={busy} onClick={onDelete}>
                Delete
              </button>
              <button className="btn btn-small btn-ghost" type="button" onClick={() => onConfirm(false)}>
                Keep
              </button>
            </span>
          ) : (
            <button className="btn btn-small quiet" type="button" onClick={() => onConfirm(true)}>
              Delete file
            </button>
          ))}
      </div>
    </aside>
  );
}

function Lightbox({
  assets,
  current,
  onShow,
  onClose,
}: {
  assets: ProjectAsset[];
  current: string;
  onShow: (id: string) => void;
  onClose: () => void;
}) {
  const [zoom, setZoom] = useState<string | null>(null);
  const index = Math.max(0, assets.findIndex((a) => a.id === current));
  const asset = assets[index];

  useEffect(() => setZoom(null), [current]);

  // The page behind must not scroll while the preview covers it.
  useEffect(() => {
    document.body.style.overflow = 'hidden';
    return () => {
      document.body.style.overflow = '';
    };
  }, []);

  if (!asset) return null;

  const step = (by: number) => onShow(assets[(index + by + assets.length) % assets.length].id);

  const origin = (e: MouseEvent<HTMLElement>) => {
    const box = e.currentTarget.getBoundingClientRect();
    return `${((e.clientX - box.left) / box.width) * 100}% ${((e.clientY - box.top) / box.height) * 100}%`;
  };

  return (
    <div className="lightbox" role="dialog" aria-modal="true" aria-label={asset.fileName}>
      <div className="lb-top">
        <span className="kind" data-k={asset.kind}>
          {kindLabel(asset.kind)}
        </span>
        <strong>{asset.fileName}</strong>
        <span className="lb-count">
          {index + 1} / {assets.length}
        </span>
        <span className="spacer" />
        <span className="lb-count">Click to zoom · ← → to browse</span>
        <a className="lb-btn" href={asset.url} target="_blank" rel="noreferrer">
          Open original
        </a>
        <button className="lb-btn" type="button" onClick={onClose}>
          Close
        </button>
      </div>

      <div className="lb-stage">
        {assets.length > 1 && (
          <button className="lb-nav lb-prev" type="button" aria-label="Previous file" onClick={() => step(-1)}>
            ‹
          </button>
        )}

        <span
          className={`lb-image${zoom ? ' zoomed' : ''}`}
          style={zoom ? { transformOrigin: zoom } : undefined}
          onClick={(e) => setZoom(zoom ? null : origin(e))}
          onMouseMove={(e) => zoom && setZoom(origin(e))}
        >
          <Preview asset={asset} />
        </span>

        {assets.length > 1 && (
          <button className="lb-nav lb-next" type="button" aria-label="Next file" onClick={() => step(1)}>
            ›
          </button>
        )}
      </div>
    </div>
  );
}

function EyeIcon() {
  return (
    <svg viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" strokeWidth="1.4" aria-hidden="true">
      <path d="M1.5 8s2.4-4.5 6.5-4.5S14.5 8 14.5 8 12.1 12.5 8 12.5 1.5 8 1.5 8z" />
      <circle cx="8" cy="8" r="2" />
    </svg>
  );
}

function TrashIcon() {
  return (
    <svg viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" strokeWidth="1.4" aria-hidden="true">
      <path d="M2.5 4h11M6 4V2.5h4V4M4 4l.7 9.5h6.6L12 4" />
    </svg>
  );
}

function UploadIcon() {
  return (
    <svg viewBox="0 0 16 16" width="26" height="26" fill="none" stroke="currentColor" strokeWidth="1.1" aria-hidden="true">
      <path d="M8 10.5V2.5M4.5 6 8 2.5 11.5 6M2.5 10v3.5h11V10" />
    </svg>
  );
}
