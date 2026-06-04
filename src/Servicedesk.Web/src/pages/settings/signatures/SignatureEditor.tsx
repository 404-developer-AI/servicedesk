import { useEffect, useMemo, useRef, useState } from "react";
import {
  ArrowDown,
  ArrowUp,
  Check,
  ChevronDown,
  Plus,
  Save,
  Tag,
  Trash2,
  X,
} from "lucide-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { taxonomyApi } from "@/lib/api";
import {
  signaturesApi,
  signatureProfileApi,
  EMPTY_DESIGN,
  type SignatureBlock,
  type SignatureBlockType,
  type SignatureColumn,
  type SignatureDesign,
  type SignatureRow,
  type SignatureSocialItem,
  type SignatureUpsert,
} from "@/lib/signatures-api";
import { renderSignaturePreviewHtml } from "@/lib/signaturePreview";

const SIGS_LIST_KEY = ["signatures", "list"] as const;

// ---- helpers ----

function cloneDesign(d: SignatureDesign): SignatureDesign {
  return JSON.parse(JSON.stringify(d)) as SignatureDesign;
}

function makeBlock(type: SignatureBlockType): SignatureBlock {
  switch (type) {
    case "text":
    case "disclaimer":
      return { type, html: "" };
    case "image":
      return { type };
    case "divider":
      return { type, color: "#dddddd", thicknessPx: 1, marginPx: 8 };
    case "spacer":
      return { type, heightPx: 16 };
    case "social":
      return { type, social: [] };
    case "contactline":
      return { type, html: "", widthPx: 16 };
  }
}

function makeColumn(): SignatureColumn {
  return { blocks: [] };
}

function makeRow(): SignatureRow {
  return { columns: [makeColumn()] };
}

// ---- token insertion helper for plain textarea ----

function insertAtCursor(
  ref: React.RefObject<HTMLTextAreaElement | null>,
  token: string,
  current: string,
  onChange: (v: string) => void,
) {
  const el = ref.current;
  if (!el) {
    onChange(current + token);
    return;
  }
  const start = el.selectionStart ?? current.length;
  const end = el.selectionEnd ?? current.length;
  const next = current.slice(0, start) + token + current.slice(end);
  onChange(next);
  requestAnimationFrame(() => {
    el.focus();
    el.setSelectionRange(start + token.length, start + token.length);
  });
}

// ---- block editors ----

function TextBlockEditor({
  block,
  tokens,
  onChange,
}: {
  block: SignatureBlock;
  tokens: { token: string; label: string }[];
  onChange: (b: SignatureBlock) => void;
}) {
  const textareaRef = useRef<HTMLTextAreaElement | null>(null);
  const html = block.html ?? "";

  return (
    <div className="space-y-2">
      <div className="flex items-center justify-between">
        <span className="text-[11px] uppercase tracking-wider text-muted-foreground/60">
          {block.type === "disclaimer" ? "Disclaimer HTML" : "Content HTML"}
        </span>
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <button
              type="button"
              className={cn(
                "inline-flex items-center gap-1 rounded border px-2 py-0.5 text-[11px] transition-colors",
                "border-violet-400/30 bg-violet-400/10 text-violet-200 hover:bg-violet-400/15",
              )}
            >
              <Tag className="h-3 w-3" />
              Insert variable
              <ChevronDown className="h-3 w-3" />
            </button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end" className="max-h-72 w-64 overflow-auto">
            <DropdownMenuLabel className="text-[11px] uppercase tracking-wider text-muted-foreground/70">
              Available variables
            </DropdownMenuLabel>
            <DropdownMenuSeparator />
            {tokens.length === 0 ? (
              <div className="px-2 py-2 text-xs text-muted-foreground">Loading…</div>
            ) : (
              tokens.map((t) => (
                <DropdownMenuItem
                  key={t.token}
                  onSelect={(e) => {
                    e.preventDefault();
                    insertAtCursor(textareaRef, t.token, html, (v) =>
                      onChange({ ...block, html: v }),
                    );
                  }}
                  className="flex items-center justify-between gap-3"
                >
                  <span className="truncate text-sm">{t.label}</span>
                  <code className="shrink-0 rounded bg-glass-strong px-1 py-0.5 font-mono text-[10px] text-muted-foreground">
                    {t.token}
                  </code>
                </DropdownMenuItem>
              ))
            )}
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
      <textarea
        ref={textareaRef}
        value={html}
        onChange={(e) => onChange({ ...block, html: e.target.value })}
        rows={4}
        placeholder="HTML content — use Insert variable to add {{agent.*}} tokens"
        className={cn(
          "w-full resize-y rounded-md border border-input bg-glass px-3 py-2 font-mono text-xs text-foreground",
          "placeholder:text-muted-foreground/50 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring",
        )}
      />
    </div>
  );
}

