import type { Config } from "tailwindcss";
import animate from "tailwindcss-animate";

export default {
  darkMode: "class",
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  theme: {
    extend: {
      fontFamily: {
        sans: ["Inter", "system-ui", "sans-serif"],
        display: ["Inter", "system-ui", "sans-serif"],
        mono: ["ui-monospace", "SFMono-Regular", "Menlo", "monospace"],
      },
      colors: {
        background: "hsl(var(--background))",
        foreground: "hsl(var(--foreground))",
        border: "hsl(var(--border))",
        ring: "hsl(var(--ring))",
        muted: {
          DEFAULT: "hsl(var(--muted))",
          foreground: "hsl(var(--muted-foreground))",
        },
        card: {
          DEFAULT: "hsl(var(--card))",
          foreground: "hsl(var(--card-foreground))",
        },
        popover: {
          DEFAULT: "hsl(var(--popover))",
          foreground: "hsl(var(--popover-foreground))",
        },
        primary: {
          DEFAULT: "hsl(var(--primary))",
          foreground: "hsl(var(--primary-foreground))",
        },
        secondary: {
          DEFAULT: "hsl(var(--secondary))",
          foreground: "hsl(var(--secondary-foreground))",
        },
        accent: {
          DEFAULT: "hsl(var(--accent))",
          foreground: "hsl(var(--accent-foreground))",
          purple: "hsl(265 89% 70%)",
          blue: "hsl(220 89% 65%)",
        },
        destructive: {
          DEFAULT: "hsl(var(--destructive))",
          foreground: "hsl(var(--destructive-foreground))",
        },
        input: "hsl(var(--input))",
      },
      // Semantic glass/overlay utilities — the alpha is baked into each CSS
      // variable so Tailwind's opacity-modifier syntax (bg-glass/50) is NOT
      // supported here. These exist as backgroundColor / borderColor and not
      // under the generic `colors` map so they don't accidentally show up
      // wherever a colour is consumed.
      backgroundColor: {
        glass: "hsl(var(--glass-bg))",
        "glass-strong": "hsl(var(--glass-bg-strong))",
        "glass-hover": "hsl(var(--glass-hover))",
        overlay: "hsl(var(--overlay))",
      },
      borderColor: {
        glass: "hsl(var(--glass-border))",
        "glass-strong": "hsl(var(--glass-border-strong))",
      },
      divideColor: {
        glass: "hsl(var(--glass-border))",
        "glass-strong": "hsl(var(--glass-border-strong))",
      },
      borderRadius: {
        lg: "var(--radius)",
        md: "calc(var(--radius) - 2px)",
        sm: "calc(var(--radius) - 4px)",
      },
      fontSize: {
        "display-2xl": ["4.5rem", { lineHeight: "1.02", letterSpacing: "-0.035em" }],
        "display-xl": ["3.5rem", { lineHeight: "1.05", letterSpacing: "-0.03em" }],
        "display-lg": ["2.75rem", { lineHeight: "1.08", letterSpacing: "-0.028em" }],
        "display-md": ["2.125rem", { lineHeight: "1.1", letterSpacing: "-0.025em" }],
        "display-sm": ["1.625rem", { lineHeight: "1.15", letterSpacing: "-0.022em" }],
      },
      keyframes: {
        "accordion-down": {
          from: { height: "0" },
          to: { height: "var(--radix-accordion-content-height)" },
        },
        "accordion-up": {
          from: { height: "var(--radix-accordion-content-height)" },
          to: { height: "0" },
        },
      },
      animation: {
        "accordion-down": "accordion-down 0.2s ease-out",
        "accordion-up": "accordion-up 0.2s ease-out",
      },
    },
  },
  plugins: [animate],
} satisfies Config;
