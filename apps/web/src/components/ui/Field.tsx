import {
  useId,
  type InputHTMLAttributes,
  type ReactNode,
  type TextareaHTMLAttributes,
} from 'react'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

/**
 * Form alanı sarmalayıcıları: etiket, ipucu ve hata mesajı tek yerde.
 * Hata varsa ipucu yerine hata gösterilir; `aria-describedby` doğru
 * öğeye bağlanır, `aria-invalid` işaretlenir.
 */

function Shell({
  id,
  label,
  hint,
  error,
  required,
  children,
}: {
  id: string
  label: string
  hint?: string
  error?: string
  required?: boolean
  children: ReactNode
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <Label htmlFor={id} className="text-[13px]">
        {label}
        {required && <span className="font-normal text-muted-foreground">{tx('zorunlu')}</span>}
      </Label>
      {children}
      {hint && !error && (
        <p id={`${id}-hint`} className="text-[12px] leading-relaxed text-muted-foreground">
          {hint}
        </p>
      )}
      {error && (
        <p id={`${id}-error`} role="alert" className="text-[12px] leading-relaxed text-destructive">
          {error}
        </p>
      )}
    </div>
  )
}

const describedBy = (id: string, hint?: string, error?: string) =>
  error ? `${id}-error` : hint ? `${id}-hint` : undefined

interface Base {
  label: string
  hint?: string
  error?: string
}

export function TextField({
  label,
  hint,
  error,
  id: idProp,
  className,
  ...props
}: Base & InputHTMLAttributes<HTMLInputElement>) {
  const auto = useId()
  const id = idProp ?? auto
  return (
    <Shell id={id} label={label} hint={hint} error={error} required={props.required}>
      <Input
        id={id}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy(id, hint, error)}
        className={className}
        {...props}
      />
    </Shell>
  )
}

export function TextAreaField({
  label,
  hint,
  error,
  id: idProp,
  className,
  ...props
}: Base & TextareaHTMLAttributes<HTMLTextAreaElement>) {
  const auto = useId()
  const id = idProp ?? auto
  return (
    <Shell id={id} label={label} hint={hint} error={error} required={props.required}>
      <Textarea
        id={id}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy(id, hint, error)}
        className={cn('min-h-20', className)}
        {...props}
      />
    </Shell>
  )
}

export interface SelectOption {
  value: string
  label: string
  disabled?: boolean
}

export function SelectField({
  label,
  hint,
  error,
  id: idProp,
  value,
  onChange,
  options,
  placeholder = tx('Seçin'),
  disabled,
  required,
  className,
}: Base & {
  id?: string
  value: string
  onChange: (value: string) => void
  options: SelectOption[]
  placeholder?: string
  disabled?: boolean
  required?: boolean
  className?: string
}) {
  const auto = useId()
  const id = idProp ?? auto
  return (
    <Shell id={id} label={label} hint={hint} error={error} required={required}>
      <Select value={value} onValueChange={onChange} disabled={disabled}>
        <SelectTrigger
          id={id}
          className={cn('w-full', className)}
          aria-invalid={error ? true : undefined}
          aria-describedby={describedBy(id, hint, error)}
        >
          <SelectValue placeholder={placeholder} />
        </SelectTrigger>
        <SelectContent>
          {options.map((option) => (
            <SelectItem key={option.value} value={option.value} disabled={option.disabled}>
              {option.label}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </Shell>
  )
}
