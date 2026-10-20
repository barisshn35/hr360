/**
 * Modül durum rozetleri.
 *
 * Renk hiçbir zaman tek başına anlam taşımaz: her rozet metin de içerir,
 * böylece renk körlüğünde ve gri baskıda bilgi kaybolmaz.
 *
 * fe14'ten taşındı; ton sözlüğü yeni tasarım token'larına eşlendi
 * (wait→warning, ok→success, stop→danger, live→info).
 */

import { StatusBadge, type StatusTone } from './StatusBadge'

import {
  employeeStatusLabels,
  stepDecisionLabels,
  workflowStatusLabels,
  type EmployeeStatusValue,
  type StepDecision,
  type WorkflowStatus,
} from '@/api/types'

const employeeTone: Record<EmployeeStatusValue, StatusTone> = { 0: 'success', 1: 'warning', 2: 'neutral' }

export function EmployeeStatusBadge({ status }: { status: EmployeeStatusValue }) {
  return (
    <StatusBadge tone={employeeTone[status] ?? 'neutral'}>
      {employeeStatusLabels[status] ?? tx('Bilinmiyor')}
    </StatusBadge>
  )
}

const workflowTone: Record<WorkflowStatus, StatusTone> = {
  Pending: 'warning',
  Approved: 'success',
  Rejected: 'danger',
  Cancelled: 'neutral',
}

export function WorkflowStatusBadge({ status }: { status: WorkflowStatus }) {
  return <StatusBadge tone={workflowTone[status] ?? 'warning'}>{workflowStatusLabels[status] ?? status}</StatusBadge>
}

const stepTone: Record<StepDecision, StatusTone> = {
  Pending: 'warning',
  Approved: 'success',
  Rejected: 'danger',
  Delegated: 'neutral',
}

export function StepDecisionBadge({ decision }: { decision: StepDecision }) {
  return <StatusBadge tone={stepTone[decision] ?? 'warning'}>{stepDecisionLabels[decision] ?? decision}</StatusBadge>
}

import {
  applicationStatusLabels,
  assetStatusLabels,
  caseStatusLabels,
  casePriorityLabels,
  claimStatusLabels,
  enrollmentStatusLabels,
  interviewResultLabels,
  jobPostingStatusLabels,
  leaveStatusLabels,
  notificationStatusLabels,
  onboardingTaskStatusLabels,
  planStatusLabels,
  type ApplicationStatus,
  type AssetStatus,
  type CasePriority,
  type CaseStatus,
  type ClaimStatus,
  type EnrollmentStatus,
  type InterviewResult,
  type JobPostingStatus,
  type LeaveStatus,
  type NotificationStatus,
  type OnboardingTaskStatus,
  type PlanStatus,
} from '@/api/types'
import { tx } from '@/lib/i18n'

/**
 * Modül durum rozetleri.
 *
 * Renk hicbir zaman tek basina anlam tasimaz: her rozet metin de icerir,
 * boylece renk korlugunde ve gri baskida bilgi kaybolmaz.
 */

const leaveTone: Record<LeaveStatus, StatusTone> = {
  Draft: 'neutral',
  Submitted: 'warning',
  Approved: 'success',
  Rejected: 'danger',
  Cancelled: 'neutral',
}
export const LeaveStatusBadge = ({ status }: { status: LeaveStatus }) => (
  <StatusBadge tone={leaveTone[status] ?? 'neutral'}>{leaveStatusLabels[status] ?? status}</StatusBadge>
)

const postingTone: Record<JobPostingStatus, StatusTone> = {
  Draft: 'neutral',
  Published: 'success',
  OnHold: 'warning',
  Closed: 'neutral',
}
export const JobPostingStatusBadge = ({ status }: { status: JobPostingStatus }) => (
  <StatusBadge tone={postingTone[status] ?? 'neutral'}>{jobPostingStatusLabels[status] ?? status}</StatusBadge>
)

const applicationTone: Record<ApplicationStatus, StatusTone> = {
  Applied: 'neutral',
  Screening: 'warning',
  Interview: 'info',
  Offer: 'info',
  Hired: 'success',
  Rejected: 'danger',
  Withdrawn: 'neutral',
}
export const ApplicationStatusBadge = ({ status }: { status: ApplicationStatus }) => (
  <StatusBadge tone={applicationTone[status] ?? 'neutral'}>
    {applicationStatusLabels[status] ?? status}
  </StatusBadge>
)

