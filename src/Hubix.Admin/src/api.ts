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
  /** Null until somebody publishes it; the public site gates on exactly this. */
  publishedAt: string | null;
  developerId: string | null;
  total: number;
  available: number;
  held: number;
  reserved: number;
  sold: number;
}

/** Mirrors the server enum. Draft is where a project starts. */
export const PROJECT_STATUSES = [
  'Draft',
  'Upcoming',
  'Selling',
  'SoldOut',
  'Delivered',
  'Archived',
] as const;

export type ProjectStatus = (typeof PROJECT_STATUSES)[number];

/** How the homes are meant to be let. Drives the yield model Hubix publishes. */
export const RENTAL_PROFILES = ['ShortTerm', 'MidTerm', 'LongTerm', 'Mixed'] as const;

export type RentalProfile = (typeof RENTAL_PROFILES)[number];

/** The two languages the site offers. Romanian is the one Hubix writes in. */
export const CULTURES = ['en-GB', 'ro-RO'] as const;

export interface Developer {
  id: string;
  name: string;
  description: string | null;
  yearsActive: number | null;
  unitsBuilt: number | null;
  website: string | null;
  /** Shown by the delete button, so "this cannot go" is visible before it is clicked. */
  projectCount: number;
}

export interface DeveloperEdit {
  name: string;
  description: string | null;
  yearsActive: number | null;
  unitsBuilt: number | null;
  website: string | null;
}

/** Everything the setup form owns. Unlike a unit edit, null here means null. */
export interface ProjectEdit {
  name: string;
  city: string;
  slug: string;
  developerId: string | null;
  status: ProjectStatus;
  unitsCommitted: number | null;
  deliveryDate: string | null;
  deliveryGraceMonths: number;
  rentalProfile: RentalProfile | null;
  reservationFee: number | null;
  currency: string;
  holdDurationHours: number;
  mapsUrl: string | null;
}

export interface ProjectDetails extends ProjectEdit {
  id: string;
  publishedAt: string | null;
  /** Publishing is refused below one, so the button can say why before it is pressed. */
  units: number;
}

export interface ProjectTranslationRow {
  culture: string;
  /** Romanian is what the public pages fall back to, so it is labelled as the source. */
  isSource: boolean;
  city: string | null;
  summary: string | null;
  body: string | null;
  penaltyClause: string | null;
  parking: string | null;
}

export interface ProjectSetup {
  project: ProjectDetails;
  developers: { id: string; name: string }[];
  /** Always one row per offered language, blank where nothing is stored yet. */
  translations: ProjectTranslationRow[];
}

/** How a payment's apartment portion is worked out. */
export const TRANCHE_BASES = ['AdvancePercentage', 'Percentage', 'Remainder'] as const;

export type TrancheBasis = (typeof TRANCHE_BASES)[number];

export const TRANCHE_DUES = ['AtSigning', 'Interim', 'AtDelivery'] as const;

export type TrancheDue = (typeof TRANCHE_DUES)[number];

export interface Tranche {
  label: string;
  basis: TrancheBasis;
  /** Exactly one payment collects the cesiune; it is paid once. */
  includesCesiune: boolean;
  due: TrancheDue;
  /** Required when basis is Percentage, ignored otherwise. */
  percentage: number | null;
}

export interface PricingRule {
  id: string;
  effectiveFrom: string;
  advancePct: number;
  vatStandard: number;
  cesiuneVatPct: number;
  vatReduced: number | null;
  tranches: Tranche[];
}

/** A plan without its identity — what the editor holds and sends. */
export type PricingDraft = Omit<PricingRule, 'id' | 'effectiveFrom'>;

export interface PricingResponse {
  /** Null until somebody sets one. Every price on the public site depends on it. */
  current: PricingRule | null;
  /** Plans dated ahead, soonest first. They sit inert until their day. */
  scheduled: PricingRule[];
  sample: {
    code: string;
    basePriceNet: number;
    cesiuneNet: number;
    reducedVatEligible: boolean;
  } | null;
}

