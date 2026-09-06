/**
 * Everything the admin client knows how to ask the server.
 *
 * A 401 means the cookie expired rather than that something broke, so it is surfaced as its
 * own type and the shell shows a sign-in prompt instead of a red error.
 */

export class SessionExpired extends Error {
  constructor() {
    super('Your session has ended.');
  }
}

export class ApiError extends Error {}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...(init?.headers ?? {}) },
    credentials: 'same-origin',
  });

  if (response.status === 401) throw new SessionExpired();

  if (!response.ok) {
    // The API reports refusals as { error } so they can be shown verbatim.
    const message = await response
      .json()
      .then((body: { error?: string }) => body?.error)
      .catch(() => undefined);

    throw new ApiError(message ?? `Request failed (${response.status})`);
  }

  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}

export interface Me {
  name: string;
  email: string;
  role: string;
  canEdit: boolean;
}

export interface ProjectSummary {
  id: string;
  slug: string;
  name: string;
  city: string;
  status: string;
  unitsCommitted: number | null;
  total: number;
  available: number;
  held: number;
  reserved: number;
  sold: number;
}

/** The kinds of parking space a unit can have. Mirrors the server enum, sent as its name. */
export const PARKING_SPACE_KINDS = [
  'AboveGroundUncovered',
  'AboveGroundCovered',
  'Underground',
] as const;

export type ParkingSpaceKind = (typeof PARKING_SPACE_KINDS)[number];

export interface UnitType {
  id: string;
  name: string;
}

export interface Unit {
  id: string;
  crt: number | null;
  code: string;
  building: string | null;
  stair: string | null;
  floorLevel: number | null;
  unitTypeId: string;
  orientation: string | null;
  areaBuilt: number | null;
  areaUsable: number;
  areaBalcony: number;
  areaTotal: number;
  basePriceNet: number;
  cesiunePriceNet: number | null;
  reducedVatEligible: boolean;
  parkingAvailable: boolean;
  parkingPriceNet: number | null;
  parkingAdvanceNet: number | null;
  parkingSpaceKind: ParkingSpaceKind | null;
  state: string;
  adminNotes: string | null;
  holdExpiresAt: string | null;
  heldBy: string | null;
}

export interface UnitsResponse {
  project: { id: string; name: string; slug: string };
  types: UnitType[];
  units: Unit[];
}

/**
 * Only the fields the grid may change. State is deliberately not among them, and neither is
 * parkingAvailable: the server derives that from the parking price.
 */
export type UnitEdit = Partial<
  Pick<
    Unit,
    | 'building'
    | 'stair'
    | 'floorLevel'
    | 'unitTypeId'
    | 'orientation'
    | 'areaBuilt'
    | 'areaUsable'
    | 'areaBalcony'
    | 'basePriceNet'
    | 'cesiunePriceNet'
    | 'reducedVatEligible'
    | 'parkingPriceNet'
    | 'parkingAdvanceNet'
    | 'parkingSpaceKind'
    | 'adminNotes'
  >
>;

/**
 * A stage of the sales pipeline, as the server describes it.
 *
 * Not a union type and not a constant list here. The pipeline was inferred from Hubix's
 * documents rather than observed, so it will need correcting; a copy in this file would be a
 * second place to correct and a second place to get it wrong.
 */
export interface PipelineStage {
  state: string;
  label: string;
  /** Hubix's own spreadsheet column, where this stage came from one. */
  sourceColumn: string | null;
  unitState: string;
  recordsDate: boolean;
  /** The reservation field this stage's date lives in, camelCased. */
  dateField: string | null;
}

export interface Pipeline {
  stages: PipelineStage[];
  cancelled: string;
}

export interface Reservation {
  id: string;
  unitId: string;
  unitCode: string;
  unitType: string;
  floorLevel: number | null;
  state: string;
  stateChangedAt: string;
  buyerEmail: string;
  buyerName: string | null;
  buyerType: string;
  contractNumber: string | null;
  notaryLocation: string | null;
  notaryAppointmentAt: string | null;
  feePaidAt: string | null;
  agreementSentAt: string | null;
  signedAgreementAt: string | null;
  cesiuneSignedAt: string | null;
  completedAt: string | null;
  cancelledAt: string | null;
  cancellationReason: string | null;
  internalNotes: string | null;
  reservationFee: number | null;
  totalGross: number;
}

