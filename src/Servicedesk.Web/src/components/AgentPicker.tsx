import * as React from "react"
import { useQuery } from "@tanstack/react-query"
import { ChevronDown, UserCircle } from "lucide-react"
import { userApi } from "@/lib/ticket-api"
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover"
import { Skeleton } from "@/components/ui/skeleton"
import { cn } from "@/lib/utils"

type AgentPickerProps = {
  value: string | null
  onChange: (userId: string | null) => void
  placeholder?: string
  className?: string
}

export function AgentPicker({
  value,
  onChange,
  placeholder = "Unassigned",
  className,
}: AgentPickerProps) {
  const [open, setOpen] = React.useState(false)

  const { data: agents } = useQuery({
    queryKey: ["agents"],
    queryFn: userApi.listAgents,
  })

  const selectedAgent = agents?.find((a) => a.id === value) ?? null

  function handleSelect(userId: string | null) {
    onChange(userId)
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
            !selectedAgent && "text-muted-foreground",
            className
          )}
        >
          <span className="flex items-center gap-2 min-w-0">
            <UserCircle className="h-3.5 w-3.5 shrink-0 opacity-50" />
            <span className="truncate">
              {selectedAgent ? selectedAgent.email : placeholder}
            </span>
          </span>
          <ChevronDown className="h-3.5 w-3.5 shrink-0 opacity-40" />
        </button>
      </PopoverTrigger>

      <PopoverContent className="w-[320px] p-0 glass-card border-white/10">
        <div className="max-h-[280px] overflow-y-auto">
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
              Unassigned
            </button>

            {!agents ? (
              <div className="mt-1 space-y-1">
                {[...Array(3)].map((_, i) => (
                  <Skeleton key={i} className="h-9 w-full" />
                ))}
              </div>
            ) : (
              agents.map((agent) => {
                const isAdmin = agent.roleName === "Admin"
                return (
                  <button
                    key={agent.id}
                    type="button"
                    onClick={() => handleSelect(agent.id)}
                    className={cn(
                      "w-full rounded-[calc(var(--radius)-2px)] px-3 py-2 text-left text-sm",
                      "transition-colors hover:bg-white/[0.07]",
                      agent.id === value && "bg-white/[0.07]"
                    )}
                  >
                    <div className="flex items-center justify-between gap-2">
                      <span className="truncate text-white">{agent.email}</span>
                      <span
                        className={cn(
                          "shrink-0 rounded px-1.5 py-0.5 text-xs font-medium",
                          isAdmin
                            ? "bg-purple-500/20 text-purple-300 border border-purple-500/30"
                            : "bg-blue-500/20 text-blue-300 border border-blue-500/30"
                        )}
                      >
                        {agent.roleName}
                      </span>
                    </div>
                  </button>
                )
              })
            )}
          </div>
        </div>
      </PopoverContent>
    </Popover>
  )
}