function ContactLineBlockEditor({
  block,
  signatureId,
  tokens,
  onChange,
}: {
  block: SignatureBlock;
  signatureId: string;
  tokens: { token: string; label: string }[];
  onChange: (b: SignatureBlock) => void;
}) {
  const [uploading, setUploading] = useState(false);
  const fileRef = useRef<HTMLInputElement | null>(null);

  const handleUpload = async (file: File) => {
    setUploading(true);
    try {
      const asset = await signaturesApi.uploadAsset(signatureId, file);
      onChange({ ...block, assetId: asset.id });
    } catch {
      toast.error("Icon upload failed.");
    } finally {
      setUploading(false);
    }
  };

  return (
    <div className="space-y-2">
      <div className="flex items-center gap-2">
        <span className="text-[11px] uppercase tracking-wider text-muted-foreground/60">Icon</span>
        <Button size="sm" variant="secondary" disabled={uploading} onClick={() => fileRef.current?.click()}>
          {uploading ? "Uploading…" : block.assetId ? "Replace icon" : "Upload icon"}
        </Button>
        {block.assetId && (
          <Button size="sm" variant="ghost" onClick={() => onChange({ ...block, assetId: null })}>
            Remove
          </Button>
        )}
        <input
          ref={fileRef}
          type="file"
          accept="image/*"
          className="hidden"
          onChange={(e) => {
            const f = e.target.files?.[0];
            if (f) void handleUpload(f);
            e.currentTarget.value = "";
          }}
        />
      </div>
      <TextBlockEditor block={block} tokens={tokens} onChange={onChange} />
    </div>
  );
}

function ImageBlockEditor({
  block,
  signatureId,
  onChange,
}: {
  block: SignatureBlock;
  signatureId: string;
  onChange: (b: SignatureBlock) => void;
}) {
  const [uploading, setUploading] = useState(false);
  const fileRef = useRef<HTMLInputElement | null>(null);
  const isSenderPhoto = block.variable === "agent.photo";

  const handleUpload = async (file: File) => {
    setUploading(true);
    try {
      const asset = await signaturesApi.uploadAsset(signatureId, file);
      onChange({ ...block, assetId: asset.id, variable: null });
    } catch {
      toast.error("Image upload failed.");
    } finally {
      setUploading(false);
    }
  };

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-3">
        <button
          type="button"
          onClick={() => onChange({ ...block, variable: null })}
          className={cn(
            "rounded border px-2.5 py-1 text-xs transition-colors",
            !isSenderPhoto
              ? "border-primary/40 bg-primary/15 text-primary-foreground"
              : "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover",
          )}
        >
          Upload image
        </button>
        <button
          type="button"
          onClick={() => onChange({ ...block, variable: "agent.photo", assetId: null })}
          className={cn(
            "rounded border px-2.5 py-1 text-xs transition-colors",
            isSenderPhoto
              ? "border-primary/40 bg-primary/15 text-primary-foreground"
              : "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover",
          )}
        >
          Sender photo
        </button>
      </div>

      {!isSenderPhoto && (
        <div className="space-y-2">
          {block.assetId && (
            <p className="text-[11px] text-muted-foreground">
              Asset ID: <code className="font-mono">{block.assetId}</code>
            </p>
          )}
          <div className="flex items-center gap-2">
            <Button
              size="sm"
              variant="ghost"
              disabled={uploading}
              onClick={() => fileRef.current?.click()}
            >
              {uploading ? "Uploading…" : block.assetId ? "Replace image" : "Choose file"}
            </Button>
            <input
              ref={fileRef}
              type="file"
              accept="image/*"
              className="hidden"
              onChange={(e) => {
                const file = e.target.files?.[0];
                if (file) handleUpload(file);
                e.target.value = "";
              }}
            />
          </div>
        </div>
      )}

      {isSenderPhoto && (
        <p className="text-[11px] text-muted-foreground/70">
          Resolved at send time to the sender's profile photo URL.
        </p>
      )}

      <div className="grid grid-cols-2 gap-3">
        <div className="space-y-1">
          <label className="text-[11px] text-muted-foreground/70">Width (px)</label>
          <Input
            type="number"
            value={block.widthPx ?? ""}
            onChange={(e) =>
              onChange({ ...block, widthPx: e.target.value ? Number(e.target.value) : null })
            }
            placeholder="auto"
            className="h-8 bg-glass font-mono text-xs"
          />
        </div>
        <div className="space-y-1">
          <label className="text-[11px] text-muted-foreground/70">Height (px)</label>
          <Input
            type="number"
            value={block.heightPx ?? ""}
            onChange={(e) =>
              onChange({ ...block, heightPx: e.target.value ? Number(e.target.value) : null })
            }
            placeholder="auto"
            className="h-8 bg-glass font-mono text-xs"
          />
        </div>
        <div className="space-y-1">
          <label className="text-[11px] text-muted-foreground/70">Border radius (px)</label>
          <Input
            type="number"
            value={block.radiusPx ?? ""}
            onChange={(e) =>
              onChange({ ...block, radiusPx: e.target.value ? Number(e.target.value) : null })
            }
            placeholder="0"
            className="h-8 bg-glass font-mono text-xs"
          />
        </div>
        <div className="space-y-1">
          <label className="text-[11px] text-muted-foreground/70">Alt text</label>
          <Input
            value={block.alt ?? ""}
            onChange={(e) => onChange({ ...block, alt: e.target.value || null })}
            placeholder="Descriptive alt text"
            className="h-8 bg-glass text-xs"
          />
        </div>
      </div>
      <div className="space-y-1">
        <label className="text-[11px] text-muted-foreground/70">Link (href)</label>
        <Input
          value={block.href ?? ""}
          onChange={(e) => onChange({ ...block, href: e.target.value || null })}
          placeholder="https://…"
          className="h-8 bg-glass text-xs"
        />
      </div>
    </div>
  );
}

