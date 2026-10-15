import { apiFetch, qs } from './client'
import { tx } from '@/lib/i18n'

const BASE = '/api/onboarding'

/* ------------------------------------------------------------------ tipler */

export type PlanStatus = 'NotStarted' | 'InProgress' | 'Completed' | 'Cancelled'
export type TaskCategory = 'IT' | 'HR' | 'Facility' | 'Training' | 'Legal' | 'Other'
export type OnboardingTaskStatus = 'Pending' | 'InProgress' | 'Done' | 'Blocked'
export type AssetType = 'Laptop' | 'Phone' | 'Monitor' | 'AccessCard' | 'Vehicle' | 'Other'
export type AssetStatus = 'Available' | 'Assigned' | 'Maintenance' | 'Retired' | 'Lost'

export const planStatusLabels: Record<PlanStatus, string> = {
  NotStarted: tx('Başlamadı'),
  InProgress: tx('Sürüyor'),
  Completed: tx('Tamamlandı'),
  Cancelled: tx('İptal edildi'),
}

export const taskCategoryLabels: Record<TaskCategory, string> = {
  IT: tx('Bilgi teknolojileri'),
  HR: tx('İnsan kaynakları'),
  Facility: tx('İdari işler'),
  Training: tx('Eğitim'),
  Legal: tx('Hukuk'),
  Other: tx('Diğer'),
}

export const onboardingTaskStatusLabels: Record<OnboardingTaskStatus, string> = {
  Pending: tx('Bekliyor'),
  InProgress: tx('Sürüyor'),
  Done: tx('Tamamlandı'),
  Blocked: tx('Engellendi'),
}

export const assetTypeLabels: Record<AssetType, string> = {
  Laptop: tx('Dizüstü bilgisayar'),
  Phone: tx('Telefon'),
  Monitor: tx('Monitör'),
  AccessCard: tx('Giriş kartı'),
  Vehicle: tx('Araç'),
  Other: tx('Diğer'),
}

export const assetStatusLabels: Record<AssetStatus, string> = {
  Available: tx('Boşta'),
  Assigned: tx('Zimmetli'),
  Maintenance: tx('Bakımda'),
  Retired: tx('Hurdaya ayrıldı'),
  Lost: tx('Kayıp'),
}

export interface OnboardingTask {
  id: string
  title: string
  category: TaskCategory
  status: OnboardingTaskStatus
  dueDate: string | null
  assigneeEmployeeId: string | null
  completedAt: string | null
  /** G14: HR | Manager | IT | Buddy | Employee (şablondan gelir). */
  ownerRole?: 'HR' | 'Manager' | 'IT' | 'Buddy' | 'Employee' | null
}

export interface OnboardingPlan {
  id: string
  employeeId: string
  startDate: string
  templateName: string | null
  status: PlanStatus
  createdAt: string
  tasks?: OnboardingTask[]
  /** G14: yol arkadaşı, ilk gün buluşma yeri, uygulanan şablonlar, karşılama zamanı. */
  buddyEmployeeId?: string | null
  location?: string | null
  appliedTemplates?: string | null
  welcomeSentAt?: string | null
}

export interface Asset {
  id: string
  assetTag: string
  type: AssetType
  model: string | null
  serialNumber: string | null
  status: AssetStatus
  /** Sadece acik (henuz iade edilmemis) atama varsa dolu - bkz. AssetsController.GetAll/GetById. */
  assignedEmployeeId: string | null
  assignedOn: string | null
  /** G16: QR etiketinin opak kodu ve açık zimmetin beklenen iade tarihi. */
  qrCode?: string | null
  expectedReturnOn?: string | null
}

/** `GET /assets/by-employee/{id}` ve zimmet atama/iade uclarinin donus tipi -
 * Asset DEGIL, AssetAssignment (ic ice `asset` alaniyla). */
export interface AssetAssignment {
  id: string
  assetId: string
  asset: Asset | null
  employeeId: string
  assignedOn: string
  returnedOn: string | null
  conditionOnReturn: string | null
  notes: string | null
  expectedReturnOn?: string | null
}

