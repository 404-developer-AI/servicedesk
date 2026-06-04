import { useCallback, useEffect, useRef, useState } from "react";
import { ImageIcon, Layers, Layers2, Upload } from "lucide-react";
import { toast } from "sonner";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { FRAME_URL, teamProfilesApi } from "@/lib/signatures-api";

// ---- types ----

interface Props {
  userId: string;
  frameVersion: number;
  onSaved: () => void;
}

// ---- constants ----

const MAX_CANVAS_WIDTH = 600;

// ---- component ----

export function PhotoCompositor({ userId, frameVersion, onSaved }: Props) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const frameImgRef = useRef<HTMLImageElement | null>(null);
  const photoImgRef = useRef<HTMLImageElement | null>(null);

  const [canvasW, setCanvasW] = useState(400);
  const [canvasH, setCanvasH] = useState(400);

  const [offsetX, setOffsetX] = useState(0);
  const [offsetY, setOffsetY] = useState(0);
  const [scale, setScale] = useState(1);
  const [photoInFront, setPhotoInFront] = useState(true);

  const [photoLoaded, setPhotoLoaded] = useState(false);
  const [frameLoaded, setFrameLoaded] = useState(false);
  const [saving, setSaving] = useState(false);

  const dragging = useRef(false);
  const dragStart = useRef({ x: 0, y: 0, ox: 0, oy: 0 });

  // Load the frame image once (re-load if frameVersion changes)
  useEffect(() => {
    const img = new Image();
    img.onload = () => {
      frameImgRef.current = img;
      const naturalW = img.naturalWidth;
      const naturalH = img.naturalHeight;
      const w = Math.min(naturalW, MAX_CANVAS_WIDTH);
      const h = Math.round((naturalH / naturalW) * w);
      setCanvasW(w);
      setCanvasH(h);
      setFrameLoaded(true);
    };
    img.onerror = () => {
      setFrameLoaded(false);
    };
    img.src = `${FRAME_URL}?v=${frameVersion}`;
  }, [frameVersion]);

  // Centre the headshot on the canvas with a reasonable default scale
  const centrePhoto = useCallback(() => {
    const photo = photoImgRef.current;
    if (!photo) return;
    const fittedW = canvasW * 0.6;
    const fittedH = (photo.naturalHeight / photo.naturalWidth) * fittedW;
    const newScale = fittedW / photo.naturalWidth;
    setScale(newScale);
    setOffsetX(Math.round((canvasW - photo.naturalWidth * newScale) / 2));
    setOffsetY(Math.round((canvasH - fittedH) / 2));
  }, [canvasW, canvasH]);

  // Redraw canvas whenever state changes
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext("2d");
    if (!ctx) return;

    ctx.clearRect(0, 0, canvas.width, canvas.height);

    const frame = frameImgRef.current;
    const photo = photoImgRef.current;

    const drawFrame = () => {
      if (frame && frameLoaded) {
        ctx.drawImage(frame, 0, 0, canvas.width, canvas.height);
      }
    };

    const drawPhoto = () => {
      if (photo && photoLoaded) {
        const w = photo.naturalWidth * scale;
        const h = photo.naturalHeight * scale;
        ctx.drawImage(photo, offsetX, offsetY, w, h);
      }
    };

    if (photoInFront) {
      drawFrame();
      drawPhoto();
    } else {
      drawPhoto();
      drawFrame();
    }
  }, [offsetX, offsetY, scale, photoInFront, photoLoaded, frameLoaded, canvasW, canvasH]);

  // Pointer drag handlers
  const onPointerDown = (e: React.PointerEvent<HTMLCanvasElement>) => {
    if (!photoLoaded) return;
    dragging.current = true;
    dragStart.current = { x: e.clientX, y: e.clientY, ox: offsetX, oy: offsetY };
    (e.target as HTMLCanvasElement).setPointerCapture(e.pointerId);
  };

  const onPointerMove = (e: React.PointerEvent<HTMLCanvasElement>) => {
    if (!dragging.current) return;
    const dx = e.clientX - dragStart.current.x;
    const dy = e.clientY - dragStart.current.y;
    setOffsetX(dragStart.current.ox + dx);
    setOffsetY(dragStart.current.oy + dy);
  };

  const onPointerUp = () => {
    dragging.current = false;
  };

  const handlePhotoFile = (file: File) => {
    const img = new Image();
    const url = URL.createObjectURL(file);
    img.onload = () => {
      URL.revokeObjectURL(url);
      photoImgRef.current = img;
      setPhotoLoaded(true);
      // Reset position to centred after loading
      const fittedW = canvasW * 0.6;
      const fittedH = (img.naturalHeight / img.naturalWidth) * fittedW;
      const newScale = fittedW / img.naturalWidth;
      setScale(newScale);
      setOffsetX(Math.round((canvasW - img.naturalWidth * newScale) / 2));
      setOffsetY(Math.round((canvasH - fittedH) / 2));
    };
    img.onerror = () => toast.error("Could not load the selected image.");
    img.src = url;
  };

  const handleSave = () => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    setSaving(true);
    canvas.toBlob(
      (blob) => {
        if (!blob) {
          toast.error("Could not export the canvas.");
          setSaving(false);
          return;
        }
        const file = new File([blob], "photo.png", { type: "image/png" });
        teamProfilesApi
          .uploadPhoto(userId, file)
          .then(() => {
            toast.success("Photo saved.");
            onSaved();
          })
          .catch(() => toast.error("Upload failed."))
          .finally(() => setSaving(false));
      },
      "image/png",
    );
  };

  return (
    <div className="flex flex-col gap-4">
      {/* Canvas */}
      <div className="relative overflow-hidden rounded-lg border border-glass-strong bg-glass-strong">
        <canvas
          ref={canvasRef}
          width={canvasW}
          height={canvasH}
          className={cn(
            "block w-full",
            photoLoaded ? "cursor-grab active:cursor-grabbing" : "cursor-default",
          )}
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onPointerCancel={onPointerUp}
        />
        {!photoLoaded && (
          <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 bg-black/30">
            <ImageIcon className="h-8 w-8 text-muted-foreground/40" />
            <p className="text-xs text-muted-foreground">Pick a headshot to start compositing</p>
          </div>
        )}
      </div>

      {/* Controls */}
      <div className="flex flex-wrap items-center gap-3">
        {/* Pick headshot */}
        <label className={cn(
          "inline-flex cursor-pointer items-center gap-1.5 rounded border px-3 py-1.5 text-xs transition-colors",
          "border-glass-strong bg-glass text-muted-foreground hover:bg-glass-hover",
        )}>
          <Upload className="h-3.5 w-3.5" />
          {photoLoaded ? "Replace headshot" : "Pick headshot"}
          <input
            type="file"
            accept="image/*"
            className="hidden"
            onChange={(e) => {
              const file = e.target.files?.[0];
              if (file) handlePhotoFile(file);
              e.target.value = "";
            }}
          />
        </label>

        {/* Centre button */}
        {photoLoaded && (
          <Button size="sm" variant="ghost" onClick={centrePhoto} type="button">
            Centre
          </Button>
        )}

        {/* Layer order toggle */}
        <Button
          size="sm"
          variant="ghost"
          type="button"
          onClick={() => setPhotoInFront((v) => !v)}
          title={photoInFront ? "Photo is in front of frame" : "Photo is behind frame"}
          className="gap-1.5"
        >
          {photoInFront ? (
            <Layers className="h-3.5 w-3.5 text-sky-300" />
          ) : (
            <Layers2 className="h-3.5 w-3.5 text-muted-foreground" />
          )}
          {photoInFront ? "Photo in front" : "Photo behind"}
        </Button>
      </div>

      {/* Scale slider */}
      {photoLoaded && (
        <div className="flex items-center gap-3">
          <span className="w-16 text-right text-[11px] text-muted-foreground">Scale</span>
          <input
            type="range"
            min={0.05}
            max={3}
            step={0.01}
            value={scale}
            onChange={(e) => setScale(parseFloat(e.target.value))}
            className="h-1.5 w-full flex-1 appearance-none rounded-full bg-glass-strong accent-primary"
          />
          <span className="w-12 text-[11px] tabular-nums text-muted-foreground">
            {Math.round(scale * 100)}%
          </span>
        </div>
      )}

      {/* Save */}
      <div className="flex justify-end gap-2 border-t border-glass pt-3">
        <Button
          disabled={!photoLoaded || saving}
          onClick={handleSave}
          type="button"
        >
          {saving ? "Saving…" : "Save composite photo"}
        </Button>
      </div>
    </div>
  );
}