function DividerBlockEditor({
  block,
  onChange,
}: {
  block: SignatureBlock;
  onChange: (b: SignatureBlock) => void;
}) {
  return (
    <div className="grid grid-cols-3 gap-3">
      <div className="space-y-1">
        <label className="text-[11px] text-muted-foreground/70">Color</label>
        <Input
          value={block.color ?? "#dddddd"}
          onChange={(e) => onChange({ ...block, color: e.target.value })}
          placeholder="#dddddd"
          className="h-8 bg-glass font-mono text-xs"
        />
      </div>
      <div className="space-y-1">
        <label className="text-[11px] text-muted-foreground/70">Thickness (px)</label>
        <Input
          type="number"
          value={block.thicknessPx ?? 1}
          onChange={(e) =>
            onChange({ ...block, thicknessPx: e.target.value ? Number(e.target.value) : 1 })
          }
          className="h-8 bg-glass font-mono text-xs"
        />
      </div>
      <div className="space-y-1">
        <label className="text-[11px] text-muted-foreground/70">Margin (px)</label>
        <Input
          type="number"
          value={block.marginPx ?? 8}
          onChange={(e) =>
            onChange({ ...block, marginPx: e.target.value ? Number(e.target.value) : 8 })
          }
          className="h-8 bg-glass font-mono text-xs"
        />
      </div>
    </div>
  );
}

function SpacerBlockEditor({
  block,
  onChange,
}: {
  block: SignatureBlock;
  onChange: (b: SignatureBlock) => void;
}) {
  return (
    <div className="space-y-1 w-32">
      <label className="text-[11px] text-muted-foreground/70">Height (px)</label>
      <Input
        type="number"
        value={block.heightPx ?? 16}
        onChange={(e) =>
          onChange({ ...block, heightPx: e.target.value ? Number(e.target.value) : 16 })
        }
        className="h-8 bg-glass font-mono text-xs"
      />
    </div>
  );
}

