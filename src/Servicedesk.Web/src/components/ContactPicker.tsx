import * as React from "react"
import { useQuery, useQueryClient } from "@tanstack/react-query"
import { ChevronDown, Search, Plus, User } from "lucide-react"
import { contactApi, type Contact, type ContactInput } from "@/lib/ticket-api"
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover"
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter } from "@/components/ui/dialog"
import { Skeleton } from "@/components/ui/skeleton"
import { Badge } from "@/components/ui/badge"
import { cn } from "@/lib/utils"

type ContactPickerProps = {
  value: string | null
  onChange: (contactId: string) => void
  placeholder?: string
  className?: string
}

type CreateFormState = {
  firstName: string
  lastName: string
  email: string
}

type CreateFormErrors = {
  email?: string
}

export function ContactPicker({
  value,
  onChange,
  placeholder = "Select a contact…",
  className,
}: ContactPickerProps) {
  const queryClient = useQueryClient()
  const [open, setOpen] = React.useState(false)
  const [dialogOpen, setDialogOpen] = React.useState(false)
  const [search, setSearch] = React.useState("")
  const [debouncedSearch, setDebouncedSearch] = React.useState("")
  const [selectedContact, setSelectedContact] = React.useState<Contact | null>(null)
  const [createForm, setCreateForm] = React.useState<CreateFormState>({
    firstName: "",
    lastName: "",
    email: "",
  })
  const [createErrors, setCreateErrors] = React.useState<CreateFormErrors>({})
  const [creating, setCreating] = React.useState(false)
  const searchRef = React.useRef<HTMLInputElement>(null)

  React.useEffect(() => {
    const timer = setTimeout(() => setDebouncedSearch(search), 300)
    return () => clearTimeout(timer)
  }, [search])

  React.useEffect(() => {
    if (open && searchRef.current) {
      setTimeout(() => searchRef.current?.focus(), 50)
    }
    if (!open) setSearch("")
  }, [open])

  const { data: contacts, isFetching } = useQuery({
    queryKey: ["contacts", "picker", debouncedSearch],
    queryFn: () => contactApi.list(debouncedSearch || undefined),
    placeholderData: (prev) => prev,
  })

  React.useEffect(() => {
    if (value && !selectedContact) {
      contactApi.list().then((all) => {
        const found = all.find((c) => c.id === value)
        if (found) setSelectedContact(found)
      })
    }
    if (!value) setSelectedContact(null)
  }, [value, selectedContact])

  function handleSelect(contact: Contact) {
    setSelectedContact(contact)
    onChange(contact.id)
    setOpen(false)
  }

  function validateCreate(form: CreateFormState): CreateFormErrors {
    const errs: CreateFormErrors = {}
    if (!form.email.trim()) {
      errs.email = "Email is required"
    } else if (!form.email.includes("@")) {
      errs.email = "Enter a valid email address"
    }
    return errs
  }

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault()
    const errs = validateCreate(createForm)
    if (Object.keys(errs).length > 0) {
      setCreateErrors(errs)
      return
    }
    setCreating(true)
    try {
      const input: ContactInput = {
        email: createForm.email.trim(),
        firstName: createForm.firstName.trim() || undefined,
        lastName: createForm.lastName.trim() || undefined,
      }
      const created = await contactApi.create(input)
      await queryClient.invalidateQueries({ queryKey: ["contacts"] })
      setSelectedContact(created)
      onChange(created.id)
      setDialogOpen(false)
      setOpen(false)
      setCreateForm({ firstName: "", lastName: "", email: "" })
      setCreateErrors({})
    } finally {
      setCreating(false)
    }
  }

  const displayLabel = selectedContact
    ? [selectedContact.firstName, selectedContact.lastName].filter(Boolean).join(" ") ||
      selectedContact.email
    : null

  const displaySub = selectedContact && displayLabel !== selectedContact.email
    ? selectedContact.email
    : null

  return (
    <>
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverTrigger asChild>
          <button
            type="button"
            className={cn(
              "h-9 px-3 rounded-[var(--radius)] border border-white/10 bg-white/[0.04] text-sm",
              "hover:bg-white/[0.07] transition-colors w-full text-left",
              "flex items-center justify-between gap-2",
              !displayLabel && "text-muted-foreground",
              className
            )}
          >
            <span className="flex items-center gap-2 min-w-0">
              <User className="h-3.5 w-3.5 shrink-0 opacity-50" />
              <span className="truncate">
                {displayLabel ?? placeholder}
              </span>
              {displaySub && (
                <span className="text-muted-foreground truncate text-xs">
                  {displaySub}
                </span>
              )}
            </span>
            <ChevronDown className="h-3.5 w-3.5 shrink-0 opacity-40" />
          </button>
        </PopoverTrigger>

        <PopoverContent className="w-[350px] p-0 glass-card border-white/10">
          <div className="flex items-center border-b border-white/10 px-3">
            <Search className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
            <input
              ref={searchRef}
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search contacts…"
              className="flex h-9 w-full bg-transparent px-2 text-sm outline-none placeholder:text-muted-foreground"
            />
            {isFetching && (
              <div className="h-3 w-3 animate-spin rounded-full border border-white/20 border-t-white/60" />
            )}
          </div>

          <div className="max-h-[240px] overflow-y-auto">
            {!contacts ? (
              <div className="space-y-1 p-2">
                {[...Array(4)].map((_, i) => (
                  <Skeleton key={i} className="h-8 w-full" />
                ))}
              </div>
            ) : contacts.length === 0 ? (
              <p className="py-6 text-center text-sm text-muted-foreground">
                No contacts found
              </p>
            ) : (
              <div className="p-1">
                {contacts.map((contact) => {
                  const name = [contact.firstName, contact.lastName]
                    .filter(Boolean)
                    .join(" ")
                  const isSelected = contact.id === value
                  return (
                    <button
                      key={contact.id}
                      type="button"
                      onClick={() => handleSelect(contact)}
                      className={cn(
                        "w-full rounded-[calc(var(--radius)-2px)] px-3 py-2 text-left text-sm",
                        "transition-colors hover:bg-white/[0.07]",
                        isSelected && "bg-white/[0.07]"
                      )}
                    >
                      <div className="flex items-center justify-between gap-2">
                        <div className="min-w-0">
                          {name && (
                            <div className="truncate font-medium text-white">
                              {name}
                            </div>
                          )}
                          <div className="truncate text-xs text-muted-foreground">
                            {contact.email}
                          </div>
                        </div>
                        {contact.primaryCompanyId && (
                          <Badge
                            variant="outline"
                            className="shrink-0 border-white/10 text-xs text-muted-foreground"
                          >
                            {contact.companyRole || "Contact"}
                          </Badge>
                        )}
                      </div>
                    </button>
                  )
                })}
              </div>
            )}
          </div>

          <div className="border-t border-white/10 p-1">
            <button
              type="button"
              onClick={() => {
                setDialogOpen(true)
                setOpen(false)
              }}
              className="flex w-full items-center gap-2 rounded-[calc(var(--radius)-2px)] px-3 py-2 text-sm text-muted-foreground transition-colors hover:bg-white/[0.07] hover:text-white"
            >
              <Plus className="h-3.5 w-3.5" />
              New contact
            </button>
          </div>
        </PopoverContent>
      </Popover>

      <Dialog open={dialogOpen} onOpenChange={setDialogOpen}>
        <DialogContent className="glass-card border-white/10 sm:max-w-[400px]">
          <DialogHeader>
            <DialogTitle>New contact</DialogTitle>
          </DialogHeader>
          <form onSubmit={handleCreate} className="space-y-3">
            <div className="grid grid-cols-2 gap-3">
              <div className="space-y-1">
                <label className="text-xs text-muted-foreground">
                  First name
                </label>
                <input
                  value={createForm.firstName}
                  onChange={(e) =>
                    setCreateForm((f) => ({ ...f, firstName: e.target.value }))
                  }
                  className="flex h-9 w-full rounded-[var(--radius)] border border-white/10 bg-white/[0.04] px-3 text-sm outline-none placeholder:text-muted-foreground focus:border-white/20 focus:bg-white/[0.06] transition-colors"
                  placeholder="First"
                />
              </div>
              <div className="space-y-1">
                <label className="text-xs text-muted-foreground">
                  Last name
                </label>
                <input
                  value={createForm.lastName}
                  onChange={(e) =>
                    setCreateForm((f) => ({ ...f, lastName: e.target.value }))
                  }
                  className="flex h-9 w-full rounded-[var(--radius)] border border-white/10 bg-white/[0.04] px-3 text-sm outline-none placeholder:text-muted-foreground focus:border-white/20 focus:bg-white/[0.06] transition-colors"
                  placeholder="Last"
                />
              </div>
            </div>
            <div className="space-y-1">
              <label className="text-xs text-muted-foreground">
                Email <span className="text-destructive">*</span>
              </label>
              <input
                type="email"
                value={createForm.email}
                onChange={(e) => {
                  setCreateForm((f) => ({ ...f, email: e.target.value }))
                  setCreateErrors({})
                }}
                className={cn(
                  "flex h-9 w-full rounded-[var(--radius)] border border-white/10 bg-white/[0.04] px-3 text-sm outline-none placeholder:text-muted-foreground focus:border-white/20 focus:bg-white/[0.06] transition-colors",
                  createErrors.email && "border-destructive/50"
                )}
                placeholder="email@example.com"
              />
              {createErrors.email && (
                <p className="text-xs text-destructive">{createErrors.email}</p>
              )}
            </div>
            <DialogFooter>
              <button
                type="button"
                onClick={() => setDialogOpen(false)}
                className="h-9 px-4 rounded-[var(--radius)] border border-white/10 bg-white/[0.04] text-sm hover:bg-white/[0.07] transition-colors"
              >
                Cancel
              </button>
              <button
                type="submit"
                disabled={creating}
                className="h-9 px-4 rounded-[var(--radius)] bg-primary text-primary-foreground text-sm font-medium hover:bg-primary/90 transition-colors disabled:opacity-50"
              >
                {creating ? "Creating…" : "Create contact"}
              </button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </>
  )
}