export interface CreatePlanInput {
  employeeId: string
  startDate: string
  templateName?: string
  useDefaultTasks: boolean
}

export interface CreateTaskInput {
  title: string
  category: TaskCategory
  dueDate?: string
  assigneeEmployeeId?: string
}

export interface CreateAssetInput {
  assetTag: string
  type: AssetType
  model?: string
  serialNumber?: string
}

export interface AssignAssetInput {
  employeeId: string
  assignedOn: string
  notes?: string
  /** G16: beklenen iade tarihi (hatırlatma işi buna göre çalışır). */
  expectedReturnOn?: string
}

export interface ReturnAssetInput {
  returnedOn: string
  condition?: string
  markAsRetired: boolean
}

/* ------------------------------------------------------------------ servis */

export const onboardingApi = {
  listPlans: (
    filters: { employeeId?: string; status?: PlanStatus } = {},
    signal?: AbortSignal,
  ) => apiFetch<OnboardingPlan[]>(`${BASE}/onboarding-plans${qs(filters)}`, { signal }),

  getPlan: (id: string, signal?: AbortSignal) =>
    apiFetch<OnboardingPlan>(`${BASE}/onboarding-plans/${id}`, { signal }),

  createPlan: (input: CreatePlanInput) =>
    apiFetch<OnboardingPlan>(`${BASE}/onboarding-plans`, { method: 'POST', body: input }),

  addTask: (planId: string, input: CreateTaskInput) =>
    apiFetch<OnboardingTask>(`${BASE}/onboarding-plans/${planId}/tasks`, {
      method: 'POST',
      body: input,
    }),

  setTaskStatus: (planId: string, taskId: string, status: OnboardingTaskStatus) =>
    apiFetch<OnboardingTask>(`${BASE}/onboarding-plans/${planId}/tasks/${taskId}/status`, {
      method: 'POST',
      body: { status },
    }),

  listAssets: (filters: { status?: AssetStatus; type?: AssetType } = {}, signal?: AbortSignal) =>
    apiFetch<Asset[]>(`${BASE}/assets${qs(filters)}`, { signal }),

  getAsset: (id: string, signal?: AbortSignal) =>
    apiFetch<Asset>(`${BASE}/assets/${id}`, { signal }),

  createAsset: (input: CreateAssetInput) =>
    apiFetch<Asset>(`${BASE}/assets`, { method: 'POST', body: input }),

  assignAsset: (id: string, input: AssignAssetInput) =>
    apiFetch<AssetAssignment>(`${BASE}/assets/${id}/assign`, { method: 'POST', body: input }),

  returnAsset: (id: string, input: ReturnAssetInput) =>
    apiFetch<AssetAssignment>(`${BASE}/assets/${id}/return`, { method: 'POST', body: input }),

  /** Demirbaş bilgisini düzeltir; etiket kiracı içinde tekil (409). */
  updateAsset: (id: string, input: CreateAssetInput) =>
    apiFetch<Asset>(`${BASE}/assets/${id}`, { method: 'PUT', body: input }),

  /** Elle durum: Boşta ↔ Bakımda / Hurda / Kayıp. Zimmetliyken 409. */
  setAssetStatus: (id: string, status: Exclude<AssetStatus, 'Assigned'>, note?: string) =>
    apiFetch<{ id: string; status: AssetStatus }>(`${BASE}/assets/${id}/status`, { method: 'PUT', body: { status, note } }),

  /** Zimmet ya da bakım geçmişi olan demirbaş silinmez (409; hurdaya ayırın). */
  deleteAsset: (id: string) => apiFetch<void>(`${BASE}/assets/${id}`, { method: 'DELETE' }),

  assetsByEmployee: (employeeId: string, signal?: AbortSignal) =>
    apiFetch<AssetAssignment[]>(`${BASE}/assets/by-employee/${employeeId}`, { signal }),
}