function SocialBlockEditor({
  block,
  signatureId,
  onChange,
}: {
  block: SignatureBlock;
  signatureId: string;
  onChange: (b: SignatureBlock) => void;
}) {
  const items = block.social ?? [];
  const [uploading, setUploading] = useState<number | null>(null);

  const updateItem = (idx: number, partial: Partial<SignatureSocialItem>) => {
    const next = items.map((item, i) => (i === idx ? { ...item, ...partial } : item));
    onChange({ ...block, social: next });
  };

  const addItem = () => {
    onChange({ ...block, social: [...items, { network: "", url: "" }] });
  };

  const removeItem = (idx: number) => {
    onChange({ ...block, social: items.filter((_, i) => i !== idx) });
  };

  const handleIconUpload = async (idx: number, file: File) => {
    setUploading(idx);
    try {
      const asset = await signaturesApi.uploadAsset(signatureId, file);
      updateItem(idx, { assetId: asset.id });
    } catch {
      toast.error("Icon upload failed.");
    } finally {
      setUploading(null);
    }
  };

  return (
    <div className="space-y-3">
      {items.map((item, idx) => (
        <div key={idx} className="rounded border border-glass-strong bg-glass/50 p-3 space-y-2">
          <div className="flex items-center justify-between">
            <span className="text-[11px] text-muted-foreground/70">Item {idx + 1}</span>
            <Button size="sm" variant="ghost" onClick={() => removeItem(idx)}>
              <Trash2 className="h-3.5 w-3.5" />
            </Button>
          </div>
          <div className="grid grid-cols-2 gap-2">
            <div className="space-y-1">
              <label className="text-[11px] text-muted-foreground/70">Network</label>
              <Input
                value={item.network}
                onChange={(e) => updateItem(idx, { network: e.target.value })}
                placeholder="LinkedIn"
                className="h-8 bg-glass text-xs"
              />
            </div>
            <div className="space-y-1">
              <label className="text-[11px] text-muted-foreground/70">URL</label>
              <Input
                value={item.url}
                onChange={(e) => updateItem(idx, { url: e.target.value })}
                placeholder="https://…"
                className="h-8 bg-glass text-xs"
              />
            </div>
          </div>
          <div className="flex items-center gap-2">
            <span className="text-[11px] text-muted-foreground/70">Icon:</span>
            {item.assetId ? (
              <span className="text-[11px] text-muted-foreground">
                Asset <code className="font-mono">{item.assetId.slice(0, 8)}…</code>
              </span>
            ) : (
              <span className="text-[11px] text-muted-foreground/50">No icon (shows network name)</span>
            )}
            <label className={cn(
              "cursor-pointer rounded border px-2 py-0.5 text-[11px] transition-colors",
              "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover",
              uploading === idx && "opacity-50 pointer-events-none",
            )}>
              {uploading === idx ? "Uploading…" : item.assetId ? "Replace" : "Upload icon"}
              <input
                type="file"
                accept="image/*"
                className="hidden"
                onChange={(e) => {
                  const file = e.target.files?.[0];
                  if (file) handleIconUpload(idx, file);
                  e.target.value = "";
                }}
              />
            </label>
          </div>
        </div>
      ))}
      <Button size="sm" variant="ghost" onClick={addItem}>
        <Plus className="h-3.5 w-3.5" />
        Add social link
      </Button>
    </div>
  );
}

// ---- block wrapper ----

function BlockEditor({
  block,
  tokens,
  signatureId,
  canMoveUp,
  canMoveDown,
  onChange,
  onMoveUp,
  onMoveDown,
  onDelete,
}: {
  block: SignatureBlock;
  tokens: { token: string; label: string }[];
  signatureId: string;
  canMoveUp: boolean;
  canMoveDown: boolean;
  onChange: (b: SignatureBlock) => void;
  onMoveUp: () => void;
  onMoveDown: () => void;
  onDelete: () => void;
}) {
  const typeLabel: Record<SignatureBlockType, string> = {
    text: "Text",
    disclaimer: "Disclaimer",
    image: "Image",
    divider: "Divider",
    spacer: "Spacer",
    social: "Social links",
    contactline: "Contact line",
  };

  return (
    <div className="rounded border border-glass-strong bg-glass p-3 space-y-2">
      <div className="flex items-center justify-between gap-2">
        <span className="rounded bg-glass-strong px-2 py-0.5 text-[10px] uppercase tracking-wider text-muted-foreground">
          {typeLabel[block.type] ?? block.type}
        </span>
        <div className="flex items-center gap-1">
          <Button size="sm" variant="ghost" disabled={!canMoveUp} onClick={onMoveUp} aria-label="Move block up">
            <ArrowUp className="h-3.5 w-3.5" />
          </Button>
          <Button size="sm" variant="ghost" disabled={!canMoveDown} onClick={onMoveDown} aria-label="Move block down">
            <ArrowDown className="h-3.5 w-3.5" />
          </Button>
          <Button size="sm" variant="ghost" onClick={onDelete} aria-label="Delete block">
            <X className="h-3.5 w-3.5" />
          </Button>
        </div>
      </div>

      {(block.type === "text" || block.type === "disclaimer") && (
        <TextBlockEditor block={block} tokens={tokens} onChange={onChange} />
      )}
      {block.type === "image" && (
        <ImageBlockEditor block={block} signatureId={signatureId} onChange={onChange} />
      )}
      {block.type === "divider" && (
        <DividerBlockEditor block={block} onChange={onChange} />
      )}
      {block.type === "spacer" && (
        <SpacerBlockEditor block={block} onChange={onChange} />
      )}
      {block.type === "social" && (
        <SocialBlockEditor block={block} signatureId={signatureId} onChange={onChange} />
      )}
      {block.type === "contactline" && (
        <ContactLineBlockEditor block={block} signatureId={signatureId} tokens={tokens} onChange={onChange} />
      )}
    </div>
  );
}

