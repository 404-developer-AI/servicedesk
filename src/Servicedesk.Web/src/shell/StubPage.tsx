import { Badge } from "@/components/ui/badge";
import { MeshSurface } from "@/shell/MeshBackground";

type StubPageProps = {
  title: string;
  description: string;
  comingIn: string;
  icon?: React.ReactNode;
};

export function StubPage({ title, description, comingIn, icon }: StubPageProps) {
  return (
    <div className="relative flex min-h-[calc(100vh-8rem)] items-center justify-center p-8">
      <MeshSurface className="absolute inset-0" />
      <div className="glass-card relative z-10 w-full max-w-xl p-10">
        <div className="flex items-start justify-between gap-4">
          <div className="space-y-2">
            {icon && <div className="mb-4 text-primary">{icon}</div>}
            <h1 className="text-display-md font-semibold text-foreground">{title}</h1>
            <p className="text-sm text-muted-foreground">{description}</p>
          </div>
          <Badge variant="secondary" className="shrink-0 border border-white/10 bg-white/[0.05] text-xs font-normal text-muted-foreground">
            Coming in {comingIn}
          </Badge>
        </div>
      </div>
    </div>
  );
}
