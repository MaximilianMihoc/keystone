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
    | 'parkingAvailable'
    | 'parkingPriceNet'
    | 'parkingAdvanceNet'
    | 'adminNotes'
  >
>;

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
};