// ---- column editor ----

const BLOCK_TYPES: SignatureBlockType[] = ["text", "disclaimer", "image", "divider", "spacer", "social", "contactline"];

function ColumnEditor({
  column,
  tokens,
  signatureId,
  colIndex,
  totalCols,
  canMoveLeft,
  canMoveRight,
  onChange,
  onAddColumn,
  onDeleteColumn,
}: {
  column: SignatureColumn;
  tokens: { token: string; label: string }[];
  signatureId: string;
  colIndex: number;
  totalCols: number;
  canMoveLeft: boolean;
  canMoveRight: boolean;
  onChange: (c: SignatureColumn) => void;
  onAddColumn: () => void;
  onDeleteColumn: () => void;
}) {
  void canMoveLeft;
  void canMoveRight;

  const updateBlock = (idx: number, block: SignatureBlock) => {
    const blocks = column.blocks.map((b, i) => (i === idx ? block : b));
    onChange({ ...column, blocks });
  };

  const moveBlock = (idx: number, dir: -1 | 1) => {
    const blocks = [...column.blocks];
    const target = idx + dir;
    if (target < 0 || target >= blocks.length) return;
    [blocks[idx], blocks[target]] = [blocks[target]!, blocks[idx]!];
    onChange({ ...column, blocks });
  };

  const deleteBlock = (idx: number) => {
    onChange({ ...column, blocks: column.blocks.filter((_, i) => i !== idx) });
  };

  const addBlock = (type: SignatureBlockType) => {
    onChange({ ...column, blocks: [...column.blocks, makeBlock(type)] });
  };

  return (
    <div className="flex-1 min-w-0 rounded border border-glass bg-glass/30 p-3 space-y-3">
      <div className="flex items-center justify-between gap-2">
        <span className="text-[11px] text-muted-foreground/60">
          Column {colIndex + 1} of {totalCols}
        </span>
        <div className="flex items-center gap-1.5">
          <div className="flex items-center gap-1">
            <label className="text-[11px] text-muted-foreground/60">Width%</label>
            <Input
              type="number"
              value={column.widthPct ?? ""}
              onChange={(e) =>
                onChange({ ...column, widthPct: e.target.value ? Number(e.target.value) : null })
              }
              placeholder="auto"
              className="h-6 w-16 bg-glass font-mono text-[11px]"
            />
          </div>
          <Select
            value={column.valign ?? "top"}
            onValueChange={(v) =>
              onChange({ ...column, valign: v as "top" | "middle" | "bottom" })
            }
          >
            <SelectTrigger className="h-6 w-24 bg-glass text-[11px]">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="top">Top</SelectItem>
              <SelectItem value="middle">Middle</SelectItem>
              <SelectItem value="bottom">Bottom</SelectItem>
            </SelectContent>
          </Select>
          {totalCols < 3 && (
            <Button size="sm" variant="ghost" onClick={onAddColumn} aria-label="Add column">
              <Plus className="h-3.5 w-3.5" />
            </Button>
          )}
          {totalCols > 1 && (
            <Button size="sm" variant="ghost" onClick={onDeleteColumn} aria-label="Delete column">
              <Trash2 className="h-3.5 w-3.5" />
            </Button>
          )}
        </div>
      </div>

      <div className="space-y-2">
        {column.blocks.map((block, idx) => (
          <BlockEditor
            key={idx}
            block={block}
            tokens={tokens}
            signatureId={signatureId}
            canMoveUp={idx > 0}
            canMoveDown={idx < column.blocks.length - 1}
            onChange={(b) => updateBlock(idx, b)}
            onMoveUp={() => moveBlock(idx, -1)}
            onMoveDown={() => moveBlock(idx, 1)}
            onDelete={() => deleteBlock(idx)}
          />
        ))}
      </div>

      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button size="sm" variant="ghost">
            <Plus className="h-3.5 w-3.5" />
            Add block
            <ChevronDown className="h-3.5 w-3.5" />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start">
          {BLOCK_TYPES.map((t) => (
            <DropdownMenuItem key={t} onSelect={() => addBlock(t)}>
              {t.charAt(0).toUpperCase() + t.slice(1)}
            </DropdownMenuItem>
          ))}
        </DropdownMenuContent>
      </DropdownMenu>
    </div>
  );
}

