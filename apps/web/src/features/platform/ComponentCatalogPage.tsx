import { useState, type ReactNode } from 'react'
import { Plus, Sparkles, Users } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead, DataField } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { Callout } from '@/components/ui/callout'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { DataTable, type Column } from '@/components/ui/data-table'
import { Drawer } from '@/components/ui/drawer'
import { SelectField, TextAreaField, TextField } from '@/components/ui/field'
import { Modal } from '@/components/ui/modal'
import { ProgressBar } from '@/components/ui/progress'
import { StatCard } from '@/components/ui/stat-card'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { StatusBadge, type StatusTone } from '@/components/ui/status-badge'
import { Tabs } from '@/components/ui/tabs'
import { useToast } from '@/components/ui/toast'
import { tx } from '@/lib/i18n'

/**
 * Bileşen kataloğu (Platform › Bileşenler): ortak arayüz parçalarının hepsi tek sayfada, iki temada
 * denetlenebilsin; yeni ekran yazılırken buradan seçilir. Ekranlar components/ui/primitives/ altındaki
 * ilkelleri doğrudan kullanmaz (bkz. src/components/ui/primitives.test.ts).
 */
const TONES: StatusTone[] = ['neutral', 'info', 'success', 'warning', 'danger']

function Section({ title, note, children }: { title: string; note: string; children: ReactNode }) {
  return (
    <Panel>
      <PanelHead title={title} note={note} />
      <PanelBody className="space-y-4">{children}</PanelBody>
    </Panel>
  )
}

function Code({ children }: { children: string }) {
  return <code className="rounded bg-muted px-1.5 py-0.5 font-mono text-[12px]">{children}</code>
}

interface Row { id: string; name: string; dept: string; status: StatusTone }

