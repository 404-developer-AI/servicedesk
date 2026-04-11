import type { Config } from "tailwindcss";

export default {
  darkMode: "class",
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  theme: {
    extend: {
      fontFamily: {
        sans: ["Inter", "system-ui", "sans-serif"],
        display: ["Inter", "system-ui", "sans-serif"],
      },
      colors: {
        background: "hsl(240 10% 4%)",
        foreground: "hsl(0 0% 98%)",
        accent: {
          DEFAULT: "hsl(265 89% 70%)",
          blue: "hsl(220 89% 65%)",
        },
      },
    },
  },
  plugins: [],
} satisfies Config;