// ---- row editor ----

function RowEditor({
  row,
  rowIndex,
  totalRows,
  tokens,
  signatureId,
  onChange,
  onMoveUp,
  onMoveDown,
  onDelete,
}: {
  row: SignatureRow;
  rowIndex: number;
  totalRows: number;
  tokens: { token: string; label: string }[];
  signatureId: string;
  onChange: (r: SignatureRow) => void;
  onMoveUp: () => void;
  onMoveDown: () => void;
  onDelete: () => void;
}) {
  const updateColumn = (idx: number, col: SignatureColumn) => {
    const columns = row.columns.map((c, i) => (i === idx ? col : c));
    onChange({ ...row, columns });
  };

  const addColumn = () => {
    if (row.columns.length >= 3) return;
    onChange({ ...row, columns: [...row.columns, makeColumn()] });
  };

  const deleteColumn = (idx: number) => {
    if (row.columns.length <= 1) return;
    onChange({ ...row, columns: row.columns.filter((_, i) => i !== idx) });
  };

  return (
    <div className="rounded-lg border border-glass-strong bg-glass p-4 space-y-3">
      <div className="flex items-center justify-between gap-2">
        <span className="text-xs font-medium text-muted-foreground/70">Row {rowIndex + 1}</span>
        <div className="flex items-center gap-1">
          <Button size="sm" variant="ghost" disabled={rowIndex === 0} onClick={onMoveUp} aria-label="Move row up">
            <ArrowUp className="h-4 w-4" />
          </Button>
          <Button size="sm" variant="ghost" disabled={rowIndex === totalRows - 1} onClick={onMoveDown} aria-label="Move row down">
            <ArrowDown className="h-4 w-4" />
          </Button>
          <Button size="sm" variant="ghost" onClick={onDelete} aria-label="Delete row">
            <Trash2 className="h-4 w-4" />
          </Button>
        </div>
      </div>
      <div className="flex gap-3">
        {row.columns.map((col, idx) => (
          <ColumnEditor
            key={idx}
            column={col}
            tokens={tokens}
            signatureId={signatureId}
            colIndex={idx}
            totalCols={row.columns.length}
            canMoveLeft={idx > 0}
            canMoveRight={idx < row.columns.length - 1}
            onChange={(c) => updateColumn(idx, c)}
            onAddColumn={addColumn}
            onDeleteColumn={() => deleteColumn(idx)}
          />
        ))}
      </div>
    </div>
  );
}

// ---- live preview ----

function SignaturePreview({
  design,
  assetUrlById,
}: {
  design: SignatureDesign;
  assetUrlById: (id: string) => string | null;
}) {
  const varsQ = useQuery({
    queryKey: ["me", "signature-variables"],
    queryFn: () => signatureProfileApi.variables(),
    staleTime: 60_000,
  });

  const vars = varsQ.data ?? {
    fullName: "Jane Smith",
    firstName: "Jane",
    lastName: "Smith",
    jobTitle: "Support Agent",
    email: "jane@example.com",
    phone: "",
    mobile: "",
    photoUrl: null,
  };

  const html = useMemo(
    () => renderSignaturePreviewHtml(design, assetUrlById, vars),
    [design, assetUrlById, vars],
  );

  const inner = useMemo(() => ({ __html: html }), [html]);

  return (
    <div className="rounded-lg border border-glass-strong bg-glass p-4 space-y-2">
      <h3 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
        Live preview
      </h3>
      <div className="rounded bg-white p-4 text-sm shadow-inner">
        <div dangerouslySetInnerHTML={inner} />
      </div>
      {varsQ.isLoading && (
        <p className="text-[11px] text-muted-foreground/50">Loading preview variables…</p>
      )}
    </div>
  );
}

// ---- main editor ----

