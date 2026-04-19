import * as React from "react"
import { useQuery } from "@tanstack/react-query"
import { ChevronDown, Search, Plus, User } from "lucide-react"
import { contactApi, type Contact } from "@/lib/ticket-api"
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover"
import { ContactFormDialog } from "@/components/ContactFormDialog"
import { Skeleton } from "@/components/ui/skeleton"
import { Badge } from "@/components/ui/badge"
import { cn } from "@/lib/utils"

type ContactPickerProps = {
  value: string | null
  onChange: (contactId: string) => void
  placeholder?: string
  className?: string
}

export function ContactPicker({
  value,
  onChange,
  placeholder = "Select a contact…",
  className,
}: ContactPickerProps) {
  const [open, setOpen] = React.useState(false)
  const [dialogOpen, setDialogOpen] = React.useState(false)
  const [search, setSearch] = React.useState("")
  const [debouncedSearch, setDebouncedSearch] = React.useState("")
  const [selectedContact, setSelectedContact] = React.useState<Contact | null>(null)
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

      <ContactFormDialog
        open={dialogOpen}
        mode="create"
        onClose={() => setDialogOpen(false)}
        onSaved={(created) => {
          // Adopt the freshly-created contact into this form field so the
          // calling drawer can submit without a second round-trip.
          setSelectedContact(created)
          onChange(created.id)
          setDialogOpen(false)
        }}
      />
    </>
  )
}