export interface PricingPreview {
  /** Empty means the plan is safe to save. */
  problems: string[];
  schedule: {
    against: string | null;
    basePriceNet: number;
    cesiuneNet: number;
    developerPriceNet: number;
    vatRate: number;
    totalNet: number;
    totalVat: number;
    totalGross: number;
    tranches: {
      label: string;
      due: TrancheDue;
      percentage: number | null;
      apartmentNet: number;
      cesiuneNet: number;
      net: number;
      vat: number;
      gross: number;
    }[];
  } | null;
}

export interface UnitTypeRow {
  id: string;
  /** Canonical Romanian, and the importer's match key. Not editable. */
  name: string;
  sortOrder: number;
  units: number;
  waiting: number;
  displayNames: { culture: string; displayName: string }[];
}

export interface FloorRow {
  id: string;
  building: string | null;
  level: number;
  label: string | null;
  hasPlan: boolean;
}

export const STAFF_ROLES = ['Admin', 'Sales', 'Viewer'] as const;

export type StaffRole = (typeof STAFF_ROLES)[number];

export interface StaffMember {
  id: string;
  email: string;
  name: string;
  role: StaffRole;
  /** Inactive keeps the history but refuses them a sign-in link. */
  isActive: boolean;
}

/** What creating an apartment by hand needs. The four required fields match the importer's. */
export interface UnitCreate {
  code: string;
  unitTypeId: string;
  areaUsable: number;
  basePriceNet: number;
  crt: number | null;
  building: string | null;
  stair: string | null;
  floorLevel: number | null;
  orientation: string | null;
  areaBuilt: number | null;
  areaBalcony: number | null;
  cesiunePriceNet: number | null;
  reducedVatEligible: boolean;
  adminNotes: string | null;
}

export interface ProjectStructure {
  floors: FloorRow[];
  /** Levels the apartments actually claim, joined by building and level, not by key. */
  occupied: { building: string | null; level: number; units: number }[];
}

/** The apartment types, which belong to the portal rather than to any one project. */
export interface UnitTypeCatalogue {
  types: UnitTypeRow[];
  cultures: string[];
}

/** Mirrors the server enum, sent as its name rather than its ordinal. */
export const PARKING_SPACE_KINDS = [
  'AboveGroundUncovered',
  'AboveGroundCovered',
  'Underground',
] as const;

export type ParkingSpaceKind = (typeof PARKING_SPACE_KINDS)[number];

/** One numbered space in a project's car park. */
export interface ParkingSpace {
  id: string;
  code: string;
  kind: ParkingSpaceKind;
  /** Signed: underground negative, ground 0, above ground positive. */
  level: number;
  state: string;
  adminNotes: string | null;
  /** The apartment this space is held or reserved against, when it is. */
  heldForUnit: string | null;
}

/** What a kind of space costs here. A space of a kind with no price is not for sale. */
export interface ParkingPrice {
  kind: ParkingSpaceKind;
  priceNet: number;
  depositNet: number;
}

export interface ParkingResponse {
  maxPerReservation: number;
  prices: ParkingPrice[];
  spaces: ParkingSpace[];
}

export type ParkingSpaceEdit = Partial<
  Pick<ParkingSpace, 'code' | 'kind' | 'level' | 'adminNotes'>
>;

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
  state: string;
  adminNotes: string | null;
  holdExpiresAt: string | null;
  heldBy: string | null;
  /** A reservation stands behind the state, so the pipeline owns it and this grid does not. */
  hasReservation: boolean;
}

export interface UnitsResponse {
  project: { id: string; name: string; slug: string };
  types: UnitType[];
  units: Unit[];
}

/** Only the fields the grid may change. State is deliberately not among them. */
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
  parkingNet: number;
  parkingDepositNet: number;
  parking: ReservationParking[];
}

/** One space on a sale, with what was agreed for it. */
export interface ReservationParking {
  id: string;
  code: string;
  kind: ParkingSpaceKind;
  level: number;
  /** Null only for a space that joined before prices were kept per space. */
  priceNet: number | null;
}

/** A free bay this buyer could still add, at today's price. */
export interface AvailableParking {
  id: string;
  code: string;
  kind: ParkingSpaceKind;
  level: number;
  priceNet: number;
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