export function SignatureEditor({
  initialId,
  onDone,
}: {
  initialId: string | null;
  onDone: () => void;
}) {
  const qc = useQueryClient();

  // Track the actual signature id (null until the draft is created for "new")
  const [sigId, setSigId] = useState<string | null>(initialId);
  const [creatingDraft, setCreatingDraft] = useState(initialId === null);

  const [name, setName] = useState("Untitled signature");
  const [enabled, setEnabled] = useState(true);
  const [isSystem, setIsSystem] = useState(false);
  const [queueIds, setQueueIds] = useState<string[]>([]);
  const [design, setDesign] = useState<SignatureDesign>(cloneDesign(EMPTY_DESIGN));

  // asset lookup built from the loaded detail
  const [assetMap, setAssetMap] = useState<Map<string, string>>(new Map());

  const detailQ = useQuery({
    queryKey: ["signatures", "detail", sigId],
    queryFn: () => signaturesApi.get(sigId!),
    enabled: sigId !== null,
  });

  const tokensQ = useQuery({
    queryKey: ["signatures", "tokens"],
    queryFn: () => signaturesApi.listTokens(),
    staleTime: Infinity,
  });

  const queuesQ = useQuery({
    queryKey: ["taxonomy", "queues"],
    queryFn: () => taxonomyApi.queues.list(),
  });

  // For new signatures: create the draft immediately so asset uploads have an id
  useEffect(() => {
    if (initialId !== null || !creatingDraft) return;
    let cancelled = false;
    signaturesApi
      .create({
        name: "Untitled signature",
        design: EMPTY_DESIGN,
        isSystem: false,
        enabled: true,
        queueIds: [],
      })
      .then((detail) => {
        if (cancelled) return;
        setSigId(detail.id);
        setCreatingDraft(false);
      })
      .catch(() => {
        if (cancelled) return;
        toast.error("Could not create signature draft.");
        onDone();
      });
    return () => {
      cancelled = true;
    };
  }, [initialId, creatingDraft, onDone]);

  // Seed the editable state ONCE per loaded signature. Re-seeding on every
  // detailQ refetch (focus, invalidation, …) would clobber unsaved edits — the
  // bug where a just-picked column valign snapped straight back to its saved
  // value.
  const seededRef = useRef<string | null>(null);
  useEffect(() => {
    const d = detailQ.data;
    if (!d || seededRef.current === d.id) return;
    seededRef.current = d.id;
    setName(d.name);
    setEnabled(d.enabled);
    setIsSystem(d.isSystem);
    setQueueIds(d.queueIds);
    setDesign(cloneDesign(d.design));
  }, [detailQ.data]);

  // Keep the asset-id → URL map in sync with the loaded detail. This never
  // clobbers design edits; it only feeds the live preview's images.
  useEffect(() => {
    const d = detailQ.data;
    if (!d) return;
    const map = new Map<string, string>();
    for (const a of d.assets) map.set(a.id, a.url);
    setAssetMap(map);
  }, [detailQ.data]);

  const tokens = tokensQ.data?.tokens ?? [];
  const queues = queuesQ.data ?? [];
  const queueSet = useMemo(() => new Set(queueIds), [queueIds]);

  const assetUrlById = useMemo(
    () => (id: string) => assetMap.get(id) ?? null,
    [assetMap],
  );

  const save = useMutation({
    mutationFn: async () => {
      if (!sigId) throw new Error("No signature id");
      const payload: SignatureUpsert = {
        name: name.trim() || "Untitled signature",
        design,
        isSystem,
        enabled,
        queueIds,
      };
      return signaturesApi.update(sigId, payload);
    },
    onSuccess: () => {
      toast.success("Signature saved.");
      qc.invalidateQueries({ queryKey: SIGS_LIST_KEY });
      qc.invalidateQueries({ queryKey: ["signatures", "detail", sigId] });
      onDone();
    },
    onError: () => toast.error("Save failed."),
  });

  const toggleQueue = (id: string) => {
    setQueueIds((prev) =>
      prev.includes(id) ? prev.filter((q) => q !== id) : [...prev, id],
    );
  };

  const updateRow = (idx: number, row: SignatureRow) => {
    setDesign((d) => {
      const next = cloneDesign(d);
      next.rows[idx] = row;
      return next;
    });
  };

  const moveRow = (idx: number, dir: -1 | 1) => {
    setDesign((d) => {
      const next = cloneDesign(d);
      const target = idx + dir;
      if (target < 0 || target >= next.rows.length) return next;
      [next.rows[idx], next.rows[target]] = [next.rows[target]!, next.rows[idx]!];
      return next;
    });
  };

  const deleteRow = (idx: number) => {
    setDesign((d) => {
      const next = cloneDesign(d);
      next.rows = next.rows.filter((_, i) => i !== idx);
      return next;
    });
  };

  const addRow = () => {
    setDesign((d) => {
      const next = cloneDesign(d);
      next.rows = [...next.rows, makeRow()];
      return next;
    });
  };

  if (creatingDraft || (sigId && detailQ.isLoading)) {
    return (
      <div className="flex items-center justify-center py-12 text-sm text-muted-foreground">
        {creatingDraft ? "Creating signature draft…" : "Loading signature…"}
      </div>
    );
  }

  const canSave = !!sigId && !save.isPending;

  return (
    <div className="flex flex-col gap-5">
      {/* Top bar */}
      <div className="flex flex-wrap items-center gap-4">
        <div className="flex-1 min-w-48">
          <Input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Signature name"
            className="h-9 bg-glass font-medium"
          />
        </div>

        <div className="flex items-center gap-2">
          <span className="text-xs text-muted-foreground">Enabled</span>
          <Switch checked={enabled} onCheckedChange={setEnabled} />
        </div>

        <div className="flex items-center gap-2">
          <TooltipProvider delayDuration={200}>
            <Tooltip>
              <TooltipTrigger asChild>
                <span className="text-xs text-muted-foreground cursor-default">System signature</span>
              </TooltipTrigger>
              <TooltipContent side="top" className="max-w-xs text-xs">
                {"Used for trigger-fired and automated mail. No person variables ({{agent.*}}) are injected."}
              </TooltipContent>
            </Tooltip>
          </TooltipProvider>
          <Switch checked={isSystem} onCheckedChange={setIsSystem} />
        </div>

        <div className="flex items-center gap-2 ml-auto">
          <Button variant="ghost" onClick={onDone}>
            Cancel
          </Button>
          <Button disabled={!canSave} onClick={() => save.mutate()}>
            <Save className="h-4 w-4" />
            Save signature
          </Button>
        </div>
      </div>

      {/* Queue scope */}
      <div className="rounded-lg border border-glass-strong bg-glass p-4 space-y-2">
        <div className="flex items-center justify-between">
          <h3 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
            Assigned mailboxes
          </h3>
          {queueIds.length > 0 && (
            <button
              type="button"
              onClick={() => setQueueIds([])}
              className="text-[11px] text-muted-foreground hover:text-foreground"
            >
              Clear (all queues)
            </button>
          )}
        </div>
        <p className="text-xs text-muted-foreground">
          Leave empty to apply to all queues. Select queues to restrict this signature to specific mailboxes.
        </p>
        {queuesQ.isLoading ? (
          <p className="text-xs text-muted-foreground">Loading queues…</p>
        ) : queues.length === 0 ? (
          <p className="text-xs text-muted-foreground">No queues configured.</p>
        ) : (
          <div className="flex flex-wrap gap-2">
            {queues.map((q) => {
              const selected = queueSet.has(q.id);
              return (
                <button
                  key={q.id}
                  type="button"
                  onClick={() => toggleQueue(q.id)}
                  className={cn(
                    "inline-flex items-center gap-1.5 rounded-full border px-3 py-1 text-xs transition-colors",
                    selected
                      ? "border-primary/40 bg-primary/15 text-primary-foreground"
                      : "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover",
                  )}
                  aria-pressed={selected}
                >
                  {selected && <Check className="h-3.5 w-3.5" />}
                  {q.name}
                </button>
              );
            })}
          </div>
        )}
      </div>

      {/* Two-pane layout */}
      <div className="grid grid-cols-1 gap-5 lg:grid-cols-2">
        {/* Structure editor */}
        <div className="space-y-4">
          <div className="flex items-center justify-between">
            <h3 className="text-xs font-medium uppercase tracking-widest text-muted-foreground/60">
              Structure
            </h3>
            <Button size="sm" variant="ghost" onClick={addRow}>
              <Plus className="h-4 w-4" />
              Add row
            </Button>
          </div>

          {design.rows.length === 0 && (
            <p className="rounded-lg border border-dashed border-glass-strong py-8 text-center text-sm text-muted-foreground">
              No rows yet. Add a row to start building the signature.
            </p>
          )}

          {design.rows.map((row, idx) => (
            <RowEditor
              key={idx}
              row={row}
              rowIndex={idx}
              totalRows={design.rows.length}
              tokens={tokens}
              signatureId={sigId ?? ""}
              onChange={(r) => updateRow(idx, r)}
              onMoveUp={() => moveRow(idx, -1)}
              onMoveDown={() => moveRow(idx, 1)}
              onDelete={() => deleteRow(idx)}
            />
          ))}
        </div>

        {/* Live preview — sticky */}
        <div className="lg:sticky lg:top-6 lg:self-start">
          <SignaturePreview design={design} assetUrlById={assetUrlById} />
        </div>
      </div>
    </div>
  );
}
