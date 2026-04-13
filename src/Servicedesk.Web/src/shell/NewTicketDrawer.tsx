import { useState, useEffect, type ReactNode } from "react";
import { useForm, Controller } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Drawer } from "vaul";
import { Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";

import { RichTextEditor } from "@/components/RichTextEditor";
import { ContactPicker } from "@/components/ContactPicker";
import { AgentPicker } from "@/components/AgentPicker";
import { agentQueueApi, taxonomyApi } from "@/lib/api";
import { ticketApi } from "@/lib/ticket-api";
import { cn } from "@/lib/utils";

const createTicketSchema = z.object({
  subject: z.string().min(3, "Subject must be at least 3 characters"),
  bodyHtml: z.string().optional(),
  requesterContactId: z.string().uuid("Select a requester"),
  queueId: z.string().uuid(),
  statusId: z.string().uuid(),
  priorityId: z.string().uuid(),
  categoryId: z.string().uuid().optional().or(z.literal("")),
  assigneeUserId: z.string().uuid().optional().or(z.literal("")).or(z.null()),
});

type CreateTicketForm = z.infer<typeof createTicketSchema>;

const STALE_TIME = 60_000;

const STATE_CATEGORY_COLORS: Record<string, string> = {
  New: "bg-blue-500/20 text-blue-300 border-blue-500/30",
  Open: "bg-green-500/20 text-green-300 border-green-500/30",
  Pending: "bg-yellow-500/20 text-yellow-300 border-yellow-500/30",
  Resolved: "bg-purple-500/20 text-purple-300 border-purple-500/30",
  Closed: "bg-zinc-500/20 text-zinc-300 border-zinc-500/30",
};

function FormLabel({ children }: { children: ReactNode }) {
  return (
    <label className="text-xs font-medium text-muted-foreground uppercase tracking-wider mb-1.5 block">
      {children}
    </label>
  );
}

function FieldError({ message }: { message?: string }) {
  if (!message) return null;
  return <p className="mt-1 text-xs text-destructive">{message}</p>;
}

function ColorDot({ color }: { color: string }) {
  return (
    <span
      className="inline-block h-2 w-2 rounded-full shrink-0"
      style={{ backgroundColor: color || "#6b7280" }}
    />
  );
}

type TaxonomySelectProps = {
  value: string;
  onChange: (value: string) => void;
  options: Array<{ id: string; name: string; color: string; badge?: string }>;
  placeholder?: string;
  disabled?: boolean;
  allowEmpty?: boolean;
  emptyLabel?: string;
};

function TaxonomySelect({
  value,
  onChange,
  options,
  placeholder,
  disabled,
  allowEmpty,
  emptyLabel = "None",
}: TaxonomySelectProps) {
  return (
    <Select
      value={value || undefined}
      onValueChange={(v) => onChange(v === "__empty__" ? "" : v)}
      disabled={disabled}
    >
      <SelectTrigger
        className={cn(
          "h-9 border-white/10 bg-white/[0.04] focus:border-white/20 focus:bg-white/[0.06] transition-colors",
          !value && "text-muted-foreground",
        )}
      >
        <SelectValue placeholder={placeholder} />
      </SelectTrigger>
      <SelectContent className="border-white/10 bg-background/95 backdrop-blur-xl">
        {allowEmpty && (
          <SelectItem value="__empty__">{emptyLabel}</SelectItem>
        )}
        {options.map((opt) => (
          <SelectItem key={opt.id} value={opt.id}>
            <div className="flex items-center gap-2">
              <ColorDot color={opt.color} />
              <span>{opt.name}</span>
              {opt.badge && (
                <span className="text-[10px] text-muted-foreground">
                  ({opt.badge})
                </span>
              )}
            </div>
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}

export function NewTicketDrawer({ children }: { children: ReactNode }) {
  const [open, setOpen] = useState(false);
  const queryClient = useQueryClient();

  const { data: queues } = useQuery({
    queryKey: ["accessible-queues"],
    queryFn: agentQueueApi.list,
    staleTime: 60_000,
  });

  const { data: priorities } = useQuery({
    queryKey: ["taxonomy", "priorities"],
    queryFn: taxonomyApi.priorities.list,
    staleTime: STALE_TIME,
  });

  const { data: statuses } = useQuery({
    queryKey: ["taxonomy", "statuses"],
    queryFn: taxonomyApi.statuses.list,
    staleTime: STALE_TIME,
  });

  const { data: categories } = useQuery({
    queryKey: ["taxonomy", "categories"],
    queryFn: taxonomyApi.categories.list,
    staleTime: STALE_TIME,
  });

  const defaultQueueId =
    queues?.find((q) => q.isActive)?.id ?? "";

  const defaultPriorityId =
    priorities?.find((p) => p.isDefault && p.isActive)?.id ??
    priorities?.find((p) => p.isActive)?.id ??
    "";

  const defaultStatusId =
    statuses?.find((s) => s.isDefault && s.stateCategory === "New")?.id ??
    statuses?.find((s) => s.isDefault)?.id ??
    statuses?.find((s) => s.stateCategory === "New")?.id ??
    "";

  const {
    register,
    handleSubmit,
    control,
    reset,
    setValue,
    formState: { errors },
  } = useForm<CreateTicketForm>({
    resolver: zodResolver(createTicketSchema),
    defaultValues: {
      subject: "",
      bodyHtml: "",
      requesterContactId: "",
      queueId: "",
      statusId: "",
      priorityId: "",
      categoryId: "",
      assigneeUserId: null,
    },
  });

  useEffect(() => {
    if (defaultQueueId) setValue("queueId", defaultQueueId);
  }, [defaultQueueId, setValue]);

  useEffect(() => {
    if (defaultPriorityId) setValue("priorityId", defaultPriorityId);
  }, [defaultPriorityId, setValue]);

  useEffect(() => {
    if (defaultStatusId) setValue("statusId", defaultStatusId);
  }, [defaultStatusId, setValue]);

  const { mutate: createTicket, isPending } = useMutation({
    mutationFn: ticketApi.create,
    onSuccess: () => {
      toast.success("Ticket created");
      queryClient.invalidateQueries({ queryKey: ["tickets"] });
      setOpen(false);
      reset();
    },
    onError: () => {
      toast.error("Failed to create ticket");
    },
  });

  function onSubmit(data: CreateTicketForm) {
    createTicket({
      subject: data.subject,
      bodyHtml: data.bodyHtml || undefined,
      requesterContactId: data.requesterContactId,
      queueId: data.queueId,
      statusId: data.statusId,
      priorityId: data.priorityId,
      categoryId: data.categoryId || undefined,
      assigneeUserId: data.assigneeUserId || undefined,
      source: "Web",
    });
  }

  function handleClose() {
    setOpen(false);
    reset();
  }

  const activeQueues = (queues ?? []).filter((q) => q.isActive);
  const activePriorities = (priorities ?? [])
    .filter((p) => p.isActive)
    .sort((a, b) => a.sortOrder - b.sortOrder);
  const activeStatuses = (statuses ?? []).filter((s) => s.isActive);
  const activeCategories = (categories ?? []).filter((c) => c.isActive);

  const queueOptions = activeQueues.map((q) => ({
    id: q.id,
    name: q.name,
    color: q.color,
  }));

  const priorityOptions = activePriorities.map((p) => ({
    id: p.id,
    name: p.name,
    color: p.color,
  }));

  const statusOptions = activeStatuses.map((s) => ({
    id: s.id,
    name: s.name,
    color: s.color,
    badge: s.stateCategory,
  }));

  const categoryOptions = activeCategories.map((c) => ({
    id: c.id,
    name: c.name,
    color: "#6b7280",
  }));

  const taxonomyReady =
    queues !== undefined &&
    priorities !== undefined &&
    statuses !== undefined &&
    categories !== undefined;

  return (
    <Drawer.Root open={open} onOpenChange={setOpen}>
      <Drawer.Trigger asChild>{children}</Drawer.Trigger>
      <Drawer.Portal>
        <Drawer.Overlay className="fixed inset-0 z-50 bg-black/60 backdrop-blur-sm" />
        <Drawer.Content className="fixed inset-x-0 bottom-0 z-50 mx-auto flex max-h-[90vh] max-w-2xl flex-col rounded-t-[var(--radius)] border border-white/10 bg-background/90 backdrop-blur-xl">
          <Drawer.Title className="sr-only">New ticket</Drawer.Title>
          <Drawer.Description className="sr-only">
            Create a new support ticket.
          </Drawer.Description>

          <div className="mx-auto mt-3 h-1 w-10 shrink-0 rounded-full bg-white/20" aria-hidden />

          <div className="flex items-center justify-between px-6 py-4 border-b border-white/10 shrink-0">
            <h2 className="font-display text-display-sm font-semibold">New ticket</h2>
            {!taxonomyReady && (
              <span className="flex items-center gap-1.5 text-xs text-muted-foreground">
                <Loader2 className="h-3 w-3 animate-spin" />
                Loading…
              </span>
            )}
          </div>

          <form
            onSubmit={handleSubmit(onSubmit)}
            className="flex min-h-0 flex-1 flex-col"
          >
            <div className="flex min-h-0 flex-1 overflow-y-auto">
              <div className="flex flex-1 flex-col gap-0 min-[560px]:flex-row">
                {/* Left column — Subject + Body */}
                <div className="flex flex-1 flex-col gap-4 p-6 min-[560px]:border-r min-[560px]:border-white/10">
                  <div>
                    <FormLabel>Subject *</FormLabel>
                    <Input
                      {...register("subject")}
                      placeholder="Brief summary of the issue"
                      className="border-white/10 bg-white/[0.04] focus:border-white/20 focus:bg-white/[0.06]"
                    />
                    <FieldError message={errors.subject?.message} />
                  </div>

                  <div className="flex-1">
                    <FormLabel>Description</FormLabel>
                    <Controller
                      name="bodyHtml"
                      control={control}
                      render={({ field }) => (
                        <RichTextEditor
                          content={field.value ?? ""}
                          onChange={field.onChange}
                          placeholder="Describe the issue…"
                          minHeight="180px"
                        />
                      )}
                    />
                  </div>
                </div>

                {/* Right column — metadata */}
                <div className="flex w-full flex-col gap-4 p-6 min-[560px]:w-[280px] min-[560px]:shrink-0">
                  <div>
                    <FormLabel>Requester *</FormLabel>
                    <Controller
                      name="requesterContactId"
                      control={control}
                      render={({ field }) => (
                        <ContactPicker
                          value={field.value || null}
                          onChange={field.onChange}
                          placeholder="Select a contact…"
                        />
                      )}
                    />
                    <FieldError message={errors.requesterContactId?.message} />
                  </div>

                  <div>
                    <FormLabel>Queue *</FormLabel>
                    <Controller
                      name="queueId"
                      control={control}
                      render={({ field }) => (
                        <TaxonomySelect
                          value={field.value}
                          onChange={field.onChange}
                          options={queueOptions}
                          placeholder="Select queue…"
                          disabled={!taxonomyReady}
                        />
                      )}
                    />
                    <FieldError message={errors.queueId?.message} />
                  </div>

                  <div>
                    <FormLabel>Priority *</FormLabel>
                    <Controller
                      name="priorityId"
                      control={control}
                      render={({ field }) => (
                        <TaxonomySelect
                          value={field.value}
                          onChange={field.onChange}
                          options={priorityOptions}
                          placeholder="Select priority…"
                          disabled={!taxonomyReady}
                        />
                      )}
                    />
                    <FieldError message={errors.priorityId?.message} />
                  </div>

                  <div>
                    <FormLabel>Status *</FormLabel>
                    <Controller
                      name="statusId"
                      control={control}
                      render={({ field }) => (
                        <>
                          <TaxonomySelect
                            value={field.value}
                            onChange={field.onChange}
                            options={statusOptions}
                            placeholder="Select status…"
                            disabled={!taxonomyReady}
                          />
                          {field.value && (() => {
                            const st = statuses?.find((s) => s.id === field.value);
                            return st ? (
                              <span
                                className={cn(
                                  "mt-1.5 inline-flex items-center rounded border px-1.5 py-0.5 text-[10px] font-medium",
                                  STATE_CATEGORY_COLORS[st.stateCategory] ??
                                    "bg-zinc-500/20 text-zinc-300 border-zinc-500/30",
                                )}
                              >
                                {st.stateCategory}
                              </span>
                            ) : null;
                          })()}
                        </>
                      )}
                    />
                    <FieldError message={errors.statusId?.message} />
                  </div>

                  <div>
                    <FormLabel>Category</FormLabel>
                    <Controller
                      name="categoryId"
                      control={control}
                      render={({ field }) => (
                        <TaxonomySelect
                          value={field.value ?? ""}
                          onChange={field.onChange}
                          options={categoryOptions}
                          placeholder="Select category…"
                          disabled={!taxonomyReady}
                          allowEmpty
                          emptyLabel="None"
                        />
                      )}
                    />
                  </div>

                  <div>
                    <FormLabel>Assignee</FormLabel>
                    <Controller
                      name="assigneeUserId"
                      control={control}
                      render={({ field }) => (
                        <AgentPicker
                          value={field.value ?? null}
                          onChange={field.onChange}
                          placeholder="Unassigned"
                        />
                      )}
                    />
                  </div>
                </div>
              </div>
            </div>

            {/* Footer */}
            <div className="flex shrink-0 items-center justify-between gap-3 border-t border-white/10 px-6 py-4">
              <div className="flex items-center gap-2">
                {Object.keys(errors).length > 0 && (
                  <p className="text-xs text-destructive">
                    Please fix the errors above.
                  </p>
                )}
              </div>
              <div className="flex items-center gap-2">
                <Button
                  type="button"
                  variant="ghost"
                  onClick={handleClose}
                  disabled={isPending}
                >
                  Cancel
                </Button>
                <Button
                  type="submit"
                  disabled={isPending || !taxonomyReady}
                  className="bg-gradient-to-r from-accent-purple to-accent-blue text-white hover:opacity-90 transition-opacity border-0"
                >
                  {isPending ? (
                    <>
                      <Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" />
                      Creating…
                    </>
                  ) : (
                    "Create ticket"
                  )}
                </Button>
              </div>
            </div>
          </form>
        </Drawer.Content>
      </Drawer.Portal>
    </Drawer.Root>
  );
}
