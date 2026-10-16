import { Suspense, lazy, type ComponentType, type ReactNode } from 'react'
import { BrowserRouter, Navigate, Outlet, Route, Routes } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ApiError } from '@/api/client'
import { AuthProvider } from '@/auth/AuthProvider'
import { RequireAuth } from '@/auth/RequireAuth'
import { RequirePermission } from '@/auth/RequirePermission'
import type { Permission, Role } from '@/auth/roles'
import { AppShell } from '@/components/layout/AppShell'
import { ToastProvider } from '@/components/ui/Toast'
import { ConfirmProvider } from '@/components/ui/Confirm'
import { CenteredSpinner, FullPageSpinner } from '@/components/ui/States'
import { useAuth } from '@/auth/useAuth'
import { ErrorBoundary } from '@/app/ErrorBoundary'
import { DashboardPage } from '@/features/dashboard/DashboardPage'
import { NotFoundPage } from '@/features/NotFoundPage'
import { PasswordLinkDialogProvider } from '@/features/access/PasswordLinkCard'
import { NoTenantNotice, TenantGate } from '@/features/shared/FeatureGate'
import { CookieNotice, RobotsMeta } from '@/features/site/CookieNotice'

/**
 * Adlandırılmış dışa aktarımı olan sayfaları tembel yükler.
 * Her modül kendi chunk'ında kalsın: ilk yükleme paketi küçük olur, panele
 * hiç girmeyen ziyaretçi 14 modülün kodunu indirmez.
 */
function page<N extends string>(
  loader: () => Promise<Record<N, ComponentType>>,
  name: N,
) {
  return lazy(async () => ({ default: (await loader())[name] }))
}

const SignInPage = page(() => import('@/features/auth/SignInPage'), 'SignInPage')
const RegisterCompanyWizard = page(
  () => import('@/features/registration/RegisterCompanyWizard'),
  'RegisterCompanyWizard',
)