const interviewTone: Record<InterviewResult, StatusTone> = {
  Pending: 'warning',
  Pass: 'success',
  Fail: 'danger',
  NoShow: 'neutral',
  Cancelled: 'neutral',
}
export const InterviewResultBadge = ({ result }: { result: InterviewResult }) => (
  <StatusBadge tone={interviewTone[result] ?? 'neutral'}>{interviewResultLabels[result] ?? result}</StatusBadge>
)

const planTone: Record<PlanStatus, StatusTone> = {
  NotStarted: 'neutral',
  InProgress: 'info',
  Completed: 'success',
  Cancelled: 'neutral',
}
export const PlanStatusBadge = ({ status }: { status: PlanStatus }) => (
  <StatusBadge tone={planTone[status] ?? 'neutral'}>{planStatusLabels[status] ?? status}</StatusBadge>
)

const taskTone: Record<OnboardingTaskStatus, StatusTone> = {
  Pending: 'neutral',
  InProgress: 'info',
  Done: 'success',
  Blocked: 'danger',
}
export const TaskStatusBadge = ({ status }: { status: OnboardingTaskStatus }) => (
  <StatusBadge tone={taskTone[status] ?? 'neutral'}>{onboardingTaskStatusLabels[status] ?? status}</StatusBadge>
)

const assetTone: Record<AssetStatus, StatusTone> = {
  Available: 'success',
  Assigned: 'info',
  Maintenance: 'warning',
  Retired: 'neutral',
  Lost: 'danger',
}
export const AssetStatusBadge = ({ status }: { status: AssetStatus }) => (
  <StatusBadge tone={assetTone[status] ?? 'neutral'}>{assetStatusLabels[status] ?? status}</StatusBadge>
)

const enrollmentTone: Record<EnrollmentStatus, StatusTone> = {
  Enrolled: 'neutral',
  InProgress: 'info',
  Completed: 'success',
  Failed: 'danger',
  Dropped: 'neutral',
}
export const EnrollmentStatusBadge = ({ status }: { status: EnrollmentStatus }) => (
  <StatusBadge tone={enrollmentTone[status] ?? 'neutral'}>
    {enrollmentStatusLabels[status] ?? status}
  </StatusBadge>
)

const claimTone: Record<ClaimStatus, StatusTone> = {
  Draft: 'neutral',
  Submitted: 'warning',
  Approved: 'success',
  Rejected: 'danger',
  Paid: 'info',
}
export const ClaimStatusBadge = ({ status }: { status: ClaimStatus }) => (
  <StatusBadge tone={claimTone[status] ?? 'neutral'}>{claimStatusLabels[status] ?? status}</StatusBadge>
)

const caseTone: Record<CaseStatus, StatusTone> = {
  Open: 'warning',
  InProgress: 'info',
  WaitingOnEmployee: 'warning',
  Resolved: 'success',
  Closed: 'neutral',
}
export const CaseStatusBadge = ({ status }: { status: CaseStatus }) => (
  <StatusBadge tone={caseTone[status] ?? 'neutral'}>{caseStatusLabels[status] ?? status}</StatusBadge>
)

const priorityTone: Record<CasePriority, StatusTone> = {
  Low: 'neutral',
  Normal: 'neutral',
  High: 'warning',
  Urgent: 'danger',
}
export const CasePriorityBadge = ({ priority }: { priority: CasePriority }) => (
  <StatusBadge tone={priorityTone[priority] ?? 'neutral'}>
    {casePriorityLabels[priority] ?? priority}
  </StatusBadge>
)

const notificationTone: Record<NotificationStatus, StatusTone> = {
  Pending: 'warning',
  Sent: 'neutral',
  Failed: 'danger',
  Read: 'success',
  Suppressed: 'neutral',
  DigestQueued: 'warning',
  Digested: 'neutral',
}
export const NotificationStatusBadge = ({ status }: { status: NotificationStatus }) => (
  <StatusBadge tone={notificationTone[status] ?? 'neutral'}>
    {notificationStatusLabels[status] ?? status}
  </StatusBadge>
)