export function ComponentCatalogPage() {
  const confirm = useConfirm()
  const toast = useToast()
  const [modal, setModal] = useState(false)
  const [drawer, setDrawer] = useState(false)
  const [tab, setTab] = useState<'a' | 'b' | 'c'>('a')
  const [check, setCheck] = useState(true)
  const [select, setSelect] = useState('')
  const rows: Row[] = [
    { id: '1', name: 'Ayşe Yılmaz', dept: tx('İnsan kaynakları'), status: 'success' },
    { id: '2', name: 'Mehmet Kaya', dept: tx('Finans'), status: 'warning' },
    { id: '3', name: 'Zeynep Demir', dept: tx('Bilgi teknolojileri'), status: 'neutral' },
  ]
  const columns: Array<Column<Row>> = [
    { id: 'name', header: tx('Ad'), cell: (r) => r.name, searchText: (r) => r.name, sortValue: (r) => r.name },
    { id: 'dept', header: tx('Departman'), cell: (r) => r.dept, searchText: (r) => r.dept, hideBelow: 'sm' },
    { id: 'status', header: tx('Durum'), cell: (r) => <StatusBadge tone={r.status}>{r.status}</StatusBadge> },
  ]

  return (
    <div className="space-y-5">
      <PageHeader title={tx('Bileşenler')} eyebrow={[tx('Platform')]}
        description={tx('Ortak arayüz parçaları. Yeni ekranlar yalnızca bunları kullanır; temayı üst çubuktan değiştirerek iki görünümü de denetleyin.')} />

      <Section title={tx('Düğmeler')} note="components/ui/button">
        <div className="flex flex-wrap gap-2">
          <Button>{tx('Birincil')}</Button>
          <Button variant="outline">{tx('Çerçeveli')}</Button>
          <Button variant="secondary">{tx('İkincil')}</Button>
          <Button variant="ghost">{tx('Sade')}</Button>
          <Button variant="destructive">{tx('Yıkıcı')}</Button>
          <Button variant="link">{tx('Bağlantı')}</Button>
          <Button disabled>{tx('Devre dışı')}</Button>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Button size="sm"><Plus className="size-4" />{tx('Küçük')}</Button>
          <Button><Plus className="size-4" />{tx('Varsayılan')}</Button>
          <Button size="lg">{tx('Büyük')}</Button>
        </div>
      </Section>

      <Section title={tx('Durum rozetleri ve ilerleme')} note="StatusBadge, ProgressBar">
        <div className="flex flex-wrap gap-2">{TONES.map((t) => <StatusBadge key={t} tone={t}>{t}</StatusBadge>)}</div>
        <div className="grid gap-3 sm:grid-cols-2">
          <ProgressBar value={72} label={tx('İlerleme')} />
          <ProgressBar value={35} tone="warning" label={tx('İlerleme')} />
        </div>
      </Section>

      <Section title={tx('Bilgi kutuları')} note={tx('Callout: renkli kutuyu elle yazmayın; tonu seçin. InfoNote = nötr Callout.')}>
        <InfoNote>{tx('Nötr not: ek bilgi, yönlendirme.')}</InfoNote>
        <Callout tone="info" title={tx('Yeni sürüm var')} action={<Button size="sm">{tx('Güncelle')}</Button>}>{tx('Başlık, açıklama ve sağda eylem.')}</Callout>
        <Callout tone="success">{tx('İşlem tamamlandı.')}</Callout>
        <Callout tone="warning">{tx('Bellek az; kullanmadığınız modülleri kapatın.')}</Callout>
        <Callout tone="danger">{tx('Arşivleme hatası: 3 başarısız deneme.')}</Callout>
      </Section>

      <Section title={tx('Kart ve alanlar')} note="Panel, PanelHead, PanelBody, DataField">
        <Panel>
          <PanelHead title={tx('Kart başlığı')} note={tx('Kısa açıklama')} action={<StatusBadge tone="info">{tx('Etiket')}</StatusBadge>} />
          <PanelBody>
            <dl className="grid gap-4 sm:grid-cols-3">
              <DataField label={tx('Ad')}>Ayşe Yılmaz</DataField>
              <DataField label={tx('Departman')}>{tx('İnsan kaynakları')}</DataField>
              <DataField label={tx('Başlangıç')}>01.03.2024</DataField>
            </dl>
          </PanelBody>
        </Panel>
        <div className="grid gap-3 sm:grid-cols-3">
          <StatCard label={tx('Çalışan')} count={248} icon={Users} trend="+4" trendDirection="up" compareLabel={tx('geçen aya göre')} />
          <StatCard label={tx('Bekleyen onay')} value={7} attention />
          <StatCard label={tx('Ortalama skor')} value="4,2" icon={Sparkles} series={[3.6, 3.9, 4.0, 3.8, 4.2]} />
        </div>
      </Section>

      <Section title={tx('Form alanları')} note="TextField, TextAreaField, SelectField, Checkbox">
        <div className="grid gap-3 sm:grid-cols-2">
          <TextField label={tx('Ad')} hint={tx('Yardım metni')} placeholder="Ayşe" />
          <TextField label={tx('E-posta')} error={tx('Geçerli bir e-posta adresi girin')} defaultValue="ayse@" />
          <SelectField label={tx('Departman')} value={select} onChange={setSelect}
            options={[{ value: 'ik', label: tx('İnsan kaynakları') }, { value: 'fin', label: tx('Finans') }]} />
          <TextAreaField label={tx('Açıklama')} rows={2} />
        </div>
        <label className="flex cursor-pointer items-center gap-2 text-[13px]"><Checkbox checked={check} onCheckedChange={(v) => setCheck(v === true)} />{tx('Onay kutusu')}</label>
      </Section>

      <Section title={tx('Pencereler ve bildirimler')} note={tx('Modal (orta), Drawer (sağdan), useConfirm (onay), useToast (kısa bildirim)')}>
        <div className="flex flex-wrap gap-2">
          <Button variant="outline" onClick={() => setModal(true)}>Modal</Button>
          <Button variant="outline" onClick={() => setDrawer(true)}>Drawer</Button>
          <Button variant="outline" onClick={() => void confirm({ title: tx('Silinsin mi?'), note: tx('Bu işlem geri alınamaz.'), action: tx('Sil') })}>useConfirm</Button>
          <Button variant="outline" onClick={() => toast.ok(tx('Kaydedildi.'))}>toast.ok</Button>
          <Button variant="outline" onClick={() => toast.stop(tx('Kaydedilemedi.'))}>toast.stop</Button>
        </div>
        <p className="text-[12px] text-muted-foreground">{tx('Yazılı onay gereken yıkıcı işlemler (ör. geri yükleme) için onay sözcüğü isteyen pencere kullanılır.')}</p>
      </Section>

      <Section title={tx('Sekmeler ve tablo')} note="Tabs, DataTable">
        <Tabs label={tx('Örnek sekmeler')} value={tab} onChange={setTab}
          tabs={[{ key: 'a', label: tx('Genel') }, { key: 'b', label: tx('Bekleyen'), count: 3 }, { key: 'c', label: tx('Arşiv') }]} />
        <DataTable rows={rows} rowKey={(r) => r.id} columns={columns} searchPlaceholder={tx('Ara')} pageSize={5} />
      </Section>

      <Section title={tx('Liste hâlleri')} note={tx('RowsSkeleton (yükleniyor), EmptyState (boş), ErrorState (hata). Ekranlar kendi hâlini kurgulamaz.')}>
        <RowsSkeleton rows={2} columns={3} />
        <EmptyState title={tx('Henüz kayıt yok')} detail={tx('İlk kaydı ekleyerek başlayın.')} action={<Button size="sm">{tx('Ekle')}</Button>} />
        <ErrorState onRetry={() => toast.info(tx('Yeniden denendi.'))} />
      </Section>

      <Section title={tx('Kurallar')} note={tx('Kod incelemesinde bakılanlar')}>
        <ul className="list-disc space-y-1.5 pl-5 text-[13px] leading-relaxed text-muted-foreground">
          <li>{tx('Renk yazmayın; tema tokenlarını ve bileşen tonlarını kullanın.')} <Code>bg-amber-500</Code> → <Code>{'<Callout tone="warning">'}</Code></li>
          <li>{tx('İlkeller (dialog, sheet, card, badge, table, alert-dialog) yalnızca ortak bileşenlerin içinde kullanılır.')}</li>
          <li>{tx('Dosya adları kebab-case; bileşen adları PascalCase.')}</li>
          <li>{tx('Her metin tx() ile; İngilizce karşılığı locales/en.json dosyasına.')}</li>
        </ul>
      </Section>

      <Modal open={modal} onClose={() => setModal(false)} title={tx('Pencere başlığı')} note={tx('Kısa açıklama')}
        footer={<><Button variant="outline" onClick={() => setModal(false)}>{tx('Vazgeç')}</Button><Button onClick={() => setModal(false)}>{tx('Kaydet')}</Button></>}>
        <TextField label={tx('Ad')} />
      </Modal>
      <Drawer open={drawer} onClose={() => setDrawer(false)} title={tx('Ayrıntı paneli')} note={tx('Kısa açıklama')} eyebrow={tx('İnsan kaynakları')}
        footer={<Button onClick={() => setDrawer(false)}>{tx('Kapat')}</Button>}>
        <dl className="grid gap-4"><DataField label={tx('Ad')}>Ayşe Yılmaz</DataField><DataField label={tx('Başlangıç')}>01.03.2024</DataField></dl>
      </Drawer>
    </div>
  )
}