/** Somebody waiting for an apartment type. */
export interface WaitlistEntry {
  id: string;
  typeId: string | null;
  /** Null means they will take any apartment. */
  typeName: string | null;
  email: string;
  name: string | null;
  phone: string | null;
  joinedAt: string;
  notifiedAt: string | null;
}

/** A unit a reservation could be started on. */
export interface ReservationCandidate {
  id: string;
  code: string;
  state: string;
  heldBy: string | null;
}

export interface ReservationsResponse {
  project: { id: string; name: string; reservationFee: number | null };
  reservations: Reservation[];
  candidates: ReservationCandidate[];
}

export type ReservationEdit = Partial<
  Pick<Reservation, 'contractNumber' | 'notaryLocation' | 'notaryAppointmentAt' | 'internalNotes'>
> & { buyerType?: string };

export interface ProjectAsset {
  id: string;
  kind: string;
  fileName: string;
  contentType: string | null;
  sizeBytes: number;
  caption: string | null;
  createdAt: string;
  unitIds: string[];
  /** Signed and time-limited. Never a permanent address. */
  url: string;
}

export interface FloorRow {
  building: string | null;
  level: number;
  floorPlanAssetId: string | null;
}

export interface AssetsResponse {
  assets: ProjectAsset[];
  floors: FloorRow[];
  /** Every building-and-level the units actually occupy, whether or not a floor row exists. */
  levels: { building: string | null; level: number }[];
}

/** A field the importer can fill from a column. */
export interface ImportFieldInfo {
  field: string;
  label: string;
  required: boolean;
  aliases: string[];
}

export interface SheetColumn {
  letter: string;
  header: string | null;
  /** The header cell holds a record rather than a column name. Needs a person. */
  looksLikeData: boolean;
  sample: string[];
  suggested: string | null;
}

export interface PlannedRow {
  sourceRow: number;
  code: string | null;
  action: 'Create' | 'Update' | 'Unchanged' | 'Skipped' | 'Failed';
  reason: string | null;
  /** Where the file's availability disagrees with what we know. Reported, never applied. */
  staleNote: string | null;
  changes: Record<string, { before: unknown; after: unknown }>;
}

export interface ImportPlan {
  canCommit: boolean;
  creates: number;
  updates: number;
  unchanged: number;
  skipped: number;
  failed: number;
  problems: string[];
  newTypes: string[];
  fileHash: string;
  notInFile: { code: string; state: string }[];
  rows: PlannedRow[];
}

export interface ImportPreview {
  worksheets: string[];
  worksheet: string;
  headerRow: number;
  columns: SheetColumn[];
  /** Field key to column letter. */
  mapping: Record<string, string>;
  rowCount: number;
  plan: ImportPlan;
}

export interface ImportProfile {
  id: string;
  name: string;
  worksheetName: string | null;
  columns: Record<string, string>;
  lastUsedAt: string | null;
}

export interface ImportOutcome {
  created: number;
  updated: number;
  skipped: number;
  notes: string[];
}

async function upload<T>(path: string, file: File, fields: Record<string, string>): Promise<T> {
  const body = new FormData();
  body.append('file', file);

  for (const [key, value] of Object.entries(fields)) {
    body.append(key, value);
  }

  // No Content-Type header: the browser sets it with the multipart boundary.
  const response = await fetch(path, { method: 'POST', body, credentials: 'same-origin' });

  if (response.status === 401) throw new SessionExpired();

  if (!response.ok) {
    const message = await response
      .json()
      .then((b: { error?: string }) => b?.error)
      .catch(() => undefined);

    throw new ApiError(message ?? `Request failed (${response.status})`);
  }

  return (await response.json()) as T;
}

