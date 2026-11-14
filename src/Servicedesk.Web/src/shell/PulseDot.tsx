import { motion } from "framer-motion";

/// Small red-dot badge with a slow pulse for the sidebar blocks (Tags,
/// Group tags, Following). `count` null → pure indicator, used when an
/// adjacent count badge already shows the number.
export function PulseDot({ count }: { count: number | null }) {
  return (
    <motion.span
      animate={{ opacity: [1, 0.35, 1] }}
      transition={{ duration: 2, repeat: Infinity, ease: "easeInOut" }}
      className="absolute -top-0.5 -right-0.5 flex h-2.5 min-w-2.5 items-center justify-center rounded-full bg-red-500 text-[9px] font-bold leading-none text-white shadow-[0_0_0_2px_hsl(var(--background))]"
      aria-hidden={count === null ? true : undefined}
    >
      {count !== null && count > 0 && count < 10 ? count : ""}
    </motion.span>
  );
}