  developers: () => request<Developer[]>('/api/admin/developers'),

  createDeveloper: (edit: DeveloperEdit) =>
    request<{ id: string }>('/api/admin/developers', {
      method: 'POST',
      body: JSON.stringify(edit),
    }),

  editDeveloper: (developerId: string, edit: DeveloperEdit) =>
    request<{ changed: Record<string, unknown> }>(`/api/admin/developers/${developerId}`, {
      method: 'PUT',
      body: JSON.stringify(edit),
    }),

  deleteDeveloper: (developerId: string) =>
    request<void>(`/api/admin/developers/${developerId}`, { method: 'DELETE' }),

  projectSetup: (projectId: string) =>
    request<ProjectSetup>(`/api/admin/projects/${projectId}/setup`),

  createProject: (body: { name: string; city: string; slug: string | null; developerId: string | null }) =>
    request<{ id: string }>('/api/admin/projects', {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  editProject: (projectId: string, edit: ProjectEdit) =>
    request<{ changed: Record<string, unknown> }>(`/api/admin/projects/${projectId}`, {
      method: 'PUT',
      body: JSON.stringify(edit),
    }),

  deleteProject: (projectId: string) =>
    request<void>(`/api/admin/projects/${projectId}`, { method: 'DELETE' }),

  setProjectPublished: (projectId: string, published: boolean) =>
    request<{ changed: Record<string, unknown> }>(`/api/admin/projects/${projectId}/published`, {
      method: 'POST',
      body: JSON.stringify({ published }),
    }),

  staff: () => request<StaffMember[]>('/api/admin/staff'),

  createStaff: (body: { email: string; name: string; role: StaffRole; isActive: boolean }) =>
    request<{ id: string }>('/api/admin/staff', {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  editStaff: (staffId: string, body: { name: string; role: StaffRole; isActive: boolean }) =>
    request<{ changed: Record<string, unknown> }>(`/api/admin/staff/${staffId}`, {
      method: 'PUT',
      body: JSON.stringify(body),
    }),

  createUnit: (projectId: string, body: UnitCreate) =>
    request<{ id: string }>(`/api/admin/projects/${projectId}/units`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  deleteUnit: (unitId: string) => request<void>(`/api/admin/units/${unitId}`, { method: 'DELETE' }),

  renameUnit: (unitId: string, code: string) =>
    request<{ changed: Record<string, unknown> }>(`/api/admin/units/${unitId}/code`, {
      method: 'POST',
      body: JSON.stringify({ code }),
    }),

  pricing: (projectId: string) =>
    request<PricingResponse>(`/api/admin/projects/${projectId}/pricing`),

  previewPricing: (projectId: string, body: {
    rule: PricingDraft;
    samplePriceNet: number | null;
    sampleCesiuneNet: number | null;
    reducedVat: boolean;
  }) =>
    request<PricingPreview>(`/api/admin/projects/${projectId}/pricing/preview`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  savePricing: (projectId: string, rule: PricingDraft) =>
    request<{ id?: string; changed?: Record<string, unknown> }>(
      `/api/admin/projects/${projectId}/pricing`,
      { method: 'PUT', body: JSON.stringify(rule) },
    ),

  schedulePricing: (projectId: string, rule: PricingDraft, effectiveFrom: string) =>
    request<{ id: string }>(`/api/admin/projects/${projectId}/pricing/scheduled`, {
      method: 'POST',
      body: JSON.stringify({ rule, effectiveFrom }),
    }),

  deleteScheduledPricing: (ruleId: string) =>
    request<void>(`/api/admin/pricing/${ruleId}`, { method: 'DELETE' }),

  structure: (projectId: string) =>
    request<ProjectStructure>(`/api/admin/projects/${projectId}/structure`),

  unitTypes: () => request<UnitTypeCatalogue>('/api/admin/unit-types'),

  createUnitType: (body: { name: string; sortOrder: number }) =>
    request<{ id: string }>('/api/admin/unit-types', {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  editUnitType: (unitTypeId: string, body: { sortOrder: number; displayNames: Record<string, string> }) =>
    request<{ changed: Record<string, unknown> }>(`/api/admin/unit-types/${unitTypeId}`, {
      method: 'PUT',
      body: JSON.stringify(body),
    }),

  deleteUnitType: (unitTypeId: string) =>
    request<void>(`/api/admin/unit-types/${unitTypeId}`, { method: 'DELETE' }),

  createFloor: (projectId: string, body: { building: string | null; level: number; label: string | null }) =>
    request<{ id: string }>(`/api/admin/projects/${projectId}/floors`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  editFloor: (floorId: string, body: { building: string | null; level: number; label: string | null }) =>
    request<{ changed: Record<string, unknown> }>(`/api/admin/floors/${floorId}`, {
      method: 'PUT',
      body: JSON.stringify(body),
    }),

  deleteFloor: (floorId: string) =>
    request<void>(`/api/admin/floors/${floorId}`, { method: 'DELETE' }),

  saveProjectTranslation: (
    projectId: string,
    culture: string,
    body: Omit<ProjectTranslationRow, 'culture' | 'isSource'>,
  ) =>
    request<{ changed: Record<string, unknown> }>(
      `/api/admin/projects/${projectId}/translations/${culture}`,
      { method: 'PUT', body: JSON.stringify(body) },
    ),

  units: (projectId: string) =>
    request<UnitsResponse>(`/api/admin/projects/${projectId}/units`),

  editUnit: (unitId: string, edit: UnitEdit) =>
    request<{ changed: Record<string, unknown> }>(`/api/admin/units/${unitId}`, {
      method: 'PATCH',
      body: JSON.stringify(edit),
    }),

  parking: (projectId: string) =>
    request<ParkingResponse>(`/api/admin/projects/${projectId}/parking`),

  addParkingSpaces: (projectId: string, body: {
    prefix: string;
    from: number;
    to: number;
    kind: ParkingSpaceKind;
    level: number;
  }) =>
    request<{ created: number; skipped: string[] }>(
      `/api/admin/projects/${projectId}/parking`,
      { method: 'POST', body: JSON.stringify(body) },
    ),

  editParkingSpace: (spaceId: string, edit: ParkingSpaceEdit) =>
    request<{ changed: Record<string, unknown> }>(`/api/admin/parking/${spaceId}`, {
      method: 'PATCH',
      body: JSON.stringify(edit),
    }),

  reservationParkingAvailable: (reservationId: string) =>
    request<AvailableParking[]>(`/api/admin/reservations/${reservationId}/parking-available`),

  addReservationParking: (reservationId: string, spaceId: string) =>
    request<{ changed: Record<string, unknown> }>(
      `/api/admin/reservations/${reservationId}/parking/${spaceId}`,
      { method: 'POST' },
    ),

  removeReservationParking: (reservationId: string, spaceId: string) =>
    request<{ changed: Record<string, unknown> }>(
      `/api/admin/reservations/${reservationId}/parking/${spaceId}`,
      { method: 'DELETE' },
    ),

  removeParkingSpace: (spaceId: string) =>
    request<void>(`/api/admin/parking/${spaceId}`, { method: 'DELETE' }),

  removeParkingSpaces: (projectId: string, prefix: string, from: number, to: number) =>
    request<{ removed: number; kept: string[] }>(
      `/api/admin/projects/${projectId}/parking` +
        `?prefix=${encodeURIComponent(prefix)}&from=${from}&to=${to}`,
      { method: 'DELETE' },
    ),

  setParkingPrice: (projectId: string, price: ParkingPrice) =>
    request<void>(`/api/admin/projects/${projectId}/parking-prices`, {
      method: 'PUT',
      body: JSON.stringify(price),
    }),

  setMaxParkingPerReservation: (projectId: string, max: number) =>
    request<void>(`/api/admin/projects/${projectId}/parking-max`, {
      method: 'PUT',
      body: JSON.stringify({ max }),
    }),

  setReserved: (unitId: string, reserved: boolean) =>
    request<{ changed: Record<string, unknown> }>(
      `/api/admin/units/${unitId}/reserved`,
      { method: 'POST', body: JSON.stringify({ reserved }) },
    ),

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