export const api = {
  me: () => request<Me>('/api/admin/me'),

  projects: () => request<ProjectSummary[]>('/api/admin/projects'),

  units: (projectId: string) =>
    request<UnitsResponse>(`/api/admin/projects/${projectId}/units`),

  editUnit: (unitId: string, edit: UnitEdit) =>
    request<{ changed: Record<string, unknown> }>(`/api/admin/units/${unitId}`, {
      method: 'PATCH',
      body: JSON.stringify(edit),
    }),

  setWithdrawn: (unitId: string, withdrawn: boolean) =>
    request<{ changed: Record<string, unknown> }>(
      `/api/admin/units/${unitId}/withdrawn`,
      { method: 'POST', body: JSON.stringify({ withdrawn }) },
    ),

  pipeline: () => request<Pipeline>('/api/admin/pipeline'),

  waitlist: (projectId: string) =>
    request<WaitlistEntry[]>(`/api/admin/projects/${projectId}/waitlist`),

  removeFromWaitlist: (entryId: string) =>
    request<{ removed: boolean }>(`/api/admin/waitlist/${entryId}`, { method: 'DELETE' }),

  assets: (projectId: string) =>
    request<AssetsResponse>(`/api/admin/projects/${projectId}/assets`),

  uploadAsset: (projectId: string, file: File, kind: string, caption?: string) =>
    upload<{ id: string; fileName: string }>(
      `/api/admin/projects/${projectId}/assets`,
      file,
      { kind, ...(caption ? { caption } : {}) },
    ),

  setFloorPlan: (
    projectId: string,
    body: { building: string | null; level: number; assetId: string | null },
  ) =>
    request<{ ok: boolean }>(`/api/admin/projects/${projectId}/floors/plan`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  attachAsset: (unitId: string, assetId: string) =>
    request<{ ok: boolean }>(`/api/admin/units/${unitId}/assets/${assetId}`, { method: 'POST' }),

  detachAsset: (unitId: string, assetId: string) =>
    request<{ ok: boolean }>(`/api/admin/units/${unitId}/assets/${assetId}`, { method: 'DELETE' }),

  deleteAsset: (assetId: string) =>
    request<{ ok: boolean }>(`/api/admin/assets/${assetId}`, { method: 'DELETE' }),

  importFields: () => request<ImportFieldInfo[]>('/api/admin/import/fields'),

  importProfiles: (projectId: string) =>
    request<ImportProfile[]>(`/api/admin/projects/${projectId}/import/profiles`),

  saveImportProfile: (
    projectId: string,
    body: { name: string; columns: Record<string, string>; worksheet: string | null },
  ) =>
    request<{ id: string; name: string }>(
      `/api/admin/projects/${projectId}/import/profiles`,
      { method: 'POST', body: JSON.stringify(body) },
    ),

  previewImport: (
    projectId: string,
    file: File,
    options: { mapping?: Record<string, string>; worksheet?: string } = {},
  ) =>
    upload<ImportPreview>(`/api/admin/projects/${projectId}/import/preview`, file, {
      ...(options.mapping ? { mapping: JSON.stringify(options.mapping) } : {}),
      ...(options.worksheet ? { worksheet: options.worksheet } : {}),
    }),

  commitImport: (
    projectId: string,
    file: File,
    options: { mapping: Record<string, string>; worksheet?: string; hash: string },
  ) =>
    upload<ImportOutcome>(`/api/admin/projects/${projectId}/import/commit`, file, {
      mapping: JSON.stringify(options.mapping),
      hash: options.hash,
      ...(options.worksheet ? { worksheet: options.worksheet } : {}),
    }),

  exportUrl: (projectId: string) => `/api/admin/projects/${projectId}/export`,

  reservations: (projectId: string) =>
    request<ReservationsResponse>(`/api/admin/projects/${projectId}/reservations`),

  startReservation: (
    unitId: string,
    buyer?: { email: string; fullName?: string; phone?: string },
  ) =>
    request<{ id: string; state: string }>(`/api/admin/units/${unitId}/reservation`, {
      method: 'POST',
      body: JSON.stringify(buyer ?? {}),
    }),

  moveReservation: (id: string, state: string, reason?: string) =>
    request<{ state: string; unitState: string }>(`/api/admin/reservations/${id}/move`, {
      method: 'POST',
      body: JSON.stringify({ state, reason }),
    }),

  editReservation: (id: string, edit: ReservationEdit) =>
    request<{ changed: Record<string, unknown> }>(`/api/admin/reservations/${id}`, {
      method: 'PATCH',
      body: JSON.stringify(edit),
    }),
};
