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
  /** Blank falls back to the portal inbox on the Email settings screen. */
  salesInbox: string | null;
}

export interface ProjectDetails extends ProjectEdit {
  id: string;
  publishedAt: string | null;
  /** Publishing is refused below one, so the button can say why before it is pressed. */
  units: number;
}


/** The email settings a screen may change, as they are sent back. */
export interface EmailSettingsEdit {
  salesInbox: string | null;
  publicBaseUrl: string | null;
  liveSendingEnabled: boolean;
  redirectAllTo: string | null;
  allowedRecipients: string[];
  blockedRecipients: string[];
}

/** What became of a test message. */
export interface TestSendResult {
  ok: boolean;
  to: string;
  /** True only when a provider actually accepted it. */
  transmitted?: boolean;
  /** Why it went to the outbox instead. */
  reason?: string | null;
  /** Present when the provider refused it outright. */
  error?: string;
}

export interface EmailSettings extends EmailSettingsEdit {
  /** False until somebody saves: until then the file is the whole answer. */
  saved: boolean;
  savedAt: string | null;
  /** No mail provider is registered, so nothing leaves the machine whatever these say. */
  sendingPossible: boolean;
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
  /** Shown at the top of this project's reservation form. Blank on most projects. */
  reservationNotice: string | null;
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

/** Someone who has held, reserved or queued for a unit. Not a staff account. */
export interface Prospect {
  id: string;
  email: string;
  firstName: string | null;
  surname: string | null;
  /** The two above joined. Computed by the database — edit the parts, never this. */
  fullName: string | null;
  phone: string | null;
  city: string | null;
  /** Null means Romania, which is what the public forms tell buyers. */
  country: string | null;
  source: string | null;
  firstSeen: string;
  /** False means they are barred from taking a hold, and blockReason says why. */
  canHold: boolean;
  blockReason: string | null;
  /** Set only where an erasure had to keep the record. Null for everybody else. */
  anonymisedAt: string | null;
  /** Holds they let lapse. Counted by the rule, never by a member of staff. */
  lapsedHoldCount: number;
  activeHolds: number;
  openReservations: number;
}

/**
 * Which prospects to list. Leaving a flag out means "either".
 *
 * `blocked: false` hides the people barred from holding; `erased: false` hides the records an
 * erasure had to keep. Both read backwards at a glance, so the panel names them by what it is
 * showing rather than passing these around.
 *
 * Note that the panel's default view is not the absent one: it sends `erased: false`, because
 * a record with no name, no phone and an unreachable address is not somebody to sell to.
 */
export interface ProspectFilter {
  search: string;
  blocked?: boolean;
  erased?: boolean;
}

export const PRIVACY_KINDS = ['Access', 'Erasure'] as const;

export type PrivacyRequestKind = (typeof PRIVACY_KINDS)[number];

/**
 * Somebody exercising a right over their own data, and whether they have been answered yet.
 *
 * Kept apart from the audit trail on purpose: that says what was done, this says how long it
 * took. Only the second one answers a regulator, and it cannot be reconstructed afterwards.
 */
export interface PrivacyRequest {
  id: string;
  /** Null once an erasure has been carried out — the dates are the evidence, not the address. */
  email: string | null;
  kind: PrivacyRequestKind;
  requestedAt: string;
  /** One month after it arrived, per GDPR Article 12(3). */
  dueAt: string;
  completedAt: string | null;
  outcome: string | null;
  note: string | null;
  overdue: boolean;
}

/** What staff may correct about a prospect: exactly what the public forms ask for. */
export interface ProspectEdit {
  email: string;
  firstName: string | null;
  surname: string | null;
  phone: string | null;
  city: string | null;
  country: string | null;
}

export interface ProspectHold {
  id: string;
  unitCode: string;
  project: string;
  takenAt: string;
  expiresAt: string;
  releasedAt: string | null;
  releaseReason: string | null;
}

export interface ProspectReservation {
  id: string;
  unitCode: string;
  project: string;
  state: string;
  startedAt: string;
  cancelledAt: string | null;
  cancellationReason: string | null;
}

export interface ProspectWaitlist {
  id: string;
  project: string;
  typeName: string | null;
  position: number;
  joinedAt: string;
  notifiedAt: string | null;
  removedAt: string | null;
}

export interface ProspectDetail extends Prospect {
  locale: string;
  gdprConsentAt: string | null;
  gdprConsentPurpose: string[];
  gdprConsentSource: string | null;
  holds: ProspectHold[];
  reservations: ProspectReservation[];
  waitlist: ProspectWaitlist[];
}

/** One line of the audit log. */
export interface AuditRow {
  at: string;
  /** The actor's label, falling back to their type, so this is never blank. */
  actor: string;
  actorType: string;
  action: string;
  entityType: string;
  entityId: string;
  /** JSON as the service wrote it, or null where the action had no prior state. */
  before: string | null;
  after: string | null;
}

/**
 * The vocabulary present in the log.
 *
 * Read from the rows rather than listed here. The action names are spread across every service
 * that writes one, so a copy in this file would be a second place to update and the first to go
 * stale — and it would offer filters matching nothing.
 */
export interface AuditKinds {
  entityTypes: string[];
  /**
   * Paired with their entity type, because the action name does not imply it: floor.created,
   * pricing.created and translation.saved are all filed under `project`.
   */
  actions: { entityType: string; action: string }[];
  actorTypes: string[];
}

export interface AuditFilter {
  entityType?: string;
  action?: string;
  actorType?: string;
  actor?: string;
  from?: string;
  to?: string;
  take?: number;
}

/**
 * XHR rather than fetch, because fetch still cannot report upload progress and a 20 MB
 * brochure otherwise sits at "uploading" with nothing moving.
 */
function upload<T>(
  path: string,
  file: File,
  fields: Record<string, string>,
  onProgress?: (fraction: number) => void,
): Promise<T> {
  const body = new FormData();
  body.append('file', file);

  for (const [key, value] of Object.entries(fields)) {
    body.append(key, value);
  }

  return new Promise<T>((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', path);
    xhr.responseType = 'json';

    if (onProgress) {
      xhr.upload.onprogress = (e) => e.lengthComputable && onProgress(e.loaded / e.total);
    }

    xhr.onerror = () => reject(new ApiError('The upload did not reach the server.'));

    xhr.onload = () => {
      if (xhr.status === 401) return reject(new SessionExpired());

      const response = xhr.response as ({ error?: string } & T) | null;

      if (xhr.status < 200 || xhr.status >= 300) {
        return reject(new ApiError(response?.error ?? `Request failed (${xhr.status})`));
      }

      resolve(response as T);
    };

    // No Content-Type header: the browser sets it with the multipart boundary.
    xhr.send(body);
  });
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

  /**
   * Searching and filtering happen on the server, unlike every other list here. This is the
   * one table that grows with visitors rather than with the inventory, so the response is
   * bounded and the browser is not asked to hold all of it.
   */
  prospects: (filter: ProspectFilter) => {
    const query = new URLSearchParams();

    if (filter.search.trim()) query.set('search', filter.search.trim());

    // Both are tri-state on the server and omitted means "either". Sending false is what
    // hides a group, so `if (x)` would quietly drop exactly the case this exists for.
    if (filter.blocked !== undefined) query.set('blocked', String(filter.blocked));
    if (filter.erased !== undefined) query.set('erased', String(filter.erased));

    return request<Prospect[]>(`/api/admin/prospects?${query}`);
  },

  audit: (filter: AuditFilter) => {
    const query = new URLSearchParams();

    for (const [key, value] of Object.entries(filter)) {
      if (value !== undefined && value !== '') query.set(key, String(value));
    }

    return request<AuditRow[]>(`/api/admin/audit?${query}`);
  },

  auditKinds: () => request<AuditKinds>('/api/admin/audit/kinds'),

  prospect: (prospectId: string) =>
    request<ProspectDetail>(`/api/admin/prospects/${prospectId}`),

  /**
   * A plain link, not a fetch: the session cookie rides along and the browser saves the file
   * itself, which is what a subject access request needs forwarding to the person who asked.
   */
  privacyExportUrl: (email: string) =>
    `/api/admin/privacy/export?email=${encodeURIComponent(email)}`,

  /** One person's requests, or everything still open when no address is given. */
  privacyRequests: (email?: string) =>
    request<PrivacyRequest[]>(
      `/api/admin/privacy/requests${email ? `?email=${encodeURIComponent(email)}` : ''}`,
    ),

  logPrivacyRequest: (body: {
    email: string;
    kind: PrivacyRequestKind;
    requestedAt: string | null;
    note: string | null;
  }) =>
    request<{ id: string; dueAt: string }>('/api/admin/privacy/requests', {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  markPrivacyRequestAnswered: (requestId: string) =>
    request<{ answered: boolean }>(`/api/admin/privacy/requests/${requestId}/answered`, {
      method: 'POST',
    }),

  /** Irreversible. The caller is expected to have made the user type the address first. */
  forgetProspect: (email: string) =>
    request<{ outcome: string; message: string }>('/api/admin/privacy/forget', {
      method: 'POST',
      body: JSON.stringify({ email }),
    }),

  /** Corrects what the buyer typed. The joined name follows from the two parts. */
  editProspect: (prospectId: string, edit: ProspectEdit) =>
    request<{ changed: Record<string, unknown> }>(`/api/admin/prospects/${prospectId}`, {
      method: 'PATCH',
      body: JSON.stringify(edit),
    }),

  setProspectBlocked: (prospectId: string, blocked: boolean, reason?: string) =>
    request<{ changed: Record<string, unknown> }>(
      `/api/admin/prospects/${prospectId}/blocked`,
      { method: 'POST', body: JSON.stringify({ blocked, reason }) },
    ),

  pipeline: () => request<Pipeline>('/api/admin/pipeline'),

  waitlist: (projectId: string) =>
    request<WaitlistEntry[]>(`/api/admin/projects/${projectId}/waitlist`),

  removeFromWaitlist: (entryId: string) =>
    request<{ removed: boolean }>(`/api/admin/waitlist/${entryId}`, { method: 'DELETE' }),

  assets: (projectId: string) =>
    request<AssetsResponse>(`/api/admin/projects/${projectId}/assets`),

  uploadAsset: (projectId: string, file: File, kind: string, onProgress?: (fraction: number) => void) =>
    upload<{ id: string; fileName: string }>(
      `/api/admin/projects/${projectId}/assets`,
      file,
      { kind },
      onProgress,
    ),

  setAssetKind: (assetId: string, kind: string) =>
    request<{ ok: boolean }>(`/api/admin/assets/${assetId}/kind`, {
      method: 'PUT',
      body: JSON.stringify({ kind }),
    }),

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
    buyer?: { email: string; firstName?: string; surname?: string; phone?: string },
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

  /**
   * What the system is really using: the file, with whatever has been saved laid over it.
   */
  emailSettings: () => request<EmailSettings>('/api/admin/settings/email'),

  /**
   * Refused for any combination a production instance would refuse to start on, so a rejection
   * here is the same rule that would otherwise have stopped the next deploy.
   */
  /**
   * Sends one message down the real path: the same guards, the same provider. The reply says
   * whether it was transmitted or kept, and why, rather than only whether the call succeeded.
   */
  sendTestEmail: (to: string | null) =>
    request<TestSendResult>('/api/admin/settings/email/test', {
      method: 'POST',
      body: JSON.stringify({ to }),
    }),

  saveEmailSettings: (body: EmailSettingsEdit) =>
    request<{ savedAt: string; warnings: string[] }>('/api/admin/settings/email', {
      method: 'PUT',
      body: JSON.stringify(body),
    }),
};