const WorkflowInboxPage = page(() => import('@/features/workflows/WorkflowInboxPage'), 'WorkflowInboxPage')
const WorkflowDetailPage = page(() => import('@/features/workflows/WorkflowDetailPage'), 'WorkflowDetailPage')
const LeavePage = page(() => import('@/features/leave/LeavePage'), 'LeavePage')
const ExpensePage = page(() => import('@/features/expense/ExpensePage'), 'ExpensePage')
const ExpenseClaimPage = page(() => import('@/features/expense/ExpenseClaimPage'), 'ExpenseClaimPage')
const CasesPage = page(() => import('@/features/cases/CasesPage'), 'CasesPage')
const CaseDetailPage = page(() => import('@/features/cases/CaseDetailPage'), 'CaseDetailPage')
const TimesheetPage = page(() => import('@/features/timeshift/TimesheetPage'), 'TimesheetPage')
const ShiftEnginePage = page(() => import('@/features/timeshift/engine/ShiftEnginePage'), 'ShiftEnginePage')
const OrganizationPage = page(() => import('@/features/organization/OrganizationPage'), 'OrganizationPage')
const CompanyDetailPage = page(() => import('@/features/organization/CompanyDetailPage'), 'CompanyDetailPage')
const EmployeeListPage = page(() => import('@/features/employees/EmployeeListPage'), 'EmployeeListPage')
const EmployeeDetailPage = page(() => import('@/features/employees/EmployeeDetailPage'), 'EmployeeDetailPage')
const JobPostingsPage = page(() => import('@/features/recruitment/JobPostingsPage'), 'JobPostingsPage')
const JobPostingDetailPage = page(() => import('@/features/recruitment/JobPostingDetailPage'), 'JobPostingDetailPage')
const CandidatesPage = page(() => import('@/features/recruitment/CandidatesPage'), 'CandidatesPage')
const CareerPage = page(() => import('@/features/recruitment/CareerPages'), 'CareerPage')
const CandidateSelfServicePage = page(() => import('@/features/recruitment/CareerPages'), 'CandidateSelfServicePage')
const OfferSignPage = page(() => import('@/features/recruitment/OfferSignPage'), 'OfferSignPage')
const MyInterviewsPage = page(() => import('@/features/recruitment/RecruitmentPlus'), 'MyInterviewsPage')
// Dalga 11: çalışan önerisi, işe alım analizi, ilan ayrıntısı (JSON-LD) ve aday durum bağlantısı.
const MyReferralsPage = page(() => import('@/features/recruitment/RecruitmentW11'), 'MyReferralsPage')
const ReferralsAdminPage = page(() => import('@/features/recruitment/RecruitmentW11'), 'ReferralsAdminPage')
const RecruitmentAnalyticsPage = page(() => import('@/features/recruitment/RecruitmentW11'), 'RecruitmentAnalyticsPage')
const CareerJobPage = page(() => import('@/features/recruitment/CareerW11Pages'), 'CareerJobPage')
const CandidateStatusPage = page(() => import('@/features/recruitment/CareerW11Pages'), 'CandidateStatusPage')
const OnboardingPage = page(() => import('@/features/onboarding/OnboardingPage'), 'OnboardingPage')
const OnboardingPlanPage = page(() => import('@/features/onboarding/OnboardingPlanPage'), 'OnboardingPlanPage')
const AssetsPage = page(() => import('@/features/onboarding/AssetsPage'), 'AssetsPage')
const AssetScanPage = page(() => import('@/features/onboarding/AssetOps'), 'AssetScanPage')
const ShiftSwapPage = page(() => import('@/features/timeshift/ShiftSwapPage'), 'ShiftSwapPage')
const PerformanceIndex = page(() => import('@/features/performance/PerformanceIndex'), 'PerformanceIndex')
const MetricsPage = page(() => import('@/features/performance/metrics/MetricsPage'), 'MetricsPage')
const TeamsPage = page(() => import('@/features/performance/teams/TeamsPage'), 'TeamsPage')
const CyclesPage = page(() => import('@/features/performance/cycles/CyclesPage'), 'CyclesPage')
const GoalsPage = page(() => import('@/features/performance/goals/GoalsPage'), 'GoalsPage')
const ReviewsPage = page(() => import('@/features/performance/reviews/ReviewsPage'), 'ReviewsPage')
const NewReviewPage = page(() => import('@/features/performance/reviews/NewReviewPage'), 'NewReviewPage')
const ReviewFormPage = page(() => import('@/features/performance/reviews/ReviewFormPage'), 'ReviewFormPage')
const ScorePage = page(() => import('@/features/performance/score/ScorePage'), 'ScorePage')
const AnalyticsPage = page(() => import('@/features/performance/analytics/AnalyticsPage'), 'AnalyticsPage')
const FeedbackPage = page(() => import('@/features/performance/feedback/FeedbackPage'), 'FeedbackPage')
const RecommendationsPage = page(() => import('@/features/performance/recommendations/RecommendationsPage'), 'RecommendationsPage')
const MyPerformancePage = page(() => import('@/features/performance/me/MyPerformancePage'), 'MyPerformancePage')
const ScoringSettingsPage = page(() => import('@/features/performance/settings/ScoringSettingsPage'), 'ScoringSettingsPage')
const LearningPage = page(() => import('@/features/learning/LearningPage'), 'LearningPage')
const CoursePlayerPage = page(() => import('@/features/learning/CoursePlayerPage'), 'CoursePlayerPage')
const CertificatePage = page(() => import('@/features/learning/CertificatePage'), 'CertificatePage')
const CompetencyMatrixPage = page(() => import('@/features/learning/CompetencyMatrixPage'), 'CompetencyMatrixPage')
const CareerPathsPage = page(() => import('@/features/learning/CareerPathsPage'), 'CareerPathsPage')
const DueTrainingPage = page(() => import('@/features/learning/DueTrainingPage'), 'DueTrainingPage')
const NineBoxPage = page(() => import('@/features/performance/ninebox/NineBoxPage'), 'NineBoxPage')
const CalibrationPage = page(() => import('@/features/performance/ninebox/CalibrationPage'), 'CalibrationPage')
const OkrTreePage = page(() => import('@/features/performance/goals/OkrTreePage'), 'OkrTreePage')
const Feedback360Page = page(() => import('@/features/performance/feedback/Feedback360Page'), 'Feedback360Page')
const CompensationPage = page(() => import('@/features/compensation/CompensationPage'), 'CompensationPage')
const PayrollPage = page(() => import('@/features/compensation/PayrollPage'), 'PayrollPage')
const PayrollPeriodPage = page(() => import('@/features/compensation/PayrollPage'), 'PayrollPeriodPage')
const MyPayslipsPage = page(() => import('@/features/compensation/MyPayslipsPage'), 'MyPayslipsPage')
const BenefitsPage = page(() => import('@/features/compensation/PayrollExtras'), 'BenefitsPage')
const RaiseCyclesPage = page(() => import('@/features/compensation/PayrollExtras'), 'RaiseCyclesPage')
const TravelPage = page(() => import('@/features/expense/TravelPage'), 'TravelPage')
const TimeClockPage = page(() => import('@/features/timeshift/TimeClockPage'), 'TimeClockPage')
const TimeClockAdminPage = page(() => import('@/features/timeshift/TimeClockPage'), 'TimeClockAdminPage')
const MyDocumentsPage = page(() => import('@/features/documents/DocumentRequestPages'), 'MyDocumentsPage')
const VerifyDocumentPage = page(() => import('@/features/documents/DocumentRequestPages'), 'VerifyDocumentPage')
// Dalga 9 (madde 68): paylaşılan tablet için oturumsuz QR / PIN kiosku (terminal anahtarıyla).
const KioskPage = page(() => import('@/features/timeshift/KioskPage'), 'KioskPage')
const EmailDecisionPage = page(() => import('@/features/documents/DocumentRequestPages'), 'EmailDecisionPage')
const WorkflowDesignerPage = page(() => import('@/features/workflows/WorkflowDesignerPage'), 'WorkflowDesignerPage')
const DocumentsPage = page(() => import('@/features/documents/DocumentsPage'), 'DocumentsPage')
const MySignaturesPage = page(() => import('@/features/documents/DocumentSignature'), 'MySignaturesPage')
const NotificationsPage = page(() => import('@/features/notification/NotificationsPage'), 'NotificationsPage')
const SettingsPage = page(() => import('@/features/settings/SettingsPage'), 'SettingsPage')
const RolesPage = page(() => import('@/features/settings/RolesPage'), 'RolesPage')
const TenantsPage = page(() => import('@/features/platform/TenantsPage'), 'TenantsPage')
const SystemSettingsPage = page(() => import('@/features/platform/SystemSettingsPage'), 'SystemSettingsPage')
const SetPasswordPage = page(() => import('@/features/access/PasswordPages'), 'SetPasswordPage')
const ForgotPasswordPage = page(() => import('@/features/access/PasswordPages'), 'ForgotPasswordPage')
const SessionExpiredPage = page(() => import('@/features/auth/SessionExpiredPage'), 'SessionExpiredPage')
const FaqPage = page(() => import('@/features/site/SitePages'), 'FaqPage')
const PrivacyPage = page(() => import('@/features/site/SitePages'), 'PrivacyPage')
const TermsPage = page(() => import('@/features/site/SitePages'), 'TermsPage')
const ContactPage = page(() => import('@/features/site/SitePages'), 'ContactPage')
const ThanksPage = page(() => import('@/features/site/ThanksPage'), 'ThanksPage')

