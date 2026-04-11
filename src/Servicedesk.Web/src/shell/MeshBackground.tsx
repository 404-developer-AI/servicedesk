import { useEffect, useMemo, useRef, useState } from "react";
import { Canvas, useFrame } from "@react-three/fiber";
import * as THREE from "three";
import { cn } from "@/lib/utils";

// Animated purple/blue "mesh gradient" on a full-screen quad. Cheap fragment
// shader, no post-processing. Scoped to "low-work" surfaces only (stub pages,
// login, 404) — inside the working app the cheaper CSS `.app-background` is
// used instead. See ARCHITECTURE.md § UI shell & navigation.

const vertexShader = /* glsl */ `
  varying vec2 vUv;
  void main() {
    vUv = uv;
    gl_Position = vec4(position, 1.0);
  }
`;

const fragmentShader = /* glsl */ `
  precision highp float;
  varying vec2 vUv;
  uniform float uTime;
  uniform vec2 uResolution;

  // Soft two-lobe radial gradient, animated.
  vec3 palette(float t) {
    vec3 purple = vec3(0.45, 0.18, 0.85); // ~ hsl(265 89% 45%)
    vec3 blue   = vec3(0.20, 0.30, 0.90); // ~ hsl(220 89% 55%)
    vec3 dark   = vec3(0.04, 0.04, 0.06);
    vec3 mid = mix(purple, blue, 0.5 + 0.5 * sin(t));
    return mix(dark, mid, smoothstep(0.0, 1.0, t));
  }

  void main() {
    vec2 uv = vUv;
    float aspect = uResolution.x / max(uResolution.y, 1.0);
    vec2 p = vec2((uv.x - 0.5) * aspect, uv.y - 0.5);

    float t = uTime * 0.12;
    vec2 c1 = vec2(cos(t) * 0.35 - 0.25, sin(t * 0.8) * 0.25 - 0.15);
    vec2 c2 = vec2(sin(t * 0.6) * 0.35 + 0.30, cos(t * 0.7) * 0.25 + 0.20);

    float d1 = length(p - c1);
    float d2 = length(p - c2);

    vec3 purple = vec3(0.45, 0.18, 0.85);
    vec3 blue   = vec3(0.20, 0.30, 0.90);
    vec3 dark   = vec3(0.04, 0.04, 0.07);

    float f1 = smoothstep(0.6, 0.0, d1);
    float f2 = smoothstep(0.7, 0.0, d2);

    vec3 col = dark;
    col = mix(col, purple, f1 * 0.55);
    col = mix(col, blue, f2 * 0.45);

    // Subtle vignette.
    float v = smoothstep(1.1, 0.2, length(p));
    col *= mix(0.85, 1.0, v);

    gl_FragColor = vec4(col, 1.0);
  }
`;

function MeshPlane() {
  const materialRef = useRef<THREE.ShaderMaterial>(null);
  const uniforms = useMemo(
    () => ({
      uTime: { value: 0 },
      uResolution: { value: new THREE.Vector2(1, 1) },
    }),
    []
  );

  useFrame((state) => {
    if (!materialRef.current) return;
    materialRef.current.uniforms.uTime.value = state.clock.getElapsedTime();
    const size = state.size;
    materialRef.current.uniforms.uResolution.value.set(size.width, size.height);
  });

  return (
    <mesh>
      <planeGeometry args={[2, 2]} />
      <shaderMaterial
        ref={materialRef}
        uniforms={uniforms}
        vertexShader={vertexShader}
        fragmentShader={fragmentShader}
      />
    </mesh>
  );
}

function usePrefersReducedMotion(): boolean {
  const [reduced, setReduced] = useState<boolean>(() => {
    if (typeof window === "undefined" || !window.matchMedia) return false;
    return window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  });

  useEffect(() => {
    const mql = window.matchMedia("(prefers-reduced-motion: reduce)");
    const handler = (e: MediaQueryListEvent) => setReduced(e.matches);
    mql.addEventListener("change", handler);
    return () => mql.removeEventListener("change", handler);
  }, []);

  return reduced;
}

function useDocumentVisible(): boolean {
  const [visible, setVisible] = useState<boolean>(() => {
    if (typeof document === "undefined") return true;
    return document.visibilityState !== "hidden";
  });
  useEffect(() => {
    const handler = () => setVisible(document.visibilityState !== "hidden");
    document.addEventListener("visibilitychange", handler);
    return () => document.removeEventListener("visibilitychange", handler);
  }, []);
  return visible;
}

type MeshSurfaceProps = {
  className?: string;
};

/**
 * WebGL mesh background restricted to "low-work" surfaces (stub pages, login,
 * 404). Falls back to the static CSS gradient when the user prefers reduced
 * motion, and pauses the render loop when the tab is hidden.
 */
export function MeshSurface({ className }: MeshSurfaceProps) {
  const reduced = usePrefersReducedMotion();
  const visible = useDocumentVisible();

  if (reduced) {
    return <div className={cn("app-background", className)} aria-hidden />;
  }

  return (
    <div className={cn("pointer-events-none", className)} aria-hidden>
      <Canvas
        orthographic
        camera={{ position: [0, 0, 1], zoom: 1 }}
        dpr={[1, 1.5]}
        frameloop={visible ? "always" : "never"}
        gl={{ antialias: false, powerPreference: "low-power" }}
      >
        <MeshPlane />
      </Canvas>
    </div>
  );
}
