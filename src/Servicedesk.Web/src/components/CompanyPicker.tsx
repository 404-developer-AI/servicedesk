import * as React from "react"
import { useQuery } from "@tanstack/react-query"
import { ChevronDown, Search, Building2, X } from "lucide-react"
import { companyApi } from "@/lib/ticket-api"
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover"
import { Skeleton } from "@/components/ui/skeleton"
import { cn } from "@/lib/utils"

type CompanyPickerProps = {
  value: string | null
  onChange: (companyId: string | null) => void
  placeholder?: string
  className?: string
}

export function CompanyPicker({
  value,
  onChange,
  placeholder = "Select a company…",
  className,
}: CompanyPickerProps) {
  const [open, setOpen] = React.useState(false)
  const [search, setSearch] = React.useState("")
  const [debouncedSearch, setDebouncedSearch] = React.useState("")
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

  const { data: companies, isFetching } = useQuery({
    queryKey: ["companies", "picker", debouncedSearch],
    queryFn: () => companyApi.list(debouncedSearch || undefined),
    placeholderData: (prev) => prev,
  })

  const selectedCompany = companies?.find((c) => c.id === value) ?? null

  function handleSelect(companyId: string | null) {
    onChange(companyId)
    setOpen(false)
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          className={cn(
            "h-9 px-3 rounded-[var(--radius)] border border-white/10 bg-white/[0.04] text-sm",
            "hover:bg-white/[0.07] transition-colors w-full text-left",
            "flex items-center justify-between gap-2",
            !selectedCompany && "text-muted-foreground",
            className
          )}
        >
          <span className="flex items-center gap-2 min-w-0">
            <Building2 className="h-3.5 w-3.5 shrink-0 opacity-50" />
            <span className="truncate">
              {selectedCompany ? selectedCompany.name : placeholder}
            </span>
          </span>
          <span className="flex items-center gap-1 shrink-0">
            {value && (
              <span
                role="button"
                tabIndex={0}
                onClick={(e) => {
                  e.stopPropagation()
                  onChange(null)
                }}
                onKeyDown={(e) => {
                  if (e.key === "Enter" || e.key === " ") {
                    e.stopPropagation()
                    onChange(null)
                  }
                }}
                className="rounded p-0.5 hover:bg-white/10 transition-colors"
                aria-label="Clear company"
              >
                <X className="h-3 w-3 opacity-40 hover:opacity-70" />
              </span>
            )}
            <ChevronDown className="h-3.5 w-3.5 opacity-40" />
          </span>
        </button>
      </PopoverTrigger>

      <PopoverContent className="w-[350px] p-0 glass-card border-white/10">
        <div className="flex items-center border-b border-white/10 px-3">
          <Search className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
          <input
            ref={searchRef}
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search companies…"
            className="flex h-9 w-full bg-transparent px-2 text-sm outline-none placeholder:text-muted-foreground"
          />
          {isFetching && (
            <div className="h-3 w-3 animate-spin rounded-full border border-white/20 border-t-white/60" />
          )}
        </div>

        <div className="max-h-[240px] overflow-y-auto">
          <div className="p-1">
            <button
              type="button"
              onClick={() => handleSelect(null)}
              className={cn(
                "w-full rounded-[calc(var(--radius)-2px)] px-3 py-2 text-left text-sm text-muted-foreground",
                "transition-colors hover:bg-white/[0.07] hover:text-white",
                value === null && "bg-white/[0.07] text-white"
              )}
            >
              None
            </button>

            {!companies ? (
              <div className="mt-1 space-y-1">
                {[...Array(4)].map((_, i) => (
                  <Skeleton key={i} className="h-8 w-full" />
                ))}
              </div>
            ) : companies.length === 0 ? (
              <p className="py-5 text-center text-sm text-muted-foreground">
                No companies found
              </p>
            ) : (
              companies.map((company) => (
                <button
                  key={company.id}
                  type="button"
                  onClick={() => handleSelect(company.id)}
                  className={cn(
                    "w-full rounded-[calc(var(--radius)-2px)] px-3 py-2 text-left text-sm font-medium text-white",
                    "transition-colors hover:bg-white/[0.07]",
                    company.id === value && "bg-white/[0.07]"
                  )}
                >
                  {company.name}
                </button>
              ))
            )}
          </div>
        </div>
      </PopoverContent>
    </Popover>
  )
}