/* -------- Topluluk, gelişim, kişiler, içgörü, yönetişim (2026-10 eklemeleri) -------- */
const ProfilePage = page(() => import('@/features/profile/ProfilePage'), 'ProfilePage')
const ChatLinkPage = page(() => import('@/features/profile/ChatAccounts'), 'ChatLinkPage')
const ChatStepUpPage = page(() => import('@/features/profile/ChatStepUp'), 'ChatStepUpPage')
const KudosPage = page(() => import('@/features/community/KudosPage'), 'KudosPage')
const EomPage = page(() => import('@/features/community/eom/EomPage'), 'EomPage')
const EomAdminPage = page(() => import('@/features/community/eom/EomAdminPage'), 'EomAdminPage')
const EomResultsPage = page(() => import('@/features/community/eom/EomResultsPage'), 'EomResultsPage')
const CelebrationsPage = page(() => import('@/features/community/CelebrationsPage'), 'CelebrationsPage')
const SurveysPage = page(() => import('@/features/community/SurveysPage'), 'SurveysPage')
const DirectoryPage = page(() => import('@/features/community/DirectoryPage'), 'DirectoryPage')
const WorkplacePage = page(() => import('@/features/workplace/WorkplacePage'), 'WorkplacePage')
const MentorshipPage = page(() => import('@/features/growth/MentorshipPage'), 'MentorshipPage')
const MobilityPage = page(() => import('@/features/growth/MobilityPage'), 'MobilityPage')
const OneOnOnesPage = page(() => import('@/features/growth/OneOnOnesPage'), 'OneOnOnesPage')
const TeamHealthPage = page(() => import('@/features/growth/TeamHealthPage'), 'TeamHealthPage')
const OffboardingPage = page(() => import('@/features/people/OffboardingPage'), 'OffboardingPage')
const SuccessionPage = page(() => import('@/features/people/SuccessionPage'), 'SuccessionPage')
const OrgScenariosPage = page(() => import('@/features/people/OrgScenariosPage'), 'OrgScenariosPage')
const HireSagaPage = page(() => import('@/features/people/HireSagaPage'), 'HireSagaPage')
const InsightsAnalyticsPage = page(() => import('@/features/insights/AnalyticsPage'), 'AnalyticsPage')
const ModelCardPage = page(() => import('@/features/insights/ModelCardPage'), 'ModelCardPage')
const NlReportPage = page(() => import('@/features/insights/NlReportPage'), 'NlReportPage')
const CustomFieldsPage = page(() => import('@/features/governance/CustomFields'), 'CustomFieldsPage')
const TimeMachinePage = page(() => import('@/features/insights/TimeMachinePage'), 'TimeMachinePage')
const EventRadarPage = page(() => import('@/features/insights/EventRadarPage'), 'EventRadarPage')
const AiToolsPage = page(() => import('@/features/insights/AiToolsPage'), 'AiToolsPage')
const PayrollSimPage = page(() => import('@/features/insights/PayrollSimPage'), 'PayrollSimPage')
const AuditPage = page(() => import('@/features/governance/AuditPage'), 'AuditPage')
const PrivacyAdminPage = page(() => import('@/features/governance/PrivacyAdminPage'), 'PrivacyAdminPage')
const DocTemplatesPage = page(() => import('@/features/governance/DocTemplatesPage'), 'DocTemplatesPage')
const RulesPage = page(() => import('@/features/governance/RulesPage'), 'RulesPage')
const IntegrationsPage = page(() => import('@/features/governance/IntegrationsPage'), 'IntegrationsPage')
const SecurityPage = page(() => import('@/features/governance/SecurityPage'), 'SecurityPage')
const DataProtectionPage = page(() => import('@/features/governance/DataProtectionPage'), 'DataProtectionPage')
const AccessReviewMyPage = page(() => import('@/features/governance/DataProtectionPage'), 'AccessReviewMyPage')
const ImportExportPage = page(() => import('@/features/governance/ImportExportPage'), 'ImportExportPage')
const ApiDocsPage = page(() => import('@/features/governance/ApiDocsPage'), 'ApiDocsPage')
const OrgPresentationPage = page(() => import('@/features/organization/OrgPresentationPage'), 'OrgPresentationPage')
// Dalga 5c — işyeri uyumu (governance-service)
const AnnouncementsPage = page(() => import('@/features/compliance/AnnouncementsPage'), 'AnnouncementsPage')
const LibraryPage = page(() => import('@/features/compliance/LibraryPage'), 'LibraryPage')
const EthicsInboxPage = page(() => import('@/features/compliance/EthicsPages'), 'EthicsInboxPage')
const PublicEthicsPage = page(() => import('@/features/compliance/EthicsPages'), 'PublicEthicsPage')
const OshPage = page(() => import('@/features/compliance/OshPage'), 'OshPage')
const DisciplinaryPage = page(() => import('@/features/compliance/DisciplinaryPage'), 'DisciplinaryPage')
const MyDefencesPage = page(() => import('@/features/compliance/DisciplinaryPage'), 'MyDefencesPage')

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      refetchOnWindowFocus: false,
      retry: (failureCount, error) => {
        // Yetki/bulunamadı hatalarında yeniden denemek anlamsız.
        if (error instanceof ApiError && [401, 403, 404].includes(error.status)) return false
        return failureCount < 2
      },
    },
    mutations: {
      // Çevrimdışıyken de mutationFn çalışsın: React Query varsayılanı ('online') mutasyonu duraklatır ve
      // apiFetch'teki çevrimdışı taslak kuyruğu hiç devreye girmez (düğme sonsuza dek bekler). Sıraya
      // alınabilen talepler taslak olarak saklanır, diğerleri ağ hatasıyla hemen sonuçlanır.
      networkMode: 'always',
    },
  },
})

