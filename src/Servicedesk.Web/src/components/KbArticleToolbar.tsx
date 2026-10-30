import { useEditorState, type Editor } from "@tiptap/react";
import {
  Bold,
  ChevronDown,
  Code,
  Heading2,
  Heading3,
  Info,
  Italic,
  Lightbulb,
  Link as LinkIcon,
  List,
  ListOrdered,
  MessageSquareWarning,
  Minus,
  MousePointerClick,
  OctagonAlert,
  Paperclip,
  Quote,
  Table,
  TriangleAlert,
  Underline,
} from "lucide-react";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { cn } from "@/lib/utils";
import { ToolbarButton, ToolbarMenuButton, ToolbarSeparator } from "./EditorToolbarButton";
import { CALLOUT_TYPES, type CalloutType, type ImageSize } from "./KbArticleExtensions";

type Props = {
  editor: Editor | null;
  onLink: () => void;
  onAttach?: () => void;
};

const CALLOUT_META: Record<CalloutType, { label: string; icon: typeof Info; hint: string }> = {
  info: { label: "Info", icon: Info, hint: "Background or context" },
  tip: { label: "Tip", icon: Lightbulb, hint: "A shortcut or good practice" },
  warning: { label: "Warning", icon: TriangleAlert, hint: "Something to watch out for" },
  important: { label: "Important", icon: OctagonAlert, hint: "A rule that must always be followed" },
};

const IMAGE_SIZE_OPTIONS: { size: ImageSize; label: string; title: string }[] = [
  { size: "sm", label: "S", title: "Small image" },
  { size: "md", label: "M", title: "Medium image" },
  { size: null, label: "Full", title: "Full-width image" },
];