/** Rota tanımlarını kısaltır; izin kontrolü tek kapıdan geçer. */
/** Şirkete bağlı veri gösteren ekranlar: kiracısız oturumda (platform yöneticisi) boş durum gösterir. */
function TenantOnly({ children }: { children: ReactNode }) {
  const { tenantSlug } = useAuth()
  if (!tenantSlug) return <NoTenantNotice />
  return <>{children}</>
}

function guarded(permission: Permission, element: ReactNode, requireRoles?: Role[]) {
  return (
    <RequirePermission permission={permission} requireRoles={requireRoles}>
      {element}
    </RequirePermission>
  )
}

export function App() {
  return (
    <ErrorBoundary>
      <QueryClientProvider client={queryClient}>
        <AuthProvider>
          <ToastProvider>
          <ConfirmProvider>
          <PasswordLinkDialogProvider>
            <BrowserRouter>
              {/* Çerez bilgilendirmesi (yalnızca zorunlu çerezler) ve arama motoru meta etiketi */}
              <CookieNotice />
              <RobotsMeta />
              <Routes>
                {/* ------------------------------ Herkese açık ------------------------------ */}
                {/* Ayrı bir tanıtım/ana sayfası yok — kök adres doğrudan girişe yönlenir. */}
                <Route path="/" element={<Navigate to="/giris" replace />} />
                <Route
                  path="/giris"
                  element={
                    <Suspense fallback={<FullPageSpinner />}>
                      <SignInPage />
                    </Suspense>
                  }
                />
                {/* Şirket kaydı ANONİM: token gerekmez, RequireAuth yok. */}
                <Route
                  path="/kayit"
                  element={
                    <Suspense fallback={<FullPageSpinner />}>
                      <RegisterCompanyWizard />
                    </Suspense>
                  }
                />

                {/* Belge doğrulama ve e-postadan tek tıkla karar: oturum gerekmez (kişisel veri göstermez). */}
                <Route path="/belge-dogrula" element={<Suspense fallback={<FullPageSpinner />}><VerifyDocumentPage /></Suspense>} />
                <Route path="/kiosk" element={<Suspense fallback={<FullPageSpinner />}><KioskPage /></Suspense>} />
                <Route path="/belge-dogrula/:code" element={<Suspense fallback={<FullPageSpinner />}><VerifyDocumentPage /></Suspense>} />
                {/* Y15 anonim etik hattı: oturum gerektirmez */}
                <Route path="/etik/:tenant" element={<Suspense fallback={<FullPageSpinner />}><PublicEthicsPage /></Suspense>} />
                <Route path="/onay-eposta" element={<Suspense fallback={<FullPageSpinner />}><EmailDecisionPage /></Suspense>} />
                {/* Süreli parola linki ve "parolamı unuttum": oturum gerekmez; link yalnızca jeton özetiyle eşlenir. */}
                <Route path="/parola-belirle/:token" element={<Suspense fallback={<FullPageSpinner />}><SetPasswordPage /></Suspense>} />
                <Route path="/parola-sifirla" element={<Suspense fallback={<FullPageSpinner />}><ForgotPasswordPage /></Suspense>} />
                {/* Herkese açık site sayfaları: içerik Platform › Sistem ayarları'ndan (boşsa varsayılan şablon). */}
                <Route path="/sss" element={<Suspense fallback={<FullPageSpinner />}><FaqPage /></Suspense>} />
                <Route path="/gizlilik" element={<Suspense fallback={<FullPageSpinner />}><PrivacyPage /></Suspense>} />
                <Route path="/kosullar" element={<Suspense fallback={<FullPageSpinner />}><TermsPage /></Suspense>} />
                <Route path="/iletisim" element={<Suspense fallback={<FullPageSpinner />}><ContactPage /></Suspense>} />
                <Route path="/tesekkurler" element={<Suspense fallback={<FullPageSpinner />}><ThanksPage /></Suspense>} />
                {/* Kariyer sayfası ve aday öz-hizmeti (Y16): oturum gerekmez; aday bağlantısı yalnızca jeton özetiyle eşlenir. */}
                <Route path="/kariyer/:tenant" element={<Suspense fallback={<FullPageSpinner />}><CareerPage /></Suspense>} />
                <Route path="/kariyer/:tenant/basvuru/:token" element={<Suspense fallback={<FullPageSpinner />}><CandidateSelfServicePage /></Suspense>} />
                <Route path="/kariyer/:tenant/ilan/:jobId" element={<Suspense fallback={<FullPageSpinner />}><CareerJobPage /></Suspense>} />
                <Route path="/kariyer/:tenant/durum/:token" element={<Suspense fallback={<FullPageSpinner />}><CandidateStatusPage /></Suspense>} />
                <Route path="/kariyer/:tenant/teklif/:token" element={<Suspense fallback={<FullPageSpinner />}><OfferSignPage /></Suspense>} />

                {/* --------------------------- Oturum gerektiren --------------------------- */}
                <Route
                  path="/panel"
                  element={
                    <RequireAuth>
                      <AppShell />
                    </RequireAuth>
                  }
                >
                  <Route
                    element={
                      <Suspense fallback={<CenteredSpinner />}>
                        <Outlet />
                      </Suspense>
                    }
                  >
                    <Route index element={<DashboardPage />} />

                    <Route path="onaylar" element={guarded('workflow:view', <WorkflowInboxPage />)} />
                    <Route path="onay-akislari" element={guarded('employee:manage', <WorkflowDesignerPage />)} />
                    <Route
                      path="onaylar/:workflowId"
                      element={guarded('workflow:view', <WorkflowDetailPage />)}
                    />

                    <Route path="izin" element={guarded('leave:view', <LeavePage />)} />

                    <Route path="masraf" element={guarded('expense:view', <ExpensePage />)} />
                    <Route
                      path="masraf/:claimId"
                      element={guarded('expense:view', <ExpenseClaimPage />)}
                    />

                    <Route path="ik-vakalari" element={guarded('case:view', <CasesPage />)} />
                    <Route
                      path="ik-vakalari/:caseId"
                      element={guarded('case:view', <CaseDetailPage />)}
                    />

                    <Route path="puantaj" element={guarded('timeshift:view', <TimesheetPage />)} />
                    <Route path="giris-cikis" element={guarded('timeshift:clock', <TimeClockPage />)} />
                    <Route path="vardiya-takasi" element={guarded('timeshift:view', <ShiftSwapPage />)} />
                    <Route path="giris-cikis/yonetim" element={guarded('employee:manage', <TimeClockAdminPage />)} />
                    <Route
                      path="vardiya-motoru"
                      element={guarded('timeshift:view', <ShiftEnginePage />)}
                    />

                    <Route
                      path="organizasyon"
                      element={guarded('organization:view', <OrganizationPage />)}
                    />
                    {/* Diyagram herkese açık; yönetim görünümü sayfa içinde team:manage ile açılır. */}
                    <Route
                      path="organizasyon/ekipler"
                      element={guarded('organization:view', <TeamsPage />)}
                    />
                    <Route
                      path="organizasyon/:companyId"
                      element={guarded('organization:view', <CompanyDetailPage />)}
                    />

                    <Route
                      path="calisanlar"
                      element={guarded('employee:viewAll', <EmployeeListPage />)}
                    />
                    <Route
                      path="calisanlar/:employeeId"
                      element={guarded('employee:viewAll', <EmployeeDetailPage />)}
                    />

                    {/* "adaylar" ilan kimliğinden ÖNCE eşleşmeli */}
                    {/* Görüşmeci olan her çalışan (yönetici olmasa da) kendi mülakatlarını görür. */}
                    <Route path="mulakatlarim" element={<MyInterviewsPage />} />
                    <Route path="aday-oner" element={<MyReferralsPage />} />
                    <Route path="ise-alim" element={guarded('recruitment:view', <JobPostingsPage />)} />
                    <Route path="ise-alim/oneriler" element={guarded('recruitment:publish', <ReferralsAdminPage />, ['hr-admin', 'tenant-admin', 'platform-admin'])} />
                    <Route path="ise-alim/analiz" element={guarded('recruitment:publish', <RecruitmentAnalyticsPage />, ['hr-admin', 'tenant-admin', 'platform-admin'])} />
                    <Route
                      path="ise-alim/adaylar"
                      element={guarded('recruitment:view', <CandidatesPage />)}
                    />
                    <Route
                      path="ise-alim/:postingId"
                      element={guarded('recruitment:view', <JobPostingDetailPage />)}
                    />

                    <Route path="onboarding" element={guarded('onboarding:view', <OnboardingPage />)} />
                    <Route
                      path="onboarding/:planId"
                      element={guarded('onboarding:view', <OnboardingPlanPage />)}
                    />
                    <Route path="zimmet" element={guarded('onboarding:view', <AssetsPage />)} />
                    <Route path="zimmet/tara" element={<AssetScanPage />} />

                    {/* ------------------------------ Performans ------------------------------ */}
                    <Route path="performans" element={guarded('performance:view', <PerformanceIndex />)} />
                    <Route
                      path="performans/metrikler"
                      element={guarded('performance:manage', <MetricsPage />)}
                    />
                    <Route
                      path="performans/ayarlar"
                      element={guarded('performance:manage', <ScoringSettingsPage />)}
                    />
                    <Route
                      path="performans/donemler"
                      element={guarded('performance:manage', <CyclesPage />)}
                    />
                    <Route path="performans/hedefler" element={guarded('performance:view', <GoalsPage />)} />
                    <Route path="performans/degerlendirme" element={guarded('performance:view', <ReviewsPage />)} />
                    <Route path="performans/degerlendirme/yeni" element={guarded('performance:view', <NewReviewPage />)} />
                    <Route path="performans/degerlendirme/:reviewId" element={guarded('performance:view', <ReviewFormPage />)} />
                    <Route path="performans/puan" element={guarded('performance:view', <ScorePage />)} />
                    <Route path="performans/analiz" element={guarded('performance:manage', <AnalyticsPage />)} />
                    <Route path="performans/geri-bildirim" element={guarded('performance:view', <FeedbackPage />)} />
                    <Route path="performans/oneriler" element={guarded('performance:manage', <RecommendationsPage />)} />
                    <Route path="performans/benim" element={guarded('performance:view', <MyPerformancePage />)} />
                    <Route path="performans/dokuz-kutu" element={guarded('performance:manage', <NineBoxPage />)} />
                    <Route path="performans/kalibrasyon" element={guarded('performance:manage', <CalibrationPage />)} />
                    <Route path="performans/okr" element={guarded('performance:view', <OkrTreePage />)} />
                    <Route path="performans/360" element={guarded('performance:view', <Feedback360Page />)} />

                    <Route path="egitim" element={guarded('learning:view', <LearningPage />)} />
                    <Route path="egitim/sertifika/:id" element={guarded('learning:view', <CertificatePage />)} />
                    <Route path="egitim/:courseId" element={guarded('learning:view', <CoursePlayerPage />)} />
                    <Route path="yetkinlikler" element={guarded('learning:view', <CompetencyMatrixPage />)} />
                    <Route path="kariyer-yollari" element={guarded('learning:view', <CareerPathsPage />)} />
                    <Route path="egitim-takibi" element={guarded('learning:view', <DueTrainingPage />)} />

                    {/* Ücret hassas veri — yalnızca İK yönetimi */}
                    <Route path="ucret" element={guarded('compensation:view', <CompensationPage />)} />
                    <Route path="bordro" element={guarded('compensation:view', <PayrollPage />)} />
                    <Route path="bordro/:id" element={guarded('compensation:view', <PayrollPeriodPage />)} />

                    <Route path="dokumanlar" element={guarded('document:manage', <DocumentsPage />)} />
                    <Route
                      path="bildirimler"
                      element={guarded('notification:view', <NotificationsPage />)}
                    />
                    <Route path="ayarlar" element={<SettingsPage />} />
                    <Route path="ayarlar/ozel-alanlar" element={guarded('employee:manage', <TenantOnly><CustomFieldsPage /></TenantOnly>)} />
                    {/* Rol atama backend'de RequireHrAdmin gerektirir (bkz.
                        TeamMembersController.AssignRole) - aynı izinle koru.
                        requireRoles: bu sayfanın veri uçları (GetAll members
                        dahil) tenant-service'in KENDİ RequireHrAdmin
                        policy'sini kullanıyor - bilerek "ext-*" ile
                        genişletilmedi (rol atama gücünün Ek İzin ile
                        devredilmesi istenmiyor). employee:manage Ek İznini
                        alan biri, can('employee:manage') ile buraya girip
                        veri çekemezdi (403) - requireRoles bunu, backend'in
                        gerçekte kabul ettiği sabit rollerle eşleştirerek
                        önlüyor. */}
                    <Route
                      path="roller"
                      element={guarded('employee:manage', <RolesPage />, [
                        'hr-admin',
                        'tenant-admin',
                        'platform-admin',
                      ])}
                    />

                    {/* Platform yönetimi — çok kiracılılık */}
                    <Route
                      path="platform/kiracilar"
                      element={guarded('platform:manage', <TenantsPage />)}
                    />
                    <Route
                      path="platform/sistem"
                      element={guarded('platform:manage', <SystemSettingsPage />)}
                    />

                    {/* ---------------- Hesabım ---------------- */}
                    <Route path="profil" element={<ProfilePage />} />
                    <Route path="sohbet-bagla" element={<ChatLinkPage />} />
                    <Route path="sohbet-onay" element={<ChatStepUpPage />} />
                    <Route path="bordrolarim" element={<MyPayslipsPage />} />
                    <Route path="yan-haklar" element={<BenefitsPage />} />
                    <Route path="zam-donemi" element={<RaiseCyclesPage />} />
                    <Route path="seyahat" element={guarded('expense:view', <TravelPage />)} />
                    <Route path="belge-talebi" element={<MyDocumentsPage />} />
                    <Route path="imzalarim" element={<MySignaturesPage />} />

                    {/* ---------------- Topluluk ---------------- */}
                    <Route path="takdir" element={<TenantGate><KudosPage /></TenantGate>} />
                    <Route path="ayin-calisani" element={<TenantGate><EomPage /></TenantGate>} />
                    <Route path="ayin-calisani/yonetim" element={<TenantGate><EomAdminPage /></TenantGate>} />
                    <Route path="ayin-calisani/sonuc/:periodId" element={<TenantGate><EomResultsPage /></TenantGate>} />
                    <Route path="kutlamalar" element={<TenantGate><CelebrationsPage /></TenantGate>} />
                    <Route path="anketler" element={<TenantGate><SurveysPage /></TenantGate>} />
                    <Route path="yetenek-dizini" element={<TenantGate><DirectoryPage /></TenantGate>} />
                    <Route path="ofis" element={<TenantGate><WorkplacePage /></TenantGate>} />
                    <Route path="bordro-simulasyonu" element={<TenantGate><PayrollSimPage /></TenantGate>} />

                    {/* ---------------- Gelişim ---------------- */}
                    <Route path="mentorluk" element={<TenantGate><MentorshipPage /></TenantGate>} />
                    <Route path="ic-ilanlar" element={<TenantGate><MobilityPage /></TenantGate>} />
                    <Route path="birebir" element={<TenantGate><OneOnOnesPage /></TenantGate>} />
                    <Route path="ekip-sagligi" element={guarded('performance:manage', <TenantGate><TeamHealthPage /></TenantGate>)} />

                    {/* ---------------- Kişiler ---------------- */}
                    <Route path="offboarding" element={guarded('onboarding:manage', <TenantGate><OffboardingPage /></TenantGate>)} />
                    <Route path="ardil-planlama" element={guarded('performance:manage', <TenantGate><SuccessionPage /></TenantGate>)} />
                    <Route path="org-senaryolari" element={guarded('performance:manage', <TenantGate><OrgScenariosPage /></TenantGate>)} />
                    <Route path="ise-alim/saga" element={guarded('recruitment:view', <TenantGate><HireSagaPage /></TenantGate>, ['hr-admin', 'tenant-admin', 'platform-admin'])} />
                    <Route path="organizasyon/sunum" element={guarded('organization:view', <TenantGate><OrgPresentationPage /></TenantGate>)} />

                    {/* ---------------- İçgörü ---------------- */}
                    <Route path="analitik" element={guarded('performance:manage', <TenantGate><InsightsAnalyticsPage /></TenantGate>)} />
                    <Route path="model-karti" element={guarded('performance:manage', <TenantGate allowPlatform><ModelCardPage /></TenantGate>, ['hr-admin', 'tenant-admin', 'platform-admin'])} />
                    <Route path="rapor-asistani" element={guarded('performance:manage', <TenantGate><NlReportPage /></TenantGate>)} />
                    <Route path="zaman-makinesi" element={guarded('performance:manage', <TenantGate><TimeMachinePage /></TenantGate>)} />
                    <Route path="olay-radari" element={guarded('employee:manage', <TenantGate><EventRadarPage /></TenantGate>)} />
                    <Route path="ai-araclari" element={guarded('recruitment:view', <TenantGate><AiToolsPage /></TenantGate>)} />

                    {/* ---------------- Yönetişim ---------------- */}
                    <Route path="denetim" element={guarded('employee:manage', <TenantGate><AuditPage /></TenantGate>)} />
                    <Route path="kvkk" element={guarded('employee:manage', <TenantGate><PrivacyAdminPage /></TenantGate>)} />
                    <Route path="belge-sablonlari" element={guarded('document:manage', <TenantGate><DocTemplatesPage /></TenantGate>)} />
                    <Route path="kural-motoru" element={guarded('employee:manage', <TenantGate><RulesPage /></TenantGate>)} />
                    <Route path="entegrasyonlar" element={guarded('employee:manage', <TenantGate><IntegrationsPage /></TenantGate>)} />
                    <Route path="guvenlik" element={guarded('tenant:manage', <TenantGate><SecurityPage /></TenantGate>, ['tenant-admin', 'platform-admin'])} />
                    {/* Güvenlik dalgası 2B: veri koruma (İK/şirket yöneticisi) ve yöneticinin erişim gözden geçirmesi */}
                    <Route path="veri-koruma" element={guarded('employee:manage', <TenantOnly><DataProtectionPage /></TenantOnly>, ['hr-admin', 'tenant-admin'])} />
                    <Route path="erisim-gozden-gecirme" element={guarded('employee:viewAll', <TenantOnly><AccessReviewMyPage /></TenantOnly>, ['manager', 'hr-admin', 'tenant-admin'])} />
                    <Route path="ice-disa-aktarim" element={guarded('employee:manage', <TenantGate><ImportExportPage /></TenantGate>)} />
                    <Route path="api-belgeleri" element={guarded('employee:manage', <ApiDocsPage />)} />

                    {/* ---------------- İşyeri uyumu (Dalga 5c) ---------------- */}
                    <Route path="duyurular" element={<TenantOnly><AnnouncementsPage /></TenantOnly>} />
                    <Route path="belgeler-kutuphanesi" element={<TenantOnly><LibraryPage /></TenantOnly>} />
                    <Route path="etik" element={<TenantOnly><EthicsInboxPage /></TenantOnly>} />
                    <Route path="isg" element={<TenantOnly><OshPage /></TenantOnly>} />
                    <Route path="disiplin" element={guarded('performance:manage', <TenantOnly><DisciplinaryPage /></TenantOnly>, ['manager', 'hr-admin', 'tenant-admin', 'platform-admin'])} />
                    <Route path="disiplin/savunmalarim" element={<TenantOnly><MyDefencesPage /></TenantOnly>} />

                    <Route path="404" element={<NotFoundPage />} />
                    <Route path="*" element={<Navigate to="/panel/404" replace />} />
                  </Route>
                </Route>

                <Route path="/oturum-sona-erdi" element={<Suspense fallback={<FullPageSpinner />}><SessionExpiredPage /></Suspense>} />

                {/* Panel dışındaki bilinmeyen adresler: tam ekran 404 */}
                <Route path="*" element={<NotFoundPage standalone />} />
              </Routes>
            </BrowserRouter>
          </PasswordLinkDialogProvider>
          </ConfirmProvider>
          </ToastProvider>
        </AuthProvider>
      </QueryClientProvider>
    </ErrorBoundary>
  )
}
// CI/CD web deploy dogrulama