/// Toolbar for the KB article editor (RichTextEditor variant="article").
/// Reads its active states through useEditorState so the buttons follow the
/// selection, not only content changes. Context groups (image size, table
/// actions) appear only while an image or table is selected.
export function KbArticleToolbar({ editor, onLink, onAttach }: Props) {
  const s = useEditorState({
    editor,
    selector: ({ editor: e }) => {
      if (!e) return null;
      const image = e.isActive("image");
      return {
        bold: e.isActive("bold"),
        italic: e.isActive("italic"),
        underline: e.isActive("underline"),
        uiLabel: e.isActive("uiLabel"),
        code: e.isActive("code"),
        link: e.isActive("link"),
        h2: e.isActive("heading", { level: 2 }),
        h3: e.isActive("heading", { level: 3 }),
        bullet: e.isActive("bulletList"),
        ordered: e.isActive("orderedList"),
        quote: e.isActive("blockquote"),
        callout: CALLOUT_TYPES.find((t) => e.isActive("callout", { type: t })) ?? null,
        table: e.isActive("table"),
        image,
        imageSize: image ? ((e.getAttributes("image").size as ImageSize) ?? null) : null,
      };
    },
  });

  if (!editor || !s) return null;
  const chain = () => editor.chain().focus();
  // Radix hands focus back to the trigger when a menu closes; send it to the
  // text instead so the author can keep typing after picking an action.
  const returnFocus = (e: Event) => {
    e.preventDefault();
    editor.commands.focus();
  };

  const setCallout = (type: CalloutType) => {
    if (s.callout === type) chain().lift("callout").run();
    else if (s.callout) chain().updateAttributes("callout", { type }).run();
    else chain().wrapIn("callout", { type }).run();
  };

  const ActiveCalloutIcon = s.callout ? CALLOUT_META[s.callout].icon : MessageSquareWarning;

  return (
    <div className="flex items-center gap-1 px-3 py-2 border-b border-glass bg-glass flex-wrap">
      <ToolbarButton onClick={() => chain().toggleHeading({ level: 2 }).run()} active={s.h2} title="Heading">
        <Heading2 size={14} />
      </ToolbarButton>
      <ToolbarButton onClick={() => chain().toggleHeading({ level: 3 }).run()} active={s.h3} title="Subheading">
        <Heading3 size={14} />
      </ToolbarButton>

      <ToolbarSeparator />

      <ToolbarButton onClick={() => chain().toggleBold().run()} active={s.bold} title="Bold">
        <Bold size={14} />
      </ToolbarButton>
      <ToolbarButton onClick={() => chain().toggleItalic().run()} active={s.italic} title="Italic">
        <Italic size={14} />
      </ToolbarButton>
      <ToolbarButton onClick={() => chain().toggleUnderline().run()} active={s.underline} title="Underline">
        <Underline size={14} />
      </ToolbarButton>
      <ToolbarButton
        onClick={() => chain().toggleMark("uiLabel").run()}
        active={s.uiLabel}
        title="Interface label (button, menu or field name)"
      >
        <MousePointerClick size={14} />
      </ToolbarButton>
      <ToolbarButton onClick={() => chain().toggleCode().run()} active={s.code} title="Inline code">
        <Code size={14} />
      </ToolbarButton>

      <ToolbarSeparator />

      <ToolbarButton onClick={() => chain().toggleBulletList().run()} active={s.bullet} title="Bullet list">
        <List size={14} />
      </ToolbarButton>
      <ToolbarButton onClick={() => chain().toggleOrderedList().run()} active={s.ordered} title="Numbered steps">
        <ListOrdered size={14} />
      </ToolbarButton>
      <ToolbarButton onClick={() => chain().toggleBlockquote().run()} active={s.quote} title="Quote">
        <Quote size={14} />
      </ToolbarButton>

      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <ToolbarMenuButton active={s.callout !== null} title="Callout block">
            <ActiveCalloutIcon size={14} />
            <ChevronDown size={10} className="opacity-60" />
          </ToolbarMenuButton>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start" className="w-64" onCloseAutoFocus={returnFocus}>
          {CALLOUT_TYPES.map((type) => {
            const meta = CALLOUT_META[type];
            const Icon = meta.icon;
            return (
              <DropdownMenuItem key={type} onSelect={() => setCallout(type)} className="gap-2.5">
                <span className={cn("sd-callout-swatch", `sd-callout-swatch--${type}`)}>
                  <Icon size={13} />
                </span>
                <span className="flex flex-col">
                  <span className="text-sm">{meta.label}</span>
                  <span className="text-[11px] text-muted-foreground">{meta.hint}</span>
                </span>
              </DropdownMenuItem>
            );
          })}
          {s.callout && (
            <>
              <DropdownMenuSeparator />
              <DropdownMenuItem onSelect={() => chain().lift("callout").run()}>
                Remove callout
              </DropdownMenuItem>
            </>
          )}
        </DropdownMenuContent>
      </DropdownMenu>

      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <ToolbarMenuButton active={s.table} title="Table">
            <Table size={14} />
            <ChevronDown size={10} className="opacity-60" />
          </ToolbarMenuButton>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start" className="w-52" onCloseAutoFocus={returnFocus}>
          {!s.table ? (
            <DropdownMenuItem
              onSelect={() => chain().insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run()}
            >
              Insert table
            </DropdownMenuItem>
          ) : (
            <>
              <DropdownMenuItem onSelect={() => chain().addRowAfter().run()}>Add row below</DropdownMenuItem>
              <DropdownMenuItem onSelect={() => chain().addColumnAfter().run()}>Add column right</DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem onSelect={() => chain().deleteRow().run()}>Delete row</DropdownMenuItem>
              <DropdownMenuItem onSelect={() => chain().deleteColumn().run()}>Delete column</DropdownMenuItem>
              <DropdownMenuItem onSelect={() => chain().toggleHeaderRow().run()}>Toggle header row</DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem
                onSelect={() => chain().deleteTable().run()}
                className="text-destructive focus:text-destructive"
              >
                Delete table
              </DropdownMenuItem>
            </>
          )}
        </DropdownMenuContent>
      </DropdownMenu>

      <ToolbarButton onClick={() => chain().setHorizontalRule().run()} title="Divider">
        <Minus size={14} />
      </ToolbarButton>

      <ToolbarSeparator />

      <ToolbarButton onClick={onLink} active={s.link} title="Link">
        <LinkIcon size={14} />
      </ToolbarButton>
      {onAttach && (
        <ToolbarButton onClick={onAttach} title="Attach file or image">
          <Paperclip size={14} />
        </ToolbarButton>
      )}

      {s.image && (
        <>
          <ToolbarSeparator />
          <span className="px-1 text-[11px] uppercase tracking-wider text-muted-foreground">Image</span>
          {IMAGE_SIZE_OPTIONS.map((opt) => (
            <ToolbarButton
              key={opt.label}
              onClick={() => chain().updateAttributes("image", { size: opt.size }).run()}
              active={s.imageSize === opt.size}
              title={opt.title}
            >
              <span className="block min-w-4 text-[11px] font-semibold leading-3.5">{opt.label}</span>
            </ToolbarButton>
          ))}
        </>
      )}
    </div>
  );
}
